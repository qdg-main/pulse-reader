namespace PulseReader.Monitoring;

// T3 can remount earlier assistant text as part of a working redraw or after
// navigation. Keep bounded fingerprints from the current latch.
internal sealed class T3ResponseHistory
{
    private const int ReplyLimit = 16;
    private const int OpeningUnitLimit = 128;
    private const int MinimumSharedWords = 12;
    private const int PublishedUnitLimit = 4_096;
    private const int MinimumRemountedUnits = 4;
    private const int MinimumRemountedWords = 48;
    private readonly Dictionary<(string View, string Response), CompletedReply> _replies = new();
    private readonly Queue<(string View, string Response)> _order = new();
    private readonly Dictionary<(string View, string Unit), (string Response, int Occurrences)> _publishedUnits = new();
    private readonly Queue<(string View, string Unit)> _publicationOrder = new();
    private string _view = "";
    private string _baselineResponse = "";
    private string _promptResponse = "";
    private string _promptHash = "";
    private string _remountedResponse = "";
    private bool _restoringView;
    private bool _hasNewPrompt;

    public void EstablishBaseline(WindowTextSnapshot snapshot)
    {
        var view = snapshot.Context?.ViewKey;
        _view = view?.StartsWith("t3:", StringComparison.Ordinal) == true ? view : "";
        _restoringView = _view.Length > 0;
        _baselineResponse = snapshot.ResponseCycleKey;
        _promptResponse = "";
        _promptHash = "";
        _remountedResponse = "";
        _hasNewPrompt = false;
        ObservePrompt(snapshot);
    }

    public void ObservePrompt(WindowTextSnapshot snapshot)
    {
        _hasNewPrompt = snapshot.T3HasNewPrompt && Tracks(snapshot.ResponseCycleKey)
            && snapshot.ResponseCycleKey != _baselineResponse;
        if (_hasNewPrompt) _restoringView = false;
        if (!Tracks(snapshot.ResponseCycleKey)
            || (snapshot.ResponseCycleKey == _promptResponse && snapshot.T3PromptHash.Length == 0)) return;
        _promptResponse = snapshot.ResponseCycleKey;
        _promptHash = snapshot.T3PromptHash;
    }

    public bool IsRestoredReply(string response, string text, WindowTextFingerprinter fingerprinter)
    {
        if (!_restoringView || !Tracks(response) || _replies.Count == 0) return false;
        var hash = fingerprinter.CreateTextHash(text);
        var prompt = GetPromptHash(response);
        return _replies.Any(entry => entry.Key.View == _view && entry.Value.Hash == hash
            && (prompt.Length == 0 || entry.Value.PromptHash.Length == 0 || entry.Value.PromptHash == prompt));
    }

    public void ObserveLiveReply(string response)
    {
        if (Tracks(response)) _restoringView = false;
    }

    public void Remember(string response, string text, IReadOnlyList<string> units, WindowTextFingerprinter fingerprinter)
    {
        if (!Tracks(response)) return;
        var key = (_view, response);
        var hash = fingerprinter.CreateTextHash(text);
        if (_replies.TryGetValue(key, out var previous))
        {
            if (previous.Hash == hash && units.Count == 0) return;
        }
        else
        {
            _order.Enqueue(key);
        }
        _replies[key] = new(hash, GetPromptHash(response), units.Take(OpeningUnitLimit).Select(fingerprinter.CreateTextHash).ToArray());
        while (_order.Count > ReplyLimit)
            _replies.Remove(_order.Dequeue());
    }

    public int CountRepeatedOpening(string response, IReadOnlyList<string> units, WindowTextFingerprinter fingerprinter)
    {
        if (!ShouldCheckHistoricalOpening(response) || units.Count < 2 || _replies.Count == 0) return 0;
        var opening = units.Take(OpeningUnitLimit).Select(fingerprinter.Create).ToArray();
        if (opening[0].Hash == opening[1].Hash) return 0;
        var longest = 0;
        foreach (var (key, reply) in _replies)
        {
            if (key.View != _view || key.Response == response) continue;
            var earlierOpening = reply.Opening;
            var count = 0;
            var words = 0;
            while (count < opening.Length && count < earlierOpening.Length
                && opening[count].Hash == earlierOpening[count])
            {
                words += opening[count].WordCount;
                count++;
            }
            if (count >= 2 && words >= MinimumSharedWords)
                longest = Math.Max(longest, count);
        }
        return longest;
    }

    public void RememberPublishedUnits(string response, IReadOnlyList<string> units,
        WindowTextFingerprinter fingerprinter)
    {
        if (!Tracks(response)) return;
        foreach (var unit in units)
            RememberUnit(response, fingerprinter.CreateTextHash(unit), 1, baseline: false);
    }

    public void RememberBaselineUnits(string response, IReadOnlyList<string> units,
        WindowTextFingerprinter fingerprinter)
    {
        if (!Tracks(response)) return;
        foreach (var group in units.Where(unit => fingerprinter.Tokenize(unit).Count == 1)
                     .GroupBy(fingerprinter.CreateTextHash, StringComparer.Ordinal))
            RememberUnit(response, group.Key, group.Count(), baseline: true);
    }

    private void RememberUnit(string response, string hash, int count, bool baseline)
    {
        var key = (_view, hash);
        if (_publishedUnits.TryGetValue(key, out var previous))
        {
            if (previous.Response == response)
                count = baseline ? Math.Max(count, previous.Occurrences) : count + previous.Occurrences;
        }
        else _publicationOrder.Enqueue(key);
        _publishedUnits[key] = (response, count);
        while (_publicationOrder.Count > PublishedUnitLimit)
            _publishedUnits.Remove(_publicationOrder.Dequeue());
    }

    public IReadOnlyList<string> FilterRepeatedSingleWords(string response, IReadOnlyList<string> units,
        WindowTextFingerprinter fingerprinter)
    {
        if (!Tracks(response) || units.Count == 0 || _publishedUnits.Count == 0) return units;
        // The general playback filter lets short sentences through on purpose.
        // On a T3 working replacement, suppress only as many exact occurrences
        // of each one-word sentence as this response already published.
        var retained = new List<string>(units.Count);
        var suppressed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            if (fingerprinter.Tokenize(unit).Count == 1)
            {
                var hash = fingerprinter.CreateTextHash(unit);
                var count = suppressed.GetValueOrDefault(hash);
                if (_publishedUnits.TryGetValue((_view, hash), out var previous)
                    && previous.Response == response && count < previous.Occurrences)
                {
                    suppressed[hash] = count + 1;
                    continue;
                }
            }
            retained.Add(unit);
        }
        return retained;
    }

    public int CountRemountedOpening(string response, IReadOnlyList<string> units,
        WindowTextFingerprinter fingerprinter)
    {
        if (!ShouldCheckHistoricalOpening(response) || units.Count < MinimumRemountedUnits || _publishedUnits.Count == 0) return 0;
        var count = 0;
        var words = 0;
        string? firstResponse = null;
        var multipleResponses = false;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            var hash = fingerprinter.CreateTextHash(unit);
            if (!_publishedUnits.TryGetValue((_view, hash), out var previous) || previous.Response == response) break;
            firstResponse ??= previous.Response;
            multipleResponses |= previous.Response != firstResponse;
            count++;
            if (distinct.Add(hash)) words += fingerprinter.Tokenize(unit).Count;
        }
        // A new answer can repeat a previous one verbatim. Require a substantial
        // exact opening from multiple older turns to identify a remount. Once
        // identified, its subsequent redraws can retain only one of those turns.
        if (distinct.Count < MinimumRemountedUnits || words < MinimumRemountedWords
            || (!multipleResponses && _remountedResponse != response)) return 0;
        _remountedResponse = response;
        return count;
    }

    private bool Tracks(string response) => _view.Length > 0
        && response.StartsWith("t3-message:", StringComparison.Ordinal);

    private bool ShouldCheckHistoricalOpening(string response) => Tracks(response) && (!_hasNewPrompt || response != _promptResponse);

    private string GetPromptHash(string response) => response == _promptResponse ? _promptHash : "";

    private sealed record CompletedReply(string Hash, string PromptHash, string[] Opening);
}
