using System.Collections.ObjectModel;

namespace PulseReader.TextPreparation;

internal static class ReadingUnitAnalyzer
{
    internal const int MaximumUnitCharacters = 600;

    private static readonly HashSet<string> CommonAbbreviations = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "dr",
        "e.g",
        "etc",
        "i.e",
        "jr",
        "mr",
        "mrs",
        "ms",
        "mx",
        "prof",
        "rev",
        "sr",
        "st",
        "vs"
    };

    private static readonly HashSet<string> ContinuationWords = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "because", "but", "by", "for", "from", "if",
        "in", "into", "of", "on", "or", "the", "that", "to", "when", "which", "while",
        "with"
    };

    public static PreparedDocument Prepare(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return PreparedDocument.Empty(text);
        }

        var lines = ReadLines(text);
        var candidates = new List<UnitCandidate>();

        for (var lineIndex = 0; lineIndex < lines.Count;)
        {
            if (lines[lineIndex].IsBlank(text))
            {
                lineIndex++;
                continue;
            }

            if (TryReadFencedCodeBlock(text, lines, ref lineIndex, out var codeBlock))
            {
                candidates.Add(codeBlock);
                continue;
            }

            var blockStart = lineIndex;
            while (lineIndex < lines.Count && !lines[lineIndex].IsBlank(text))
            {
                lineIndex++;
            }

            AnalyzeBlock(text, lines, blockStart, lineIndex, candidates);
        }

        var units = candidates
            .OrderBy(candidate => candidate.Start)
            .Select(
                (candidate, index) =>
                {
                    var range = new SourceTextRange(
                        candidate.Start,
                        candidate.End - candidate.Start);
                    var original = range.Slice(text);
                    return new ReadingUnit(
                        index,
                        range,
                        candidate.Kind,
                        candidate.Confidence,
                        original,
                        original);
                })
            .ToArray();

        return new PreparedDocument(
            text,
            new ReadOnlyCollection<ReadingUnit>(units),
            AssessComplexity(text, lines, units));
    }

    private static void AnalyzeBlock(
        string text,
        IReadOnlyList<SourceLine> lines,
        int startLine,
        int endLine,
        List<UnitCandidate> units)
    {
        if (startLine >= endLine)
        {
            return;
        }

        if (IsHeading(text, lines[startLine]))
        {
            AddTrimmedCandidate(
                text,
                lines[startLine].Start,
                lines[startLine].ContentEnd,
                ReadingUnitKind.Heading,
                ReadingBoundaryConfidence.High,
                units);
            AnalyzeBlock(text, lines, startLine + 1, endLine, units);
            return;
        }

        if (IsListMarker(text, lines[startLine]))
        {
            AnalyzeList(text, lines, startLine, endLine, units);
            return;
        }

        if (AllLinesMatch(text, lines, startLine, endLine, IsDialogueLine))
        {
            for (var index = startLine; index < endLine; index++)
            {
                AddTrimmedCandidate(
                    text,
                    lines[index].Start,
                    lines[index].ContentEnd,
                    ReadingUnitKind.Dialogue,
                    ReadingBoundaryConfidence.High,
                    units);
            }

            return;
        }

        if (LooksLikeTable(text, lines, startLine, endLine))
        {
            for (var index = startLine; index < endLine; index++)
            {
                AddTrimmedCandidate(
                    text,
                    lines[index].Start,
                    lines[index].ContentEnd,
                    ReadingUnitKind.TableRow,
                    ReadingBoundaryConfidence.High,
                    units);
            }

            return;
        }

        if (LooksStructured(text, lines, startLine, endLine))
        {
            AddTrimmedCandidate(
                text,
                lines[startLine].Start,
                lines[endLine - 1].ContentEnd,
                ReadingUnitKind.StructuredBlock,
                ReadingBoundaryConfidence.High,
                units);
            return;
        }

        if (TryAnalyzeEmbeddedMarkedList(text, lines, startLine, endLine, units)
            || TryAnalyzeEmbeddedUnmarkedList(text, lines, startLine, endLine, units))
        {
            return;
        }

        var unmarkedLayout = ClassifyUnmarkedLineLayout(text, lines, startLine, endLine);
        if (unmarkedLayout != UnmarkedLineLayout.Prose)
        {
            var confidence = unmarkedLayout == UnmarkedLineLayout.List
                ? ReadingBoundaryConfidence.High
                : ReadingBoundaryConfidence.Medium;
            for (var index = startLine; index < endLine; index++)
            {
                AddTrimmedCandidate(
                    text,
                    lines[index].Start,
                    lines[index].ContentEnd,
                    ReadingUnitKind.UnmarkedListItem,
                    confidence,
                    units);
            }

            return;
        }

        SplitProse(
            text,
            lines[startLine].Start,
            lines[endLine - 1].ContentEnd,
            units);
    }

    private static bool TryAnalyzeEmbeddedMarkedList(
        string text,
        IReadOnlyList<SourceLine> lines,
        int startLine,
        int endLine,
        List<UnitCandidate> units)
    {
        var runStart = -1;
        for (var lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            var isMarker = lineIndex < endLine && IsListMarker(text, lines[lineIndex]);
            if (isMarker)
            {
                runStart = runStart < 0 ? lineIndex : runStart;
                continue;
            }

            if (runStart < 0 || lineIndex - runStart < 2)
            {
                runStart = -1;
                continue;
            }

            if (runStart == startLine && lineIndex == endLine)
            {
                return false;
            }

            AnalyzeBlock(text, lines, startLine, runStart, units);
            AnalyzeList(text, lines, runStart, lineIndex, units);
            AnalyzeBlock(text, lines, lineIndex, endLine, units);
            return true;
        }

        return false;
    }

    private static bool TryAnalyzeEmbeddedUnmarkedList(
        string text,
        IReadOnlyList<SourceLine> lines,
        int startLine,
        int endLine,
        List<UnitCandidate> units)
    {
        var runStart = -1;
        for (var lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            var isCandidate = lineIndex < endLine
                && IsEmbeddedUnmarkedListItem(lines[lineIndex].Trimmed(text));
            if (isCandidate)
            {
                runStart = runStart < 0 ? lineIndex : runStart;
                continue;
            }

            if (runStart < 0 || lineIndex - runStart < 3)
            {
                runStart = -1;
                continue;
            }

            if (runStart == startLine && lineIndex == endLine)
            {
                return false;
            }

            AnalyzeBlock(text, lines, startLine, runStart, units);
            for (var itemIndex = runStart; itemIndex < lineIndex; itemIndex++)
            {
                AddTrimmedCandidate(
                    text,
                    lines[itemIndex].Start,
                    lines[itemIndex].ContentEnd,
                    ReadingUnitKind.UnmarkedListItem,
                    ReadingBoundaryConfidence.High,
                    units);
            }

            AnalyzeBlock(text, lines, lineIndex, endLine, units);
            return true;
        }

        return false;
    }

    private static bool IsEmbeddedUnmarkedListItem(string value) =>
        value.Length is > 0 and <= 60
        && StartsLikeIndependentItem(value)
        && !EndsWithSentencePunctuation(value)
        && !EndsWithContinuationCue(value);

    private static UnmarkedLineLayout ClassifyUnmarkedLineLayout(
        string text,
        IReadOnlyList<SourceLine> lines,
        int startLine,
        int endLine)
    {
        var lineCount = endLine - startLine;
        if (lineCount == 2)
        {
            return (LooksLikeTitleAndLabel(text, lines[startLine], lines[startLine + 1])
                    || LooksLikeTitleAndProse(text, lines[startLine], lines[startLine + 1]))
                ? UnmarkedLineLayout.List
                : UnmarkedLineLayout.Prose;
        }

        if (lineCount < 3)
        {
            return UnmarkedLineLayout.Prose;
        }

        var values = new string[lineCount];
        var shortLines = 0;
        var unpunctuatedLines = 0;
        var independentStarts = 0;
        var continuationEndings = 0;
        for (var index = 0; index < lineCount; index++)
        {
            var value = lines[startLine + index].Trimmed(text);
            values[index] = value;
            shortLines += value.Length <= 60 ? 1 : 0;
            unpunctuatedLines += EndsWithSentencePunctuation(value) ? 0 : 1;
            independentStarts += StartsLikeIndependentItem(value) ? 1 : 0;
            continuationEndings += index + 1 < lineCount && EndsWithContinuationCue(value) ? 1 : 0;
        }

        if (shortLines * 4 < lineCount * 3
            || unpunctuatedLines * 4 < lineCount * 3
            || LooksLikeHardWrappedProse(values, continuationEndings))
        {
            return UnmarkedLineLayout.Prose;
        }

        if (independentStarts * 4 >= lineCount * 3 && continuationEndings == 0)
        {
            return UnmarkedLineLayout.List;
        }

        return continuationEndings <= 1
            ? UnmarkedLineLayout.Ambiguous
            : UnmarkedLineLayout.Prose;
    }

    private static bool LooksLikeTitleAndLabel(
        string text,
        SourceLine titleLine,
        SourceLine labelLine)
    {
        var title = titleLine.Trimmed(text);
        return title.Length is > 0 and <= 100
            && StartsLikeIndependentItem(title)
            && !EndsWithSentencePunctuation(title)
            && !EndsWithContinuationCue(title)
            && IsDialogueLine(text, labelLine);
    }

    private static bool LooksLikeTitleAndProse(
        string text,
        SourceLine titleLine,
        SourceLine proseLine)
    {
        var title = titleLine.Trimmed(text);
        var prose = proseLine.Trimmed(text);
        return title.Length is > 0 and <= 80
            && prose.Length >= 80
            && prose.Length >= title.Length * 2
            && StartsLikeIndependentItem(title)
            && StartsLikeIndependentItem(prose)
            && !EndsWithSentencePunctuation(title)
            && !EndsWithContinuationCue(title)
            && EndsWithSentencePunctuation(prose);
    }

    private static bool StartsLikeIndependentItem(string value)
    {
        var index = 0;
        while (index < value.Length && value[index] is '"' or '\'' or '(' or '[')
        {
            index++;
        }

        return index < value.Length
            && (char.IsUpper(value[index]) || char.IsDigit(value[index]));
    }

    private static bool EndsWithContinuationCue(string value)
    {
        var trimmed = value.TrimEnd();
        if (trimmed.Length == 0
            || trimmed[^1] is ',' or ';' or ':' or '-' or '\u2013' or '\u2014' or '/' or '(')
        {
            return true;
        }

        var wordEnd = trimmed.Length;
        var wordStart = wordEnd;
        while (wordStart > 0 && char.IsLetter(trimmed[wordStart - 1]))
        {
            wordStart--;
        }

        return ContinuationWords.Contains(trimmed[wordStart..wordEnd]);
    }

    private static bool LooksLikeHardWrappedProse(
        IReadOnlyList<string> lines,
        int continuationEndings)
    {
        if (continuationEndings * 2 >= lines.Count - 1)
        {
            return true;
        }

        var wrappingLines = lines.Take(lines.Count - 1).Select(value => value.Length).ToArray();
        if (wrappingLines.Length < 2 || wrappingLines.Min() < 45)
        {
            return false;
        }

        return wrappingLines.Max() - wrappingLines.Min() <= 12;
    }

    private static bool EndsWithSentencePunctuation(string value)
    {
        var trimmed = value.TrimEnd('"', '\'', '\u2019', '\u201d', ')', ']', '}');
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?');
    }

    private static void AnalyzeList(
        string text,
        IReadOnlyList<SourceLine> lines,
        int startLine,
        int endLine,
        List<UnitCandidate> units)
    {
        var itemStart = startLine;
        for (var index = startLine + 1; index < endLine; index++)
        {
            if (!IsListMarker(text, lines[index]))
            {
                continue;
            }

            AddTrimmedCandidate(
                text,
                lines[itemStart].Start,
                lines[index - 1].ContentEnd,
                ReadingUnitKind.ListItem,
                ReadingBoundaryConfidence.High,
                units);
            itemStart = index;
        }

        AddTrimmedCandidate(
            text,
            lines[itemStart].Start,
            lines[endLine - 1].ContentEnd,
            ReadingUnitKind.ListItem,
            ReadingBoundaryConfidence.High,
            units);
    }

    private static void SplitProse(
        string text,
        int start,
        int end,
        List<UnitCandidate> units)
    {
        var unitStart = start;
        var foundSentence = false;

        for (var index = start; index < end; index++)
        {
            if (text[index] is not ('.' or '!' or '?'))
            {
                continue;
            }

            var punctuationEnd = ConsumeSentenceEnding(text, index, end);
            if (!IsSentenceBoundary(text, index, punctuationEnd, end))
            {
                index = punctuationEnd - 1;
                continue;
            }

            AddLengthBoundedCandidates(
                text,
                unitStart,
                punctuationEnd,
                ReadingUnitKind.Sentence,
                ReadingBoundaryConfidence.High,
                units);
            unitStart = punctuationEnd;
            index = punctuationEnd - 1;
            foundSentence = true;
        }

        if (ContainsNonWhitespace(text, unitStart, end))
        {
            AddLengthBoundedCandidates(
                text,
                unitStart,
                end,
                foundSentence ? ReadingUnitKind.Sentence : ReadingUnitKind.Paragraph,
                foundSentence
                    ? ReadingBoundaryConfidence.Medium
                    : ReadingBoundaryConfidence.Low,
                units);
        }
    }

    private static void AddLengthBoundedCandidates(
        string text,
        int start,
        int end,
        ReadingUnitKind kind,
        ReadingBoundaryConfidence confidence,
        List<UnitCandidate> units)
    {
        TrimRange(text, ref start, ref end);
        while (end - start > MaximumUnitCharacters)
        {
            var preferredEnd = Math.Min(end, start + MaximumUnitCharacters);
            var split = FindFallbackSplit(text, start, preferredEnd);
            AddTrimmedCandidate(
                text,
                start,
                split,
                kind,
                ReadingBoundaryConfidence.Low,
                units);
            start = split;
            TrimRange(text, ref start, ref end);
        }

        AddTrimmedCandidate(text, start, end, kind, confidence, units);
    }

    private static int FindFallbackSplit(string text, int start, int preferredEnd)
    {
        var minimumSplit = start + MaximumUnitCharacters / 2;
        for (var index = preferredEnd; index > minimumSplit; index--)
        {
            if (text[index - 1] is '\r' or '\n')
            {
                return index;
            }
        }

        for (var index = preferredEnd; index > minimumSplit; index--)
        {
            if (char.IsWhiteSpace(text[index - 1]))
            {
                return index;
            }
        }

        return preferredEnd;
    }

    private static int ConsumeSentenceEnding(string text, int punctuationStart, int end)
    {
        var index = punctuationStart + 1;
        while (index < end && text[index] is '.' or '!' or '?')
        {
            index++;
        }

        while (index < end && text[index] is '"' or '\'' or '\u2019' or '\u201d' or ')' or ']' or '}')
        {
            index++;
        }

        return index;
    }

    private static bool IsSentenceBoundary(
        string text,
        int punctuationStart,
        int punctuationEnd,
        int blockEnd)
    {
        if (punctuationEnd < blockEnd && !char.IsWhiteSpace(text[punctuationEnd]))
        {
            return false;
        }

        if (text[punctuationStart] != '.')
        {
            return true;
        }

        if (punctuationStart > 0
            && punctuationStart + 1 < text.Length
            && char.IsDigit(text[punctuationStart - 1])
            && char.IsDigit(text[punctuationStart + 1]))
        {
            return false;
        }

        var tokenStart = punctuationStart;
        while (tokenStart > 0 && IsAbbreviationCharacter(text[tokenStart - 1]))
        {
            tokenStart--;
        }

        var token = text[tokenStart..punctuationStart].Trim('.');
        if (CommonAbbreviations.Contains(token))
        {
            return false;
        }

        if (token.Length == 1 && char.IsUpper(token[0]))
        {
            return false;
        }

        return !token.Contains('.');
    }

    private static bool IsAbbreviationCharacter(char value) =>
        char.IsLetter(value) || value == '.';

    private static bool TryReadFencedCodeBlock(
        string text,
        IReadOnlyList<SourceLine> lines,
        ref int lineIndex,
        out UnitCandidate candidate)
    {
        candidate = default;
        var opening = lines[lineIndex].Trimmed(text);
        var marker = opening.StartsWith("```", StringComparison.Ordinal)
            ? "```"
            : opening.StartsWith("~~~", StringComparison.Ordinal)
                ? "~~~"
                : null;
        if (marker is null)
        {
            return false;
        }

        var startLine = lineIndex++;
        while (lineIndex < lines.Count)
        {
            var current = lines[lineIndex];
            lineIndex++;
            if (current.Trimmed(text).StartsWith(marker, StringComparison.Ordinal))
            {
                break;
            }
        }

        candidate = new UnitCandidate(
            lines[startLine].Start,
            lines[lineIndex - 1].ContentEnd,
            ReadingUnitKind.CodeBlock,
            ReadingBoundaryConfidence.High);
        return true;
    }

    private static bool IsHeading(string text, SourceLine line)
    {
        var value = line.Trimmed(text);
        if (value.Length == 0 || value.Length > 100)
        {
            return false;
        }

        if (value[0] == '#')
        {
            var markerLength = 0;
            while (markerLength < value.Length && value[markerLength] == '#')
            {
                markerLength++;
            }

            return markerLength is >= 1 and <= 6
                && markerLength < value.Length
                && char.IsWhiteSpace(value[markerLength]);
        }

        var letters = value.Where(char.IsLetter).ToArray();
        if (letters.Length >= 2 && letters.All(char.IsUpper))
        {
            return true;
        }

        return false;
    }

    private static bool IsListMarker(string text, SourceLine line)
    {
        var value = line.Trimmed(text);
        if (value.StartsWith("- ", StringComparison.Ordinal)
            || value.StartsWith("* ", StringComparison.Ordinal)
            || value.StartsWith("+ ", StringComparison.Ordinal)
            || value.StartsWith("\u2022 ", StringComparison.Ordinal)
            || value.StartsWith("\u00b7 ", StringComparison.Ordinal))
        {
            return true;
        }

        var index = 0;
        while (index < value.Length && char.IsDigit(value[index]))
        {
            index++;
        }

        return index > 0
            && index + 1 < value.Length
            && value[index] is '.' or ')'
            && char.IsWhiteSpace(value[index + 1]);
    }

    private static bool IsDialogueLine(string text, SourceLine line)
    {
        var value = line.Trimmed(text);
        if (value.StartsWith("> ", StringComparison.Ordinal))
        {
            return true;
        }

        var colon = value.IndexOf(':');
        if (colon is <= 0 or > 32 || colon + 1 >= value.Length || !char.IsWhiteSpace(value[colon + 1]))
        {
            return false;
        }

        return value[..colon].All(
            character => char.IsLetter(character)
                || char.IsWhiteSpace(character)
                || character is '\'' or '-' or '.');
    }

    private static bool LooksStructured(
        string text,
        IReadOnlyList<SourceLine> lines,
        int startLine,
        int endLine)
    {
        var score = 0;
        for (var index = startLine; index < endLine; index++)
        {
            var value = lines[index].Trimmed(text);
            var pipeCount = value.Count(character => character == '|');
            if (pipeCount >= 2)
            {
                score += 3;
            }

            if (value is "{" or "}" or "[" or "]"
                || value.EndsWith(';')
                || value.Contains("=>", StringComparison.Ordinal)
                || value.Contains("\":", StringComparison.Ordinal)
                || value.StartsWith("at ", StringComparison.Ordinal)
                || LooksLikeTimestamp(value))
            {
                score += 2;
            }

            if (SymbolDensity(value) >= 0.20)
            {
                score++;
            }
        }

        var lineCount = endLine - startLine;
        return score >= Math.Max(3, lineCount);
    }

    private static bool LooksLikeTable(
        string text,
        IReadOnlyList<SourceLine> lines,
        int startLine,
        int endLine)
    {
        if (endLine - startLine < 2)
        {
            return false;
        }

        var tableLines = 0;
        for (var index = startLine; index < endLine; index++)
        {
            if (lines[index].Trimmed(text).Count(character => character == '|') >= 2)
            {
                tableLines++;
            }
        }

        return tableLines >= Math.Max(2, endLine - startLine - 1);
    }

    private static bool LooksLikeTimestamp(string value)
    {
        if (value.Length < 8)
        {
            return false;
        }

        return char.IsDigit(value[0])
            && char.IsDigit(value[1])
            && value[2] == ':'
            && char.IsDigit(value[3])
            && char.IsDigit(value[4])
            && value[5] == ':'
            && char.IsDigit(value[6])
            && char.IsDigit(value[7]);
    }

    private static bool AllLinesMatch(
        string text,
        IReadOnlyList<SourceLine> lines,
        int startLine,
        int endLine,
        Func<string, SourceLine, bool> predicate)
    {
        for (var index = startLine; index < endLine; index++)
        {
            if (!predicate(text, lines[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static TextComplexityAssessment AssessComplexity(
        string text,
        IReadOnlyList<SourceLine> lines,
        IReadOnlyList<ReadingUnit> units)
    {
        var score = 0;
        var signals = new List<TextComplexitySignal>();

        if (units.Any(
                unit => unit.Kind is ReadingUnitKind.CodeBlock
                    or ReadingUnitKind.StructuredBlock
                    or ReadingUnitKind.TableRow))
        {
            AddSignal(TextComplexitySignal.StructuredContent, 35);
        }

        var nonWhitespace = text.Count(character => !char.IsWhiteSpace(character));
        if (nonWhitespace > 0 && SymbolDensity(text) >= 0.15)
        {
            AddSignal(TextComplexitySignal.DenseSymbols, 20);
        }

        if (lines.Any(
                line => line.ContentEnd - line.Start >= 180
                    && line.Trimmed(text).IndexOfAny(['.', '!', '?']) < 0))
        {
            AddSignal(TextComplexitySignal.LongUnpunctuatedLine, 25);
        }

        if (units.Select(unit => unit.Kind).Distinct().Count() >= 3)
        {
            AddSignal(TextComplexitySignal.MixedLayout, 15);
        }

        var linkCount = CountOccurrences(text, "://") + CountOccurrences(text, "www.");
        if (linkCount >= 2)
        {
            AddSignal(TextComplexitySignal.MultipleLinks, 10);
        }

        score = Math.Min(score, 100);
        var level = score >= 50
            ? TextComplexityLevel.Complex
            : score >= 25
                ? TextComplexityLevel.Moderate
                : TextComplexityLevel.Simple;
        return new TextComplexityAssessment(score, level, signals.AsReadOnly());

        void AddSignal(TextComplexitySignal signal, int weight)
        {
            signals.Add(signal);
            score += weight;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static double SymbolDensity(string value)
    {
        var nonWhitespace = 0;
        var symbols = 0;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            nonWhitespace++;
            if (!char.IsLetterOrDigit(character)
                && character is not ('.' or ',' or '!' or '?' or '\'' or '"'))
            {
                symbols++;
            }
        }

        return nonWhitespace == 0 ? 0 : (double)symbols / nonWhitespace;
    }

    private static List<SourceLine> ReadLines(string text)
    {
        var lines = new List<SourceLine>();
        for (var start = 0; start < text.Length;)
        {
            var contentEnd = start;
            while (contentEnd < text.Length && text[contentEnd] is not ('\r' or '\n'))
            {
                contentEnd++;
            }

            var end = contentEnd;
            if (end < text.Length && text[end] == '\r')
            {
                end++;
            }

            if (end < text.Length && text[end] == '\n')
            {
                end++;
            }

            lines.Add(new SourceLine(start, contentEnd, end));
            start = end;
        }

        return lines;
    }

    private static void AddTrimmedCandidate(
        string text,
        int start,
        int end,
        ReadingUnitKind kind,
        ReadingBoundaryConfidence confidence,
        List<UnitCandidate> units)
    {
        TrimRange(text, ref start, ref end);
        if (start < end)
        {
            units.Add(new UnitCandidate(start, end, kind, confidence));
        }
    }

    private static void TrimRange(string text, ref int start, ref int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
    }

    private static bool ContainsNonWhitespace(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct SourceLine(int Start, int ContentEnd, int End)
    {
        public bool IsBlank(string text) =>
            string.IsNullOrWhiteSpace(text[Start..ContentEnd]);

        public string Trimmed(string text) => text[Start..ContentEnd].Trim();
    }

    private readonly record struct UnitCandidate(
        int Start,
        int End,
        ReadingUnitKind Kind,
        ReadingBoundaryConfidence Confidence);

    private enum UnmarkedLineLayout
    {
        Prose,
        List,
        Ambiguous
    }
}
