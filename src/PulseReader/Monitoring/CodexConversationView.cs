namespace PulseReader.Monitoring;

// Per-snapshot candidates, never a process-wide or persisted chat identity.
internal sealed class CodexConversationView
{
    private readonly HashSet<string> _transcripts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scrollContainers = new(StringComparer.Ordinal);

    internal const string LegacyTranscriptPrefix = "_transcriptContent_";
    internal const string TranscriptPrefix = "transcriptContent-";

    internal static bool IsTranscript(string className) => className
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Any(token => token.StartsWith(LegacyTranscriptPrefix, StringComparison.Ordinal)
            || token.StartsWith(TranscriptPrefix, StringComparison.Ordinal));

    internal static bool IsContainer(string className) => IsTranscript(className) || className
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Contains("thread-scroll-container", StringComparer.OrdinalIgnoreCase);

    internal static bool IsBlockingOverlay(string role, string className, string ariaProperties)
    {
        // Radix popovers expose role=dialog without replacing the transcript.
        // Keep explicit modals and unknown dialogs conservative; only exempt
        // the observed popover primitive, not dialogs based on their size.
        if (ariaProperties.Split(';').Any(property =>
            property.Trim().Equals("modal=true", StringComparison.OrdinalIgnoreCase))) return true;
        var isPopover = className.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("origin-[var(--radix-popover-content-transform-origin)]", StringComparer.Ordinal);
        return role.Equals("dialog", StringComparison.OrdinalIgnoreCase) && !isPopover;
    }

    public void Observe(string key, string className, bool visible)
    {
        if (!visible) return;
        if (IsTranscript(className)) _transcripts.Add(key);
        else if (IsContainer(className)) _scrollContainers.Add(key);
    }

    public string Resolve(bool hasModal)
    {
        // The same transcript is exposed with or without a scrolling wrapper.
        // Prefer its identity so that a layout change does not rebaseline speech.
        var candidates = _transcripts.Count > 0 ? _transcripts : _scrollContainers;
        return !hasModal && candidates.Count == 1 ? candidates.Single() : "";
    }
}
