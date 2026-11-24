using System.Security.Cryptography;
using System.Text;

namespace PulseReader.Monitoring;

internal sealed class WindowTextFingerprinter : IDisposable
{
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private bool _disposed;

    public WindowTextFingerprint Create(string text)
    {
        var hash = CreateTextHash(text);
        var words = Tokenize(text);
        return new WindowTextFingerprint(
            hash,
            words.Select(word => Hash(word.Value.ToLowerInvariant())).ToArray(),
            words.Count);
    }

    internal string CreateTextHash(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Hash(Normalize(text));
    }

    public IReadOnlyList<TextToken> Tokenize(string text)
    {
        var tokens = new List<TextToken>();
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var tokenCharacter = index < text.Length
                                 && (char.IsLetterOrDigit(text[index])
                                     || text[index] is '\'' or '\u2019');
            if (tokenCharacter && start < 0)
            {
                start = index;
            }
            else if (!tokenCharacter && start >= 0)
            {
                tokens.Add(new TextToken(text[start..index], start));
                start = -1;
            }
        }

        return tokens;
    }

    private string Hash(string value)
    {
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var input = new byte[_salt.Length + valueBytes.Length];
        _salt.CopyTo(input, 0);
        valueBytes.CopyTo(input, _salt.Length);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string Normalize(string value) =>
        string.Join(
                ' ',
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_salt);
    }

    internal readonly record struct TextToken(string Value, int Start);
}

internal sealed class PublishedWindowTextUnitFilter
{
    private static readonly TimeSpan DuplicateLifetime = TimeSpan.FromSeconds(2);
    private const int MinimumWords = 3;
    private const int MaximumRecentUnits = 64;
    private const int MaximumResponseCycleUnits = 512;

    private readonly WindowTextFingerprinter _fingerprinter;
    private readonly Dictionary<string, DateTimeOffset> _lastPublishedAt =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _publishedWords = new(StringComparer.Ordinal);
    private bool _responseCycleActive;

    public PublishedWindowTextUnitFilter(WindowTextFingerprinter fingerprinter)
    {
        _fingerprinter = fingerprinter;
    }

    public void StartResponseCycle()
    {
        _responseCycleActive = true;
        _lastPublishedAt.Clear();
        _publishedWords.Clear();
    }

    public IReadOnlyList<string> Filter(
        IReadOnlyList<string> units,
        DateTimeOffset now)
    {
        if (units.Count == 0)
        {
            return units;
        }

        foreach (var expired in _responseCycleActive
                     ? Array.Empty<string>()
                     : _lastPublishedAt
                         .Where(pair => now - pair.Value > DuplicateLifetime)
                         .Select(pair => pair.Key)
                         .ToArray())
        {
            _lastPublishedAt.Remove(expired);
            _publishedWords.Remove(expired);
        }

        var rememberedBeforeThisBatch = new HashSet<string>(
            _lastPublishedAt.Keys,
            StringComparer.Ordinal);
        var ready = new List<string>(units.Count);
        var observed = new List<string>(units.Count);
        foreach (var unit in units)
        {
            var fingerprint = _fingerprinter.Create(unit);
            if (fingerprint.WordCount == 0)
            {
                continue;
            }

            var trimmed = unit.Trim();
            if (_responseCycleActive
                && fingerprint.WordCount >= 8
                && char.IsLower(trimmed[0])
                && _publishedWords.Values.Any(previous =>
                    previous.Count > fingerprint.WordCount
                    && previous.Count - fingerprint.WordCount <= 2
                    && WindowTextChangePolicy.FindSequence(previous, fingerprint.WordHashes)
                       == previous.Count - fingerprint.WordCount))
            {
                continue;
            }
            if (_responseCycleActive
                && fingerprint.WordCount == 1
                && trimmed.Length <= 2
                && trimmed.All(char.IsLower))
            {
                continue;
            }

            var minimumWords = _responseCycleActive ? 2 : MinimumWords;
            if (fingerprint.WordCount < minimumWords)
            {
                ready.Add(unit);
                continue;
            }

            observed.Add(fingerprint.Hash);
            _publishedWords[fingerprint.Hash] = fingerprint.WordHashes;
            if (!rememberedBeforeThisBatch.Contains(fingerprint.Hash))
            {
                ready.Add(unit);
            }
        }

        foreach (var hash in observed)
        {
            _lastPublishedAt[hash] = now;
        }

        var maximumRememberedUnits = _responseCycleActive
            ? MaximumResponseCycleUnits
            : MaximumRecentUnits;
        if (_lastPublishedAt.Count > maximumRememberedUnits)
        {
            foreach (var excess in _lastPublishedAt
                         .OrderByDescending(pair => pair.Value)
                         .Skip(maximumRememberedUnits)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _lastPublishedAt.Remove(excess);
                _publishedWords.Remove(excess);
            }
        }

        return ready;
    }
}

internal sealed class WindowTextChangePolicy
{
    private readonly WindowTextFingerprinter _fingerprinter;
    private readonly SubmittedTextEchoFilter _echoFilter;
    private VolatileTextChangeFilter _volatileFilter = new();
    private ProvisionalTextChangeFilter _provisionalFilter;
    private ConsolidatedTextFilter _consolidatedFilter;
    private string _responseCycleKey = "";
    private readonly CodexFinalResponseGate _codexFinalGate = new();
    private readonly CodexThoughtSettlementFilter? _codexThoughtSettlement;

    public WindowTextChangePolicy(WindowTextFingerprinter fingerprinter, bool settleCodexThoughts = false)
    {
        _fingerprinter = fingerprinter;
        _echoFilter = new SubmittedTextEchoFilter(fingerprinter);
        _provisionalFilter = new ProvisionalTextChangeFilter(fingerprinter);
        _consolidatedFilter = new ConsolidatedTextFilter(fingerprinter);
        if (settleCodexThoughts) _codexThoughtSettlement = new CodexThoughtSettlementFilter(fingerprinter);
    }

    public void EstablishBaseline(WindowTextSnapshot snapshot, DateTimeOffset now)
    {
        _responseCycleKey = snapshot.ResponseCycleKey;
        _echoFilter.EstablishBaseline(snapshot, now);
        _codexFinalGate.EstablishBaseline(snapshot);
        _codexThoughtSettlement?.EstablishBaseline(snapshot, now);
    }

    public bool ResponseCycleStarted { get; private set; }

    public void ObserveEditableFingerprints(
        IReadOnlyList<WindowTextFingerprint> fingerprints,
        DateTimeOffset now) =>
        _echoFilter.Observe(fingerprints, now);

    public IReadOnlyList<WindowTextChange> Process(
        WindowTextSnapshot snapshot,
        IReadOnlyList<WindowTextChange> changes,
        DateTimeOffset now)
    {
        ResponseCycleStarted = false;
        var coalesced = CoalesceContentGroups(CoalesceSiblingFragments(changes));
        var withoutEcho = _echoFilter.Filter(snapshot, coalesced, now);
        var suppressedEchoChanges = coalesced.Except(withoutEcho).ToArray();
        // In a recognized conversation the turn identity is authoritative.
        // Editable nodes can disappear/reappear during layout changes, after
        // the actual submission. Treating that as another turn loses a final
        // response waiting in the settlement gate. Generic streams retain the
        // editable-submission heuristic and echo suppression still runs above.
        ResponseCycleStarted = snapshot.ResponseCycleKey.Length == 0 && _echoFilter.SubmissionDetected;
        if (snapshot.ResponseCycleKey.Length > 0 && snapshot.ResponseCycleKey != _responseCycleKey)
        {
            ResponseCycleStarted = true;
            _responseCycleKey = snapshot.ResponseCycleKey;
        }
        if (ResponseCycleStarted)
        {
            _codexFinalGate.Reset();
            _codexThoughtSettlement?.Reset();
        }
        _codexThoughtSettlement?.SuppressEchoChanges(snapshot, suppressedEchoChanges);
        withoutEcho = _codexFinalGate.Observe(withoutEcho, now);
        var completed = withoutEcho.Where(change => change.IsFinalResponse).ToArray();
        if (ResponseCycleStarted || completed.Length > 0)
        {
            _volatileFilter = new VolatileTextChangeFilter();
            _provisionalFilter = new ProvisionalTextChangeFilter(_fingerprinter);
            _consolidatedFilter = new ConsolidatedTextFilter(_fingerprinter);
        }
        if (completed.Length > 0)
        {
            _codexThoughtSettlement?.Reset();
            // Completion must reach the speech queue even when the final text
            // matches its draft. Pending draft fragments are now superseded.
            return completed;
        }
        if (_codexThoughtSettlement is not null)
            withoutEcho = _codexThoughtSettlement.Filter(snapshot, withoutEcho, now);
        var currentKeys = snapshot.Blocks.Select(block => block.Key).ToHashSet(StringComparer.Ordinal);
        currentKeys.UnionWith(
            withoutEcho
                .Where(change => !string.IsNullOrEmpty(change.Key))
                .Select(change => change.Key));
        var withoutVolatile = _volatileFilter.Filter(withoutEcho, currentKeys, now);
        var withoutProvisional = _provisionalFilter.Filter(withoutVolatile, now);
        var withoutAggregateDuplicates = RemoveAggregateStatusDuplicates(
            withoutProvisional,
            _fingerprinter);
        return RemoveCoveredFragments(_consolidatedFilter.Filter(withoutAggregateDuplicates, now));
    }

    internal IReadOnlyList<WindowTextChange> ConfirmPendingFinal() => _codexFinalGate.ConfirmPending();

    internal bool HasPendingFinal => _codexFinalGate.HasPending;

    public IReadOnlyList<WindowTextChange> TakeSettled(DateTimeOffset now)
    {
        var final = _codexFinalGate.TakeReady(now);
        if (final.Count > 0)
        {
            _codexThoughtSettlement?.Reset();
            _volatileFilter = new VolatileTextChangeFilter();
            _provisionalFilter = new ProvisionalTextChangeFilter(_fingerprinter);
            _consolidatedFilter = new ConsolidatedTextFilter(_fingerprinter);
            return final;
        }
        var settled = _volatileFilter.TakeSettled(now)
            .Concat(_provisionalFilter.TakeSettled(now))
            .Concat(_codexThoughtSettlement?.TakeReady(now) ?? [])
            .ToArray();
        return RemoveCoveredFragments(_consolidatedFilter.Filter(settled, now));
    }

    internal IReadOnlyList<WindowTextChange> RemoveCoveredFragments(IReadOnlyList<WindowTextChange> changes)
    {
        var words = changes.Select(change => _fingerprinter.Create(change.Text).WordHashes).ToArray();
        return changes.Where((change, index) => !changes.Where((other, otherIndex) =>
            otherIndex != index
            && !string.IsNullOrEmpty(change.GroupKey)
            && change.GroupKey == other.GroupKey
            && (!change.IsNewBlock || change.IsProvisional || other.IsProvisional)
            && words[index].Count >= 8
            && words[otherIndex].Count > words[index].Count
            && words[otherIndex].Count - words[index].Count <= 2
            && FindSequence(words[otherIndex], words[index]) >= 0).Any()).ToArray();
    }

    public TimeSpan? TimeUntilNextSettlement(DateTimeOffset now)
    {
        var waits = new[]
        {
            _volatileFilter.TimeUntilNextSettlement(now),
            _provisionalFilter.TimeUntilNextSettlement(now),
            _codexFinalGate.TimeUntilReady(now),
            _codexThoughtSettlement?.TimeUntilNextSettlement(now)
        }.OfType<TimeSpan>().ToArray();
        return waits.Length == 0 ? null : waits.Min();
    }

    internal static IReadOnlyList<WindowTextChange> CoalesceSiblingFragments(
        IReadOnlyList<WindowTextChange> changes)
    {
        if (changes.Count < 2)
        {
            return changes;
        }

        var result = new List<WindowTextChange>();
        for (var index = 0; index < changes.Count; index++)
        {
            var first = changes[index];
            if (!CanCoalesce(first))
            {
                result.Add(first);
                continue;
            }

            var text = first.Text.Trim();
            var lastOrder = first.Order;
            var end = index;
            while (end + 1 < changes.Count)
            {
                var next = changes[end + 1];
                if (!CanCoalesce(next)
                    || !string.Equals(
                        first.ContainerKey,
                        next.ContainerKey,
                        StringComparison.Ordinal)
                    || next.Order < lastOrder)
                {
                    break;
                }

                text = Join(text, next.Text.Trim());
                lastOrder = next.Order;
                end++;
            }

            result.Add(first with { Text = text, IsProvisional = end > index });
            index = end;
        }

        return result;
    }

    internal static IReadOnlyList<WindowTextChange> CoalesceContentGroups(
        IReadOnlyList<WindowTextChange> changes)
    {
        if (changes.Count < 2)
        {
            return changes;
        }

        var result = new List<WindowTextChange>();
        for (var index = 0; index < changes.Count; index++)
        {
            var first = changes[index];
            if (!CanCoalesceContentGroup(first))
            {
                result.Add(first);
                continue;
            }

            var text = first.Text.Trim();
            var provisional = first.IsProvisional;
            var lastOrder = first.Order;
            var previousContainer = first.ContainerKey;
            var end = index;
            while (end + 1 < changes.Count)
            {
                var next = changes[end + 1];
                if (!CanCoalesceContentGroup(next)
                    || !string.Equals(first.GroupKey, next.GroupKey, StringComparison.Ordinal)
                    || next.Order < lastOrder)
                {
                    break;
                }

                text = string.Equals(previousContainer, next.ContainerKey, StringComparison.Ordinal)
                    ? Join(text, next.Text.Trim())
                    : string.Concat(text, Environment.NewLine, next.Text.Trim());
                provisional |= next.IsProvisional;
                previousContainer = next.ContainerKey;
                lastOrder = next.Order;
                end++;
            }

            result.Add(
                first with
                {
                    Text = text,
                    ContainerKey = first.GroupKey,
                    IsProvisional = provisional
                });
            index = end;
        }

        return result;
    }

    private static bool CanCoalesceContentGroup(WindowTextChange change) =>
        change.IsNewBlock
        && !change.IsCompletedAggregate
        && !change.IsReplacement
        && !change.IsStatus
        && !string.IsNullOrEmpty(change.GroupKey);

    private static bool CanCoalesce(WindowTextChange change) =>
        change.IsNewBlock
        && !change.IsCompletedAggregate
        && !change.IsReplacement
        && !change.IsStatus
        && !string.IsNullOrEmpty(change.ContainerKey);

    internal static string Join(string left, string right)
    {
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0)
        {
            return left;
        }

        return right[0] is '.' or ',' or '!' or '?' or ':' or ';' or ')' or ']' or '}'
            ? left + right
            : $"{left} {right}";
    }

    internal static IReadOnlyList<WindowTextChange> RemoveAggregateStatusDuplicates(
        IReadOnlyList<WindowTextChange> changes,
        WindowTextFingerprinter fingerprinter)
    {
        if (changes.Count < 2)
        {
            return changes;
        }

        var fingerprints = changes
            .Select(change => fingerprinter.Create(change.Text).WordHashes)
            .ToArray();
        var suppressed = new bool[changes.Count];
        for (var candidateIndex = 0; candidateIndex < changes.Count; candidateIndex++)
        {
            if (!changes[candidateIndex].IsStatus
                || fingerprints[candidateIndex].Count < 8)
            {
                continue;
            }

            var covered = new bool[fingerprints[candidateIndex].Count];
            for (var otherIndex = 0; otherIndex < changes.Count; otherIndex++)
            {
                if (otherIndex == candidateIndex
                    || changes[otherIndex].IsStatus
                    || fingerprints[otherIndex].Count < 3)
                {
                    continue;
                }

                var matchAt = FindSequence(
                    fingerprints[candidateIndex],
                    fingerprints[otherIndex]);
                if (matchAt < 0)
                {
                    continue;
                }

                for (var word = matchAt;
                     word < matchAt + fingerprints[otherIndex].Count;
                     word++)
                {
                    covered[word] = true;
                }
            }

            if (covered.Count(value => value)
                >= Math.Ceiling(fingerprints[candidateIndex].Count * 0.6))
            {
                suppressed[candidateIndex] = true;
            }
        }

        return changes.Where((_, index) => !suppressed[index]).ToArray();
    }

    internal static int FindSequence(
        IReadOnlyList<string> value,
        IReadOnlyList<string> sequence)
    {
        if (sequence.Count == 0 || sequence.Count > value.Count)
        {
            return -1;
        }

        for (var start = 0; start <= value.Count - sequence.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < sequence.Count; offset++)
            {
                if (!string.Equals(
                        value[start + offset],
                        sequence[offset],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return start;
            }
        }

        return -1;
    }
}

internal sealed class SubmittedTextEchoFilter
{
    internal static readonly TimeSpan CandidateLifetime = TimeSpan.FromSeconds(45);
    private const int MaximumCandidates = 24;
    private const int MaximumCombinedChanges = 16;

    private readonly WindowTextFingerprinter _fingerprinter;
    private readonly Dictionary<string, WindowTextFingerprint> _previousEditable =
        new(StringComparer.Ordinal);
    private readonly List<SubmissionCandidate> _candidates = [];
    private bool _submissionObserved;

    public SubmittedTextEchoFilter(WindowTextFingerprinter fingerprinter)
    {
        _fingerprinter = fingerprinter;
    }

    public void EstablishBaseline(WindowTextSnapshot snapshot, DateTimeOffset now)
    {
        SubmissionDetected = false;
        _submissionObserved = false;
        _previousEditable.Clear();
        foreach (var fingerprint in snapshot.EditableTextFingerprints)
        {
            _previousEditable[fingerprint.Hash] = fingerprint;
        }

        Prune(now);
    }

    public bool SubmissionDetected { get; private set; }

    public void Observe(
        IReadOnlyList<WindowTextFingerprint> fingerprints,
        DateTimeOffset now)
    {
        Prune(now);
        var current = new Dictionary<string, WindowTextFingerprint>(StringComparer.Ordinal);
        foreach (var fingerprint in fingerprints)
        {
            // Multiple editable nodes can expose the same normalized draft.
            // Track content presence, as EstablishBaseline does, rather than node count.
            current[fingerprint.Hash] = fingerprint;
        }
        foreach (var previous in _previousEditable.Values)
        {
            if (!current.ContainsKey(previous.Hash) && previous.WordCount > 0)
            {
                _submissionObserved |= current.Count == 0;
                _candidates.RemoveAll(candidate => candidate.Fingerprint.Hash == previous.Hash);
                _candidates.Add(new SubmissionCandidate(previous, now + CandidateLifetime));
            }
        }

        _previousEditable.Clear();
        foreach (var fingerprint in current.Values)
        {
            _previousEditable[fingerprint.Hash] = fingerprint;
        }

        while (_candidates.Count > MaximumCandidates)
        {
            _candidates.RemoveAt(0);
        }
    }

    public IReadOnlyList<WindowTextChange> Filter(
        WindowTextSnapshot snapshot,
        IReadOnlyList<WindowTextChange> changes,
        DateTimeOffset now)
    {
        SubmissionDetected = ConsumeSubmissionObservation();
        Observe(snapshot.EditableTextFingerprints, now);
        SubmissionDetected |= ConsumeSubmissionObservation();

        if (_candidates.Count == 0 || changes.Count == 0)
        {
            return changes;
        }

        var suppressed = new bool[changes.Count];
        for (var start = 0; start < changes.Count; start++)
        {
            if (suppressed[start])
            {
                continue;
            }

            var combinedWords = new List<string>();
            for (var end = start;
                 end < changes.Count && end < start + MaximumCombinedChanges;
                 end++)
            {
                combinedWords.AddRange(_fingerprinter.Create(changes[end].Text).WordHashes);
                var candidateIndex = _candidates.FindIndex(
                    candidate => candidate.Fingerprint.WordHashes.SequenceEqual(combinedWords));
                if (candidateIndex >= 0)
                {
                    for (var index = start; index <= end; index++)
                    {
                        suppressed[index] = true;
                    }

                    _candidates.RemoveAt(candidateIndex);
                    SubmissionDetected = true;
                    break;
                }

                if (_candidates.All(
                        candidate => !StartsWith(candidate.Fingerprint.WordHashes, combinedWords)))
                {
                    break;
                }
            }
        }

        return changes.Where((_, index) => !suppressed[index]).ToArray();
    }

    private bool ConsumeSubmissionObservation()
    {
        var observed = _submissionObserved;
        _submissionObserved = false;
        return observed;
    }

    private void Prune(DateTimeOffset now) =>
        _candidates.RemoveAll(candidate => candidate.ExpiresAt <= now);

    private static bool StartsWith(
        IReadOnlyList<string> value,
        IReadOnlyList<string> prefix)
    {
        if (prefix.Count > value.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            if (!string.Equals(value[index], prefix[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record SubmissionCandidate(
        WindowTextFingerprint Fingerprint,
        DateTimeOffset ExpiresAt);
}

internal sealed class VolatileTextChangeFilter
{
    internal static readonly TimeSpan SettlementDelay = TimeSpan.FromMilliseconds(2200);
    private static readonly TimeSpan ReplacementSequenceWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VolatileLifetime = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, ReplacementState> _states = new(StringComparer.Ordinal);

    public IReadOnlyList<WindowTextChange> Filter(
        IReadOnlyList<WindowTextChange> changes,
        IReadOnlySet<string> currentKeys,
        DateTimeOffset now)
    {
        Prune(now);
        foreach (var pair in _states.ToArray())
        {
            if (!pair.Value.Volatile && !currentKeys.Contains(pair.Key))
            {
                _states.Remove(pair.Key);
            }
        }

        var ready = new List<WindowTextChange>();
        foreach (var change in changes)
        {
            // Recognized conversation adapters supply stable, semantic message
            // identities. Their append-only token fragments are not volatile
            // status labels; waiting for quiet here starves continuous streams.
            if (ConversationMessageKey.IsKnown(change.Key) && !change.IsReplacement && !_states.ContainsKey(change.Key))
            {
                ready.Add(change);
                continue;
            }
            if (string.IsNullOrEmpty(change.Key))
            {
                ready.Add(change);
                continue;
            }

            if (_states.TryGetValue(change.Key, out var state))
            {
                if (state.Volatile)
                {
                    _states[change.Key] = state with { LastChangedAt = now };
                    continue;
                }

                if (change.IsReplacement
                    && now - state.LastChangedAt < ReplacementSequenceWindow)
                {
                    _states[change.Key] = state with
                    {
                        Pending = null,
                        LastChangedAt = now,
                        Volatile = true
                    };
                    continue;
                }

                if (!change.IsReplacement && state.Pending is { } pending)
                {
                    _states[change.Key] = state with
                    {
                        Pending = pending with
                        {
                            Text = pending.Text + change.Text,
                            StartsAtBoundary = pending.StartsAtBoundary
                                               || change.StartsAtBoundary
                        },
                        LastChangedAt = now
                    };
                    continue;
                }

                _states.Remove(change.Key);
            }

            if (change.IsReplacement || IsShortUnpunctuated(change.Text))
            {
                _states[change.Key] = new ReplacementState(change, now, Volatile: false);
            }
            else
            {
                ready.Add(change);
            }
        }

        ready.AddRange(TakeSettled(now));
        return ready;
    }

    public IReadOnlyList<WindowTextChange> TakeSettled(DateTimeOffset now)
    {
        var ready = new List<WindowTextChange>();
        foreach (var pair in _states.ToArray())
        {
            var state = pair.Value;
            if (!state.Volatile
                && state.Pending is { } pending
                && now - state.LastChangedAt >= SettlementDelay)
            {
                ready.Add(pending);
                _states.Remove(pair.Key);
            }
        }

        return ready;
    }

    public TimeSpan? TimeUntilNextSettlement(DateTimeOffset now)
    {
        var remaining = _states.Values
            .Where(state => !state.Volatile && state.Pending is not null)
            .Select(state => SettlementDelay - (now - state.LastChangedAt))
            .DefaultIfEmpty(TimeSpan.MaxValue)
            .Min();
        return remaining == TimeSpan.MaxValue
            ? null
            : remaining <= TimeSpan.Zero
                ? TimeSpan.Zero
                : remaining;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _states.ToArray())
        {
            if (pair.Value.Volatile && now - pair.Value.LastChangedAt >= VolatileLifetime)
            {
                _states.Remove(pair.Key);
            }
        }
    }

    private static bool IsShortUnpunctuated(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0
            || trimmed.EndsWith('.')
            || trimmed.EndsWith('!')
            || trimmed.EndsWith('?'))
        {
            return false;
        }

        return trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 3;
    }

    private sealed record ReplacementState(
        WindowTextChange? Pending,
        DateTimeOffset LastChangedAt,
        bool Volatile);
}

internal sealed class ProvisionalTextChangeFilter
{
    internal static readonly TimeSpan SettlementDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(10);

    private readonly WindowTextFingerprinter _fingerprinter;
    private readonly Dictionary<string, PendingState> _pending = new(StringComparer.Ordinal);
    private readonly List<SequencedChange> _deferred = [];
    private long _nextSequence;

    public ProvisionalTextChangeFilter(WindowTextFingerprinter fingerprinter)
    {
        _fingerprinter = fingerprinter;
    }

    public IReadOnlyList<WindowTextChange> Filter(
        IReadOnlyList<WindowTextChange> changes,
        DateTimeOffset now)
    {
        Prune(now);
        var ready = new List<WindowTextChange>();
        foreach (var change in changes)
        {
            var wordHashes = _fingerprinter.Create(change.Text).WordHashes;
            if (change.IsProvisional)
            {
                if (_pending.TryGetValue(change.ContainerKey, out var existing))
                {
                    var combined = existing.Change with
                    {
                        Text = WindowTextChangePolicy.Join(existing.Change.Text, change.Text)
                    };
                    _pending[change.ContainerKey] = new PendingState(
                        combined,
                        _fingerprinter.Create(combined.Text).WordHashes,
                        now,
                        existing.Sequence);
                }
                else
                {
                    _pending[change.ContainerKey] = new PendingState(
                        change,
                        wordHashes,
                        now,
                        _nextSequence++);
                }

                continue;
            }

            long? matchedSequence = null;
            foreach (var pair in _pending.ToArray())
            {
                if (pair.Value.WordHashes.Count < 3)
                {
                    continue;
                }

                var pendingInCurrent = WindowTextChangePolicy.FindSequence(
                    wordHashes,
                    pair.Value.WordHashes);
                var currentInPending = WindowTextChangePolicy.FindSequence(
                    pair.Value.WordHashes,
                    wordHashes);
                if (pendingInCurrent >= 0
                    || currentInPending >= 0
                    || IsNearCompleteRedraw(pair.Value.WordHashes, wordHashes)
                    || IsNearCompleteRedraw(wordHashes, pair.Value.WordHashes))
                {
                    matchedSequence = matchedSequence is null
                        ? pair.Value.Sequence
                        : Math.Min(matchedSequence.Value, pair.Value.Sequence);
                    _pending.Remove(pair.Key);
                }
            }

            if (_pending.Count > 0 || _deferred.Count > 0 || matchedSequence is not null)
            {
                _deferred.Add(
                    new SequencedChange(
                        matchedSequence ?? _nextSequence++,
                        change));
            }
            else
            {
                ready.Add(change);
            }
        }

        ready.AddRange(TakeSettled(now));
        return ready;
    }

    private static bool IsNearCompleteRedraw(
        IReadOnlyList<string> provisional,
        IReadOnlyList<string> completed)
    {
        if (provisional.Count < 4 || completed.Count < provisional.Count - 2)
        {
            return false;
        }

        var requiredRun = Math.Max(3, provisional.Count - 2);
        for (var provisionalStart = 0;
             provisionalStart <= provisional.Count - requiredRun;
             provisionalStart++)
        {
            for (var completedStart = 0;
                 completedStart <= completed.Count - requiredRun;
                 completedStart++)
            {
                var matches = true;
                for (var offset = 0; offset < requiredRun; offset++)
                {
                    if (!string.Equals(
                            provisional[provisionalStart + offset],
                            completed[completedStart + offset],
                            StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public IReadOnlyList<WindowTextChange> TakeSettled(DateTimeOffset now)
    {
        var ready = new List<WindowTextChange>();
        foreach (var pair in _pending.ToArray())
        {
            if (now - pair.Value.LastChangedAt < SettlementDelay)
            {
                continue;
            }

            _deferred.Add(
                new SequencedChange(
                    pair.Value.Sequence,
                    pair.Value.Change));
            _pending.Remove(pair.Key);
        }

        if (_pending.Count == 0 && _deferred.Count > 0)
        {
            ready.AddRange(
                _deferred
                    .OrderBy(value => value.Sequence)
                    .Select(value => value.Change));
            _deferred.Clear();
        }

        return ready;
    }

    public TimeSpan? TimeUntilNextSettlement(DateTimeOffset now)
    {
        var remaining = _pending.Values
            .Select(state => SettlementDelay - (now - state.LastChangedAt))
            .DefaultIfEmpty(TimeSpan.MaxValue)
            .Min();
        return remaining == TimeSpan.MaxValue
            ? null
            : remaining <= TimeSpan.Zero
                ? TimeSpan.Zero
                : remaining;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (now - pair.Value.LastChangedAt >= PendingLifetime)
            {
                _pending.Remove(pair.Key);
            }
        }
    }

    private sealed record PendingState(
        WindowTextChange Change,
        IReadOnlyList<string> WordHashes,
        DateTimeOffset LastChangedAt,
        long Sequence);

    private readonly record struct SequencedChange(
        long Sequence,
        WindowTextChange Change);
}

internal sealed class ConsolidatedTextFilter
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CrossContainerLifetime = TimeSpan.FromSeconds(5);
    private const int MinimumCrossContainerWords = 3;
    private const int MaximumContainers = 64;

    private readonly WindowTextFingerprinter _fingerprinter;
    private readonly Dictionary<string, ContainerState> _states = new(StringComparer.Ordinal);

    public ConsolidatedTextFilter(WindowTextFingerprinter fingerprinter)
    {
        _fingerprinter = fingerprinter;
    }

    public IReadOnlyList<WindowTextChange> Filter(
        IReadOnlyList<WindowTextChange> changes,
        DateTimeOffset now)
    {
        Prune(now);
        var ready = new List<WindowTextChange>();
        foreach (var change in changes)
        {
            if (string.IsNullOrEmpty(change.ContainerKey) || change.IsReplacement)
            {
                ready.Add(change);
                continue;
            }

            var tokens = _fingerprinter.Tokenize(change.Text);
            var wordHashes = _fingerprinter.Create(change.Text).WordHashes;
            var nearDuplicateState = change.IsNewBlock && change.IsProvisional
                ? _states
                    .Where(pair => !string.Equals(
                                       pair.Key,
                                       change.ContainerKey,
                                       StringComparison.Ordinal)
                                   && now - pair.Value.LastChangedAt
                                   <= CrossContainerLifetime
                                   && AreNearDuplicates(
                                       wordHashes,
                                       pair.Value.WordHashes))
                    .OrderByDescending(pair => pair.Value.WordHashes.Count)
                    .Select(pair => pair.Value)
                    .FirstOrDefault()
                : null;
            if (nearDuplicateState is not null)
            {
                _states[change.ContainerKey] = new ContainerState(wordHashes.ToList(), now);
                continue;
            }

            var crossContainerMatch = change.IsNewBlock
                ? _states
                    .Where(pair => !string.Equals(
                                       pair.Key,
                                       change.ContainerKey,
                                       StringComparison.Ordinal)
                                   && now - pair.Value.LastChangedAt
                                   <= CrossContainerLifetime)
                    .Select(pair => new
                    {
                        State = pair.Value,
                        MatchedWordCount = FindRedrawPrefix(
                            wordHashes,
                            pair.Value.WordHashes)
                    })
                    .Where(match => match.MatchedWordCount >= MinimumCrossContainerWords)
                    .OrderByDescending(match => match.MatchedWordCount)
                    .FirstOrDefault()
                : null;
            if (crossContainerMatch is not null)
            {
                var matchedWordCount = crossContainerMatch.MatchedWordCount;
                _states[change.ContainerKey] = new ContainerState(wordHashes.ToList(), now);
                if (wordHashes.Count == matchedWordCount)
                {
                    continue;
                }

                var suffixStart = tokens[matchedWordCount].Start;
                var suffix = change.Text[suffixStart..].TrimStart();
                if (suffix.Length > 0)
                {
                    ready.Add(
                        change with
                        {
                            Text = suffix,
                            IsNewBlock = false,
                            StartsAtBoundary = true
                        });
                }

                continue;
            }

            if (!_states.TryGetValue(change.ContainerKey, out var state))
            {
                _states[change.ContainerKey] = new ContainerState(
                    wordHashes.ToList(),
                    now);
                ready.Add(change);
                continue;
            }

            if (change.IsNewBlock && StartsWith(wordHashes, state.WordHashes))
            {
                if (wordHashes.Count == state.WordHashes.Count)
                {
                    state.LastChangedAt = now;
                    continue;
                }

                var suffixStart = tokens[state.WordHashes.Count].Start;
                var suffix = change.Text[suffixStart..].TrimStart();
                state.WordHashes = wordHashes.ToList();
                state.LastChangedAt = now;
                if (suffix.Length > 0)
                {
                    ready.Add(change with { Text = suffix });
                }

                continue;
            }

            if (change.IsNewBlock && StartsWith(state.WordHashes, wordHashes))
            {
                state.LastChangedAt = now;
                continue;
            }

            state.WordHashes.AddRange(wordHashes);
            state.LastChangedAt = now;
            if (state.WordHashes.Count > 2048)
            {
                state.WordHashes.RemoveRange(0, state.WordHashes.Count - 2048);
            }

            ready.Add(change);
        }

        while (_states.Count > MaximumContainers)
        {
            var oldest = _states.MinBy(pair => pair.Value.LastChangedAt);
            _states.Remove(oldest.Key);
        }

        return ready;
    }

    private static int FindRedrawPrefix(
        IReadOnlyList<string> current,
        IReadOnlyList<string> previous)
    {
        var maximumLeadingMarkers = Math.Min(
            2,
            Math.Max(0, previous.Count - MinimumCrossContainerWords));
        for (var previousStart = 0;
             previousStart <= maximumLeadingMarkers;
             previousStart++)
        {
            var overlap = previous.Count - previousStart;
            if (overlap < MinimumCrossContainerWords || current.Count < overlap)
            {
                continue;
            }

            var matches = true;
            for (var index = 0; index < overlap; index++)
            {
                if (!string.Equals(
                        current[index],
                        previous[previousStart + index],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return overlap;
            }
        }

        return 0;
    }

    private static bool AreNearDuplicates(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var shortest = Math.Min(left.Count, right.Count);
        if (shortest < 6 || Math.Abs(left.Count - right.Count) > 2)
        {
            return false;
        }

        var prefix = 0;
        while (prefix < shortest
               && string.Equals(left[prefix], right[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < shortest - prefix
               && string.Equals(
                   left[left.Count - suffix - 1],
                   right[right.Count - suffix - 1],
                   StringComparison.Ordinal))
        {
            suffix++;
        }

        return prefix + suffix >= shortest - 2;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _states.ToArray())
        {
            if (now - pair.Value.LastChangedAt >= StateLifetime)
            {
                _states.Remove(pair.Key);
            }
        }
    }

    private static bool StartsWith(
        IReadOnlyList<string> value,
        IReadOnlyList<string> prefix)
    {
        if (prefix.Count > value.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            if (!string.Equals(value[index], prefix[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class ContainerState(
        List<string> wordHashes,
        DateTimeOffset lastChangedAt)
    {
        public List<string> WordHashes { get; set; } = wordHashes;

        public DateTimeOffset LastChangedAt { get; set; } = lastChangedAt;
    }
}
