using PulseReader.TextPreparation;

internal static class PronunciationDictionaryChecks
{
    public static void Run()
    {
        Assert(
            PronunciationDictionary.Entries.SequenceEqual(
            [
                new PronunciationEntry("dotnet", "dot net"),
                new PronunciationEntry("JSON", "Jason")
            ]),
            "The production pronunciation dictionary does not contain the approved entries.");

        const string source =
            "Run dotnet build, parse JSON, and open settings.json. Leave JsonSerializer unchanged.";
        const string expected =
            "Run dot net build, parse Jason, and open settings dot Jason. Leave JsonSerializer unchanged.";
        var actual = SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(source)).BuildSpokenText();
        Assert(
            string.Equals(expected, actual, StringComparison.Ordinal),
            "Pronunciation entries were not applied at standalone and file-extension boundaries."
            + $"{Environment.NewLine}Expected: {expected}"
            + $"{Environment.NewLine}Actual:   {actual}");

        var jsonActual = SpokenTextFormatter.Format(
                ReadingUnitAnalyzer.Prepare(
                    "{\n  \"format\": \"JSON\",\n  \"command\": \"dotnet build\"\n}"))
            .BuildSpokenText();
        const string jsonExpected = "Format: Jason. Command: dot net build.";
        Assert(
            string.Equals(jsonExpected, jsonActual, StringComparison.Ordinal),
            "Pronunciation entries were not applied to parsed JSON values."
            + $"{Environment.NewLine}Expected: {jsonExpected}"
            + $"{Environment.NewLine}Actual:   {jsonActual}");

        var tableActual = SpokenTextFormatter.Format(
                ReadingUnitAnalyzer.Prepare(
                    "| Format | Command |\n| --- | --- |\n| JSON | dotnet build |"))
            .BuildSpokenText();
        Assert(
            tableActual.Contains("Jason; dot net build.", StringComparison.Ordinal),
            "Pronunciation entries were not applied to table cells."
            + $"{Environment.NewLine}Actual: {tableActual}");

        Console.WriteLine("PASS: the pronunciation dictionary applies 2 approved whole-term overrides.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
