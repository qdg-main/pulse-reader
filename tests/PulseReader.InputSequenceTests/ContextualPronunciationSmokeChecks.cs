using PulseReader.Speech;
using PulseReader.TextPreparation;
using System.Text.RegularExpressions;

internal static class ContextualPronunciationSmokeChecks
{
    public static async Task RunAsync()
    {
        var memory = new ContextualPronunciationMemorySnapshot(
            32UL * 1024 * 1024 * 1024,
            12UL * 1024 * 1024 * 1024,
            62);
        using var service = new ContextualPronunciationService(
            captureMemory: () => memory,
            memoryCheckInterval: TimeSpan.FromMilliseconds(500));
        if (!service.Availability.IsInstalled)
        {
            throw new InvalidOperationException(
                service.Availability.Message
                ?? "The contextual-pronunciation model is not installed.");
        }

        service.SetEnabled(true);
        const string changedText =
            "Sharpen both axes before splitting the logs. "
            + "Reduce the bass slightly, then listen to the speakers again. "
            + "The old pipe contains lead, so wear gloves before moving it. "
            + "They watched the rope wind around the drum. "
            + "The presenter will go live in five minutes.";
        var changed = await service.ApplyAsync(Prepare(changedText), CancellationToken.None);
        var changedSpeech = changed.BuildSpokenText();
        string[] requiredPhonemes =
        [
            "[axes](/ˈæksɪz/)",
            "[bass](/bˈAs/)",
            "[lead](/lˈɛd/)",
            "[wind](/wˈInd/)",
            "[live](/lˈIv/)"
        ];
        Assert(
            requiredPhonemes.All(value => changedSpeech.Contains(value, StringComparison.Ordinal)),
            "The real local classifier did not apply every approved pronunciation."
            + Environment.NewLine
            + changedSpeech);

        const string unchangedText =
            "Label the horizontal and vertical axes on the chart. "
            + "The bass swam beneath the fishing boat. "
            + "Lead the visitors through the main entrance. "
            + "The cold wind rattled the windows. "
            + "Most turtles can live for many decades.";
        var unchanged = await service.ApplyAsync(Prepare(unchangedText), CancellationToken.None);
        Assert(
            !unchanged.BuildSpokenText().Contains("](/", StringComparison.Ordinal),
            "The real local classifier changed a control pronunciation."
            + Environment.NewLine
            + unchanged.BuildSpokenText());

        var liveCorpusText = string.Join(
            Environment.NewLine,
            LiveCorpus.Select(testCase => testCase.Text));
        var multiLineProgress = new List<ContextualPronunciationProgress>();
        await service.ApplyProgressivelyAsync(
            Prepare(liveCorpusText),
            (progress, _) =>
            {
                multiLineProgress.Add(progress);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        var preparedLiveCorpus = Prepare(liveCorpusText);
        var liveTargets = ContextualPronunciationRules.FindTargets(preparedLiveCorpus);
        var expectedActiveBySourceOffset = BuildExpectedLiveOccurrences(liveCorpusText);
        var expectedLabels = liveTargets
            .Select(target =>
            {
                var unit = preparedLiveCorpus.Units.Single(candidate =>
                    candidate.Index == target.UnitIndex);
                var matches = Regex.Matches(
                    unit.OriginalText,
                    @"\blive\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var sourceOffset = unit.SourceRange.Start
                    + matches[target.Occurrence - 1].Index;
                return expectedActiveBySourceOffset[sourceOffset]
                    ? target.Rule.PhonemeLabel
                    : target.Rule.Labels.First(label =>
                        !string.Equals(
                            label.Name,
                            target.Rule.PhonemeLabel,
                            StringComparison.Ordinal)).Name;
            })
            .ToArray();
        var expectedLiveCorpus = ContextualPronunciationRules.ApplyLabels(
            preparedLiveCorpus,
            liveTargets,
            expectedLabels);
        var multiLineLive = multiLineProgress[^1].Document;
        var multiLineSpeech = multiLineLive.BuildSpokenText();
        var liveCorpusFailures = multiLineLive.Units
            .Zip(expectedLiveCorpus.Units)
            .Where(pair => !string.Equals(
                pair.First.SpokenText,
                pair.Second.SpokenText,
                StringComparison.Ordinal))
            .Select(pair =>
                "Source: " + pair.First.OriginalText
                + Environment.NewLine
                + "Expected: " + pair.Second.SpokenText
                + Environment.NewLine
                + "Actual: " + pair.First.SpokenText)
            .ToArray();

        Assert(
            liveCorpusFailures.Length == 0,
            "The real local classifier regressed an accepted live-pronunciation case."
            + Environment.NewLine
            + string.Join(Environment.NewLine, liveCorpusFailures));
        Assert(
            multiLineProgress.Count > 1
            && multiLineProgress.Take(multiLineProgress.Count - 1).All(progress => !progress.IsFinal)
            && multiLineProgress[^1].IsFinal,
            "The real local classifier did not publish the multi-line selection in reading-unit order.");

        memory = memory with
        {
            AvailablePhysicalBytes = 2UL * 1024 * 1024 * 1024,
            MemoryLoadPercent = 94
        };
        await Task.Delay(TimeSpan.FromSeconds(2));
        memory = memory with
        {
            AvailablePhysicalBytes = 12UL * 1024 * 1024 * 1024,
            MemoryLoadPercent = 62
        };
        var restarted = await service.ApplyAsync(
            Prepare("The presenter will go live in five minutes."),
            CancellationToken.None);
        Assert(
            restarted.BuildSpokenText().Contains("[live](/lˈIv/)", StringComparison.Ordinal),
            "The real local classifier did not restart after its memory-pressure release."
            + Environment.NewLine
            + restarted.BuildSpokenText());

        Console.WriteLine(
            "PASS: the pinned Qwen3 4B model classified all production smoke cases and restarted after memory-pressure shutdown.");
        Console.WriteLine(changedSpeech);
    }

    private static PreparedDocument Prepare(string text) =>
        SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(text));

    private static IReadOnlyList<LivePronunciationCase> LiveCorpus { get; } =
    [
        new("Test these individually, then try the mixed examples."),
        new("“Live” meaning reside or remain alive:", false),
        new("I live near the railway station.", false),
        new("They live together in a small apartment.", false),
        new("We hope to live by the coast someday.", false),
        new("Most turtles can live for many decades.", false),
        new("These plants live in damp, shaded areas.", false),
        new("You can live comfortably on a modest budget.", false),
        new("She learned to live with the decision.", false),
        new("Few animals can live without fresh water.", false),
        new("He wants to live long enough to meet his grandchildren.", false),
        new("Live and learn, as the saying goes.", false),
        new("Long live the king.", false),
        new("They live peaceful lives in the countryside.", false),
        new("“Live” meaning real-time, active, or not recorded:", true),
        new("The concert will be broadcast live tonight.", true),
        new("She performed in front of a live audience.", true),
        new("Be careful because that electrical wire is live.", true),
        new("The new website is finally live.", true),
        new("The reporter is speaking live from London.", true),
        new("We watched the match on a live video feed.", true),
        new("The support team offers live chat during business hours.", true),
        new("The application displays live performance data.", true),
        new("The station switched to a live broadcast.", true),
        new("The band released a recording of their live performance.", true),
        new("The technician confirmed that the microphone was live.", true),
        new("The presenter will go live in five minutes.", true),
        new("Mixed examples containing both pronunciations:"),
        new("I live near the studio where they broadcast the programme live.", false, true),
        new("The musicians who live nearby will perform live tonight.", false, true),
        new("We live in Manchester and watch every match live.", false, true),
        new("People who live abroad can follow the ceremony through a live stream.", false, true),
        new("They live beside the theatre and regularly attend live performances.", false, true),
        new("I would rather live near the venue than watch its concerts live online.", false, true)
    ];

    private static IReadOnlyDictionary<int, bool> BuildExpectedLiveOccurrences(string corpus)
    {
        var expected = new Dictionary<int, bool>();
        var searchStart = 0;
        foreach (var testCase in LiveCorpus)
        {
            var caseStart = corpus.IndexOf(testCase.Text, searchStart, StringComparison.Ordinal);
            if (caseStart < 0)
            {
                throw new InvalidOperationException(
                    $"The live-pronunciation corpus lost this case: {testCase.Text}");
            }

            var matches = Regex.Matches(
                testCase.Text,
                @"\blive\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (matches.Count != testCase.ActiveOccurrences.Length)
            {
                throw new InvalidOperationException(
                    $"The live-pronunciation expectation count changed for: {testCase.Text}");
            }

            for (var index = 0; index < matches.Count; index++)
            {
                expected.Add(
                    caseStart + matches[index].Index,
                    testCase.ActiveOccurrences[index]);
            }

            searchStart = caseStart + testCase.Text.Length;
        }

        return expected;
    }

    private sealed record LivePronunciationCase(
        string Text,
        params bool[] ActiveOccurrences);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
