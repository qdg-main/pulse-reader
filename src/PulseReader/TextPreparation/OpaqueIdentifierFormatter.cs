using System.Text.RegularExpressions;

namespace PulseReader.TextPreparation;

internal static partial class OpaqueIdentifierFormatter
{
    private const int ShortNumericIdentifierLength = 12;
    private const int TruncatedPrefixLength = 4;
    private const int ShortGitHashLength = 7;

    public static string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var standaloneNumber = StandaloneLongNumericPattern().Match(text);
        if (standaloneNumber.Success)
        {
            return FormatTruncated(
                standaloneNumber.Groups["value"].Value,
                FollowingText.End);
        }

        text = LabelledTokenPattern().Replace(
            text,
            match => match.Groups["label"].Value
                + FormatTruncated(
                    match.Groups["value"].Value,
                    GetFollowingText(text, match.Index + match.Length)));
        text = LabelledNumericIdentifierPattern().Replace(
            text,
            match => match.Groups["label"].Value
                + FormatNumericIdentifier(
                    match.Groups["value"].Value,
                    GetFollowingText(text, match.Index + match.Length)));
        text = UuidPattern().Replace(
            text,
            match => FormatTruncated(
                match.Value,
                GetFollowingText(text, match.Index + match.Length)));
        text = Sha256HashPattern().Replace(
            text,
            match => FormatTruncated(
                match.Value,
                GetFollowingText(text, match.Index + match.Length)));
        text = FullGitHashPattern().Replace(
            text,
            match => SpellCharacters(match.Value[..ShortGitHashLength]));
        return NumericCommitHashPattern().Replace(
            text,
            match => SpellCharacters(match.Groups["value"].Value));
    }

    private static string FormatNumericIdentifier(string value, FollowingText followingText) =>
        value.Length <= ShortNumericIdentifierLength
            ? SpellCharacters(value)
            : FormatTruncated(value, followingText);

    private static string FormatTruncated(string value, FollowingText followingText)
    {
        var prefix = SpellCharacters(value[..Math.Min(TruncatedPrefixLength, value.Length)]);
        return followingText switch
        {
            FollowingText.Content => prefix + ", etc.,",
            FollowingText.Punctuation => prefix + ", etc",
            _ => prefix + ", etc."
        };
    }

    private static string SpellCharacters(string value) =>
        string.Join(" ", value.Select(character => character.ToString()));

    private static FollowingText GetFollowingText(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                continue;
            }

            return char.IsPunctuation(text[index])
                ? FollowingText.Punctuation
                : FollowingText.Content;
        }

        return FollowingText.End;
    }

    [GeneratedRegex(@"^\s*(?<value>\d{20,})[.!?]?\s*$")]
    private static partial Regex StandaloneLongNumericPattern();

    [GeneratedRegex(@"(?<![A-Fa-f0-9])(?=[A-Fa-f0-9]{0,39}[A-Fa-f])[A-Fa-f0-9]{40}(?![A-Fa-f0-9])")]
    private static partial Regex FullGitHashPattern();

    [GeneratedRegex(@"(?<![A-Fa-f0-9])(?=[A-Fa-f0-9]{0,63}[A-Fa-f])[A-Fa-f0-9]{64}(?![A-Fa-f0-9])")]
    private static partial Regex Sha256HashPattern();

    [GeneratedRegex(@"(?<![A-Fa-f0-9])(?:[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}-[1-5][A-Fa-f0-9]{3}-[89ABab][A-Fa-f0-9]{3}-[A-Fa-f0-9]{12})(?![A-Fa-f0-9])")]
    private static partial Regex UuidPattern();

    [GeneratedRegex(@"(?<label>\bToken:\s*)(?<value>[A-Za-z0-9_-]{24,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LabelledTokenPattern();

    [GeneratedRegex(@"(?<label>\b(?:Message\s+ID|Reference\s+number)\s+)(?<value>\d{7,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LabelledNumericIdentifierPattern();

    [GeneratedRegex(@"(?<!\w)(?<value>\d{7,12})(?=\s+[—–-])")]
    private static partial Regex NumericCommitHashPattern();

    private enum FollowingText
    {
        End,
        Punctuation,
        Content
    }
}
