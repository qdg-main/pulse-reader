using PulseReader.TextPreparation;
using PulseReader.Speech;

namespace PulseReader.Monitoring;

// Discrete posts must not pass through LLM fragment/duplicate heuristics: two
// people can legitimately send identical messages, and edits are not new posts.
internal sealed class DiscordMessageTracker
{
    private readonly HashSet<ulong> _seen = [];
    private readonly Dictionary<ulong, (string Text, int PrefixLength, WindowTextFingerprint? Body, DateTimeOffset Since, DateTimeOffset Appeared)> _pending = [];
    private readonly DiscordSubmittedDraftFilter _drafts = new();
    private ulong _floor;
    private const int MaximumRemembered = 4096;
    public long SuppressedEchoCount { get; private set; }

    public void ObserveComposer(IReadOnlyList<WindowTextFingerprint> drafts, DateTimeOffset now) => _drafts.Observe(drafts, now);

    public void EstablishBaseline(WindowTextSnapshot snapshot, DateTimeOffset now)
    {
        _seen.Clear();
        _pending.Clear();
        _drafts.Reset(snapshot.EditableFingerprints);
        // Discord's documented snowflake epoch. This excludes older history
        // loaded by scrolling, including history not exposed at attach time.
        // https://github.com/discord/discord-api-docs/blob/main/developers/reference.mdx#snowflakes
        _floor = (ulong)Math.Max(0, now.ToUnixTimeMilliseconds() - 1420070400000L) << 22;
        foreach (var block in snapshot.Blocks)
            if (DiscordConversationAdapter.TryMessageId(block.Key, out _, out var id)) _seen.Add(id);
    }

    public void Observe(WindowTextSnapshot snapshot, DateTimeOffset now)
    {
        _drafts.Observe(snapshot.EditableFingerprints, now);
        var current = new HashSet<ulong>();
        foreach (var block in snapshot.Blocks)
        {
            if (!DiscordConversationAdapter.TryMessageId(block.Key, out _, out var id)) continue;
            current.Add(id);
            if (id < _floor || _seen.Contains(id)) continue;
            var exists = _pending.TryGetValue(id, out var pending);
            if (!exists || pending.Text != block.Text)
                _pending[id] = (block.Text, block.DiscreteAuthorPrefixLength, block.DiscreteBodyFingerprint, now, exists ? pending.Appeared : now);
        }
        foreach (var id in _pending.Keys.Where(id => !current.Contains(id)).ToArray()) _pending.Remove(id);
    }

    public IReadOnlyList<SpeechRequest> TakeReady(DateTimeOffset now)
    {
        var ready = new List<SpeechRequest>();
        foreach (var pair in _pending.OrderBy(p => p.Key).ToArray())
        {
            if (now - pair.Value.Since < TimeSpan.FromMilliseconds(700)) break;
            _pending.Remove(pair.Key);
            _seen.Add(pair.Key);
            var echo = _drafts.Consume(pair.Value.Body, pair.Value.Appeared, now);
            if (echo) SuppressedEchoCount++;
            if (!echo && !string.IsNullOrWhiteSpace(pair.Value.Text))
            {
                // Deduplicate and suppress submitted drafts at the message level,
                // then prepare the whole body with selected-text rules. Table
                // and quotation context must survive the subsequent splitting.
                var prefixLength = Math.Clamp(pair.Value.PrefixLength, 0, pair.Value.Text.Length);
                var prefix = pair.Value.Text[..prefixLength];
                var body = SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(pair.Value.Text[prefixLength..]));
                foreach (var unit in body.Units)
                {
                    // Formatting can deliberately silence structural units,
                    // such as a Markdown table's separator row.
                    if (string.IsNullOrWhiteSpace(unit.SpokenText)) continue;
                    ready.Add(SpeechRequest.FromPreparedUnit(unit, body.Complexity, prefix));
                    prefix = "";
                }
            }
        }
        if (_seen.Count > MaximumRemembered)
        {
            foreach (var id in _seen.Order().Take(_seen.Count - MaximumRemembered).ToArray())
            { _seen.Remove(id); _floor = Math.Max(_floor, id + 1); }
        }
        return ready;
    }
}
