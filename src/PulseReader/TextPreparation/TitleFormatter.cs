using System.Text.RegularExpressions;

namespace PulseReader.TextPreparation;

internal static class TitleFormatter
{
    private const string CapitalizedNameLookahead =
        "(?=\\s+\\p{Lu}[\\p{L}\\p{M}'’\\-]*)";

    private static readonly Regex UndottedMsPattern = new(
        "(?<![\\p{L}\\p{N}_])Ms" + CapitalizedNameLookahead,
        RegexOptions.CultureInvariant);

    private static readonly Regex ProfessorPattern = new(
        "(?<![\\p{L}\\p{N}_])Prof\\.?" + CapitalizedNameLookahead,
        RegexOptions.CultureInvariant);

    private static readonly Regex ReverendPattern = new(
        "(?<![\\p{L}\\p{N}_])Rev\\.?" + CapitalizedNameLookahead,
        RegexOptions.CultureInvariant);

    private static readonly Regex MxPattern = new(
        "(?<![\\p{L}\\p{N}_])Mx\\.?" + CapitalizedNameLookahead,
        RegexOptions.CultureInvariant);

    public static string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = UndottedMsPattern.Replace(text, "Ms.");
        text = ProfessorPattern.Replace(text, "Professor");
        text = ReverendPattern.Replace(text, "Reverend");
        return MxPattern.Replace(text, "Mix");
    }
}
