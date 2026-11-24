using PulseReader.TextPreparation;

internal static class SpokenTextRegressionChecks
{
    public static void Run()
    {
        var failures = new List<string>();

        CheckPreparedText(
            "The expression is x = 10. Open https://example.com?a=1&b=2.",
            "The expression is x equals 10. Open https colon example.com question mark a equals 1 and b equals 2.",
            "Math preparation escaped the URL-specific literal formatting boundary.",
            failures);
        CheckPreparedText(
            @"The model is stored at C:\Users\Reader\Models\kokoro.onnx.",
            @"The model is stored at C colon \Users\Reader\Models\kokoro dot onnx.",
            "The Windows drive-letter colon was not made audible.",
            failures);
        CheckPreparedText(
            "12:04:09 Starting synthesis",
            "twelve oh four oh nine Starting synthesis",
            "Full timestamp preparation lost or split one of its digit groups.",
            failures);

        var prepared = SpokenTextFormatter.Format(
            ReadingUnitAnalyzer.Prepare(
                "Wait...\n\nIs the next voice ready? Yes, it is."));
        var productionChunks = prepared.BuildSpeechChunks();
        var evaluationChunks = TextPreparationAbGenerator.BuildPreparedSynthesisChunks(prepared);
        if (!productionChunks.SequenceEqual(evaluationChunks))
        {
            failures.Add("The A/B generator does not use the production speech chunks.");
        }

        var lineBoundaryCases = SpokenTextAbCorpus.LoadLineBoundaries();
        if (lineBoundaryCases.Any(example => example.BaselineText is null))
        {
            failures.Add("Every line-boundary A/B case must record its previous-production baseline.");
        }

        var lineBoundaryCase = lineBoundaryCases
            .Single(example => example.Id == "numeric-unmarked-list");
        const string expectedBaseline =
            "10 voices available 2 backends installed 1 hotkey configured 0 errors reported";
        var actualBaseline = TextPreparationAbGenerator.BuildOriginalSynthesisText(lineBoundaryCase);
        if (!string.Equals(expectedBaseline, actualBaseline, StringComparison.Ordinal))
        {
            failures.Add(
                "The line-boundary A/B baseline sent raw physical newlines to Kokoro instead "
                + "of reproducing the previous joined-line production behaviour.");
        }

        var compactList = SpokenTextFormatter.Format(
            ReadingUnitAnalyzer.Prepare(
                "10 voices available\n2 backends installed\n1 hotkey configured\n0 errors reported"));
        var compactListChunks = compactList.BuildSpeechChunks();
        const string expectedCompactList =
            "10 voices available. 2 backends installed. 1 hotkey configured. 0 errors reported.";
        if (compactListChunks.Count != 1
            || !string.Equals(compactListChunks[0].Text, expectedCompactList, StringComparison.Ordinal)
            || compactListChunks[0].PauseAfterMilliseconds != 0)
        {
            failures.Add(
                "Prepared unmarked lists must use one space-separated synthesis chunk without "
                + "an added explicit pause.");
        }

        var address = SpokenTextFormatter.Format(
                ReadingUnitAnalyzer.Prepare(
                    "Pulse Reader House\n14 Market Street\nLondon\nSW1A 1AA"))
            .BuildSpokenText();
        const string expectedAddress =
            "Pulse Reader House. 14 Market Street. London. SW1A 1AA";
        if (!string.Equals(address, expectedAddress, StringComparison.Ordinal))
        {
            failures.Add(
                "Address preparation must preserve the terminal postcode without appending "
                + "sentence punctuation."
                + $"{Environment.NewLine}Expected: {expectedAddress}"
                + $"{Environment.NewLine}Actual:   {address}");
        }

        CheckPreparedText(
            "Pulse Reader keeps the selected wording intact while preparing\n"
            + "the text for natural local speech and responsive playback in\n"
            + "the background utility.",
            "Pulse Reader keeps the selected wording intact while preparing "
            + "the text for natural local speech and responsive playback in "
            + "the background utility.",
            "Long wrapped prose was mistaken for an unmarked list.",
            failures);
        CheckPreparedText(
            "The backend is ready.\nThe voice is loaded.\nPlayback can begin.",
            "The backend is ready.\nThe voice is loaded.\nPlayback can begin.",
            "Already punctuated lines were altered by the unmarked-list policy.",
            failures);
        CheckPreparedText(
            "CASE 01 - Natural prose and sentence endings\n"
            + "Listen for: smooth sentence boundaries, contractions, an abbreviation, and quoted speech.",
            "CASE 01 - Natural prose and sentence endings. "
            + "Listen for: smooth sentence boundaries, contractions, an abbreviation, and quoted speech.",
            "A title followed by a labelled note was mistaken for visually wrapped prose.",
            failures);
        CheckPreparedText(
            "Unity 6, the SDK, and VCC\n"
            + "We’re hard at work on the Unity 6 SDK and VCC support! As of right now everything "
            + "is going through QA to make sure creators get a smooth upgrade experience.",
            "Unity 6, the SDK, and VCC. We're hard at work on the Unity 6 SDK and VCC support! "
            + "As of right now everything is going through QA to make sure creators get a smooth "
            + "upgrade experience.",
            "A short title-like line was merged into the longer prose paragraph beneath it.",
            failures);
        CheckPreparedText(
            "Paused navigation remains paused, Windows playback shows navigation controls disabled, "
            + "and normal Kokoro synthesis retains its existing segmentation and voice quality.\n"
            + "Validation passed:\n"
            + "Release build: 0 warnings/errors\n"
            + "Full regression suite\n"
            + "Live Kokoro next/restart/pause smoke tests\n"
            + "Updated island rendering checks\n"
            + "Pulse Reader is currently running from the Release build as PID 62428. "
            + "No commit has been made yet.",
            "Paused navigation remains paused, Windows playback shows navigation controls disabled, "
            + "and normal Kokoro synthesis retains its existing segmentation and voice quality.\n"
            + "Validation passed:\n"
            + "Release build: 0 warnings/errors. Full regression suite. "
            + "Live Kokoro next/restart/pause smoke tests. Updated island rendering checks.\n"
            + "Pulse Reader is currently running from the Release build as PID 62428. "
            + "No commit has been made yet.",
            "Surrounding prose changed an embedded unmarked list into one merged sentence.",
            failures);
        CheckPreparedText(
            "Paused navigation remains paused, Windows playback shows navigation controls disabled, "
            + "and normal Kokoro synthesis retains its existing segmentation and voice quality.\n"
            + "Validation passed:\n"
            + "- Release build: 0 warnings/errors\n"
            + "- Full regression suite\n"
            + "- Live Kokoro next/restart/pause smoke tests\n"
            + "- Updated island rendering checks\n"
            + "Pulse Reader is currently running from the Release build as PID 62428. "
            + "No commit has been made yet.",
            "Paused navigation remains paused, Windows playback shows navigation controls disabled, "
            + "and normal Kokoro synthesis retains its existing segmentation and voice quality.\n"
            + "Validation passed:\n"
            + "Release build: 0 warnings/errors\n"
            + "Full regression suite\n"
            + "Live Kokoro next/restart/pause smoke tests\n"
            + "Updated island rendering checks\n"
            + "Pulse Reader is currently running from the Release build as PID 62428. "
            + "No commit has been made yet.",
            "Surrounding prose prevented an embedded marked list from retaining its line boundaries.",
            failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }

        Console.WriteLine(
            "PASS: spoken-text regressions scope URL punctuation, preserve timestamp groups, "
            + "keep A/B synthesis aligned with production chunking, and preserve explicit "
            + "line-boundary baselines.");
    }

    private static void CheckPreparedText(
        string original,
        string expected,
        string failure,
        List<string> failures)
    {
        var actual = SpokenTextFormatter.Format(
                ReadingUnitAnalyzer.Prepare(original))
            .BuildSpokenText();
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            failures.Add($"{failure}{Environment.NewLine}Expected: {expected}{Environment.NewLine}Actual:   {actual}");
        }
    }
}
