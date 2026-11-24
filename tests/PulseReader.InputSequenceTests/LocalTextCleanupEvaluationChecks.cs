using PulseReader.TextPreparation;

internal static class LocalTextCleanupEvaluationChecks
{
    public static async Task RunAsync()
    {
        CheckProtectedContentMatching();
        CheckCorpusIntegrity();
        CheckGuardAccounting();
        await CheckRuntimeVersionTimeoutAsync();
        Console.WriteLine(
            "PASS: local text-cleanup protection, corpus integrity, guard accounting, and runtime-version timeout validate.");
    }

    private static void CheckProtectedContentMatching()
    {
        AssertNoViolations(
            "Build 1.05 used Café and af heart.",
            [
                Sequence("exact numeric punctuation", Term("1.05")),
                Sequence("canonical Unicode", Term("Cafe\u0301")),
                Sequence(
                    "configured separator flexibility",
                    Term("af_heart", allowSeparatorFlexibility: true))
            ]);

        AssertHasViolation(
            "Build 1/05 completed.",
            [Sequence("exact numeric punctuation", Term("1.05"))],
            "A slash must not satisfy a protected decimal point.");
        AssertHasViolation(
            "Use af heart.",
            [Sequence("exact identifier punctuation", Term("af_heart"))],
            "Identifier separator flexibility must be explicitly enabled.");
        AssertHasViolation(
            "Beta came before Alpha.",
            [Sequence("ordered content", Term("Alpha"), Term("Beta"))],
            "Protected terms must remain ordered.");
        AssertHasViolation(
            "Ready once.",
            [Sequence("multiplicity", Term("Ready"), Term("Ready"))],
            "One occurrence must not satisfy two protected occurrences.");
        AssertHasViolation(
            "0 backends installed. 2 errors reported.",
            [Sequence("count relationship", Term("0 errors reported"))],
            "Independent values must not satisfy a protected relationship.");
        AssertHasViolation(
            "Release published.",
            [Sequence("negation", Term("Release not published"))],
            "Removing a protected negation must fail.");
    }

    private static void CheckCorpusIntegrity()
    {
        var corpus = LocalTextCleanupCorpus.Load();
        Assert(corpus.Count == 18, "The local text-cleanup corpus must contain all 18 review cases.");
        var pathCase = corpus.Single(example => example.Id == "email-and-file");
        var pathTerm = pathCase.ProtectedSequences.SelectMany(sequence => sequence.Terms)
            .Single(term => term.SourceValue == @"C:\Tools\Pulse Reader");
        Assert(pathTerm.Value == @"C colon \Tools\Pulse Reader",
            "The path fixture must retain both the exact source path and its spoken drive colon.");
        var preparedPathCase = SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(pathCase.OriginalText)).BuildSpokenText();
        AssertHasViolation(preparedPathCase.Replace("C colon", "D colon", StringComparison.Ordinal),
            pathCase.ProtectedSequences, "Changing the protected path's drive must fail.");
        AssertHasViolation(preparedPathCase.Replace(@"\Tools\", @"\Other\", StringComparison.Ordinal),
            pathCase.ProtectedSequences, "Changing a protected path component must fail.");
        foreach (var example in corpus.Where(example => example.Id != "csharp-code"))
        {
            var deterministicText = SpokenTextFormatter.Format(
                    ReadingUnitAnalyzer.Prepare(example.OriginalText))
                .BuildSpokenText();
            var violations = LocalProtectedTermMatcher.FindViolations(
                deterministicText,
                example.ProtectedSequences);
            Assert(
                violations.Count == 0,
                $"Corpus case '{example.Id}' rejects its deterministic spoken text: "
                + string.Join(" ", violations)
                + $"{Environment.NewLine}Deterministic text: {deterministicText}");
        }

        var timestampCases = corpus
            .Where(example => example.Id is "accepted-timestamp-control" or "stack-trace")
            .ToArray();
        Assert(timestampCases.Length == 2, "Both timestamp cases must remain in the corpus.");
        foreach (var example in timestampCases)
        {
            var protectedTimestamps = example.ProtectedSequences
                .SelectMany(sequence => sequence.Terms)
                .Count(term => term.SourceValue is not null && term.SourceValue.Count(character => character == ':') == 2);
            Assert(
                protectedTimestamps == 2,
                $"Timestamp case '{example.Id}' must protect both timestamps.");
        }

        var jsonCase = corpus.Single(example => example.Id == "json-settings");
        var decimalTerm = jsonCase.ProtectedSequences
            .SelectMany(sequence => sequence.Terms)
            .Single(term => term.Value == "1.05");
        Assert(
            !decimalTerm.AllowSeparatorFlexibility,
            "The protected JSON decimal must keep exact punctuation.");

        var statusCase = corpus.Single(example => example.Id == "status-lines");
        Assert(
            statusCase.ProtectedSequences
                .SelectMany(sequence => sequence.Terms)
                .Any(term => term.Value == "0 errors reported"),
            "The status corpus must protect each count together with its status.");
        var checklistCase = corpus.Single(example => example.Id == "checklist");
        Assert(
            checklistCase.ProtectedSequences
                .SelectMany(sequence => sequence.Terms)
                .Any(term => term.Value == "Release not published"),
            "The checklist corpus must protect the publication negation.");

        ExpectInvalidCorpus("", "empty text");
        ExpectInvalidCorpus("[]", "empty array");
        ExpectInvalidCorpus("[{\"id\":\"broken\"}]", "missing structure");
        ExpectInvalidCorpus(
            System.Text.Json.JsonSerializer.Serialize(new[] { corpus[0], corpus[0] }),
            "duplicate id");
        ExpectInvalidCorpus(
            System.Text.Json.JsonSerializer.Serialize(new[]
            {
                corpus[0] with { Id = @"..\outside" }
            }),
            "unsafe id");

        var outputRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert(
            LocalTextCleanupEvaluation.ResolveOutputFilePath(outputRoot, "case.wav")
                .StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase),
            "A safe evaluation filename did not remain inside its output directory.");
        try
        {
            LocalTextCleanupEvaluation.ResolveOutputFilePath(outputRoot, @"..\outside.wav");
            throw new InvalidOperationException(
                "The evaluation output guard accepted a traversal path.");
        }
        catch (InvalidOperationException exception)
            when (exception.Message.StartsWith(
                "Refusing to write evaluation output outside",
                StringComparison.Ordinal))
        {
            // Expected.
        }
    }

    private static void CheckGuardAccounting()
    {
        var decisions = new[]
        {
            Decision("keep", applied: false),
            Decision("replace", applied: true),
            Decision("replace", applied: false, guardMessage: "unit guard"),
            Decision("invalid", applied: false, guardMessage: "invalid action")
        };
        var rejected = LocalTextCleanupEvaluation.RejectAppliedReplacements(
            decisions,
            "case guard");

        Assert(
            rejected[0].GuardMessage is null,
            "A case-level guard must not mark an unrelated keep decision as guarded.");
        Assert(
            rejected[1].GuardMessage == "case guard" && !rejected[1].Applied,
            "A case-level guard must reject the replacement that it rolled back.");
        Assert(
            LocalTextCleanupEvaluation.CountGuardedReplacements(rejected) == 2,
            "Guarded-replacement totals must include only replace decisions.");
    }

    private static async Task CheckRuntimeVersionTimeoutAsync()
    {
        var probeExecutable = Path.ChangeExtension(
            typeof(LocalTextCleanupEvaluationChecks).Assembly.Location,
            ".exe");
        var probeModelPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "startup-cleanup-probe.gguf");
        var processId = 0;
        try
        {
            await LocalTextCleanupEvaluation.ReadRuntimeVersionAsync(
                probeExecutable,
                ["-m", probeModelPath],
                TimeSpan.FromMilliseconds(500),
                startedProcessId => processId = startedProcessId);
            throw new InvalidOperationException(
                "The runtime-version timeout probe unexpectedly completed.");
        }
        catch (TimeoutException)
        {
            // Expected: the helper kills the deliberately blocked child before returning.
        }

        Assert(
            processId > 0 && !IsProcessRunning(processId),
            "The runtime-version timeout retained its child process.");
    }

    private static LocalProtectedSequence Sequence(
        string description,
        params LocalProtectedTerm[] terms) =>
        new(description, terms);

    private static LocalProtectedTerm Term(
        string value,
        bool allowSeparatorFlexibility = false) =>
        new(value, AllowSeparatorFlexibility: allowSeparatorFlexibility);

    private static LocalCleanupUnitDecision Decision(
        string action,
        bool applied,
        string? guardMessage = null) =>
        new(
            0,
            ReadingUnitKind.Sentence,
            action,
            string.Empty,
            null,
            string.Empty,
            applied,
            guardMessage,
            0,
            0,
            0,
            0,
            "{}");

    private static void AssertNoViolations(
        string text,
        IReadOnlyList<LocalProtectedSequence> sequences) =>
        Assert(
            LocalProtectedTermMatcher.FindViolations(text, sequences).Count == 0,
            "Valid protected content was rejected.");

    private static void AssertHasViolation(
        string text,
        IReadOnlyList<LocalProtectedSequence> sequences,
        string message) =>
        Assert(LocalProtectedTermMatcher.FindViolations(text, sequences).Count > 0, message);

    private static void ExpectInvalidCorpus(string json, string label)
    {
        try
        {
            LocalTextCleanupCorpus.LoadJson(json, label);
            throw new InvalidOperationException(
                $"The corpus integrity check accepted {label} content.");
        }
        catch (InvalidOperationException exception)
            when (exception.Message.StartsWith(
                "The local text-cleanup corpus",
                StringComparison.Ordinal))
        {
            // Expected.
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
