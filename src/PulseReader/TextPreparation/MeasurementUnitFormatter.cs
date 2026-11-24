using System.Globalization;
using System.Text.RegularExpressions;

namespace PulseReader.TextPreparation;

internal static class MeasurementUnitFormatter
{
    private static readonly IReadOnlyList<UnitDefinition> Definitions =
        Array.AsReadOnly<UnitDefinition>(
        [
            Binary("KiB", "kibibyte", "kibibytes", "kˈɪbɪbˌIt", "kˈɪbɪbˌIts"),
            Binary("MiB", "mebibyte", "mebibytes", "mˈɛbɪbˌIt", "mˈɛbɪbˌIts"),
            Binary("GiB", "gibibyte", "gibibytes", "ɡˈɪbɪbˌIt", "ɡˈɪbɪbˌIts"),
            Binary("TiB", "tebibyte", "tebibytes", "tˈɛbɪbˌIt", "tˈɛbɪbˌIts"),
            Unit("kWh", "kilowatt-hour", "kilowatt-hours"),
            Unit("km/h", "kilometre per hour", "kilometres per hour"),
            Unit("GB/s", "gigabyte per second", "gigabytes per second"),
            Unit("MB/s", "megabyte per second", "megabytes per second"),
            Unit("KB/s", "kilobyte per second", "kilobytes per second"),
            Unit("kB/s", "kilobyte per second", "kilobytes per second"),
            Unit("Gbps", "gigabit per second", "gigabits per second"),
            Unit("Mbps", "megabit per second", "megabits per second"),
            Unit("Kbps", "kilobit per second", "kilobits per second"),
            Unit("kbps", "kilobit per second", "kilobits per second"),
            Unit("GHz", "gigahertz", "gigahertz"),
            Unit("MHz", "megahertz", "megahertz"),
            Unit("kHz", "kilohertz", "kilohertz"),
            Unit("m/s", "metre per second", "metres per second"),
            Unit("mph", "mile per hour", "miles per hour"),
            Unit("kB", "kilobyte", "kilobytes"),
            Unit("KB", "kilobyte", "kilobytes"),
            Unit("MB", "megabyte", "megabytes"),
            Unit("GB", "gigabyte", "gigabytes"),
            Unit("TB", "terabyte", "terabytes"),
            Unit("mL", "millilitre", "millilitres"),
            Unit("ml", "millilitre", "millilitres"),
            Unit("min", "minute", "minutes"),
            Unit("ns", "nanosecond", "nanoseconds"),
            Unit("µs", "microsecond", "microseconds"),
            Unit("μs", "microsecond", "microseconds"),
            Unit("us", "microsecond", "microseconds"),
            Unit("ms", "millisecond", "milliseconds"),
            Unit("kW", "kilowatt", "kilowatts"),
            Unit("Wh", "watt-hour", "watt-hours"),
            Unit("mV", "millivolt", "millivolts"),
            Unit("mA", "milliamp", "milliamps"),
            Unit("°C", "degree Celsius", "degrees Celsius"),
            Unit("°F", "degree Fahrenheit", "degrees Fahrenheit"),
            Unit("mm", "millimetre", "millimetres"),
            Unit("cm", "centimetre", "centimetres"),
            Unit("km", "kilometre", "kilometres"),
            Unit("mg", "milligram", "milligrams"),
            Unit("kg", "kilogram", "kilograms"),
            Unit("oz", "ounce", "ounces"),
            Unit("lb", "pound", "pounds"),
            Unit("hr", "hour", "hours"),
            Unit("Hz", "hertz", "hertz"),
            Unit("B", "byte", "bytes"),
            Unit("L", "litre", "litres"),
            Unit("s", "second", "seconds"),
            Unit("h", "hour", "hours"),
            Unit("m", "metre", "metres"),
            Unit("g", "gram", "grams"),
            Unit("in", "inch", "inches"),
            Unit("ft", "foot", "feet"),
            Unit("V", "volt", "volts"),
            Unit("A", "amp", "amps"),
            Unit("W", "watt", "watts")
        ]);

    private static readonly IReadOnlyDictionary<string, UnitDefinition> DefinitionsBySymbol =
        Definitions.ToDictionary(definition => definition.Symbol, StringComparer.Ordinal);

    private static readonly Regex QuantityUnitPattern = new(
        "(?<![\\p{L}\\p{N}_])(?<quantity>\\d+(?:,\\d{3})*(?:\\.\\d+)?)\\s*(?<unit>"
        + string.Join(
            '|',
            Definitions
                .Select(definition => definition.Symbol)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(symbol => symbol.Length)
                .Select(Regex.Escape))
        + ")(?![\\p{L}\\p{N}_])",
        RegexOptions.CultureInvariant);

    private static readonly Regex DeterminerBeforeQuantityPattern = new(
        "(?:^|[^\\p{L}\\p{N}_])(?:a|an|the|this|that|these|those|each|every|another|"
        + "my|your|our|his|her|its|their)\\s+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex WordAfterMeasurementPattern = new(
        "^\\s+[\\p{L}_]",
        RegexOptions.CultureInvariant);

    // Recognise paired measurements, not lone quotes or apostrophes. Inches are
    // 0..11 (optionally fractional); the final inch mark is often omitted in chat.
    // Exclude degree-marked coordinates and matches inside words/quoted numbers.
    private static readonly Regex FeetAndInchesPattern = new(
        "(?<![\\p{L}\\p{N}_.'’′°])(?<feet>[0-9]+)[ \\t]*['’′][ \\t]*"
        + "(?<inches>1[01]|[0-9])(?:\\.(?<fraction>[0-9]+))?"
        + "(?:[ \\t]*[\"”″])?(?![\\p{L}\\p{N}_]|\\.[0-9])",
        RegexOptions.CultureInvariant);

    public static string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = FeetAndInchesPattern.Replace(text, match =>
        {
            // Degrees and minutes may be separated by ordinary or typographic
            // whitespace. Do not turn the latter into feet after a degree mark.
            var previous = match.Index - 1;
            while (previous >= 0 && char.IsWhiteSpace(text[previous])) previous--;
            if (previous >= 0 && text[previous] == '°') return match.Value;

            var feet = match.Groups["feet"].Value;
            var inches = match.Groups["inches"].Value;
            if (match.Groups["fraction"].Success) inches += "." + match.Groups["fraction"].Value;
            return feet + (IsNumericallySingular(feet) ? " foot " : " feet ")
                + inches + (IsNumericallySingular(inches) ? " inch" : " inches");
        });
        return QuantityUnitPattern.Replace(text, match => ReplaceMeasurement(text, match));
    }

    private static string ReplaceMeasurement(string text, Match match)
    {
        var quantity = match.Groups["quantity"].Value;
        var definition = DefinitionsBySymbol[match.Groups["unit"].Value];
        var singular = IsNumericallySingular(quantity)
                       || IsDeterminerLedModifier(text, match);
        return quantity + " " + definition.GetSpokenForm(singular);
    }

    private static bool IsNumericallySingular(string quantity) =>
        decimal.TryParse(
            quantity,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out var numericQuantity)
        && numericQuantity == 1m;

    private static bool IsDeterminerLedModifier(string text, Match match)
    {
        var beforeMeasurement = text[..match.Index];
        var afterMeasurement = text[(match.Index + match.Length)..];
        return DeterminerBeforeQuantityPattern.IsMatch(beforeMeasurement)
               && WordAfterMeasurementPattern.IsMatch(afterMeasurement);
    }

    private static UnitDefinition Unit(string symbol, string singular, string plural) =>
        new(symbol, singular, plural, null, null);

    private static UnitDefinition Binary(
        string symbol,
        string singular,
        string plural,
        string singularPhonemes,
        string pluralPhonemes) =>
        new(symbol, singular, plural, singularPhonemes, pluralPhonemes);

    private sealed record UnitDefinition(
        string Symbol,
        string Singular,
        string Plural,
        string? SingularPhonemes,
        string? PluralPhonemes)
    {
        public string GetSpokenForm(bool singular)
        {
            var display = singular ? Singular : Plural;
            var phonemes = singular ? SingularPhonemes : PluralPhonemes;
            return phonemes is null ? display : $"[{display}](/{phonemes}/)";
        }
    }
}
