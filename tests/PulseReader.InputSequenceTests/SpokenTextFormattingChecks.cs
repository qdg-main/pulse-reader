using PulseReader.TextPreparation;

internal static class SpokenTextFormattingChecks
{
    public static void Run()
    {
        var acceptedCorpus = SpokenTextAbCorpus.LoadAccepted();
        var edgeCorpus = SpokenTextAbCorpus.LoadEdgeCases();
        var lineBoundaryCorpus = SpokenTextAbCorpus.LoadLineBoundaries();
        var corpus = acceptedCorpus.Concat(edgeCorpus).Concat(lineBoundaryCorpus).ToArray();
        Assert(corpus.Length >= 16, "The accepted spoken-text policy corpus is unexpectedly small.");
        Assert(
            corpus.Select(example => example.Id).Distinct(StringComparer.Ordinal).Count() == corpus.Length,
            "The spoken-text A/B corpus contains duplicate identifiers.");

        var changed = 0;
        var unchanged = 0;
        foreach (var example in corpus)
        {
            var document = ReadingUnitAnalyzer.Prepare(example.OriginalText);
            var formatted = SpokenTextFormatter.Format(document);
            var spokenText = formatted.BuildSpokenText();
            var wasChanged = !string.Equals(
                example.OriginalText,
                spokenText,
                StringComparison.Ordinal);

            Assert(
                spokenText == example.ExpectedPreparedText,
                $"A/B case '{example.Id}' produced unexpected prepared text."
                + $"{Environment.NewLine}Expected: {example.ExpectedPreparedText}"
                + $"{Environment.NewLine}Actual:   {spokenText}");
            Assert(
                wasChanged == example.ExpectChange,
                $"A/B case '{example.Id}' did not match its expected change state.");
            Assert(
                formatted.BuildSpeechChunks().Sum(chunk => chunk.PauseAfterMilliseconds)
                    == example.ExpectedPauseMilliseconds,
                $"A/B case '{example.Id}' produced unexpected explicit pause timing.");
            AssertSourceIdentity(document, formatted, example.Id);

            if (wasChanged)
            {
                changed++;
            }
            else
            {
                unchanged++;
            }
        }

        Assert(changed > 0, "The accepted policy corpus contains no prepared-text cases.");
        Assert(unchanged > 0, "The accepted policy corpus contains no preserve-original cases.");
        Console.WriteLine(
            $"PASS: spoken-text preparation matches {acceptedCorpus.Count} accepted and "
            + $"{edgeCorpus.Count} edge-case and {lineBoundaryCorpus.Count} line-boundary "
            + "policy expectations."
            + $" ({changed} prepared, {unchanged} preserved.)");
    }

    private static void AssertSourceIdentity(
        PreparedDocument original,
        PreparedDocument formatted,
        string id)
    {
        Assert(
            ReferenceEquals(original.OriginalText, formatted.OriginalText)
                || original.OriginalText == formatted.OriginalText,
            $"A/B case '{id}' changed the source document.");
        Assert(
            original.Units.Count == formatted.Units.Count,
            $"A/B case '{id}' changed the reading-unit count.");

        for (var index = 0; index < original.Units.Count; index++)
        {
            Assert(
                original.Units[index].SourceRange == formatted.Units[index].SourceRange
                    && original.Units[index].OriginalText == formatted.Units[index].OriginalText
                    && original.Units[index].Kind == formatted.Units[index].Kind,
                $"A/B case '{id}' changed navigation or source mapping.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
