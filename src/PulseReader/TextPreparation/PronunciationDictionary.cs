using System.Text.RegularExpressions;

namespace PulseReader.TextPreparation;

internal static class PronunciationDictionary
{
    private static readonly IReadOnlyList<PronunciationEntry> EntryList =
        Array.AsReadOnly<PronunciationEntry>(
        [
            new("dotnet", "dot net"),
            new("JSON", "Jason")
        ]);

    private static readonly IReadOnlyDictionary<string, string> Replacements =
        EntryList.ToDictionary(
            entry => entry.WrittenForm,
            entry => entry.SpokenForm,
            StringComparer.OrdinalIgnoreCase);

    private static readonly Regex EntryPattern = new(
        "(?<![\\p{L}\\p{N}_])(?:"
        + string.Join(
            '|',
            EntryList
                .OrderByDescending(entry => entry.WrittenForm.Length)
                .Select(entry => Regex.Escape(entry.WrittenForm)))
        + ")(?![\\p{L}\\p{N}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<PronunciationEntry> Entries => EntryList;

    public static string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EntryPattern.Replace(text, match => Replacements[match.Value]);
    }
}

internal sealed record PronunciationEntry(string WrittenForm, string SpokenForm);
