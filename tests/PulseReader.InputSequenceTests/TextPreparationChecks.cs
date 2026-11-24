using System.Text.Json;
using PulseReader.TextPreparation;

internal static class TextPreparationChecks
{
    public static void Run()
    {
        var corpusPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "text-preparation-corpus.json");
        var corpus = JsonSerializer.Deserialize<CorpusCase[]>(
            File.ReadAllText(corpusPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The text-preparation corpus could not be loaded.");

        Assert(corpus.Length >= 7, "The text-preparation corpus is unexpectedly small.");
        foreach (var example in corpus)
        {
            CheckCorpusExample(example);
        }

        CheckLongUnpunctuatedFallback();
        CheckEmptyInput();

        Console.WriteLine(
            $"PASS: deterministic text preparation preserves source mapping across "
            + $"{corpus.Length} representative document types.");
    }

    private static void CheckCorpusExample(CorpusCase example)
    {
        var prepared = ReadingUnitAnalyzer.Prepare(example.Text);
        var repeated = ReadingUnitAnalyzer.Prepare(example.Text);
        var expectedKinds = example.ExpectedKinds
            .Select(value => Enum.Parse<ReadingUnitKind>(value, ignoreCase: true))
            .ToArray();
        var minimumComplexity = Enum.Parse<TextComplexityLevel>(
            example.MinimumComplexity,
            ignoreCase: true);

        Assert(
            prepared.Units.Select(unit => unit.Kind).SequenceEqual(expectedKinds),
            $"Corpus case '{example.Id}' produced unexpected reading-unit kinds: "
            + string.Join(", ", prepared.Units.Select(unit => unit.Kind)));
        Assert(
            prepared.Units.Select(unit => unit.OriginalText).SequenceEqual(example.ExpectedTexts),
            $"Corpus case '{example.Id}' assigned unexpected source text to its reading units.");
        Assert(
            prepared.Complexity.Level >= minimumComplexity,
            $"Corpus case '{example.Id}' was assessed as {prepared.Complexity.Level}, "
            + $"below the expected {minimumComplexity} level.");
        AssertSourceMapping(prepared, example.Id);
        AssertEquivalent(prepared, repeated, example.Id);
    }

    private static void CheckLongUnpunctuatedFallback()
    {
        var text = string.Join(' ', Enumerable.Repeat("unpunctuated-navigation-content", 60));
        var prepared = ReadingUnitAnalyzer.Prepare(text);

        Assert(
            prepared.Units.Count > 1,
            "Long unpunctuated text did not receive fallback navigation boundaries.");
        Assert(
            prepared.Units.All(unit => unit.OriginalText.Length <= ReadingUnitAnalyzer.MaximumUnitCharacters),
            "A long fallback reading unit exceeded the configured character limit.");
        Assert(
            prepared.Units.All(unit => unit.BoundaryConfidence == ReadingBoundaryConfidence.Low),
            "Fallback boundaries were not marked as low confidence.");
        Assert(
            prepared.Complexity.Signals.Contains(TextComplexitySignal.LongUnpunctuatedLine),
            "Long unpunctuated text did not report its complexity signal.");
        AssertSourceMapping(prepared, "long-unpunctuated");
    }

    private static void CheckEmptyInput()
    {
        foreach (var text in new[] { string.Empty, " \r\n\t " })
        {
            var prepared = ReadingUnitAnalyzer.Prepare(text);
            Assert(prepared.OriginalText == text, "Empty preparation did not preserve its source text.");
            Assert(prepared.Units.Count == 0, "Whitespace-only input created a reading unit.");
            Assert(
                prepared.Complexity == TextComplexityAssessment.Simple,
                "Whitespace-only input was not assessed as simple.");
        }
    }

    private static void AssertSourceMapping(PreparedDocument prepared, string id)
    {
        var covered = new bool[prepared.OriginalText.Length];
        var previousEnd = 0;
        for (var index = 0; index < prepared.Units.Count; index++)
        {
            var unit = prepared.Units[index];
            Assert(unit.Index == index, $"Corpus case '{id}' has a non-sequential unit index.");
            Assert(
                unit.SourceRange.Start >= previousEnd,
                $"Corpus case '{id}' contains overlapping or reordered source ranges.");
            Assert(
                unit.SourceRange.Start >= 0
                    && unit.SourceRange.End <= prepared.OriginalText.Length,
                $"Corpus case '{id}' contains an out-of-range source span.");
            Assert(
                unit.OriginalText == unit.SourceRange.Slice(prepared.OriginalText),
                $"Corpus case '{id}' lost its exact source mapping.");
            Assert(
                unit.SpokenText == unit.OriginalText,
                $"Corpus case '{id}' changed spoken text before formatting is enabled.");

            for (var sourceIndex = unit.SourceRange.Start;
                 sourceIndex < unit.SourceRange.End;
                 sourceIndex++)
            {
                Assert(!covered[sourceIndex], $"Corpus case '{id}' maps one source character twice.");
                covered[sourceIndex] = true;
            }

            previousEnd = unit.SourceRange.End;
        }

        for (var index = 0; index < prepared.OriginalText.Length; index++)
        {
            Assert(
                char.IsWhiteSpace(prepared.OriginalText[index]) || covered[index],
                $"Corpus case '{id}' left source content outside every reading unit.");
        }
    }

    private static void AssertEquivalent(
        PreparedDocument first,
        PreparedDocument second,
        string id)
    {
        Assert(
            first.Complexity.Score == second.Complexity.Score
                && first.Complexity.Level == second.Complexity.Level
                && first.Complexity.Signals.SequenceEqual(second.Complexity.Signals),
            $"Corpus case '{id}' produced a non-deterministic complexity assessment.");
        Assert(
            first.Units.Count == second.Units.Count,
            $"Corpus case '{id}' produced a non-deterministic unit count.");

        for (var index = 0; index < first.Units.Count; index++)
        {
            Assert(
                first.Units[index] == second.Units[index],
                $"Corpus case '{id}' produced a non-deterministic reading unit.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record CorpusCase(
        string Id,
        string Text,
        string[] ExpectedKinds,
        string[] ExpectedTexts,
        string MinimumComplexity);
}
