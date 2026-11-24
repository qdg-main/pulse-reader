using System.Text.RegularExpressions;

namespace PulseReader.Monitoring;

internal static class CompletedResponseAlignment
{
    internal static IReadOnlyList<string> RestoreT3StreamedBoundaries(IReadOnlyList<string> draft,
        IReadOnlyList<string> final)
    {
        if (draft.Count < 2 || final.Count == 0) return final;

        // T3 can flatten streamed paragraphs into one final paragraph. Only
        // restore boundaries when multiple complete units match word for word,
        // including punctuation and casing. Leave any new final text intact.
        var first = final[0];
        var finalWords = Regex.Matches(first, @"\S+");
        if (finalWords.Count < 12) return final;
        var draftWords = draft.Select(text => Words(text)).ToArray();
        var bestStart = -1;
        var bestUnits = 0;
        var bestWords = 0;
        var ambiguous = false;
        for (var start = 0; start + 1 < draft.Count; start++)
        {
            var matchedUnits = 0;
            var matchedWords = 0;
            foreach (var words in draftWords.Skip(start))
            {
                if (words.Length == 0 || matchedWords + words.Length > finalWords.Count) break;
                var index = 0;
                while (index < words.Length && first.AsSpan(finalWords[matchedWords + index].Index,
                    finalWords[matchedWords + index].Length).SequenceEqual(words[index].AsSpan()))
                    index++;
                if (index != words.Length) break;
                matchedUnits++;
                matchedWords += words.Length;
            }
            if (matchedUnits < 2 || matchedWords < 12 || draft[start] == draft[start + 1]) continue;
            if (matchedWords == bestWords) ambiguous = true;
            if (matchedWords <= bestWords) continue;
            bestStart = start;
            bestUnits = matchedUnits;
            bestWords = matchedWords;
            ambiguous = false;
        }
        if (bestStart < 0 || ambiguous) return final;

        var restored = draft.Skip(bestStart).Take(bestUnits).ToList();
        if (bestWords < finalWords.Count)
            restored.Add(first[finalWords[bestWords].Index..]);
        restored.AddRange(final.Skip(1));
        return restored;
    }

    public static int FindStreamedOpeningOffset(IReadOnlyList<string> draft, IReadOnlyList<string> final)
        => FindStreamedOpeningOffset(draft, final, out _);

    public static int FindStreamedOpeningOffset(IReadOnlyList<string> draft, IReadOnlyList<string> final,
        out int finalOffset)
    {
        // A final answer can start streaming after unrelated working updates.
        // A complete trailing run of at least two exact units anchors it.
        // A single substantial sentence has a stricter final-opening check.
        finalOffset = 0;
        if (draft.Count < 2 || final.Count < 2 || draft[0].Trim() == final[0].Trim()
            || HasRewrittenOpening(draft, final)) return 0;
        var before = draft.Select(unit => unit.Trim()).ToArray();
        var after = final.Select(unit => unit.Trim()).ToArray();
        var lengths = new int[draft.Count];
        var bestDraftOffset = -1;
        // Compare each pair once rather than rescanning every possible run.
        // Draft history is bounded, but a completed answer can be much longer.
        for (var end = 0; end < after.Length; end++)
        {
            for (var index = before.Length - 1; index >= 0; index--)
                lengths[index] = before[index] == after[end] ? index == 0 ? 1 : lengths[index - 1] + 1 : 0;
            var length = lengths[^1];
            if (length < 2) continue;
            var start = end - length + 1;
            var offset = before.Length - length;
            if (bestDraftOffset < 0 || start < finalOffset || start == finalOffset && offset < bestDraftOffset)
            {
                bestDraftOffset = offset;
                finalOffset = start;
            }
        }
        if (bestDraftOffset >= 0) return bestDraftOffset;
        // Sometimes completion arrives after only the first final sentence
        // streamed. Require that exact, complete sentence at both boundaries,
        // with no repeated occurrences that could make its position ambiguous.
        // Callers also require the same known response identity. Short phrases,
        // middle-of-answer matches and inserted introductions need more evidence.
        var opening = before[^1];
        if (opening == after[0] && Words(opening).Length >= 6
            && opening.Length > 0 && opening[^1] is '.' or '!' or '?'
            && before.Count(unit => unit == opening) == 1
            && after.Count(unit => unit == opening) == 1)
            return before.Length - 1;
        return 0;
    }

    // T3 can split the middle or end of a streaming answer differently from
    // its final form. Look for a unique exact opening before any aligned suffix.
    // One substantial sentence is enough only when the caller has independently
    // aligned a trailing run of at least two units.
    public static (int DraftOffset, int Length)? FindEarlierFinalOpening(IReadOnlyList<string> draft,
        IReadOnlyList<string> final, int draftPrefixOffset, int finalPrefixOffset, bool hasAlignedTail = false)
    {
        var opening = FindEarlierFinalOpeningPair(draft, final, draftPrefixOffset, finalPrefixOffset);
        if (opening is not null || !hasAlignedTail || draftPrefixOffset == 0 || finalPrefixOffset == 0)
            return opening;

        var first = final[0].Trim();
        if (Words(first).Length < 12 || first[^1] is not ('.' or '!' or '?')
            || final.Count(unit => unit.Trim() == first) != 1) return null;
        var start = -1;
        for (var index = 0; index < draft.Count; index++)
        {
            if (draft[index].Trim() != first) continue;
            if (start >= 0) return null;
            start = index;
        }
        return start >= 0 && start < draftPrefixOffset ? (start, 1) : null;
    }

    private static (int DraftOffset, int Length)? FindEarlierFinalOpeningPair(IReadOnlyList<string> draft,
        IReadOnlyList<string> final, int draftPrefixOffset, int finalPrefixOffset)
    {
        if (draftPrefixOffset < 2 || finalPrefixOffset < 2) return null;
        var first = final[0].Trim();
        var second = final[1].Trim();
        if (first == second || Words(first).Length + Words(second).Length < 12) return null;

        var start = -1;
        for (var index = 0; index + 1 < draft.Count; index++)
        {
            if (draft[index].Trim() != first || draft[index + 1].Trim() != second) continue;
            if (start >= 0) return null; // Ambiguous repeated opening.
            start = index;
        }
        if (start < 0 || start + 2 > draftPrefixOffset) return null;

        var length = 2;
        while (length < finalPrefixOffset && start + length < draftPrefixOffset
            && draft[start + length].Trim() == final[length].Trim()) length++;
        return (start, length);
    }

    // Only an anchored identifier-spacing edit within a known reply qualifies. A
    // shared phrase elsewhere, a new turn, or changed following sentences does
    // not establish that this is the opening which already began speaking.
    public static bool HasRewrittenOpening(IReadOnlyList<string> draft, IReadOnlyList<string> final)
    {
        if (draft.Count < 3 || final.Count < 3 || draft[0] == final[0]
            || draft[1] != final[1] || draft[2] != final[2]) return false;
        var before = Words(draft[0]);
        var after = Words(final[0]);
        if (before.Length > 64 || after.Length > 64 || before.Length < 5 || after.Length < 5)
            return false;
        var suffix = 0;
        while (suffix < Math.Min(before.Length, after.Length)
               && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        if (suffix < 4 || before.Length - suffix is < 1 or > 2
            || after.Length - suffix is < 1 or > 2) return false;
        var beforeHead = string.Concat(before[..^suffix]);
        var afterHead = string.Concat(after[..^suffix]);
        // UIA can expose "Manual Save-24" before consolidating "ManualSave-24".
        // Character-for-character equality preserves the identifier and its
        // number. Arbitrary prefix rewrites (Delete -> Keep, 12 -> 13) do not
        // qualify, nor does ordinary prose whose spacing changes its meaning.
        return before.Length != after.Length && beforeHead == afterHead
            && Regex.IsMatch(afterHead, @"^[\p{L}][\p{L}\p{N}_-]*\p{N}[\p{L}\p{N}_-]*$");
    }

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
