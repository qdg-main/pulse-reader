namespace PulseReader.Monitoring;

internal sealed class CodexThoughtSettlementFilter
{
    private const int MaximumCharacters = 100_000;
    private const int MaximumUnits = 512;
    private readonly TimeSpan _settlementDelay;
    private readonly WindowTextFingerprinter _fingerprinter;
    private readonly Dictionary<string, int> _emittedOccurrences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _suppressedEchoOccurrences = new(StringComparer.Ordinal);
    private List<Candidate> _candidates = [];
    private string _key = "";
    private string _body = "";
    private bool _visible;
    private bool _bypass;
    private WindowTextChange? _overflowChange;

    public CodexThoughtSettlementFilter(WindowTextFingerprinter fingerprinter,
        TimeSpan? settlementDelay = null)
    {
        _fingerprinter = fingerprinter;
        _settlementDelay = settlementDelay ?? TimeSpan.FromSeconds(3);
        if (_settlementDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settlementDelay));
    }

    public void EstablishBaseline(WindowTextSnapshot snapshot, DateTimeOffset now)
    {
        Reset();
        Observe(snapshot, [], now, baseline: true);
    }

    public void Reset()
    {
        _emittedOccurrences.Clear();
        _suppressedEchoOccurrences.Clear();
        _candidates.Clear();
        _key = "";
        _body = "";
        _visible = false;
        _bypass = false;
        _overflowChange = null;
    }

    public void SuppressEchoChanges(WindowTextSnapshot snapshot,
        IReadOnlyList<WindowTextChange> suppressed)
    {
        var key = snapshot.ResponseCycleKey.StartsWith("codex-message:", StringComparison.Ordinal)
            ? snapshot.ResponseCycleKey + ":working" : "";
        if (key.Length == 0) return;
        var echoes = suppressed.Where(change => IsWorkingChange(change, key)).ToArray();
        if (echoes.Length == 0) return;
        if (_key != key)
        {
            Reset();
            _key = key;
        }
        var accumulator = new WindowTextAccumulator();
        foreach (var echo in echoes) accumulator.Append(echo);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in accumulator.TakeCompletedUnits(flushIncomplete: true))
        {
            var hash = EchoHash(text);
            var occurrence = occurrences.GetValueOrDefault(hash) + 1;
            occurrences[hash] = occurrence;
            _suppressedEchoOccurrences[hash] = Math.Max(occurrence,
                _suppressedEchoOccurrences.GetValueOrDefault(hash));
        }
        for (var index = 0; index < _candidates.Count; index++)
        {
            var candidate = _candidates[index];
            if (_suppressedEchoOccurrences.GetValueOrDefault(candidate.EchoHash) < candidate.EchoOccurrence)
                continue;
            _emittedOccurrences[candidate.Hash] = Math.Max(candidate.Occurrence,
                _emittedOccurrences.GetValueOrDefault(candidate.Hash));
            _candidates[index] = candidate with { Change = null };
        }
        // A suppressed echo is never a reason to enter pass-through mode. The
        // same bounded unit count used by the thought body bounds echo history.
        if (_suppressedEchoOccurrences.Count > MaximumUnits)
            foreach (var excess in _suppressedEchoOccurrences.Keys.Skip(MaximumUnits).ToArray())
                _suppressedEchoOccurrences.Remove(excess);
    }

    public IReadOnlyList<WindowTextChange> Filter(WindowTextSnapshot snapshot,
        IReadOnlyList<WindowTextChange> changes, DateTimeOffset now)
    {
        var workingKey = snapshot.ResponseCycleKey + ":working";
        if (snapshot.ResponseCycleKey.StartsWith("codex-message:", StringComparison.Ordinal)
            && snapshot.Blocks.Any(block => block.IsFinalResponse
                && block.Key == snapshot.ResponseCycleKey + ":answer"))
        {
            // The final-answer quiet-period gate can already be holding the
            // final delta. Its visible answer still supersedes pending thoughts.
            Reset();
            return changes.Where(change => !IsWorkingChange(change, workingKey)).ToArray();
        }
        // Completion supersedes pending thoughts, including when the snapshot
        // still exposes an old working block alongside its answer.
        if (changes.Any(change => change.IsFinalResponse))
        {
            Reset();
            return changes;
        }

        Observe(snapshot, changes, now, baseline: false);
        if (_overflowChange is { } overflow)
        {
            _overflowChange = null;
            var fallback = changes.Where(change => !IsWorkingChange(change, _key)).ToList();
            fallback.Add(overflow);
            return fallback;
        }
        if (_bypass)
            return changes.Select(change => IsWorkingChange(change, _key)
                    ? change with { Text = WithoutSuppressedEchoUnits(change.Text) } : change)
                .Where(change => !string.IsNullOrWhiteSpace(change.Text)).ToArray();
        if (_key.Length == 0) return changes;
        var ordinary = changes.Where(change => !IsWorkingChange(change, _key)).ToList();
        ordinary.AddRange(TakeReady(now));
        return ordinary;
    }

    public IReadOnlyList<WindowTextChange> TakeReady(DateTimeOffset now)
    {
        if (!_visible || _bypass) return [];
        var ready = new List<WindowTextChange>();
        for (var index = 0; index < _candidates.Count; index++)
        {
            var candidate = _candidates[index];
            if (candidate.Change is not { } change) continue;
            // Preserve sentence order when an earlier sentence was rewritten.
            if (now - candidate.ChangedAt < _settlementDelay) break;
            ready.Add(change);
            _emittedOccurrences[candidate.Hash] = Math.Max(candidate.Occurrence,
                _emittedOccurrences.GetValueOrDefault(candidate.Hash));
            _candidates[index] = candidate with { Change = null };
        }
        return ready;
    }

    public TimeSpan? TimeUntilNextSettlement(DateTimeOffset now)
    {
        if (!_visible || _bypass) return null;
        var candidate = _candidates.FirstOrDefault(item => item.Change is not null);
        if (candidate is null) return null;
        var remaining = _settlementDelay - (now - candidate.ChangedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void Observe(WindowTextSnapshot snapshot, IReadOnlyList<WindowTextChange> changes,
        DateTimeOffset now, bool baseline)
    {
        var key = snapshot.ResponseCycleKey.StartsWith("codex-message:", StringComparison.Ordinal)
            ? snapshot.ResponseCycleKey + ":working" : "";
        if (key != _key)
        {
            Reset();
            _key = key;
        }
        if (_key.Length == 0 || _bypass) return;
        var blocks = snapshot.Blocks.Where(block => block.Key == _key
            && !block.IsStatus && !block.IsFinalResponse).OrderBy(block => block.Order).ToArray();
        if (blocks.Length == 0)
        {
            _visible = false;
            return;
        }
        var body = string.Join('\n', blocks.Select(block => block.Text));
        var block = blocks[0];
        var source = changes.LastOrDefault(change => IsWorkingChange(change, _key));
        if (source.Key != _key)
            source = new(block.Text, true, true, block.Key, block.ContainerKey,
                block.Order, GroupKey: block.GroupKey);
        if (body.Length > MaximumCharacters)
        {
            BypassOversizedTurn(source, body, baseline);
            return;
        }
        // A virtualized redraw is not a new version of the thought. Retain the
        // high-water body silently until it is visible again.
        if (_body.Length > body.Length && _body.StartsWith(body, StringComparison.Ordinal))
        {
            _visible = false;
            return;
        }
        _visible = true;
        if (body == _body && !baseline) return;
        var accumulator = new WindowTextAccumulator();
        accumulator.Append(source with { Text = body, IsNewBlock = true,
            StartsAtBoundary = true, IsReplacement = false, IsProvisional = false });
        var units = accumulator.TakeCompletedUnits(flushIncomplete: false);
        if (units.Count > MaximumUnits)
        {
            BypassOversizedTurn(source, body, baseline);
            return;
        }
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var echoOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<Candidate>(units.Count);
        for (var index = 0; index < units.Count; index++)
        {
            var text = units[index];
            var hash = _fingerprinter.Create(text).Hash;
            var occurrence = occurrences.GetValueOrDefault(hash) + 1;
            occurrences[hash] = occurrence;
            var echoHash = EchoHash(text);
            var echoOccurrence = echoOccurrences.GetValueOrDefault(echoHash) + 1;
            echoOccurrences[echoHash] = echoOccurrence;
            if (baseline) _emittedOccurrences[hash] = occurrence;
            if (_suppressedEchoOccurrences.GetValueOrDefault(echoHash) >= echoOccurrence)
                _emittedOccurrences[hash] = Math.Max(occurrence,
                    _emittedOccurrences.GetValueOrDefault(hash));
            WindowTextChange? pending = _emittedOccurrences.GetValueOrDefault(hash) >= occurrence
                ? null : source with { Text = text, IsNewBlock = true, StartsAtBoundary = true,
                    IsReplacement = false, IsProvisional = false };
            var unchanged = index < _candidates.Count && _candidates[index].Hash == hash
                && _candidates[index].Occurrence == occurrence;
            candidates.Add(new(hash, occurrence, echoHash, echoOccurrence, pending,
                unchanged ? _candidates[index].ChangedAt : now));
        }
        // Exact history also has a bound. Once exhausted, use the ordinary
        // incoming stream for this turn rather than silently dropping speech.
        if (_emittedOccurrences.Count + candidates.Count(item => item.Change is not null) > MaximumUnits)
        {
            BypassOversizedTurn(source, body, baseline);
            return;
        }
        _body = body;
        _candidates = candidates;
    }

    private void BypassOversizedTurn(WindowTextChange source, string body, bool baseline)
    {
        // Supply the full current body once: an append suffix alone would lose
        // the sentences and unfinished prefix withheld before this fallback.
        var readableBody = WithoutSuppressedEchoUnits(body);
        _overflowChange = baseline || string.IsNullOrWhiteSpace(readableBody) ? null
            : source with { Text = readableBody, IsNewBlock = true,
            StartsAtBoundary = true, IsReplacement = false, IsProvisional = false };
        _body = "";
        _candidates.Clear();
        _emittedOccurrences.Clear();
        _visible = false;
        _bypass = true;
    }

    private static bool IsWorkingChange(WindowTextChange change, string key) =>
        change.Key == key && !change.IsStatus && !change.IsFinalResponse;

    private string EchoHash(string text) => _fingerprinter.Create(text.Trim()
        .TrimEnd('.', '!', '?', ':', ';', ',', '\"', '\'', '\u2019', '\u201d', ')', ']', '}')
        .TrimEnd()).Hash;

    private string WithoutSuppressedEchoUnits(string text)
    {
        if (_suppressedEchoOccurrences.Count == 0) return text;
        var accumulator = new WindowTextAccumulator();
        accumulator.Append(new(text, true, true, _key));
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var readable = new List<string>();
        foreach (var unit in accumulator.TakeCompletedUnits(flushIncomplete: true))
        {
            var hash = EchoHash(unit);
            var occurrence = occurrences.GetValueOrDefault(hash) + 1;
            occurrences[hash] = occurrence;
            if (_suppressedEchoOccurrences.GetValueOrDefault(hash) < occurrence) readable.Add(unit);
        }
        return string.Join('\n', readable);
    }

    private sealed record Candidate(string Hash, int Occurrence, string EchoHash, int EchoOccurrence,
        WindowTextChange? Change, DateTimeOffset ChangedAt);
}
