using System.Text.RegularExpressions;

namespace PulseReader.TextPreparation;

internal static class ContextualPronunciationRules
{
    public static IReadOnlyList<ContextualPronunciationRule> All { get; } =
    [
        new(
            "axes",
            "CUTTING_TOOLS",
            "ˈæksɪz",
            [
                new("COORDINATE_DIMENSIONS", "the plural of axis, meaning graph, coordinate, or rotation dimensions"),
                new("CUTTING_TOOLS", "the plural of axe, meaning cutting tools")
            ]),
        new(
            "bass",
            "LOW_FREQUENCY_SOUND",
            "bˈAs",
            [
                new("LOW_FREQUENCY_SOUND", "low-frequency sound, an instrument, or an audio control"),
                new("FISH", "the fish")
            ]),
        new(
            "lead",
            "METAL",
            "lˈɛd",
            [
                new("METAL", "the chemical element or metal"),
                new("GUIDE_OR_FRONT", "to guide, be ahead, or occupy the principal position")
            ]),
        new(
            "wind",
            "TURN_OR_WRAP",
            "wˈInd",
            [
                new("TURN_OR_WRAP", "to turn, wrap, coil, twist, or follow a curving path"),
                new("MOVING_AIR", "moving air or weather")
            ]),
        new(
            "live",
            "REAL_TIME_OR_ACTIVE",
            "lˈIv",
            [
                new(
                    "RESIDE_OR_REMAIN_ALIVE",
                    "the verb meaning to reside, inhabit a place, or remain alive, including ordinary subject-verb uses, relative clauses such as people who live nearby or people who live abroad, and expressions such as live and learn or long live; choose this for the target in 'people who live abroad' even if a different occurrence later describes a live stream"),
                new(
                    "REAL_TIME_OR_ACTIVE",
                    "the adjective or adverb meaning happening in real time, active, connected, or not prerecorded; do not use this label for the ordinary verb meaning reside or remain alive")
            ])
    ];

    public static IReadOnlyList<ContextualPronunciationTarget> FindTargets(
        PreparedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var targets = new List<ContextualPronunciationTarget>();
        foreach (var unit in document.Units)
        {
            if (unit.Kind is ReadingUnitKind.CodeBlock or ReadingUnitKind.StructuredBlock)
            {
                continue;
            }

            foreach (var rule in All)
            {
                var originalMatches = Matches(unit.OriginalText, rule.Word);
                var spokenMatches = Matches(unit.SpokenText, rule.Word);
                var count = Math.Min(originalMatches.Count, spokenMatches.Count);
                for (var index = 0; index < count; index++)
                {
                    targets.Add(
                        new ContextualPronunciationTarget(
                            unit.Index,
                            unit.OriginalText,
                            rule,
                            index + 1,
                            spokenMatches[index].Index,
                            spokenMatches[index].Length,
                            spokenMatches[index].Value));
                }
            }
        }

        return targets
            .OrderBy(target => target.UnitIndex)
            .ThenBy(target => target.SpokenStart)
            .ToArray();
    }

    public static PreparedDocument ApplyLabels(
        PreparedDocument document,
        IReadOnlyList<ContextualPronunciationTarget> targets,
        IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(labels);
        if (targets.Count != labels.Count)
        {
            throw new ArgumentException("Each contextual-pronunciation target requires one label.");
        }

        var replacementsByUnit = targets
            .Select((target, index) => new { Target = target, Label = labels[index] })
            .Where(item => string.Equals(
                item.Label,
                item.Target.Rule.PhonemeLabel,
                StringComparison.Ordinal))
            .GroupBy(item => item.Target.UnitIndex)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Target.SpokenStart)
                    .ToArray());
        if (replacementsByUnit.Count == 0)
        {
            return document;
        }

        var units = document.Units
            .Select(unit =>
            {
                if (!replacementsByUnit.TryGetValue(unit.Index, out var replacements))
                {
                    return unit;
                }

                var spoken = unit.SpokenText;
                foreach (var replacement in replacements)
                {
                    var target = replacement.Target;
                    var phonemeLiteral = $"[{target.SpokenWord}](/{target.Rule.Phonemes}/)";
                    spoken = string.Concat(
                        spoken.AsSpan(0, target.SpokenStart),
                        phonemeLiteral,
                        spoken.AsSpan(target.SpokenStart + target.SpokenLength));
                }

                return unit with { SpokenText = spoken };
            })
            .ToArray();
        return document with { Units = Array.AsReadOnly(units) };
    }

    internal static string MarkTargetOccurrence(
        string context,
        string targetWord,
        int occurrence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetWord);

        var matches = Matches(context, targetWord);
        if (occurrence < 1 || occurrence > matches.Count)
        {
            throw new InvalidOperationException(
                $"Target occurrence {occurrence} of '{targetWord}' was not found.");
        }

        var targetMatch = matches[occurrence - 1];
        var windowStart = 0;
        var windowEnd = context.Length;
        if (matches.Count > 1)
        {
            var words = Regex.Matches(context, @"\S+");
            var targetWordIndex = Enumerable.Range(0, words.Count)
                .First(index => words[index].Index <= targetMatch.Index
                                && words[index].Index + words[index].Length
                                    >= targetMatch.Index + targetMatch.Length);
            var firstWordIndex = Math.Max(0, targetWordIndex - 3);
            var lastWordIndex = Math.Min(words.Count - 1, targetWordIndex + 3);
            windowStart = words[firstWordIndex].Index;
            windowEnd = words[lastWordIndex].Index + words[lastWordIndex].Length;
        }

        var marked = new System.Text.StringBuilder(windowEnd - windowStart + 32);
        var sourceIndex = windowStart;
        foreach (Match match in matches)
        {
            if (match.Index < windowStart || match.Index + match.Length > windowEnd)
            {
                continue;
            }

            marked.Append(context, sourceIndex, match.Index - sourceIndex);
            if (match.Index == targetMatch.Index)
            {
                marked.Append("<target>").Append(match.Value).Append("</target>");
            }
            else
            {
                marked.Append("<other-occurrence/>");
            }

            sourceIndex = match.Index + match.Length;
        }

        marked.Append(context, sourceIndex, windowEnd - sourceIndex);
        return marked.ToString();
    }

    private static MatchCollection Matches(string text, string word) =>
        Regex.Matches(
            text,
            $@"\b{Regex.Escape(word)}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

internal sealed record ContextualPronunciationRule(
    string Word,
    string PhonemeLabel,
    string Phonemes,
    IReadOnlyList<ContextualPronunciationLabel> Labels);

internal sealed record ContextualPronunciationLabel(string Name, string Definition);

internal sealed record ContextualPronunciationTarget(
    int UnitIndex,
    string Context,
    ContextualPronunciationRule Rule,
    int Occurrence,
    int SpokenStart,
    int SpokenLength,
    string SpokenWord);
