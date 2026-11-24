using System.Text.RegularExpressions;

namespace PulseReader.TextPreparation;

internal static partial class TechnicalNumberFormatter
{
    private static readonly string[] Digits =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    // A version component is an integer, not a decimal fraction. Preserve leading
    // zeroes and speak long components as identifiers rather than year pairs.
    internal static string SpeakVersion(string value)
    {
        var prefix = value.StartsWith('v') || value.StartsWith('V') ? value[..1] : "";
        var parts = value[prefix.Length..].Split('.');
        return prefix + string.Join(" point ", parts.Select(part =>
            part.Length >= 4 || part.Length > 1 && part[0] == '0' ? SpellDigits(part) : part));
    }

    internal static string ProtectEmbeddedNumbers(string text)
    {
        // The e/E of scientific notation is not evidence of an identifier.
        // Locate complete literals first, including signed exponents which can
        // span technical-token boundaries, and preserve their numeric spelling.
        var numbers = ScientificNumberPattern().Matches(text);
        return TechnicalTokenPattern().Replace(text, match =>
        {
            if (!match.Value.Any(char.IsAsciiLetter)) return match.Value;
            return LongDigitsPattern().Replace(match.Value, digits =>
            {
                var start = match.Index + digits.Index;
                if (numbers.Any(number => start >= number.Index
                    && start + digits.Length <= number.Index + number.Length)) return digits.Value;

                return (digits.Index > 0 && char.IsAsciiLetter(match.Value[digits.Index - 1]) ? " " : "")
                    + SpellDigits(digits.Value)
                    + (digits.Index + digits.Length < match.Length
                       && char.IsAsciiLetter(match.Value[digits.Index + digits.Length]) ? " " : "");
            });
        });
    }

    private static string SpellDigits(string value) =>
        string.Join(" ", value.Select(digit => Digits[digit - '0']));

    // Misaki's fallback number conversion throws for some large digit runs in
    // unfamiliar compounds (e.g. foo-20260908), while ordinary quantities work.
    [GeneratedRegex(@"[A-Za-z0-9_./\\:\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalTokenPattern();

    [GeneratedRegex(@"[0-9]{7,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigitsPattern();

    [GeneratedRegex(@"(?<![\w.])[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)[eE][+-]?[0-9]+(?!\w|\.[0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex ScientificNumberPattern();
}
