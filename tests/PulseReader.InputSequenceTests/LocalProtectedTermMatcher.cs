using System.Text;
using System.Text.RegularExpressions;

internal static class LocalProtectedTermMatcher
{
    public static IReadOnlyList<string> FindMissingTerms(
        string? text,
        IReadOnlyList<string> requiredTerms) =>
        FindMissingTerms(
            text,
            requiredTerms,
            new HashSet<string>(StringComparer.Ordinal));

    public static IReadOnlyList<string> FindMissingTerms(
        string? text,
        IReadOnlyList<string> requiredTerms,
        IReadOnlySet<string> separatorFlexibleTerms)
    {
        ArgumentNullException.ThrowIfNull(requiredTerms);
        ArgumentNullException.ThrowIfNull(separatorFlexibleTerms);

        return requiredTerms
            .Where(
                term => FindViolations(
                        text,
                        [
                            new LocalProtectedSequence(
                                "required term",
                                [
                                    new LocalProtectedTerm(
                                        term,
                                        AllowSeparatorFlexibility: separatorFlexibleTerms.Contains(term))
                                ])
                        ])
                    .Count > 0)
            .ToArray();
    }

    public static IReadOnlyList<string> FindViolations(
        string? text,
        IReadOnlyList<LocalProtectedSequence> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        var normalizedText = (text ?? string.Empty).Normalize(NormalizationForm.FormC);
        var violations = new List<string>();

        foreach (var sequence in sequences)
        {
            var searchStart = 0;
            for (var termIndex = 0; termIndex < sequence.Terms.Count; termIndex++)
            {
                var term = sequence.Terms[termIndex];
                var pattern = BuildPattern(term);
                var match = pattern.Match(normalizedText, searchStart);
                if (match.Success)
                {
                    searchStart = match.Index + match.Length;
                    continue;
                }

                var earlierMatch = pattern.Match(normalizedText);
                var reason = earlierMatch.Success && earlierMatch.Index < searchStart
                    ? "was out of order or did not appear enough times"
                    : "was missing";
                violations.Add($"{sequence.Description}: '{term.Value}' {reason}.");
                break;
            }
        }

        return violations;
    }

    private static Regex BuildPattern(LocalProtectedTerm term)
    {
        var value = term.Value.Trim().Normalize(NormalizationForm.FormC);
        var contentPattern = term.AllowSeparatorFlexibility
            ? BuildSeparatorFlexiblePattern(value)
            : BuildExactPattern(value);
        return new Regex(
            $@"(?<![\p{{L}}\p{{N}}]){contentPattern}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string BuildExactPattern(string value)
    {
        var result = new StringBuilder(value.Length * 2);
        var position = 0;
        foreach (Match whitespace in Regex.Matches(value, @"\s+", RegexOptions.CultureInvariant))
        {
            result.Append(Regex.Escape(value[position..whitespace.Index]));
            result.Append(@"\s+");
            position = whitespace.Index + whitespace.Length;
        }

        result.Append(Regex.Escape(value[position..]));
        return result.ToString();
    }

    private static string BuildSeparatorFlexiblePattern(string value)
    {
        var components = Regex.Matches(
                value,
                @"[\p{L}\p{N}]+",
                RegexOptions.CultureInvariant)
            .Select(match => Regex.Escape(match.Value))
            .ToArray();
        if (components.Length < 2)
        {
            throw new InvalidOperationException(
                $"Separator-flexible protected term '{value}' needs at least two text components.");
        }

        return string.Join(@"[\p{P}\p{S}\s]+", components);
    }
}
