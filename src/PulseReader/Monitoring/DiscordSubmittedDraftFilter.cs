namespace PulseReader.Monitoring;

// A bounded, channel-local fallback for server nicknames. Drafts are represented
// by salted fingerprints only. No fuzzy/substring matching or learned identities.
internal sealed class DiscordSubmittedDraftFilter
{
    private WindowTextFingerprint? _draft;
    private readonly List<(WindowTextFingerprint Fingerprint, DateTimeOffset At)> _submitted = [];
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    public void Reset(IReadOnlyList<WindowTextFingerprint>? drafts)
    {
        _submitted.Clear();
        _draft = drafts is { Count: 1 } ? drafts[0] : null;
    }

    public void Observe(IReadOnlyList<WindowTextFingerprint>? drafts, DateTimeOffset now)
    {
        _submitted.RemoveAll(c => now - c.At > Lifetime);
        if (drafts is null || drafts.Count > 1)
        {
            Reset(null); // missing/ambiguous editor is not proof of submission
            return;
        }
        if (drafts.Count == 0 && _draft is { WordCount: > 0 })
        {
            _submitted.Add((_draft, now));
            if (_submitted.Count > 8) _submitted.RemoveAt(0);
        }
        _draft = drafts.Count == 1 ? drafts[0] : null;
    }

    public bool Consume(WindowTextFingerprint? body, DateTimeOffset appearedAt, DateTimeOffset now)
    {
        _submitted.RemoveAll(c => now - c.At > Lifetime);
        if (body is null || body.WordCount == 0) return false;
        var index = _submitted.FindIndex(c => appearedAt >= c.At - TimeSpan.FromSeconds(1)
            && c.Fingerprint.WordHashes.SequenceEqual(body.WordHashes));
        if (index < 0) return false;
        _submitted.RemoveAt(index); // only one new post per cleared draft
        return true;
    }
}
