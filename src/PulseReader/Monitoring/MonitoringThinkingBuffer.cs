namespace PulseReader.Monitoring;

internal sealed record MonitoringThinkingSnapshot(string TurnKey, string Text, bool Expanded, bool Retired);

internal sealed class MonitoringThinkingEventArgs(MonitoringThinkingSnapshot? snapshot, long contextVersion) : EventArgs
{
    public MonitoringThinkingSnapshot? Snapshot { get; } = snapshot;
    public long ContextVersion { get; } = contextVersion;
}

// Owned by the speech queue's lock. Keep a bounded, latest view of the thought
// text and an index of units already started, rather than treating a reopened
// panel as a new stream. Hidden units are ineligible for playback.
internal sealed class MonitoringThinkingBuffer
{
    private string _text = "";
    private string[] _units = [];
    private int _next;
    private bool _retired;
    public string TurnKey { get; private set; } = "";
    public long Version { get; private set; }
    public bool Enabled { get; private set; }
    public int PendingCount => Enabled ? Math.Max(0, _units.Length - _next) : 0;

    public bool Update(MonitoringThinkingSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            // An incomplete accessibility redraw is not a new turn. Suspend
            // eligibility but keep progress for when the same panel returns.
            if (Enabled) Version++;
            Enabled = false;
            return false;
        }
        if (snapshot.TurnKey != TurnKey)
        {
            Clear();
            TurnKey = snapshot.TurnKey;
        }
        _retired |= snapshot.Retired;
        var enabled = snapshot.Expanded && !_retired;
        if (Enabled != enabled) { Version++; Enabled = enabled; }
        if (_retired) { _text = ""; _units = []; return false; }
        if (snapshot.Text.Length > MonitoringSpeechQueue.MaximumMonitoredQueueCharacters)
        {
            Clear();
            TurnKey = snapshot.TurnKey;
            _retired = true;
            return true;
        }
        if (_text != snapshot.Text)
        {
            var accumulator = new WindowTextAccumulator();
            accumulator.Append(new(snapshot.Text, true, true));
            _units = accumulator.TakeCompletedUnits(flushIncomplete: false).ToArray();
            _text = snapshot.Text;
        }
        return false;
    }

    public (string Text, int Index)? Take()
    {
        if (PendingCount == 0) return null;
        var index = _next++;
        return (_units[index], index);
    }

    public void ReturnUnstarted(string key, int index)
    {
        if (key == TurnKey && _next == index + 1) _next = index;
    }

    public void Clear()
    {
        Version++;
        TurnKey = _text = "";
        _units = [];
        _next = 0;
        Enabled = _retired = false;
    }
}
