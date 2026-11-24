using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class LocalTargetedModelEvaluation
{
    private const int InferenceThreads = 8;
    private const string EvaluatorRevision = "local-targeted-v3-balanced-live-held-out";
    private const string CancellationMeasurementScope =
        "The cancellation probe records client cancellation completion, a later health check, and a follow-up request. It does not measure when llama.cpp released its inference slot.";

    public static async Task RunAsync(
        string outputDirectory,
        string? modelFile = null,
        int reasoningBudget = 0)
    {
        var repositoryRoot = FindRepositoryRoot();
        var artifactRoot = Path.Combine(repositoryRoot, "artifacts", "local-text-cleanup");
        var serverPath = Path.Combine(artifactRoot, "runtime", "llama-server.exe");
        var modelPath = Path.Combine(
            artifactRoot,
            "models",
            modelFile ?? "Qwen3-0.6B-Q4_0.gguf");
        RequireFile(serverPath, "Run tools/setup-local-text-cleanup.ps1 to install llama.cpp.");
        RequireFile(modelPath, "Run tools/setup-local-text-cleanup.ps1 to install the evaluation model.");

        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        var modelSha256 = await HashFileAsync(modelPath);
        var runtimeVersion = await LocalTextCleanupEvaluation.ReadRuntimeVersionAsync(serverPath);
        var evaluatorSourceSha256 = await HashEvaluatorSourcesAsync(repositoryRoot);
        var classifications = new List<LocalSenseCaseResult>(SenseCases.Length);
        var transformations = new List<LocalTransformationCaseResult>(TransformationCases.Length);
        LocalCancellationProbe cancellationProbe;
        double startupMilliseconds;
        long privateMemoryAfterLoad;
        long privateMemoryAfterIdle;
        long maximumSampledPrivateMemory;
        double shutdownMilliseconds;
        bool releasedAfterShutdown;
        var serverProcessId = 0;

        var server = new LocalLlamaServer(
            serverPath,
            modelPath,
            fullOutputDirectory,
            InferenceThreads,
            reasoningBudget);
        try
        {
            await server.StartAsync(CancellationToken.None);
            startupMilliseconds = server.StartupTime.TotalMilliseconds;
            privateMemoryAfterLoad = server.PrivateMemoryBytes;
            maximumSampledPrivateMemory = privateMemoryAfterLoad;
            serverProcessId = server.ProcessId;

            foreach (var example in SenseCases)
            {
                var response = await server.ClassifySenseAsync(example, CancellationToken.None);
                classifications.Add(new LocalSenseCaseResult(
                    example,
                    response,
                    string.Equals(response.Label, example.ExpectedLabel, StringComparison.Ordinal),
                    string.Equals(
                        response.RawJson.Trim(),
                        response.Label,
                        StringComparison.Ordinal)));
                maximumSampledPrivateMemory = Math.Max(
                    maximumSampledPrivateMemory,
                    server.PrivateMemoryBytes);
            }

            foreach (var example in TransformationCases)
            {
                var response = await server.TransformAsync(example, CancellationToken.None);
                var missingTerms = FindMissingTerms(response.SpokenText, example.RequiredTerms);
                transformations.Add(new LocalTransformationCaseResult(example, response, missingTerms));
                maximumSampledPrivateMemory = Math.Max(
                    maximumSampledPrivateMemory,
                    server.PrivateMemoryBytes);
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
            privateMemoryAfterIdle = server.PrivateMemoryBytes;
            maximumSampledPrivateMemory = Math.Max(
                maximumSampledPrivateMemory,
                privateMemoryAfterIdle);
            cancellationProbe = await server.RunCancellationProbeAsync(CancellationToken.None);
            maximumSampledPrivateMemory = Math.Max(
                maximumSampledPrivateMemory,
                server.PrivateMemoryBytes);
        }
        finally
        {
            var shutdownStopwatch = Stopwatch.StartNew();
            await server.DisposeAsync();
            shutdownStopwatch.Stop();
            shutdownMilliseconds = shutdownStopwatch.Elapsed.TotalMilliseconds;
            releasedAfterShutdown = serverProcessId == 0 || !IsProcessRunning(serverProcessId);
        }

        var requestTimes = classifications
            .Select(result => result.Response.ElapsedMilliseconds)
            .ToArray();
        var warmRequestTimes = requestTimes.Skip(1).ToArray();
        var restartProbe = await RunRestartProbeAsync(
            serverPath,
            modelPath,
            fullOutputDirectory,
            SenseCases[0],
            reasoningBudget);
        var report = new LocalTargetedEvaluationReport(
            DateTimeOffset.UtcNow,
            Path.GetFileName(modelPath),
            modelSha256,
            runtimeVersion,
            InferenceThreads,
            EvaluatorRevision,
            evaluatorSourceSha256,
            reasoningBudget,
            startupMilliseconds,
            requestTimes[0],
            Percentile(warmRequestTimes, 0.5),
            Percentile(warmRequestTimes, 0.95),
            privateMemoryAfterLoad / 1024d / 1024d,
            privateMemoryAfterIdle / 1024d / 1024d,
            maximumSampledPrivateMemory / 1024d / 1024d,
            shutdownMilliseconds,
            releasedAfterShutdown,
            CancellationMeasurementScope,
            cancellationProbe,
            restartProbe,
            classifications,
            transformations);

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "focused-results.json"),
            JsonSerializer.Serialize(report, jsonOptions));
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "focused-report.md"),
            BuildMarkdown(report));

        Console.WriteLine(
            $"Sense classification on the bounded diagnostic corpus: {classifications.Count(result => result.Correct)}/{classifications.Count}");
        Console.WriteLine(
            $"Exact-label output: {classifications.Count(result => result.ExactOutput)}/{classifications.Count}");
        Console.WriteLine(
            $"Transformations passing the protected-term check: {transformations.Count(result => result.MissingTerms.Count == 0)}/{transformations.Count}");
        Console.WriteLine($"Focused report: {Path.Combine(fullOutputDirectory, "focused-report.md")}");
    }

    private static IReadOnlyList<string> FindMissingTerms(
        string? text,
        IReadOnlyList<string> requiredTerms) =>
        LocalProtectedTermMatcher.FindMissingTerms(text, requiredTerms);

    private static string BuildMarkdown(LocalTargetedEvaluationReport report)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine("# Focused local-model evaluation");
        markdown.AppendLine();
        markdown.AppendLine($"Generated UTC: {HtmlCode(report.GeneratedUtc.ToString("O"))}");
        markdown.AppendLine();
        markdown.AppendLine($"Model: {HtmlCode(report.ModelFile)}");
        markdown.AppendLine();
        markdown.AppendLine($"Model SHA-256: {HtmlCode(report.ModelSha256)}");
        markdown.AppendLine();
        markdown.AppendLine("llama.cpp version:");
        markdown.AppendLine();
        markdown.AppendLine("<pre><code>");
        markdown.AppendLine(WebUtility.HtmlEncode(report.RuntimeVersion));
        markdown.AppendLine("</code></pre>");
        markdown.AppendLine();
        markdown.AppendLine($"Inference threads: {report.InferenceThreads}");
        markdown.AppendLine();
        markdown.AppendLine($"Evaluator revision: {HtmlCode(report.EvaluatorRevision)}");
        markdown.AppendLine();
        markdown.AppendLine($"Evaluator source SHA-256: {HtmlCode(report.EvaluatorSourceSha256)}");
        markdown.AppendLine();
        markdown.AppendLine($"Reasoning budget: {report.ReasoningBudgetTokens} tokens");
        markdown.AppendLine();
        markdown.AppendLine($"Load: {report.StartupMilliseconds:0} ms");
        markdown.AppendLine();
        markdown.AppendLine($"First request: {report.FirstRequestMilliseconds:0} ms");
        markdown.AppendLine();
        markdown.AppendLine($"Warm median / P95: {report.WarmMedianMilliseconds:0} / {report.WarmP95Milliseconds:0} ms");
        markdown.AppendLine();
        markdown.AppendLine($"Private memory after load / 5 s idle: {report.PrivateMemoryAfterLoadMegabytes:0} / {report.PrivateMemoryAfterIdleMegabytes:0} MB");
        markdown.AppendLine();
        markdown.AppendLine($"Maximum sampled private memory: {report.MaximumSampledPrivateMemoryMegabytes:0} MB");
        markdown.AppendLine();
        markdown.AppendLine($"Cancellation probe: {report.CancellationMeasurementScope}");
        markdown.AppendLine();
        markdown.AppendLine("The probe's raw measurements are in `focused-results.json`.");
        markdown.AppendLine();
        markdown.AppendLine($"Unload: {report.ShutdownMilliseconds:0} ms; process released: {report.ProcessReleasedAfterShutdown}");
        markdown.AppendLine();
        markdown.AppendLine($"Reload: {report.RestartProbe.StartupMilliseconds:0} ms; first classification: {report.RestartProbe.FirstRequestMilliseconds:0} ms; correct: {report.RestartProbe.Correct}; second process released: {report.RestartProbe.ProcessReleasedAfterShutdown}");
        markdown.AppendLine();
        markdown.AppendLine("## Context-only pronunciation classification");
        markdown.AppendLine();
        markdown.AppendLine("The model receives the sentence, target occurrence, and closed label definitions. It never proposes spoken text.");
        markdown.AppendLine();
        markdown.AppendLine("The prompt contains no worked examples from this corpus. Each target word has an equal number of cases for its two labels.");
        markdown.AppendLine();
        markdown.AppendLine("This is a bounded diagnostic set, not an estimate of general accuracy. Close case counts do not establish that one model is generally better than another.");
        foreach (var result in report.Classifications)
        {
            markdown.AppendLine();
            markdown.AppendLine($"### {(result.Correct ? "PASS" : "FAIL")} - {result.Example.Id}");
            markdown.AppendLine();
            markdown.AppendLine(result.Example.Context);
            markdown.AppendLine();
            markdown.AppendLine($"Expected `{result.Example.ExpectedLabel}`; received `{result.Response.Label ?? "invalid"}` in {result.Response.ElapsedMilliseconds:0} ms.");
            if (!result.ExactOutput)
            {
                markdown.AppendLine();
                markdown.AppendLine("The accepted label was embedded in additional output rather than returned exactly.");
            }
        }

        markdown.AppendLine();
        markdown.AppendLine("## Caller-directed transformations");
        markdown.AppendLine();
        markdown.AppendLine("The caller has already decided that each transformation is required. The model cannot keep, decline, or choose another task.");
        foreach (var result in report.Transformations)
        {
            markdown.AppendLine();
            markdown.AppendLine($"### {result.Example.Id}");
            markdown.AppendLine();
            markdown.AppendLine($"Task: {result.Example.Instruction}");
            markdown.AppendLine();
            markdown.AppendLine("Output:");
            markdown.AppendLine();
            markdown.AppendLine("<pre><code>");
            markdown.AppendLine(WebUtility.HtmlEncode(
                result.Response.SpokenText ?? "[invalid output]"));
            markdown.AppendLine("</code></pre>");
            markdown.AppendLine();
            markdown.AppendLine(result.MissingTerms.Count == 0
                ? $"The protected-term matcher found no listed term missing in {result.Response.ElapsedMilliseconds:0} ms. This does not establish full fidelity."
                : $"The protected-term matcher found missing terms: {string.Join(", ", result.MissingTerms)}. This does not assess other aspects of fidelity.");
        }

        return markdown.ToString();
    }

    private static string HtmlCode(string value) =>
        $"<code>{WebUtility.HtmlEncode(value)}</code>";

    private static async Task<LocalRestartProbe> RunRestartProbeAsync(
        string serverPath,
        string modelPath,
        string outputDirectory,
        LocalSenseCase example,
        int reasoningBudget)
    {
        var restartOutputDirectory = Path.Combine(outputDirectory, "restart-probe");
        var server = new LocalLlamaServer(
            serverPath,
            modelPath,
            restartOutputDirectory,
            InferenceThreads,
            reasoningBudget);
        var processId = 0;
        double startupMilliseconds;
        LocalSenseClassification response;
        double shutdownMilliseconds;
        bool processReleasedAfterShutdown;
        try
        {
            await server.StartAsync(CancellationToken.None);
            processId = server.ProcessId;
            startupMilliseconds = server.StartupTime.TotalMilliseconds;
            response = await server.ClassifySenseAsync(example, CancellationToken.None);
        }
        finally
        {
            var shutdownStopwatch = Stopwatch.StartNew();
            await server.DisposeAsync();
            shutdownStopwatch.Stop();
            shutdownMilliseconds = shutdownStopwatch.Elapsed.TotalMilliseconds;
            processReleasedAfterShutdown = processId == 0 || !IsProcessRunning(processId);
        }

        return new LocalRestartProbe(
            startupMilliseconds,
            response.ElapsedMilliseconds,
            string.Equals(response.Label, example.ExpectedLabel, StringComparison.Ordinal),
            shutdownMilliseconds,
            processReleasedAfterShutdown);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.OrderBy(value => value).ToArray();
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PulseReader.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The Pulse Reader repository root could not be found.");
    }

    private static void RequireFile(string path, string instruction)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(instruction, path);
        }
    }

    private static async Task<string> HashEvaluatorSourcesAsync(string repositoryRoot)
    {
        var testProjectRoot = Path.Combine(
            repositoryRoot,
            "tests",
            "PulseReader.InputSequenceTests");
        var sourceFiles = new[]
        {
            Path.Combine(testProjectRoot, "LocalTargetedModelEvaluation.cs"),
            Path.Combine(testProjectRoot, "LocalLlamaServer.cs"),
            Path.Combine(testProjectRoot, "LocalProtectedTermMatcher.cs")
        };
        var components = new List<string>(sourceFiles.Length);
        foreach (var sourceFile in sourceFiles)
        {
            components.Add(
                Path.GetFileName(sourceFile)
                + ":"
                + await HashFileAsync(sourceFile));
        }

        var sourceManifest = Encoding.UTF8.GetBytes(string.Join("\n", components));
        return Convert.ToHexString(SHA256.HashData(sourceManifest)).ToLowerInvariant();
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static readonly LocalSenseCase[] SenseCases =
    [
        Sense("axes-graph", "axes", 1, "Set the X and Y axes to zero before exporting the graph.", "COORDINATE_DIMENSIONS", AxesLabels()),
        Sense("axes-tools", "axes", 1, "Sharpen both axes before splitting the logs.", "CUTTING_TOOLS", AxesLabels()),
        Sense("axes-two-occurrences-first", "axes", 1, "The axes hung beside the chart whose axes were unlabeled.", "CUTTING_TOOLS", AxesLabels()),
        Sense("axes-two-occurrences-second", "axes", 2, "The axes hung beside the chart whose axes were unlabeled.", "COORDINATE_DIMENSIONS", AxesLabels()),
        Sense("axes-hung-beside-chart", "axes", 1, "The axes hung beside the chart.", "CUTTING_TOOLS", AxesLabels()),
        Sense("axes-firefighter", "axes", 1, "The firefighter carried two axes into the shed.", "CUTTING_TOOLS", AxesLabels()),
        Sense("axes-chart", "axes", 1, "Label the horizontal and vertical axes on the chart.", "COORDINATE_DIMENSIONS", AxesLabels()),
        Sense("axes-rotation", "axes", 1, "The model can rotate around all three axes.", "COORDINATE_DIMENSIONS", AxesLabels()),
        Sense("bass-audio", "bass", 1, "Reduce the bass slightly, then listen to the speakers again.", "LOW_FREQUENCY_SOUND", BassLabels()),
        Sense("bass-fish", "bass", 1, "The bass swam beneath the fishing boat.", "FISH", BassLabels()),
        Sense("bass-distractor", "bass", 1, "Put away the fishing rod. The bass from the speakers is overpowering the vocals.", "LOW_FREQUENCY_SOUND", BassLabels()),
        Sense("bass-band", "bass", 1, "She plays bass in the band every Friday.", "LOW_FREQUENCY_SOUND", BassLabels()),
        Sense("bass-largemouth", "bass", 1, "They caught a largemouth bass near the reeds.", "FISH", BassLabels()),
        Sense("bass-grilling", "bass", 1, "Season the bass before grilling it.", "FISH", BassLabels()),
        Sense("bass-two-occurrences-first", "bass", 1, "The bass player released the bass back into the lake.", "LOW_FREQUENCY_SOUND", BassLabels()),
        Sense("bass-two-occurrences-second", "bass", 2, "The bass player released the bass back into the lake.", "FISH", BassLabels()),
        Sense("lead-metal", "lead", 1, "The old pipe contains lead, so wear gloves before moving it.", "METAL", LeadLabels()),
        Sense("lead-guide", "lead", 1, "Lead the visitors through the main entrance.", "GUIDE_OR_FRONT", LeadLabels()),
        Sense("lead-actor", "lead", 1, "The lead actor returned for the final scene.", "GUIDE_OR_FRONT", LeadLabels()),
        Sense("lead-paint", "lead", 1, "Lead paint is hazardous when it begins to flake.", "METAL", LeadLabels()),
        Sense("lead-battery", "lead", 1, "The battery contains lead and must be recycled safely.", "METAL", LeadLabels()),
        Sense("lead-project", "lead", 1, "You should take the lead on this project.", "GUIDE_OR_FRONT", LeadLabels()),
        Sense("lead-two-occurrences-first", "lead", 1, "The lead singer warned the crew about lead paint.", "GUIDE_OR_FRONT", LeadLabels()),
        Sense("lead-two-occurrences-second", "lead", 2, "The lead singer warned the crew about lead paint.", "METAL", LeadLabels()),
        Sense("wind-cable", "wind", 1, "Wind the cable around the reel, then secure the loose end.", "TURN_OR_WRAP", WindLabels()),
        Sense("wind-weather", "wind", 1, "The cold wind rattled the windows throughout the night.", "MOVING_AIR", WindLabels()),
        Sense("wind-road", "wind", 1, "The narrow road will wind through the hills.", "TURN_OR_WRAP", WindLabels()),
        Sense("wind-film", "wind", 1, "Wind the film back into the cartridge.", "TURN_OR_WRAP", WindLabels()),
        Sense("wind-north", "wind", 1, "A strong wind is blowing from the north.", "MOVING_AIR", WindLabels()),
        Sense("wind-gust", "wind", 1, "A gust of wind slammed the gate shut.", "MOVING_AIR", WindLabels()),
        Sense("wind-two-occurrences-first", "wind", 1, "The wind will wind the loose ribbon around the post.", "MOVING_AIR", WindLabels()),
        Sense("wind-two-occurrences-second", "wind", 2, "The wind will wind the loose ribbon around the post.", "TURN_OR_WRAP", WindLabels()),
        Sense("live-studio-first", "live", 1, "I live near the studio where they broadcast the programme live.", "RESIDE_OR_REMAIN_ALIVE", LiveLabels()),
        Sense("live-musicians-first", "live", 1, "The musicians who live nearby will perform live tonight.", "RESIDE_OR_REMAIN_ALIVE", LiveLabels()),
        Sense("live-manchester-first", "live", 1, "We live in Manchester and watch every match live.", "RESIDE_OR_REMAIN_ALIVE", LiveLabels()),
        Sense("live-venue-first", "live", 1, "I would rather live near the venue than watch its concerts live online.", "RESIDE_OR_REMAIN_ALIVE", LiveLabels()),
        Sense("live-longevity", "live", 1, "Most turtles can live for many decades.", "RESIDE_OR_REMAIN_ALIVE", LiveLabels()),
        Sense("live-presenter", "live", 1, "The presenter will go live in five minutes.", "REAL_TIME_OR_ACTIVE", LiveLabels()),
        Sense("live-studio-second", "live", 2, "I live near the studio where they broadcast the programme live.", "REAL_TIME_OR_ACTIVE", LiveLabels()),
        Sense("live-musicians-second", "live", 2, "The musicians who live nearby will perform live tonight.", "REAL_TIME_OR_ACTIVE", LiveLabels()),
        Sense("live-manchester-second", "live", 2, "We live in Manchester and watch every match live.", "REAL_TIME_OR_ACTIVE", LiveLabels()),
        Sense("live-venue-second", "live", 2, "I would rather live near the venue than watch its concerts live online.", "REAL_TIME_OR_ACTIVE", LiveLabels())
    ];

    private static readonly LocalTransformationCase[] TransformationCases =
    [
        new(
            "nested-json-settings",
            "Read this settings object as short natural key-value statements. Preserve the nesting by naming each parent section before its child fields. Preserve every key and value.",
            "{\"voice\":{\"backend\":\"kokoro\",\"name\":\"af_heart\"},\"playback\":{\"speed\":1.05,\"enabled\":true}}",
            ["voice", "backend", "kokoro", "name", "af_heart", "playback", "speed", "1.05", "enabled", "true"]),
        new(
            "csharp-condition",
            "Describe this code literally in one short spoken sentence. State the condition first and then the action. Preserve the object and member names; do not claim an else path or result.",
            "if (speech.IsSpeaking) { speech.Stop(); }",
            ["speech", "IsSpeaking", "Stop"]),
        new(
            "voice-table",
            "Read this table as two natural row statements. For each row, say the voice, backend, speed, and status in that order. Do not summarize or announce Markdown punctuation.",
            "Voice | Backend | Speed | Status\nHeart | Kokoro | 1.0 | Ready\nHazel | Windows | 2 | Ready",
            ["Heart", "Kokoro", "1.0", "Ready", "Hazel", "Windows", "2"])
    ];

    private static LocalSenseLabel[] AxesLabels() =>
    [
        new("COORDINATE_DIMENSIONS", "the plural of axis, meaning graph, coordinate, or rotation dimensions"),
        new("CUTTING_TOOLS", "the plural of axe, meaning cutting tools")
    ];

    private static LocalSenseLabel[] BassLabels() =>
    [
        new("LOW_FREQUENCY_SOUND", "low-frequency sound, an instrument, or an audio control"),
        new("FISH", "the fish")
    ];

    private static LocalSenseLabel[] LeadLabels() =>
    [
        new("METAL", "the chemical element or metal"),
        new("GUIDE_OR_FRONT", "to guide, be ahead, or occupy the principal position")
    ];

    private static LocalSenseLabel[] WindLabels() =>
    [
        new("TURN_OR_WRAP", "to turn, wrap, coil, twist, or follow a curving path"),
        new("MOVING_AIR", "moving air or weather")
    ];

    private static LocalSenseLabel[] LiveLabels() =>
    [
        new("RESIDE_OR_REMAIN_ALIVE", "the verb meaning to reside, inhabit a place, or remain alive"),
        new("REAL_TIME_OR_ACTIVE", "the adjective or adverb meaning happening in real time, active, connected, or not prerecorded")
    ];

    private static LocalSenseCase Sense(
        string id,
        string targetWord,
        int targetOccurrence,
        string context,
        string expectedLabel,
        IReadOnlyList<LocalSenseLabel> labels) =>
        new(id, targetWord, targetOccurrence, context, expectedLabel, labels);
}

internal sealed record LocalSenseLabel(string Name, string Definition);

internal sealed record LocalSenseCase(
    string Id,
    string TargetWord,
    int TargetOccurrence,
    string Context,
    string ExpectedLabel,
    IReadOnlyList<LocalSenseLabel> Labels);

internal sealed record LocalTransformationCase(
    string Id,
    string Instruction,
    string Input,
    IReadOnlyList<string> RequiredTerms);

internal sealed record LocalSenseCaseResult(
    LocalSenseCase Example,
    LocalSenseClassification Response,
    bool Correct,
    bool ExactOutput);

internal sealed record LocalTransformationCaseResult(
    LocalTransformationCase Example,
    LocalTargetedTransformation Response,
    IReadOnlyList<string> MissingTerms);

internal sealed record LocalTargetedEvaluationReport(
    DateTimeOffset GeneratedUtc,
    string ModelFile,
    string ModelSha256,
    string RuntimeVersion,
    int InferenceThreads,
    string EvaluatorRevision,
    string EvaluatorSourceSha256,
    int ReasoningBudgetTokens,
    double StartupMilliseconds,
    double FirstRequestMilliseconds,
    double WarmMedianMilliseconds,
    double WarmP95Milliseconds,
    double PrivateMemoryAfterLoadMegabytes,
    double PrivateMemoryAfterIdleMegabytes,
    double MaximumSampledPrivateMemoryMegabytes,
    double ShutdownMilliseconds,
    bool ProcessReleasedAfterShutdown,
    string CancellationMeasurementScope,
    LocalCancellationProbe CancellationProbe,
    LocalRestartProbe RestartProbe,
    IReadOnlyList<LocalSenseCaseResult> Classifications,
    IReadOnlyList<LocalTransformationCaseResult> Transformations);

internal sealed record LocalRestartProbe(
    double StartupMilliseconds,
    double FirstRequestMilliseconds,
    bool Correct,
    double ShutdownMilliseconds,
    bool ProcessReleasedAfterShutdown);
