using PulseReader.Monitoring;

internal static class CodexThoughtSettlementIntegrationChecks
{
    private const string Original = "The capture pipeline now keeps every spoken sentence safely stored inside the active response until the next turn starts.";
    private const string Revised = "The capture pipeline now keeps every spoken sentence securely inside the active response until the next turn starts.";
    private const string Addition = "A genuinely new explanation must still be read.";

    public static void Run()
    {
        CheckCapturedRewrite();
        CheckContinuousStream();
        CheckFinalAndContextReset();
        CheckSubmittedEchoRemainsSilent();
        Console.WriteLine("PASS: production monitoring settles revised thoughts while preserving streaming, final answers and context baselines.");
    }

    private static WindowTextSnapshot Snapshot(string text, string cycle = "codex-message:settlement",
        string view = "view", bool final = false)
    {
        var key = cycle + (final ? ":answer" : ":working");
        return new([new(key, text, ContainerKey: key, GroupKey: key, IsAggregate: true,
            IsFinalResponse: final)], ResponseCycleKey: cycle, Context: new(view));
    }

    private static void CheckCapturedRewrite()
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(Snapshot(""), now);
        session.ProcessSnapshot(Snapshot(Original + " An unfinished continuation"), now.AddSeconds(1), ref changedAt);
        Equal(spoken, [], "An unsettled original thought was published immediately.");
        // Matches the live trace's ~1.55 second replacement interval.
        session.ProcessSnapshot(Snapshot(Revised + " " + Addition), now.AddMilliseconds(2550), ref changedAt);
        session.ProcessSettled(now.AddSeconds(4), ref changedAt);
        Equal(spoken, [], "The old variant escaped while its replacement was settling.");
        session.ProcessSettled(now.AddMilliseconds(5560), ref changedAt);
        Equal(spoken, [Revised, Addition], "A rewritten thought was repeated or its continuation was lost.");
        session.ProcessSnapshot(Snapshot(Revised + " " + Addition), now.AddSeconds(8), ref changedAt);
        session.ProcessSettled(now.AddSeconds(12), ref changedAt);
        Equal(spoken, [Revised, Addition], "An unchanged settled thought was published twice.");
    }

    private static void CheckContinuousStream()
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(Snapshot(""), now);
        session.ProcessSnapshot(Snapshot(Original), now.AddSeconds(1), ref changedAt);
        session.ProcessSnapshot(Snapshot(Original + " A genuinely"), now.AddSeconds(2), ref changedAt);
        session.ProcessSnapshot(Snapshot(Original + " A genuinely new"), now.AddSeconds(3), ref changedAt);
        session.ProcessSnapshot(Snapshot(Original + " A genuinely new explanation"), now.AddMilliseconds(4100), ref changedAt);
        Equal(spoken, [Original], "Later tokens kept resetting an already complete sentence's settlement timer.");
        session.ProcessSnapshot(Snapshot(Original + " " + Addition), now.AddSeconds(5), ref changedAt);
        session.ProcessSettled(now.AddMilliseconds(8100), ref changedAt);
        Equal(spoken, [Original, Addition], "A completed continuation failed to settle independently.");
    }

    private static void CheckFinalAndContextReset()
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var emitted = new List<MonitoredTextEventArgs>();
        session.TextAvailable += (_, e) => emitted.Add(e);
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(Snapshot(""), now);
        session.ProcessSnapshot(Snapshot(Original), now.AddSeconds(1), ref changedAt);
        const string final = "The final answer supersedes the pending thought.";
        var answer = Snapshot(final, final: true);
        // Codex can keep the old working aggregate mounted while the final
        // answer is held by its own settlement gate. Do not release it meanwhile.
        session.ProcessSnapshot(answer with { Blocks = [Snapshot(Original).Blocks[0], answer.Blocks[0]] },
            now.AddSeconds(2), ref changedAt);
        session.ProcessSettled(now.AddMilliseconds(4100), ref changedAt);
        if (emitted.Any(e => e.Text == Original))
            throw new InvalidOperationException("A visible final answer did not supersede its pending thought.");
        session.ProcessSettled(now.AddSeconds(5), ref changedAt);
        session.ProcessSettled(now.AddSeconds(9), ref changedAt);
        if (emitted is not [{ Text: final, BeginsCompletedResponse: true }])
            throw new InvalidOperationException("Final-answer takeover replayed a pending thought or lost the final answer.");

        emitted.Clear();
        session.ProcessSnapshot(Snapshot(Original, "codex-message:next"), now.AddSeconds(10), ref changedAt);
        session.ProcessSettled(now.AddMilliseconds(13100), ref changedAt);
        Equal(emitted.Select(e => e.Text).ToArray(), [Original], "New-turn reset blocked an intentional repeated thought.");
        emitted.Clear();
        session.ProcessSnapshot(new([], Context: new("")), now.AddSeconds(14), ref changedAt);
        session.ProcessSnapshot(Snapshot(Original, "codex-message:next"), now.AddSeconds(15), ref changedAt);
        session.ProcessSnapshot(Snapshot(Original, "codex-message:next"), now.AddSeconds(18), ref changedAt);
        session.ProcessSettled(now.AddSeconds(22), ref changedAt);
        Equal(emitted.Select(e => e.Text).ToArray(), [], "Returning to the same context replayed its settled baseline.");
    }

    private static void Equal(IReadOnlyList<string> actual, IReadOnlyList<string> expected, string message)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(message + " Expected: " + string.Join(" | ", expected)
                + "; actual: " + string.Join(" | ", actual));
    }

    private static void CheckSubmittedEchoRemainsSilent()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprints, settleCodexThoughts: true);
        var tracker = new WindowTextDeltaTracker();
        var now = DateTimeOffset.UtcNow;
        var empty = Snapshot("");
        var baseline = new WindowTextSnapshot(empty.Blocks,
            EditableFingerprints: [fingerprints.Create(Original)],
            ResponseCycleKey: empty.ResponseCycleKey, Context: empty.Context);
        policy.EstablishBaseline(baseline, now);
        tracker.EstablishBaseline(baseline);
        var echoed = Snapshot(Original);
        Equal(policy.Process(echoed, tracker.Update(echoed), now.AddSeconds(1)).Select(c => c.Text).ToArray(),
            [], "A submitted echo bypassed its initial suppression.");
        Equal(policy.TakeSettled(now.AddSeconds(5)).Select(c => c.Text).ToArray(),
            [], "The thought gate resurrected a suppressed submitted echo.");
        var continued = Snapshot(Original + " " + Addition);
        policy.Process(continued, tracker.Update(continued), now.AddSeconds(6));
        Equal(policy.TakeSettled(now.AddSeconds(10)).Select(c => c.Text).ToArray(),
            [Addition], "An accepted append resurrected a suppressed prefix or lost its new sentence.");
    }
}
