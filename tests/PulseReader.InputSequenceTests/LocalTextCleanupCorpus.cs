using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

internal static class LocalTextCleanupCorpus
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyList<LocalTextCleanupCase> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "local-text-cleanup-corpus.json");
        return LoadJson(File.ReadAllText(path), path);
    }

    internal static IReadOnlyList<LocalTextCleanupCase> LoadJson(string json, string source)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"The local text-cleanup corpus '{source}' is empty.");
        }

        LocalTextCleanupCase[] corpus;
        try
        {
            corpus = JsonSerializer.Deserialize<LocalTextCleanupCase[]>(json, SerializerOptions)
                     ?? throw new InvalidOperationException(
                         $"The local text-cleanup corpus '{source}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The local text-cleanup corpus '{source}' is invalid: {exception.Message}",
                exception);
        }

        if (corpus.Length == 0)
        {
            throw new InvalidOperationException($"The local text-cleanup corpus '{source}' is empty.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < corpus.Length; index++)
        {
            ValidateCase(corpus[index], index, source);
            if (!ids.Add(corpus[index].Id))
            {
                throw new InvalidOperationException(
                    $"The local text-cleanup corpus '{source}' contains duplicate id '{corpus[index].Id}'.");
            }
        }

        return Array.AsReadOnly(corpus);
    }

    private static void ValidateCase(LocalTextCleanupCase? example, int index, string source)
    {
        if (example is null)
        {
            throw InvalidCase(source, index, "the case is null");
        }

        RequireText(example.Id, nameof(example.Id));
        if (!Regex.IsMatch(
                example.Id,
                @"^[a-z0-9]+(?:-[a-z0-9]+)*$",
                RegexOptions.CultureInvariant))
        {
            throw InvalidCase(
                source,
                index,
                "Id must contain lowercase letters or digits separated by single hyphens");
        }

        RequireText(example.Category, nameof(example.Category));
        RequireText(example.Title, nameof(example.Title));
        RequireText(example.OriginalText, nameof(example.OriginalText));
        RequireText(example.ReviewGoal, nameof(example.ReviewGoal));
        if (example.ProtectedSequences is null || example.ProtectedSequences.Count == 0)
        {
            throw InvalidCase(source, index, "ProtectedSequences must not be empty");
        }

        for (var sequenceIndex = 0; sequenceIndex < example.ProtectedSequences.Count; sequenceIndex++)
        {
            var sequence = example.ProtectedSequences[sequenceIndex];
            if (sequence is null)
            {
                throw InvalidCase(source, index, $"protected sequence {sequenceIndex + 1} is null");
            }

            RequireText(sequence.Description, $"ProtectedSequences[{sequenceIndex}].Description");
            if (sequence.Terms is null || sequence.Terms.Count == 0)
            {
                throw InvalidCase(
                    source,
                    index,
                    $"ProtectedSequences[{sequenceIndex}].Terms must not be empty");
            }

            for (var termIndex = 0; termIndex < sequence.Terms.Count; termIndex++)
            {
                var term = sequence.Terms[termIndex];
                if (term is null)
                {
                    throw InvalidCase(
                        source,
                        index,
                        $"ProtectedSequences[{sequenceIndex}].Terms[{termIndex}] is null");
                }

                RequireText(
                    term.Value,
                    $"ProtectedSequences[{sequenceIndex}].Terms[{termIndex}].Value");
                if (term.SourceValue is not null && string.IsNullOrWhiteSpace(term.SourceValue))
                {
                    throw InvalidCase(
                        source,
                        index,
                        $"ProtectedSequences[{sequenceIndex}].Terms[{termIndex}].SourceValue is blank");
                }
            }
        }

        var sourceViolations = LocalProtectedTermMatcher.FindViolations(
            example.OriginalText,
            example.SourceProtectedSequences);
        if (sourceViolations.Count > 0)
        {
            throw InvalidCase(
                source,
                index,
                "protected source expectations do not match the original text: "
                + string.Join(" ", sourceViolations));
        }

        return;

        void RequireText(string? value, string property)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw InvalidCase(source, index, $"{property} must not be blank");
            }
        }
    }

    private static InvalidOperationException InvalidCase(string source, int index, string message) =>
        new($"The local text-cleanup corpus '{source}' has an invalid case at index {index}: {message}.");
}

internal sealed record LocalTextCleanupCase(
    string Id,
    string Category,
    string Title,
    string OriginalText,
    string ReviewGoal,
    string? Instruction,
    bool ExpectKeep,
    IReadOnlyList<LocalProtectedSequence> ProtectedSequences)
{
    [JsonIgnore]
    public IReadOnlyList<string> RequiredTerms =>
        SourceProtectedSequences
            .SelectMany(sequence => sequence.Terms)
            .Select(term => term.Value)
            .ToArray();

    [JsonIgnore]
    public IReadOnlyList<LocalProtectedSequence> SourceProtectedSequences =>
        ProtectedSequences
            .Select(
                sequence => sequence with
                {
                    Terms = sequence.Terms
                        .Select(
                            term => term with
                            {
                                Value = term.SourceValue ?? term.Value,
                                AllowSeparatorFlexibility = false
                            })
                        .ToArray()
                })
            .ToArray();
}

internal sealed record LocalProtectedSequence(
    string Description,
    IReadOnlyList<LocalProtectedTerm> Terms);

internal sealed record LocalProtectedTerm(
    string Value,
    string? SourceValue = null,
    bool AllowSeparatorFlexibility = false);
