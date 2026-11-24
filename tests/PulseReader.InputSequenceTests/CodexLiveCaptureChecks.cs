using PulseReader.Monitoring;
using UIA = Interop.UIAutomationClient;

internal static class CodexLiveCaptureChecks
{
    public static void RunScope(IntPtr window)
    {
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var automation = (UIA.IUIAutomation)new UIA.CUIAutomation8();
        using var fp = new WindowTextFingerprinter();
        var snapshot = WindowTextSnapshotReader.Read(automation, automation.ElementFromHandle(window), fp,
            capture: new MonitoringCaptureContext(reportStage: stage => stages.Add(stage)));
        if (string.IsNullOrEmpty(snapshot.Context?.ViewKey)
            || !stages.Contains("capture-codex-landmarks") || !stages.Contains("capture-cache-transcript")
            || stages.Contains("ancestor-parent") || stages.Contains("capture-cache-ancestor")
            || stages.Any(stage => stage.StartsWith("editable-", StringComparison.Ordinal))
            || stages.Contains("capture-enumerate-visible") || stages.Contains("capture-document-fallback"))
            throw new InvalidOperationException($"Live Codex capture did not stay on the scoped conversation path. ContextAvailable={!string.IsNullOrEmpty(snapshot.Context?.ViewKey)}; stages={string.Join(',', stages.Order())}");
        Console.WriteLine($"PASS: live Codex capture used a cached-only transcript tree in {watch.ElapsedMilliseconds} ms with no live parent/pattern/ancestor requests; blocks={snapshot.Blocks.Count}.");
    }

    // Explicit live fixture only: caller supplies a synthetic sentence shown
    // in the latest test chat. Never print arbitrary captured conversation text.
    public static void Run(IntPtr window, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) throw new ArgumentException("Supply a synthetic expected sentence.");
        var automation = (UIA.IUIAutomation)new UIA.CUIAutomation8();
        var source = automation.ElementFromHandle(window);
        using var fp = new WindowTextFingerprinter();
        var snapshot = WindowTextSnapshotReader.Read(automation, source, fp);
        if (string.IsNullOrEmpty(snapshot.Context?.ViewKey))
            throw new InvalidOperationException("The live fixture was not recognised as a Codex conversation.");
        if (!snapshot.Blocks.Any(block => block.Text.Contains(expected, StringComparison.Ordinal)))
            throw new InvalidOperationException("The latest synthetic sentence was missing from conversation capture.");
        Console.WriteLine("PASS: the current Codex conversation includes the expected synthetic sentence.");
    }
}
