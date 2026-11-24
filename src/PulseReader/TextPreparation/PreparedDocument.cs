using System.Text;

namespace PulseReader.TextPreparation;

internal sealed record PreparedDocument(
    string OriginalText,
    IReadOnlyList<ReadingUnit> Units,
    TextComplexityAssessment Complexity)
{
    public static PreparedDocument Empty(string originalText) =>
        new(originalText, Array.Empty<ReadingUnit>(), TextComplexityAssessment.Simple);

    public string BuildSpokenText()
    {
        if (Units.Count == 0)
        {
            return OriginalText;
        }

        var result = new StringBuilder(OriginalText.Length);
        var sourcePosition = 0;
        ReadingUnit? previousUnit = null;
        foreach (var unit in Units)
        {
            AppendSourceSeparator(result, sourcePosition, unit.SourceRange.Start, previousUnit, unit);
            result.Append(unit.SpokenText);
            sourcePosition = unit.SourceRange.End;
            previousUnit = unit;
        }

        result.Append(OriginalText, sourcePosition, OriginalText.Length - sourcePosition);
        return result.ToString();
    }

    public IReadOnlyList<PreparedSpeechChunk> BuildSpeechChunks()
        => BuildNavigableSpeechChunks()
            .Select(chunk => new PreparedSpeechChunk(chunk.Text, chunk.PauseAfterMilliseconds))
            .ToArray();

    public IReadOnlyList<PreparedNavigableSpeechChunk> BuildNavigableSpeechChunks()
    {
        if (Units.Count == 0)
        {
            var text = BuildSpokenText();
            return
            [
                new PreparedNavigableSpeechChunk(
                    text,
                    0,
                    string.IsNullOrWhiteSpace(text)
                        ? Array.Empty<PreparedNavigationUnit>()
                        : [new PreparedNavigationUnit(0, 0, text.Length)])
            ];
        }

        var chunks = new List<PreparedNavigableSpeechChunk>();
        var current = new StringBuilder(OriginalText.Length);
        var currentUnits = new List<PreparedNavigationUnit>();
        var sourcePosition = 0;
        ReadingUnit? previousUnit = null;
        foreach (var unit in Units)
        {
            AppendSourceSeparator(current, sourcePosition, unit.SourceRange.Start, previousUnit, unit);
            var spokenStart = current.Length;
            current.Append(unit.SpokenText);
            currentUnits.Add(
                new PreparedNavigationUnit(
                    unit.Index,
                    spokenStart,
                    unit.SpokenText.Length));
            sourcePosition = unit.SourceRange.End;
            previousUnit = unit;

            if (unit.PauseAfterMilliseconds <= 0)
            {
                continue;
            }

            AddChunk(chunks, current, currentUnits, unit.PauseAfterMilliseconds);
        }

        current.Append(OriginalText, sourcePosition, OriginalText.Length - sourcePosition);
        AddChunk(chunks, current, currentUnits, 0);
        return chunks;
    }

    private void AppendSourceSeparator(
        StringBuilder result,
        int start,
        int end,
        ReadingUnit? previousUnit,
        ReadingUnit currentUnit)
    {
        var separator = OriginalText[start..end];
        if (previousUnit?.Kind == ReadingUnitKind.UnmarkedListItem
            && currentUnit.Kind == ReadingUnitKind.UnmarkedListItem
            && IsSingleWhitespaceLineBreak(separator))
        {
            result.Append(' ');
            return;
        }

        result.Append(separator);
    }

    private static bool IsSingleWhitespaceLineBreak(string value)
    {
        if (value.Length == 0 || value.Any(character => !char.IsWhiteSpace(character)))
        {
            return false;
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.Count(character => character is '\r' or '\n') == 1;
    }

    private static void AddChunk(
        List<PreparedNavigableSpeechChunk> chunks,
        StringBuilder current,
        List<PreparedNavigationUnit> currentUnits,
        int pauseAfterMilliseconds)
    {
        var text = current.ToString();
        current.Clear();
        if (!string.IsNullOrWhiteSpace(text))
        {
            chunks.Add(
                new PreparedNavigableSpeechChunk(
                    text,
                    pauseAfterMilliseconds,
                    currentUnits.ToArray()));
        }

        currentUnits.Clear();
    }
}

internal sealed record PreparedSpeechChunk(string Text, int PauseAfterMilliseconds);

internal sealed record PreparedNavigableSpeechChunk(
    string Text,
    int PauseAfterMilliseconds,
    IReadOnlyList<PreparedNavigationUnit> Units);

internal readonly record struct PreparedNavigationUnit(
    int SourceUnitIndex,
    int SpokenStart,
    int SpokenLength);

internal sealed record ReadingUnit(
    int Index,
    SourceTextRange SourceRange,
    ReadingUnitKind Kind,
    ReadingBoundaryConfidence BoundaryConfidence,
    string OriginalText,
    string SpokenText)
{
    public int PauseAfterMilliseconds { get; init; }
}

internal readonly record struct SourceTextRange(int Start, int Length)
{
    public int End => Start + Length;

    public string Slice(string text) => text.Substring(Start, Length);
}

internal enum ReadingUnitKind
{
    Sentence,
    Paragraph,
    Heading,
    ListItem,
    UnmarkedListItem,
    Dialogue,
    TableRow,
    CodeBlock,
    StructuredBlock
}

internal enum ReadingBoundaryConfidence
{
    Low,
    Medium,
    High
}

internal sealed record TextComplexityAssessment(
    int Score,
    TextComplexityLevel Level,
    IReadOnlyList<TextComplexitySignal> Signals)
{
    public static TextComplexityAssessment Simple { get; } =
        new(0, TextComplexityLevel.Simple, Array.Empty<TextComplexitySignal>());
}

internal enum TextComplexityLevel
{
    Simple,
    Moderate,
    Complex
}

internal enum TextComplexitySignal
{
    StructuredContent,
    DenseSymbols,
    LongUnpunctuatedLine,
    MixedLayout,
    MultipleLinks
}
