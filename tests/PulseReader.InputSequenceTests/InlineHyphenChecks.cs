using PulseReader.Monitoring;

internal static class InlineHyphenChecks
{
    public static void Run()
    {
        CheckFragmentedSuspendedHyphens();
        CheckScopeAndBoundaries();
        foreach (var provider in new[] { "codex-message", "lmstudio-message" })
        {
            using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
            var spoken = new List<string>();
            session.TextAvailable += (_, e) => spoken.Add(e.Text);
            const string first = "The shoulder comparison found a specific asymmetry: the saved normals agree across the gameplay right-shoulder join and both photo-mode joins, but disagree across the gameplay left-shoulder join.";
            const string second = "That supports a targeted left-side test, rather than restoring the old correction on both arms.";
            const string third = "The remaining materials will be checked next.";
            const string added = "A fresh closing sentence is now available.";
            var fragmented = first.Replace("right-shoulder", "right- shoulder", StringComparison.Ordinal)
                .Replace("photo-mode", "photo- mode", StringComparison.Ordinal)
                .Replace("left-shoulder", "left- shoulder", StringComparison.Ordinal);
            if (first.Length != 195 || fragmented.Length != 198)
                throw new InvalidOperationException("Hyphen fixture no longer matches the captured sentence lengths.");
            var cycle = provider + ":inline-hyphens";
            WindowTextSnapshot Snapshot(string text) => new([
                new(cycle + ":working", text, ContainerKey: cycle + ":working",
                    GroupKey: cycle + ":working", IsAggregate: true)],
                ResponseCycleKey: cycle, Context: new("view"));
            var now = DateTimeOffset.UtcNow;
            var changedAt = DateTimeOffset.MinValue;
            session.EstablishBaseline(Snapshot(""), now);
            session.ProcessSnapshot(Snapshot(fragmented + " "
                + second.Replace("left-side", "left- side", StringComparison.Ordinal) + " " + third),
                now.AddSeconds(1), ref changedAt);
            session.ProcessSnapshot(Snapshot(first + " " + second + " " + third + " " + added),
                now.AddSeconds(2), ref changedAt);
            session.ProcessSettled(now.AddSeconds(5), ref changedAt);
            if (!spoken.SequenceEqual(new[] { first, second, third, added }))
                throw new InvalidOperationException(provider + ": inline hyphen consolidation replayed or lost speech: "
                    + string.Join(" | ", spoken));
            var nextCycle = provider + ":new-inline-hyphens";
            WindowTextSnapshot Next(string text) => new([
                new(nextCycle + ":working", text, ContainerKey: nextCycle + ":working",
                    GroupKey: nextCycle + ":working", IsAggregate: true)],
                ResponseCycleKey: nextCycle, Context: new("view"));
            session.ProcessSnapshot(Next(first), now.AddSeconds(6), ref changedAt);
            session.ProcessSettled(now.AddSeconds(9), ref changedAt);
            var correction = first.Replace("left-shoulder", "left-elbow", StringComparison.Ordinal);
            session.ProcessSnapshot(Next(correction), now.AddSeconds(10), ref changedAt);
            session.ProcessSettled(now.AddSeconds(13), ref changedAt);
            if (!spoken.SequenceEqual(new[] { first, second, third, added, first, correction }))
                throw new InvalidOperationException("Hyphen normalization suppressed a new turn or meaningful correction.");
        }
        Console.WriteLine("PASS: consolidating streamed hyphenated words does not replay completed thoughts.");
    }

    private static void CheckFragmentedSuspendedHyphens()
    {
        foreach (var provider in new[] { "codex-message", "lmstudio-message" })
        foreach (var word in new[] { "and", "or", "nor", "but", "to", "versus", "vs", "AND", "Or" })
        for (var split = 1; split < word.Length; split++)
        {
            var accumulator = new WindowTextAccumulator();
            var key = provider + ":fragmented-coordinator:working";
            accumulator.Append(new("Support client-", true, true, key));
            accumulator.Append(new(" " + word[..split], false, true, key));
            accumulator.Append(new(word[split..] + " server-side checks.", false, true, key));
            var expected = $"Support client- {word} server-side checks.";
            if (!accumulator.TakeCompletedUnits(true).SequenceEqual(new[] { expected }))
                throw new InvalidOperationException($"Fragmented coordinator changed valid prose: {word} at {split}.");
        }
        foreach (var word in new[] { "and-roll", "android", "orbit", "versatile" })
        {
            var accumulator = new WindowTextAccumulator();
            const string key = "codex-message:fragmented-compound:working";
            accumulator.Append(new("The prefix-", true, true, key));
            accumulator.Append(new(" " + word[..1], false, true, key));
            foreach (var character in word[1..])
                accumulator.Append(new(character.ToString(), false, true, key));
            accumulator.Append(new(" compound stays joined.", false, true, key));
            var expected = $"The prefix-{word} compound stays joined.";
            if (!accumulator.TakeCompletedUnits(true).SequenceEqual(new[] { expected }))
                throw new InvalidOperationException("Deferring a coordinator prefix left a real compound split: " + word);
        }
    }

    private static void CheckScopeAndBoundaries()
    {
        foreach (var preserved in new[] { "left-\nside", "left - side", "Run tool -f now.",
                     "4- 2 is a subtraction.", "- first item\n- second item", "Re-sign differs from resign.",
                     "Support client- and server-side checks.", "Wait two- or three-day intervals.",
                     "Check pre- and post-operative symptoms.", "Wait two- to three-day intervals.",
                     "Compare client- versus server-side rendering." })
            if (StreamingTextFormatting.NormalizeInlineHyphenSpacing(preserved) != preserved)
                throw new InvalidOperationException("Inline fragment normalization changed a real boundary: " + preserved);
        if (StreamingTextFormatting.NormalizeInlineHyphenSpacing("A rock- and-roll song.") != "A rock-and-roll song.")
            throw new InvalidOperationException("Preserving suspended prefixes prevented a real hyphen chain from joining.");
        var accumulator = new WindowTextAccumulator();
        accumulator.Append(new("The right-", true, true, "codex-message:seam:working"));
        accumulator.Append(new(" shoulder remains visible.", false, true, "codex-message:seam:working"));
        if (!accumulator.TakeCompletedUnits(true).SequenceEqual(new[] { "The right-shoulder remains visible." }))
            throw new InvalidOperationException("A hyphen fragment spanning append calls was not consolidated.");
        accumulator.Append(new("The old left- side remains unfinished", true, true, "generic", "generic"));
        accumulator.Append(new("A new right- shoulder sentence.", true, true, "codex-message:mixed:working", "working"));
        if (!accumulator.TakeCompletedUnits(true).Any(unit => unit.Contains("old left- side", StringComparison.Ordinal)))
            throw new InvalidOperationException("A conversation append rewrote preceding generic pending text.");
        foreach (var key in new[] { "generic", "discord-message:seam" })
        {
            accumulator.Append(new("The left- side remains visible.", true, true, key));
            if (!accumulator.TakeCompletedUnits(true).SequenceEqual(new[] { "The left- side remains visible." }))
                throw new InvalidOperationException("Conversation normalization escaped to " + key);
        }
        foreach (var provider in new[] { "codex-message", "lmstudio-message" })
        {
            var key = provider + ":suspended:working";
            foreach (var conjunction in new[] { "and", "or", "AND", "Or" })
            {
                var expected = $"Support client- {conjunction} server-side checks.";
                accumulator.Append(new("Support client-", true, true, key));
                accumulator.Append(new($" {conjunction} server-side checks.", false, true, key));
                if (!accumulator.TakeCompletedUnits(true).SequenceEqual(new[] { expected }))
                    throw new InvalidOperationException("A split suspended hyphen changed valid prose: " + expected);
            }
        }
    }
}
