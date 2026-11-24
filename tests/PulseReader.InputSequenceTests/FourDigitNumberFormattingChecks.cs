using PulseReader.TextPreparation;

internal static class FourDigitNumberFormattingChecks
{
    public static void Run()
    {
        var cases = new (string Id, string Source, string Expected)[]
        {
            (
                "ordinary-quantities",
                "The value is 1024. There are 2024 entries. The buffer contains 4096 bytes. The query returned 9999 results.",
                "The value is one thousand and twenty-four. There are two thousand and twenty-four entries. The buffer contains four thousand and ninety-six bytes. The query returned nine thousand nine hundred and ninety-nine results."),
            (
                "grouped-and-decimal",
                "The values are 1,024 and 1024.5.",
                "The values are one thousand and twenty-four and one thousand and twenty-four point five."),
            (
                "explicit-years",
                "The year was 2024. In 1066, the situation changed. During 1999, the service expanded.",
                "The year was 2024. In 1066, the situation changed. During 1999, the service expanded."),
            (
                "calendar-date",
                "The release date was 24 August 2024.",
                "The release date was 24th of August 2024."),
            (
                "labelled-identifiers",
                "Use port 8080, version 2024.1, PIN 4096, and build 9999.",
                "Use port eight zero eight zero, version two zero two four point one, PIN four zero nine six, and build nine nine nine nine."),
            (
                "standard-identifiers",
                "Follow RFC 7231, ISO 8601, and model 4096.",
                "Follow RFC seven two three one, ISO eight six zero one, and model four zero nine six."),
            (
                "numeric-controls",
                "The values are 999, 1000, 12345, and 1,234,567.",
                "The values are 999, 1000, 12345, and 1,234,567."),
            (
                "standalone-quantity",
                "1024",
                "one thousand and twenty-four"),
            (
                "standalone-ambiguous-year",
                "2024",
                "two thousand and twenty-four")
        };

        foreach (var example in cases)
        {
            var actual = SpokenTextFormatter
                .Format(ReadingUnitAnalyzer.Prepare(example.Source))
                .BuildSpokenText();
            Assert(
                string.Equals(example.Expected, actual, StringComparison.Ordinal),
                $"Four-digit number case '{example.Id}' produced unexpected prepared text."
                + $"{Environment.NewLine}Expected: {example.Expected}"
                + $"{Environment.NewLine}Actual:   {actual}");
        }

        var listeningCases = SpokenTextAbCorpus.LoadFourDigitNumbers();
        foreach (var example in listeningCases)
        {
            var actual = SpokenTextFormatter
                .Format(ReadingUnitAnalyzer.Prepare(example.OriginalText))
                .BuildSpokenText();
            Assert(
                string.Equals(example.ExpectedPreparedText, actual, StringComparison.Ordinal),
                $"Four-digit listening case '{example.Id}' produced unexpected prepared text."
                + $"{Environment.NewLine}Expected: {example.ExpectedPreparedText}"
                + $"{Environment.NewLine}Actual:   {actual}");
        }

        Console.WriteLine(
            $"PASS: four-digit cardinal preparation matches {cases.Length} regression and "
            + $"{listeningCases.Count} listening cases.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
