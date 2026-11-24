using System.Globalization;
using System.Text.RegularExpressions;

namespace PulseReader.TextPreparation;

internal static partial class FourDigitNumberFormatter
{
    private static readonly string[] SmallNumbers =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
        "seventeen", "eighteen", "nineteen"
    ];

    private static readonly string[] Tens =
    [
        "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
    ];

    public static string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FourDigitNumberPattern().Replace(text, match => ReplaceNumber(text, match));
    }

    private static string ReplaceNumber(string text, Match match)
    {
        var integerText = match.Groups["integer"].Value;
        var digits = integerText.Replace(",", string.Empty, StringComparison.Ordinal);
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return match.Value;
        }

        if (IsLabelledIdentifier(text, match))
        {
            return SpellIdentifier(match.Value);
        }

        if (value == 1000 || IsExplicitYear(text, match))
        {
            return match.Value;
        }

        var words = SpellFourDigitInteger(value);
        var fraction = match.Groups["fraction"].Value;
        if (fraction.Length == 0)
        {
            return words;
        }

        var fractionalWords = string.Join(
            ' ',
            fraction.AsSpan(1).ToArray().Select(character => SmallNumbers[character - '0']));
        return words + " point " + fractionalWords;
    }

    private static string SpellIdentifier(string value)
    {
        var parts = value.Split('.', 2);
        var integerWords = string.Join(
            ' ',
            parts[0]
                .Where(char.IsDigit)
                .Select(character => SmallNumbers[character - '0']));
        if (parts.Length == 1)
        {
            return integerWords;
        }

        var fractionWords = string.Join(
            ' ',
            parts[1].Select(character => SmallNumbers[character - '0']));
        return integerWords + " point " + fractionWords;
    }

    private static bool IsExplicitYear(string text, Match match)
    {
        if (match.Groups["fraction"].Success)
        {
            return false;
        }

        var before = text[..match.Index];
        var after = text[(match.Index + match.Length)..];
        return YearContextBeforePattern().IsMatch(before)
               || YearContextAfterPattern().IsMatch(after)
               || NumericDateBeforePattern().IsMatch(before)
               || NumericDateAfterPattern().IsMatch(after)
               || YearRangeBeforePattern().IsMatch(before)
               || YearRangeAfterPattern().IsMatch(after);
    }

    private static bool IsLabelledIdentifier(string text, Match match) =>
        IdentifierLabelBeforePattern().IsMatch(text[..match.Index])
        || NetworkPortAfterPattern().IsMatch(text[(match.Index + match.Length)..]);

    private static string SpellFourDigitInteger(int value)
    {
        var thousands = value / 1000;
        var remainder = value % 1000;
        var words = SmallNumbers[thousands] + " thousand";
        if (remainder == 0)
        {
            return words;
        }

        return words
               + (remainder < 100 ? " and " : " ")
               + SpellUnderOneThousand(remainder);
    }

    private static string SpellUnderOneThousand(int value)
    {
        var words = new List<string>();
        if (value >= 100)
        {
            words.Add(SmallNumbers[value / 100]);
            words.Add("hundred");
            value %= 100;
            if (value > 0)
            {
                words.Add("and");
            }
        }

        if (value >= 20)
        {
            var tens = Tens[value / 10];
            var units = value % 10;
            words.Add(units == 0 ? tens : tens + "-" + SmallNumbers[units]);
        }
        else if (value > 0)
        {
            words.Add(SmallNumbers[value]);
        }

        return string.Join(' ', words);
    }

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_])(?<integer>[1-9]\d{3}|[1-9],\d{3})(?<fraction>\.\d+)?(?![\p{L}\p{N}_]|\.\d|,\d{3})",
        RegexOptions.CultureInvariant)]
    private static partial Regex FourDigitNumberPattern();

    [GeneratedRegex(
        @"(?:\b(?:in|during|since|until|from|before|after|around|circa)\s+|\byear\s+(?:(?:was|is)\s+)?|\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YearContextBeforePattern();

    [GeneratedRegex(@"^\s*(?:AD|BC|CE|BCE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YearContextAfterPattern();

    [GeneratedRegex(@"(?:^|\D)\d{1,2}[-/]\d{1,2}[-/]$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDateBeforePattern();

    [GeneratedRegex(@"^[-/]\d{1,2}[-/]\d{1,2}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDateAfterPattern();

    [GeneratedRegex(@"\b\d{4}\s*[-–—/]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex YearRangeBeforePattern();

    [GeneratedRegex(@"^\s*[-–—/]\s*\d{2,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearRangeAfterPattern();

    [GeneratedRegex(
        @"\b(?:port|version|ver|build|pin|code|id|identifier|serial|model|ticket|issue|reference|rfc|iso|iec)(?:\s+(?:number|no\.?))?\s*[:=#]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierLabelBeforePattern();

    [GeneratedRegex(@"^/(?:tcp|udp)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetworkPortAfterPattern();
}
