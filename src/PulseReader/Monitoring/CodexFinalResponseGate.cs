namespace PulseReader.Monitoring;

// A final-answer heading identifies the phase, not the end of streaming.
// Wait for a quiet interval before handing the complete visible body over.
internal sealed class CodexFinalResponseGate
{
    private static readonly TimeSpan QuietInterval = TimeSpan.FromMilliseconds(1500);
    private WindowTextChange? _pending;
    private DateTimeOffset _readyAt;
    private string _publishedKey = "";
    private string _publishedText = "";
    private readonly HashSet<string> _historicalFinalKeys = new(StringComparer.Ordinal);

    public void Reset()
    {
        _pending = null;
        _publishedKey = "";
        _publishedText = "";
        _historicalFinalKeys.Clear();
    }

    public void EstablishBaseline(WindowTextSnapshot snapshot)
    {
        Reset();
        foreach (var block in snapshot.Blocks)
            if (block.IsFinalResponse && ConversationMessageKey.IsKnown(block.Key))
                _historicalFinalKeys.Add(block.Key);
    }

    public IReadOnlyList<WindowTextChange> Observe(IReadOnlyList<WindowTextChange> changes, DateTimeOffset now)
    {
        var ordinary = new List<WindowTextChange>();
        foreach (var change in changes)
        {
            if (!change.IsFinalResponse || !ConversationMessageKey.IsKnown(change.Key))
            {
                ordinary.Add(change);
                continue;
            }
            // A historical answer can gain, lose, or replace mounted paragraphs
            // as the user scrolls. Its identity stays silent until a new response
            // cycle resets the gate; comparing only its baseline body is unsafe.
            if (_historicalFinalKeys.Contains(change.Key)) continue;
            if (change.IsConfirmedFinalHandoff)
            {
                // The following live section confirms this final has closed.
                // Publish before the next response cycle can reset the gate.
                _pending = change;
                _readyAt = now;
                ordinary.AddRange(TakeReady(now));
                continue;
            }
            if (_pending is not { } pending || pending.Key != change.Key || pending.Text != change.Text)
            {
                _pending = change;
                _readyAt = now + QuietInterval;
            }
        }
        return ordinary;
    }

    public TimeSpan? TimeUntilReady(DateTimeOffset now) => _pending is null ? null : _readyAt - now;

    internal bool HasPending => _pending is not null;

    internal IReadOnlyList<WindowTextChange> ConfirmPending() => TakeReady(_readyAt);

    public IReadOnlyList<WindowTextChange> TakeReady(DateTimeOffset now)
    {
        if (_pending is not { } pending || now < _readyAt) return [];
        _pending = null;
        var text = pending.Text;
        var result = pending;
        if (_publishedKey == pending.Key)
        {
            if (text == _publishedText) return [];
            if (text.StartsWith(_publishedText, StringComparison.Ordinal))
                result = pending with { Text = text[_publishedText.Length..], IsNewBlock = false, IsFinalResponse = false };
        }
        _publishedKey = pending.Key;
        _publishedText = text;
        return [result];
    }
}
