using System.Text.Json;

internal static class SpokenTextAbCorpus
{
    public static IReadOnlyList<SpokenTextAbCase> LoadAccepted() =>
        Load("spoken-text-ab-round2-corpus.json");

    public static IReadOnlyList<SpokenTextAbCase> LoadEdgeCases() =>
        Load("spoken-text-ab-edge-corpus.json");

    public static IReadOnlyList<SpokenTextAbCase> LoadLineBoundaries() =>
        Load("spoken-text-ab-line-boundary-corpus.json");

    public static IReadOnlyList<SpokenTextAbCase> LoadIdentifiers() =>
        Load("spoken-text-identifier-ab-corpus.json");

    public static IReadOnlyList<SpokenTextAbCase> LoadMeasurementUnits() =>
        Load("measurement-unit-ab-corpus.json");

    public static IReadOnlyList<SpokenTextAbCase> LoadFourDigitNumbers() =>
        Load("four-digit-number-ab-corpus.json");

    private static IReadOnlyList<SpokenTextAbCase> Load(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            fileName);
        return JsonSerializer.Deserialize<SpokenTextAbCase[]>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("The spoken-text A/B corpus could not be loaded.");
    }
}

internal sealed record SpokenTextAbCase(
    string Id,
    string Category,
    string Title,
    string Rationale,
    string OriginalText,
    string ExpectedPreparedText,
    bool ExpectChange,
    int ExpectedPauseMilliseconds,
    string? BaselineText = null);
