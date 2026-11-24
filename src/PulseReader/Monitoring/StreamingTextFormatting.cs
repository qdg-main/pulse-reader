using System.Text;

namespace PulseReader.Monitoring;

internal static class StreamingTextFormatting
{
    private static readonly string[] SuspendedHyphenCoordinators =
        ["and", "or", "nor", "but", "to", "versus", "vs"];

    public static string NormalizeInlineHyphenSpacing(string text)
    {
        StringBuilder? result = null;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not (' ' or '\t'))
            {
                result?.Append(text[index]);
                continue;
            }
            var end = index + 1;
            while (end < text.Length && text[end] is ' ' or '\t') end++;
            // One side of the hyphen must already touch a word. Preserve line
            // breaks, spaced list markers and ordinary "left - right" prose.
            var afterHyphen = index >= 2 && text[index - 1] == '-'
                && char.IsLetter(text[index - 2]) && end < text.Length && char.IsLetter(text[end]);
            // Do not join whitespace before a hyphen: "tool -f" is a command
            // argument, not necessarily a split hyphenated word.
            if (afterHyphen && !IsSuspendedHyphenContinuation(text, end))
                result ??= new StringBuilder(text[..index], text.Length);
            else result?.Append(text, index, end - index);
            index = end - 1;
        }
        return result?.ToString() ?? text;
    }

    private static bool IsSuspendedHyphenContinuation(string text, int start)
    {
        var end = start;
        while (end < text.Length && char.IsLetter(text[end])) end++;
        // A coordinator belongs to the sentence, not the unfinished prefix:
        // "client- and server-side", "two- or three-day", "two- to three-day".
        // Keep actual chained compounds such as "rock- and-roll" joinable.
        if (end < text.Length && text[end] == '-') return false;
        var word = text.AsSpan(start, end - start);
        foreach (var coordinator in SuspendedHyphenCoordinators)
        {
            if (word.Equals(coordinator, StringComparison.OrdinalIgnoreCase)) return true;
            // The next capture can complete "a" to "and". Removing the space
            // now is irreversible, so retain possible coordinators at the live
            // tail; a delimiter or a nonmatching continuation resolves them.
            if (end == text.Length && coordinator.AsSpan().StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
