using PulseReader.Monitoring;

internal static class CodexThoughtSettlementChecks
{
    private const string Cycle = "codex-message:settlement";
    private const string Original = "The capture pipeline now keeps every spoken sentence safely stored inside the active response until the next turn starts.";
    private const string Revised = "The capture pipeline now keeps every spoken sentence securely inside the active response until the next turn starts.";
    private const string Addition = "A new explanation remains audible.";
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    public static void Run()
    {
        CheckRevisionBeforeEmission();
        CheckContinuousAppends();
        CheckSentenceCompletedLater();
        CheckCorrections();
        CheckBaselineAndHistory();
        CheckSuppressedEchoHistory();
        CheckDisappearanceAndShrink();
        CheckResetAndNewTurn();
        CheckFinalAndOtherProviders();
        CheckMixedBatch();
        CheckRepeatedOccurrences();
        CheckLimits();
        Console.WriteLine("PASS: Codex thought sentences settle independently and exact history stays silent.");
    }

    private static void CheckRevisionBeforeEmission()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        gate.EstablishBaseline(Snapshot(""), At(0));
        Equal(Observe(gate, Original, 0), []);
        Equal(Observe(gate, Revised + " " + Addition, 1.55, replacement: true), []);
        Equal(gate.TakeReady(At(3)), []);
        Equal(gate.TakeReady(At(4.54)), []);
        Equal(gate.TakeReady(At(4.55)), [Revised, Addition]);
        Equal(gate.TakeReady(At(8)), []);
    }

    private static void CheckContinuousAppends()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        Equal(Observe(gate, Original, 0), []);
        for (var index = 1; index <= 5; index++)
            Equal(Observe(gate, Original + " Still writing" + new string(' ', index), index * .5), []);
        if (gate.TimeUntilNextSettlement(At(2.5)) != TimeSpan.FromSeconds(.5))
            throw new InvalidOperationException("Appending tokens restarted a complete sentence's timer.");
        Equal(Observe(gate, Original + " Still writing more words", 3), [Original]);
        Equal(Observe(gate, Original + " " + Addition, 4, replacement: true), []);
        Equal(gate.TakeReady(At(7)), [Addition]);
    }

    private static void CheckSentenceCompletedLater()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        Equal(Observe(gate, Original, 0), []);
        Equal(Observe(gate, Revised[..^1], 1.55, replacement: true), []);
        Equal(gate.TakeReady(At(6)), []);
        Equal(Observe(gate, Revised, 7), []);
        Equal(gate.TakeReady(At(9.99)), []);
        Equal(gate.TakeReady(At(10)), [Revised]);
    }

    private static void CheckCorrections()
    {
        foreach (var corrected in new[]
        {
            Original.Replace("safely stored", "not stored", StringComparison.Ordinal),
            Original.Replace("safely stored", "at 25", StringComparison.Ordinal),
            Original.Replace("safely stored", "deleted", StringComparison.Ordinal)
        })
        {
            using var fingerprints = new WindowTextFingerprinter();
            var gate = new CodexThoughtSettlementFilter(fingerprints);
            Equal(Observe(gate, Original, 0), []);
            Equal(gate.TakeReady(At(3)), [Original]);
            Equal(Observe(gate, corrected, 4, replacement: true), []);
            Equal(gate.TakeReady(At(7)), [corrected]);
        }
    }

    private static void CheckBaselineAndHistory()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        var history = new WindowTextBlock("codex-message:old:working", "History remains silent.");
        var baseline = Snapshot(Original + " An unfinished prefix") with
        {
            Blocks = [history, Snapshot(Original + " An unfinished prefix").Blocks[0]]
        };
        gate.EstablishBaseline(baseline, At(0));
        Equal(gate.TakeReady(At(10)), []);
        Equal(Observe(gate, Original + " " + Addition, 11, replacement: true), []);
        Equal(gate.TakeReady(At(14)), [Addition]);
        Equal(Observe(gate, Original + " " + Addition, 20, replacement: true), []);
    }

    private static void CheckDisappearanceAndShrink()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        gate.EstablishBaseline(Snapshot(Original), At(0));
        Equal(Observe(gate, Original + " " + Addition, 1), []);
        Equal(gate.Filter(new([], ResponseCycleKey: Cycle), [], At(2)), []);
        Equal(gate.TakeReady(At(5)), []);
        if (gate.TimeUntilNextSettlement(At(5)) is not null)
            throw new InvalidOperationException("A missing working block remained eligible for publication.");
        Equal(Observe(gate, Original[..40], 6, replacement: true), []);
        Equal(gate.TakeReady(At(8)), []);
        Equal(Observe(gate, Original + " " + Addition, 9), [Addition]);
        Equal(Observe(gate, Original + " " + Addition, 12), []);
    }

    private static void CheckSuppressedEchoHistory()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        var unfinished = Snapshot(Original[..^1]);
        gate.SuppressEchoChanges(unfinished, [Change(unfinished)]);
        Equal(gate.Filter(unfinished, [], At(0)), []);
        Equal(Observe(gate, Original + " " + Addition, 1), []);
        Equal(gate.TakeReady(At(4)), [Addition]);
        Equal(Observe(gate, Original + " " + Addition, 8, replacement: true), []);

        gate.Reset();
        Equal(Observe(gate, Original, 10), []);
        var completed = Snapshot(Original);
        gate.SuppressEchoChanges(completed, [Change(completed)]);
        Equal(gate.TakeReady(At(14)), []);
        Equal(Observe(gate, Original + " " + Addition, 15), []);
        Equal(gate.TakeReady(At(18)), [Addition]);

        gate.Reset();
        gate.SuppressEchoChanges(completed, [Change(completed)]);
        Equal(Observe(gate, Original + " " + Original, 20), []);
        Equal(gate.TakeReady(At(23)), [Original]);

        gate.Reset();
        gate.SuppressEchoChanges(completed, [new(Original, true, true, "lmstudio-message:other:working")]);
        Equal(Observe(gate, Original, 25), []);
        Equal(gate.TakeReady(At(28)), [Original]);

        gate.Reset();
        gate.SuppressEchoChanges(completed, [Change(completed)]);
        var largeTail = new string('x', 100_001);
        var overflow = Observe(gate, Original + " " + largeTail, 30);
        // The normal reading-unit preparation can wrap an enormous token into
        // lines. Verify the echo is gone and every non-whitespace tail character
        // survives without requiring that preparatory whitespace to be identical.
        if (overflow.Count != 1
            || !overflow[0].Text.Where(character => !char.IsWhiteSpace(character)).SequenceEqual(largeTail))
            throw new InvalidOperationException("Oversized echo filtering replayed the echo or lost tail characters.");
    }

    private static void CheckResetAndNewTurn()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        Equal(Observe(gate, Original, 0), []);
        gate.Reset();
        Equal(gate.TakeReady(At(10)), []);
        Equal(Observe(gate, Original, 11), []);
        Equal(gate.TakeReady(At(14)), [Original]);
        var next = Snapshot(Original, Cycle + ":next");
        Equal(gate.Filter(next, [Change(next)], At(15)), []);
        Equal(gate.TakeReady(At(18)), [Original]);
    }

    private static void CheckFinalAndOtherProviders()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        Equal(Observe(gate, Original, 0), []);
        var final = new WindowTextChange("The final answer is immediate.", true, true,
            Cycle + ":answer", IsFinalResponse: true);
        var bypassed = gate.Filter(Snapshot(Original), [final], At(1));
        if (bypassed.Count != 1 || bypassed[0] != final)
            throw new InvalidOperationException("Final text was changed or delayed by the thought gate.");
        Equal(gate.TakeReady(At(10)), []);
        foreach (var key in new[] { "lmstudio-message:reply:working", "discord-message:reply", "generic" })
        {
            var change = new WindowTextChange(Original, true, true, key, "container", 7,
                IsReplacement: true, IsProvisional: true);
            var result = gate.Filter(new([new(key, Original)], ResponseCycleKey: key), [change], At(11));
            if (result.Count != 1 || result[0] != change)
                throw new InvalidOperationException("A different provider's change was modified.");
        }
        var status = new WindowTextChange("Thinking", true, true, Cycle + ":working", IsStatus: true);
        var statuses = gate.Filter(new([new(status.Key, status.Text, IsStatus: true)],
            ResponseCycleKey: Cycle), [status], At(12));
        if (statuses.Count != 1 || statuses[0] != status)
            throw new InvalidOperationException("A status change was modified by the sentence gate.");
    }

    private static void CheckRepeatedOccurrences()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        Equal(Observe(gate, Original + " " + Original, 0), []);
        var settled = gate.TakeReady(At(3));
        Equal(settled, [Original, Original]);
        foreach (var change in settled)
            if (!change.IsNewBlock || !change.StartsAtBoundary || change.IsReplacement || change.IsProvisional
                || change.Key != Cycle + ":working" || change.ContainerKey != "container"
                || change.GroupKey != "group" || change.Order != 7)
                throw new InvalidOperationException("Settled sentence metadata was not preserved.");
        Equal(Observe(gate, Original + " " + Original, 4, replacement: true), []);
        Equal(gate.TakeReady(At(8)), []);
    }

    private static void CheckMixedBatch()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        Equal(Observe(gate, Original, 0), []);
        var snapshot = Snapshot(Revised);
        var ordinary = new WindowTextChange("An unrelated append stays immediate.", false, true,
            "generic", "other-container", 8);
        var mixed = gate.Filter(snapshot, [Change(snapshot, replacement: true), ordinary], At(1.55));
        if (mixed.Count != 1 || mixed[0] != ordinary)
            throw new InvalidOperationException("A Codex replacement changed another batch member.");
        Equal(gate.TakeReady(At(4.55)), [Revised]);
    }

    private static void CheckLimits()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var gate = new CodexThoughtSettlementFilter(fingerprints);
        Equal(Observe(gate, Original, 0), []);
        var large = Original + " " + new string('x', 100_001);
        Equal(Observe(gate, large, 1), [large]);
        Equal(gate.TakeReady(At(5)), []);
        var append = new WindowTextChange(" appended", false, true, Cycle + ":working");
        var result = gate.Filter(Snapshot(large + append.Text), [append], At(6));
        if (result.Count != 1 || result[0] != append)
            throw new InvalidOperationException("An oversized turn did not remain in pass-through mode.");
        gate.Reset();
        Equal(Observe(gate, Original, 7), []);
        Equal(gate.TakeReady(At(10)), [Original]);
        gate.Reset();
        var many = string.Join(' ', Enumerable.Range(0, 513).Select(index => $"Sentence {index} remains complete."));
        Equal(Observe(gate, many, 11), [many]);
    }

    private static DateTimeOffset At(double seconds) =>
        Start.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    private static WindowTextSnapshot Snapshot(string body, string cycle = Cycle) => new([
        new(cycle + ":working", body, ContainerKey: "container", Order: 7,
            GroupKey: "group", IsAggregate: true)], ResponseCycleKey: cycle);

    private static WindowTextChange Change(WindowTextSnapshot snapshot, bool replacement = false)
    {
        var block = snapshot.Blocks[0];
        return new(block.Text, true, true, block.Key, block.ContainerKey, block.Order,
            IsReplacement: replacement, IsProvisional: true, GroupKey: block.GroupKey);
    }

    private static IReadOnlyList<WindowTextChange> Observe(CodexThoughtSettlementFilter gate,
        string body, double seconds, bool replacement = false)
    {
        var snapshot = Snapshot(body);
        return gate.Filter(snapshot, [Change(snapshot, replacement)], At(seconds));
    }

    private static void Equal(IReadOnlyList<WindowTextChange> actual, IReadOnlyList<string> expected)
    {
        if (!actual.Select(change => change.Text).SequenceEqual(expected))
            throw new InvalidOperationException("Thought settlement mismatch. Expected: "
                + Describe(expected) + "; actual: " + Describe(actual.Select(change => change.Text)));
    }

    private static string Describe(IEnumerable<string> texts) => string.Join(" | ", texts.Take(8)
        .Select(text => text.Length <= 160 ? text : text[..160] + $"... ({text.Length} characters)"));
}
