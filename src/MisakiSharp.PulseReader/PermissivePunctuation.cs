using System;
using System.Collections.Generic;
using System.Text;

namespace MisakiSharp;

// Source-offset pipeline developed from the measured provider/output contract.
// Provenance: artifacts/permissive-replacement-research/baseline/SpanPrototype.cs.
// This implementation does not claim a formal clean-room development process.
internal static class PermissivePunctuation
{
    private const string LiteralCharacters = ";:,.!?¡¿—…\"«»“”(){}[]";

    private readonly record struct Piece(int Start, int Length, bool Literal);

    public static string Phonemize(string text, Func<string, string> provider, bool removeLanguageFlags)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var result = new StringBuilder();
        var hasText = false;
        foreach (var piece in Plan(text))
        {
            if (piece.Literal)
            {
                // Provider word separators do not precede retained punctuation.
                if (hasText && result.Length > 0 && result[result.Length - 1] == ' ')
                {
                    result.Length--;
                }

                result.Append(text.AsSpan(piece.Start, piece.Length));
            }
            else
            {
                var raw = provider(text.Substring(piece.Start, piece.Length));
                var normalized = Normalize(raw, removeLanguageFlags);
                // The first result after leading literal content contributes
                // one fewer separator, including annotation-created spacing.
                if (!hasText && result.Length > 0 && normalized.EndsWith(' '))
                {
                    normalized = normalized.Substring(0, normalized.Length - 1);
                }

                hasText = true;
                result.Append(normalized);
            }
        }

        // Text-bearing input retains the observed final ASCII word separator.
        // Literal-only input instead passes through verbatim.
        if (hasText && (result.Length == 0 || result[result.Length - 1] != ' '))
        {
            result.Append(' ');
        }

        return result.ToString();
    }

    private static List<Piece> Plan(string text)
    {
        var pieces = new List<Piece>();
        var pendingStart = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (!LiteralCharacters.Contains(text[index]))
            {
                index++;
                continue;
            }

            var literalStart = index;
            while (literalStart > pendingStart && char.IsWhiteSpace(text[literalStart - 1]))
            {
                literalStart--;
            }

            if (literalStart > pendingStart)
            {
                pieces.Add(new Piece(pendingStart, literalStart - pendingStart, false));
            }

            var end = index + 1;
            while (end < text.Length && (LiteralCharacters.Contains(text[end]) || char.IsWhiteSpace(text[end])))
            {
                end++;
            }

            pieces.Add(new Piece(literalStart, end - literalStart, true));
            index = pendingStart = end;
        }

        if (pendingStart < text.Length)
        {
            pieces.Add(new Piece(pendingStart, text.Length - pendingStart, false));
        }

        return pieces;
    }

    private static string Normalize(string raw, bool removeLanguageFlags)
    {
        var buffer = new StringBuilder();
        for (var index = 0; index < raw.Length; index++)
        {
            var value = raw[index];
            if (value == '\r' && index + 1 < raw.Length && raw[index + 1] == '\n')
            {
                continue;
            }

            buffer.Append(value == '\u0361' ? '^' : value);
        }

        var normalized = buffer.ToString().Trim();
        buffer.Clear();
        for (var index = 0; index < normalized.Length; index++)
        {
            buffer.Append(normalized[index] == '\n' ? ' ' : normalized[index]);
        }

        normalized = buffer.ToString();
        buffer.Clear();
        // Reduce paired ASCII spaces once, keeping the measured spacing policy.
        for (var index = 0; index < normalized.Length; index++)
        {
            buffer.Append(normalized[index]);
            if (normalized[index] == ' ' && index + 1 < normalized.Length && normalized[index + 1] == ' ')
            {
                index++;
            }
        }

        normalized = buffer.ToString();
        if (removeLanguageFlags)
        {
            buffer.Clear();
            for (var index = 0; index < normalized.Length; index++)
            {
                if (normalized[index] == '(')
                {
                    // An annotation needs an interior character. An immediate
                    // ')' can be interior content if a later ')' closes it.
                    var closing = normalized.IndexOf(')', Math.Min(index + 2, normalized.Length));
                    if (closing > index + 1)
                    {
                        index = closing;
                        continue;
                    }
                }

                buffer.Append(normalized[index]);
            }

            normalized = buffer.ToString();
        }

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        buffer.Clear();
        var start = 0;
        for (var index = 0; index <= normalized.Length; index++)
        {
            if (index < normalized.Length && normalized[index] != ' ')
            {
                continue;
            }

            buffer.Append(normalized.AsSpan(start, index - start).Trim());
            buffer.Append(' ');
            start = index + 1;
        }

        return buffer.ToString();
    }
}
