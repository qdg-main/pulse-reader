using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PulseReader.TextPreparation;

internal static partial class SpokenTextFormatter
{
    internal const int EllipsisPauseMilliseconds = 450;

    public static PreparedDocument Format(PreparedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var units = document.Units.Select(unit =>
        {
            var formatted = FormatUnit(unit);
            return formatted with
            {
                SpokenText = TechnicalNumberFormatter.ProtectEmbeddedNumbers(formatted.SpokenText)
            };
        }).ToArray();
        ApplyQuoteContext(document, units);
        return document with { Units = Array.AsReadOnly(units) };
    }

    private static ReadingUnit FormatUnit(ReadingUnit unit)
    {
        if (unit.Kind == ReadingUnitKind.CodeBlock)
        {
            var fenced = ReadFencedBlock(unit.OriginalText);
            if (fenced.Language.Equals("json", StringComparison.OrdinalIgnoreCase)
                && TryFormatJson(fenced.Content, out var fencedJsonText))
            {
                return unit with { SpokenText = fencedJsonText };
            }

            if (LooksLikeCode(fenced.Language, fenced.Content))
            {
                return unit with { SpokenText = "Code block." };
            }

            return FormatText(unit, fenced.Content);
        }

        if (unit.Kind == ReadingUnitKind.TableRow)
        {
            return unit with
            {
                SpokenText = ApplyLexicalFormatting(FormatTableRow(unit.OriginalText)),
            };
        }

        if (unit.Kind == ReadingUnitKind.StructuredBlock
            && TryFormatJson(unit.OriginalText, out var jsonText))
        {
            return unit with { SpokenText = jsonText };
        }

        var text = unit.Kind switch
        {
            ReadingUnitKind.Heading => HeadingMarkerPattern().Replace(unit.OriginalText, string.Empty),
            ReadingUnitKind.ListItem => FormatListItem(unit.OriginalText),
            ReadingUnitKind.UnmarkedListItem => AddSentenceBoundary(unit.OriginalText),
            ReadingUnitKind.Dialogue => QuoteMarkerPattern().Replace(unit.OriginalText, string.Empty),
            _ => unit.OriginalText
        };

        return FormatText(unit, text);
    }

    private static string AddSentenceBoundary(string text)
    {
        var trimmed = text.TrimEnd();
        return EndsWithSentencePunctuation(trimmed) || UkPostcodePattern().IsMatch(trimmed)
            ? text
            : trimmed + ".";
    }

    private static ReadingUnit FormatText(ReadingUnit unit, string text)
    {
        text = LineBreakPattern().Replace(text, " ");
        text = ContractionApostrophePattern().Replace(text, "'");
        text = TitleFormatter.Apply(text);

        var protectedLiterals = new List<string>();
        text = ProtectLiterals(text, EmailPattern(), SpeakEmail, protectedLiterals);
        text = ProtectLiterals(text, UrlPattern(), SpeakUrl, protectedLiterals);
        text = ProtectLiterals(text, WindowsPathPattern(), SpeakFileIdentifier, protectedLiterals);
        text = ProtectLiterals(text, BareFilenamePattern(), SpeakFileIdentifier, protectedLiterals);
        text = ProtectLiterals(text, VersionPattern(), TechnicalNumberFormatter.SpeakVersion, protectedLiterals);

        text = MeasurementUnitFormatter.Apply(text);
        text = OpaqueIdentifierFormatter.Apply(text);
        text = TimeFormatTokenPattern().Replace(text, "HH : MM : SS");
        text = FormatStandaloneCapitalA(text);
        text = IsoDatePattern().Replace(text, SpeakIsoDate);
        text = DatePattern().Replace(text, SpeakDate);
        text = TimeWithSecondsPattern().Replace(text, SpeakTimeWithSeconds);
        text = DottedMeridiemTimePattern().Replace(text, SpeakDottedMeridiemTime);
        text = TimePattern().Replace(text, SpeakTime);
        text = FourDigitNumberFormatter.Apply(text);
        text = DottedIdentifierPattern().Replace(
            text,
            match => match.Value.Replace(".", " dot ", StringComparison.Ordinal));
        text = FormatMathExpression(text);
        text = RestoreLiterals(text, protectedLiterals);
        text = DriveRootPattern().Replace(text, "${drive} colon ");
        text = PronunciationDictionary.Apply(text);

        var pauseAfter = 0;
        if (EndsWithEllipsis(text))
        {
            text = TrailingEllipsisPattern().Replace(text, string.Empty).TrimEnd();
            pauseAfter = EllipsisPauseMilliseconds;
        }

        return unit with
        {
            SpokenText = text,
            PauseAfterMilliseconds = pauseAfter
        };
    }

    private static void ApplyQuoteContext(PreparedDocument document, ReadingUnit[] units)
    {
        for (var index = 0; index < units.Length; index++)
        {
            if (!IsQuote(units[index].OriginalText)
                || index > 0
                && IsQuote(units[index - 1].OriginalText)
                && HasOnlyOneLineBreakBetween(document, units[index - 1], units[index]))
            {
                continue;
            }

            units[index] = units[index] with
            {
                SpokenText = "Quote. " + units[index].SpokenText
            };
        }
    }

    private static bool HasOnlyOneLineBreakBetween(
        PreparedDocument document,
        ReadingUnit first,
        ReadingUnit second)
    {
        var separator = document.OriginalText[first.SourceRange.End..second.SourceRange.Start];
        return separator.Count(character => character == '\n') <= 1
            && separator.Count(character => character == '\r') <= 1;
    }

    private static string FormatListItem(string text)
    {
        var unordered = UnorderedListPattern().Match(text);
        if (unordered.Success)
        {
            var content = text[unordered.Length..].TrimStart();
            var checkbox = CheckboxPattern().Match(content);
            if (checkbox.Success)
            {
                return content[checkbox.Length..].TrimStart();
            }

            return content;
        }

        return text;
    }

    private static string FormatTableRow(string text)
    {
        if (TableDividerPattern().IsMatch(text))
        {
            return string.Empty;
        }

        var cells = text.Split('|')
            .Select(cell => cell.Trim())
            .Where(cell => cell.Length > 0)
            .ToArray();
        if (cells.Length == 0)
        {
            return string.Empty;
        }

        var row = string.Join("; ", cells);
        return EndsWithSentencePunctuation(row) ? row : row + ".";
    }

    private static bool TryFormatJson(string text, out string spokenText)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var result = new StringBuilder();
            AppendJsonValue(result, document.RootElement, propertyName: null);
            spokenText = ApplyLexicalFormatting(result.ToString().Trim());
            return true;
        }
        catch (JsonException)
        {
            spokenText = text;
            return false;
        }
    }

    private static void AppendJsonValue(
        StringBuilder result,
        JsonElement element,
        string? propertyName)
    {
        if (propertyName is not null)
        {
            result.Append(HumanizeIdentifier(propertyName));
            result.Append(": ");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (propertyName is not null)
                {
                    result.Append("object. ");
                }

                foreach (var property in element.EnumerateObject())
                {
                    AppendJsonValue(result, property.Value, property.Name);
                }

                break;
            case JsonValueKind.Array:
                result.Append("list. ");
                var itemNumber = 1;
                foreach (var item in element.EnumerateArray())
                {
                    result.Append($"Item {itemNumber}. ");
                    AppendJsonValue(result, item, propertyName: null);
                    itemNumber++;
                }

                break;
            case JsonValueKind.String:
                result.Append(element.GetString());
                result.Append(". ");
                break;
            case JsonValueKind.Number:
                result.Append(element.GetRawText());
                result.Append(". ");
                break;
            case JsonValueKind.True:
                result.Append("true. ");
                break;
            case JsonValueKind.False:
                result.Append("false. ");
                break;
            case JsonValueKind.Null:
                result.Append("null. ");
                break;
            default:
                result.Append(element.GetRawText());
                result.Append(". ");
                break;
        }
    }

    private static string HumanizeIdentifier(string value)
    {
        value = value.Replace('_', ' ').Replace('-', ' ');
        value = CamelCaseBoundaryPattern().Replace(value, "${first} ${second}");
        return value.Length == 0
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string ApplyLexicalFormatting(string text) =>
        PronunciationDictionary.Apply(
            FourDigitNumberFormatter.Apply(
                OpaqueIdentifierFormatter.Apply(
                    MeasurementUnitFormatter.Apply(TitleFormatter.Apply(text)))));

    private static string SpeakEmail(string value)
    {
        var result = new StringBuilder(value.Length * 2);
        foreach (var character in value)
        {
            result.Append(
                character switch
                {
                    '.' => " dot ",
                    '@' => " at ",
                    '-' => " dash ",
                    '_' => " underscore ",
                    '+' => " plus ",
                    _ => character.ToString()
                });
        }

        return SpacePattern().Replace(result.ToString(), " ").Trim();
    }

    private static string SpeakUrl(string value) =>
        SpacePattern().Replace(
            value
                .Replace("://", " colon ", StringComparison.Ordinal)
                .Replace("-", " dash ", StringComparison.Ordinal)
                .Replace("?", " question mark ", StringComparison.Ordinal)
                .Replace("=", " equals ", StringComparison.Ordinal)
                .Replace("&", " and ", StringComparison.Ordinal),
            " ");

    private static string SpeakFileIdentifier(string value)
    {
        var lastSlash = value.LastIndexOf('\\');
        var extensionDot = value.LastIndexOf('.');
        var formatted = extensionDot > lastSlash
            ? value[..extensionDot] + " dot " + value[(extensionDot + 1)..]
            : value;
        if (formatted.Length >= 2 && char.IsLetter(formatted[0]) && formatted[1] == ':')
        {
            formatted = formatted[0] + " colon " + formatted[2..];
        }

        return formatted.Replace("-", " dash ", StringComparison.Ordinal);
    }

    private static string ProtectLiterals(
        string text,
        Regex pattern,
        Func<string, string> formatter,
        List<string> protectedLiterals) =>
        pattern.Replace(
            text,
            match =>
            {
                var index = protectedLiterals.Count;
                protectedLiterals.Add(formatter(match.Value));
                return $"\uE000{index}\uE001";
            });

    private static string RestoreLiterals(string text, IReadOnlyList<string> protectedLiterals)
    {
        for (var index = 0; index < protectedLiterals.Count; index++)
        {
            text = text.Replace(
                $"\uE000{index}\uE001",
                protectedLiterals[index],
                StringComparison.Ordinal);
        }

        return text;
    }

    private static string SpeakIsoDate(Match match)
    {
        if (!DateOnly.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return match.Value;
        return $"{OrdinalDay(date.Day)} of {date.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}";
    }

    private static string SpeakDate(Match match)
    {
        var day = int.Parse(match.Groups["day"].Value);
        var month = match.Groups["month"].Value;
        var year = match.Groups["year"].Value;
        // Use a leap year to validate a month/day without inventing its year.
        if (!DateOnly.TryParseExact($"{day} {month} {(year.Length == 0 ? "2000" : year)}",
                "d MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return match.Value;
        return $"{OrdinalDay(day)} of {month}" + (year.Length == 0 ? "" : " " + year);
    }

    private static string OrdinalDay(int day)
    {
        var suffix = (day % 100) is 11 or 12 or 13
            ? "th"
            : (day % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            };
        return $"{day}{suffix}";
    }

    private static string SpeakTime(Match match)
    {
        var hour = int.Parse(match.Groups["hour"].Value);
        var minute = int.Parse(match.Groups["minute"].Value);
        return $"{NumberToWords(hour)} {SpeakClockPart(minute)}";
    }

    private static string SpeakTimeWithSeconds(Match match)
    {
        var hour = int.Parse(match.Groups["hour"].Value);
        var minute = int.Parse(match.Groups["minute"].Value);
        var second = int.Parse(match.Groups["second"].Value);
        return $"{NumberToWords(hour)} {SpeakClockPart(minute)} {SpeakClockPart(second)}";
    }

    private static string SpeakDottedMeridiemTime(Match match)
    {
        var hour = int.Parse(match.Groups["hour"].Value);
        var minute = int.Parse(match.Groups["minute"].Value);
        return $"{NumberToWords(hour)} {SpeakClockPart(minute)}";
    }

    private static string SpeakClockPart(int value) =>
        value < 10 ? "oh " + NumberToWords(value) : NumberToWords(value);

    private static string FormatStandaloneCapitalA(string text) =>
        StandaloneCapitalAPattern().Replace(
            text,
            match => ShouldPronounceCapitalA(text, match.Index)
                && (match.Index + 1 >= text.Length || text[match.Index + 1] != '.')
                    ? "A."
                    : match.Value);

    private static bool ShouldPronounceCapitalA(string text, int index)
    {
        var next = index + 1;
        while (next < text.Length && char.IsWhiteSpace(text[next]))
        {
            next++;
        }

        if (next < text.Length && text[next] is '/' or '\u00b7' or ':')
        {
            return true;
        }

        if (StartsWithWord(text, next, "slash")
            || StartsWithWord(text, next, "versus")
            || StartsWithWord(text, next, "vs"))
        {
            return true;
        }

        var previous = index - 1;
        while (previous >= 0 && char.IsWhiteSpace(text[previous]))
        {
            previous--;
        }

        while (previous >= 0 && text[previous] is '"' or '\'' or '\u2018' or '\u201c' or '(' or '[' or '{')
        {
            previous--;
            while (previous >= 0 && char.IsWhiteSpace(text[previous]))
            {
                previous--;
            }
        }

        return previous >= 0 && text[previous] is not ('.' or '!' or '?' or ':' or ';');
    }

    private static bool StartsWithWord(string text, int index, string value) =>
        index + value.Length <= text.Length
        && text.AsSpan(index, value.Length).Equals(value, StringComparison.OrdinalIgnoreCase)
        && (index + value.Length == text.Length
            || !char.IsLetterOrDigit(text[index + value.Length]));

    private static string NumberToWords(int value)
    {
        string[] small =
        [
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
            "seventeen", "eighteen", "nineteen"
        ];
        if (value < small.Length)
        {
            return small[value];
        }

        string[] tens = ["", "", "twenty", "thirty", "forty", "fifty"];
        var remainder = value % 10;
        return remainder == 0
            ? tens[value / 10]
            : $"{tens[value / 10]}-{small[remainder]}";
    }

    private static string FormatMathExpression(string text)
        => MathExpressionPattern().Replace(text, match => SpeakMathExpression(match.Value));

    private static string SpeakMathExpression(string expression)
    {
        var formatted = expression
            .Replace(">=", " is greater than or equal to ", StringComparison.Ordinal)
            .Replace("<=", " is less than or equal to ", StringComparison.Ordinal)
            .Replace("!=", " is not equal to ", StringComparison.Ordinal)
            .Replace("==", " equals ", StringComparison.Ordinal)
            .Replace("=", " equals ", StringComparison.Ordinal)
            .Replace("*", " multiplied by ", StringComparison.Ordinal)
            .Replace(">", " is greater than ", StringComparison.Ordinal)
            .Replace("<", " is less than ", StringComparison.Ordinal);
        return SpacePattern().Replace(formatted, " ");
    }

    private static FencedBlock ReadFencedBlock(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2)
        {
            return new FencedBlock(string.Empty, text);
        }

        var opening = lines[0].Trim();
        var marker = opening.StartsWith("```", StringComparison.Ordinal)
            ? "```"
            : opening.StartsWith("~~~", StringComparison.Ordinal)
                ? "~~~"
                : string.Empty;
        if (marker.Length == 0
            || !lines[^1].TrimStart().StartsWith(marker, StringComparison.Ordinal))
        {
            return new FencedBlock(string.Empty, text);
        }

        return new FencedBlock(
            opening[marker.Length..].Trim(),
            string.Join("\n", lines[1..^1]).Trim());
    }

    private static bool LooksLikeCode(string language, string content)
    {
        if (ProgrammingLanguages.Contains(language))
        {
            return true;
        }

        if (language.Equals("text", StringComparison.OrdinalIgnoreCase)
            || language.Equals("plaintext", StringComparison.OrdinalIgnoreCase)
            || language.Equals("markdown", StringComparison.OrdinalIgnoreCase)
            || language.Equals("md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var score = 0;
        score += CodeKeywordPattern().IsMatch(content) ? 1 : 0;
        score += CodeCallPattern().IsMatch(content) ? 1 : 0;
        score += CodeOperatorPattern().IsMatch(content) ? 1 : 0;
        score += content.Contains(';') ? 1 : 0;
        score += content.Contains('{') && content.Contains('}') ? 1 : 0;
        return score >= 2;
    }

    private static bool IsQuote(string text) => QuoteMarkerPattern().IsMatch(text);

    private static bool EndsWithEllipsis(string text) =>
        text.TrimEnd().EndsWith("...", StringComparison.Ordinal)
        || text.TrimEnd().EndsWith('\u2026');

    private static bool EndsWithSentencePunctuation(string text) =>
        text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?');

    [GeneratedRegex(@"^\s*(?:[-*+]|\u2022|\u00b7)\s+")]
    private static partial Regex UnorderedListPattern();

    [GeneratedRegex(@"^\s*#{1,6}\s+")]
    private static partial Regex HeadingMarkerPattern();

    [GeneratedRegex(@"^\[(?<state>[ xX])\]\s*")]
    private static partial Regex CheckboxPattern();

    [GeneratedRegex(@"^\s*>\s+")]
    private static partial Regex QuoteMarkerPattern();

    [GeneratedRegex(@"\s*\r?\n\s*")]
    private static partial Regex LineBreakPattern();

    [GeneratedRegex(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\bhttps?://[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(
        @"\b[A-Z]:\\(?:[^\\\r\n]+\\)*[^\\\r\n]+\.(?:exe|dll|json|md|txt|onnx|wav|cs|ps1|py|sql)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"(?<![\w])(?<drive>[A-Za-z]):(?=[\\/])", RegexOptions.CultureInvariant)]
    private static partial Regex DriveRootPattern();

    // Exactly three numeric components. Do not match part of an IP address,
    // longer version, filename or another path token.
    [GeneratedRegex(@"(?<![\w./\\-])[vV]?[0-9]+\.[0-9]+\.[0-9]+(?![\w/\\-]|\.[\w])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?<![\w./\\-])[0-9]{4}-[0-9]{2}-[0-9]{2}(?![\w/\\-]|\.[\w])", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDatePattern();

    [GeneratedRegex(
        @"(?<![\\/\w])(?:[A-Z0-9][A-Z0-9_-]*)\.(?:exe|dll|json|md|txt|onnx|wav|cs|ps1|py|sql)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex BareFilenamePattern();

    [GeneratedRegex(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])\s+(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\b(?:\s+(?<year>[0-9]{4})\b)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?<!:)\b(?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d)\b(?!:)")]
    private static partial Regex TimePattern();

    [GeneratedRegex(
        @"(?<![:\d])\b(?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d):(?<second>[0-5]\d)\b(?![:\d])")]
    private static partial Regex TimeWithSecondsPattern();

    [GeneratedRegex(
        @"\b(?<hour>0?[1-9]|1[0-2])\.(?<minute>[0-5]\d)(?=\s*(?:a\.m\.|p\.m\.))",
        RegexOptions.IgnoreCase)]
    private static partial Regex DottedMeridiemTimePattern();

    [GeneratedRegex(@"\bHH:mm:ss\b")]
    private static partial Regex TimeFormatTokenPattern();

    [GeneratedRegex(@"\bA\b")]
    private static partial Regex StandaloneCapitalAPattern();

    [GeneratedRegex(@"\b[A-Z]{1,2}\d[A-Z\d]?\s+\d[A-Z]{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex UkPostcodePattern();

    [GeneratedRegex(@"\b[A-Z_]\w{1,}(?:\.[A-Z_]\w{1,})+\b", RegexOptions.IgnoreCase)]
    private static partial Regex DottedIdentifierPattern();

    [GeneratedRegex(@"(?<=\p{L})[\u2018\u2019](?=\p{L})")]
    private static partial Regex ContractionApostrophePattern();

    [GeneratedRegex(
        @"(?<![/?&#])\b(?:[A-Z_]\w*|\d+(?:\.\d+)?)(?:\s*(?:>=|<=|!=|==|=|>|<|\*)\s*(?:[A-Z_]\w*|\d+(?:\.\d+)?))+\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MathExpressionPattern();

    [GeneratedRegex(@"^(?:\s*\|?\s*:?-{3,}:?\s*)+(?:\|\s*)?$")]
    private static partial Regex TableDividerPattern();

    [GeneratedRegex(@"(?:\.{3}|\u2026)\s*$")]
    private static partial Regex TrailingEllipsisPattern();

    [GeneratedRegex(@"(?<first>[a-z0-9])(?<second>[A-Z])")]
    private static partial Regex CamelCaseBoundaryPattern();

    [GeneratedRegex(@"\b(?:if|else|for|foreach|while|return|class|struct|interface|function|def|var|let|const|public|private|internal|async|await|try|catch|throw|new)\b")]
    private static partial Regex CodeKeywordPattern();

    [GeneratedRegex(@"\b[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*\s*\([^\r\n]*\)")]
    private static partial Regex CodeCallPattern();

    [GeneratedRegex(@"(?:=>|==|!=|<=|>=|\+\+|--|&&|\|\||:=)")]
    private static partial Regex CodeOperatorPattern();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex SpacePattern();

    private static readonly HashSet<string> ProgrammingLanguages = new(
        [
            "c", "cpp", "c++", "cs", "csharp", "java", "javascript", "js", "typescript",
            "ts", "python", "py", "ruby", "rb", "rust", "go", "swift", "kotlin", "php",
            "powershell", "ps1", "bash", "sh", "sql"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly record struct FencedBlock(string Language, string Content);

}
