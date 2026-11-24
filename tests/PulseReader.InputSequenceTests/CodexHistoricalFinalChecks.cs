using PulseReader.Monitoring;

internal static class CodexHistoricalFinalChecks
{
    public static void Run(List<string> failures)
    {
        foreach (var provider in new[] { "codex-message", "lmstudio-message" })
        {
            CheckHistoricalRemounts(provider, failures);
            CheckDraftCompletion(provider, failures);
            CheckNewResponseCycles(provider, failures);
            CheckLiveFinalExtension(provider, failures);
        }
        CheckGenericFinal(failures);
    }

    private static WindowTextSnapshot Snapshot(string cycle, string text, bool final = true,
        string view = "historical-view")
    {
        var key = cycle + (final ? ":answer" : ":working");
        return new([new(key, text, GroupKey: key, IsAggregate: true, IsFinalResponse: final)],
            ResponseCycleKey: cycle, Context: new(view));
    }

    private static void CheckHistoricalRemounts(string provider, List<string> failures)
    {
        foreach (var returnToView in new[] { false, true })
        {
            using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
            var spoken = new List<string>();
            session.TextAvailable += (_, e) => spoken.Add(e.Text);
            var now = DateTimeOffset.UtcNow;
            var changedAt = DateTimeOffset.MinValue;
            var cycle = provider + ":historical";
            const string original = "Old closing paragraph.";
            session.EstablishBaseline(Snapshot(cycle, original), now);
            if (returnToView)
            {
                session.ProcessSnapshot(Snapshot(cycle, original, view: "returned-view"),
                    now.AddSeconds(1), ref changedAt);
                session.ProcessSnapshot(Snapshot(cycle, original, view: "returned-view"),
                    now.AddSeconds(3), ref changedAt);
            }
            var view = returnToView ? "returned-view" : "historical-view";
            var tick = 5;
            foreach (var text in new[]
            {
                "Old opening paragraph.\n" + original,
                original + " Old trailing paragraph.",
                "Another historical subsection.",
                "Old closing",
                original
            })
            {
                session.ProcessSnapshot(new([], ResponseCycleKey: cycle, Context: new(view)),
                    now.AddSeconds(tick++), ref changedAt);
                session.ProcessSnapshot(Snapshot(cycle, text, view: view),
                    now.AddSeconds(tick++), ref changedAt);
                session.ProcessSettled(now.AddSeconds(tick += 2), ref changedAt);
            }
            if (spoken.Count != 0)
                failures.Add($"{provider}: remounting a baselined final spoke {spoken.Count} units (returned view={returnToView}).");
        }
    }

    private static void CheckDraftCompletion(string provider, List<string> failures)
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        var cycle = provider + ":active";
        session.EstablishBaseline(Snapshot(cycle, "Existing draft sentence.", final: false), now);
        session.ProcessSnapshot(Snapshot(cycle, "Existing draft sentence. Fresh draft sentence.", final: false),
            now.AddSeconds(1), ref changedAt);
        session.ProcessSnapshot(Snapshot(cycle, "The fresh completed answer."),
            now.AddSeconds(2), ref changedAt);
        session.ProcessSettled(now.AddSeconds(4), ref changedAt);
        if (!spoken.SequenceEqual(new[] { "Fresh draft sentence.", "The fresh completed answer." }))
            failures.Add($"{provider}: historical final suppression lost a baselined active draft's fresh content or final handoff.");
    }

    private static void CheckNewResponseCycles(string provider, List<string> failures)
    {
        foreach (var newCycle in new[] { provider + ":next-turn", provider + ":historical:generation:1" })
        {
            using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
            var spoken = new List<string>();
            session.TextAvailable += (_, e) => spoken.Add(e.Text);
            var now = DateTimeOffset.UtcNow;
            var changedAt = DateTimeOffset.MinValue;
            const string repeated = "An intentionally repeated answer.";
            session.EstablishBaseline(Snapshot(provider + ":historical", repeated), now);
            session.ProcessSnapshot(Snapshot(newCycle, repeated), now.AddSeconds(1), ref changedAt);
            session.ProcessSettled(now.AddSeconds(3), ref changedAt);
            if (!spoken.SequenceEqual(new[] { repeated }))
                failures.Add($"{provider}: a new turn or generation could not repeat a historical final ({newCycle}).");
        }
    }

    private static void CheckLiveFinalExtension(string provider, List<string> failures)
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        var cycle = provider + ":live";
        const string first = "The live answer begins.";
        const string next = "A late paragraph arrives.";
        session.EstablishBaseline(new([], ResponseCycleKey: cycle, Context: new("historical-view")), now);
        session.ProcessSnapshot(Snapshot(cycle, first), now.AddSeconds(1), ref changedAt);
        session.ProcessSettled(now.AddSeconds(3), ref changedAt);
        session.ProcessSnapshot(Snapshot(cycle, first + " " + next), now.AddSeconds(4), ref changedAt);
        session.ProcessSettled(now.AddSeconds(6), ref changedAt);
        if (!spoken.SequenceEqual(new[] { first, next }))
            failures.Add($"{provider}: a live final's late paragraph was lost or replayed its published opening.");
    }

    private static void CheckGenericFinal(List<string> failures)
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        WindowTextSnapshot Generic(string text) => new([new("generic-answer", text, IsFinalResponse: true)]);
        session.EstablishBaseline(Generic("Original generic answer."), now);
        session.ProcessSnapshot(Generic("Updated generic answer."), now.AddSeconds(1), ref changedAt);
        session.ProcessSettled(now.AddSeconds(4), ref changedAt);
        if (!spoken.SequenceEqual(new[] { "Updated generic answer." }))
            failures.Add("Historical conversation final suppression changed generic final replacement behavior.");
    }
}
