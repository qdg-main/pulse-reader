using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Utilities;
using NAudio.Wave;
using PulseReader.Speech;
using PulseReader.TextPreparation;

internal static class LocalTextCleanupEvaluation
{
    private const int InferenceThreads = 8;
    private static readonly TimeSpan RuntimeVersionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RuntimeTerminationTimeout = TimeSpan.FromSeconds(2);

    public static async Task RunAsync(
        string outputDirectory,
        string? caseIds = null,
        string? modelFile = null)
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
        var corpus = FilterCorpus(LocalTextCleanupCorpus.Load(), caseIds);
        var modelHash = await HashFileAsync(modelPath);
        var runtimeVersion = await ReadRuntimeVersionAsync(serverPath);
        var results = new List<LocalCleanupCaseResult>(corpus.Count);
        LocalCancellationProbe cancellationProbe;
        double startupMilliseconds;
        long workingSetAfterLoad;
        long privateMemoryAfterLoad;
        long peakWorkingSet;
        long peakPrivateMemory;
        int serverProcessId;
        double shutdownMilliseconds;
        bool releasedAfterShutdown;

        await using var server = new LocalLlamaServer(
            serverPath,
            modelPath,
            fullOutputDirectory,
            InferenceThreads);
        await server.StartAsync(CancellationToken.None);
        startupMilliseconds = server.StartupTime.TotalMilliseconds;
        workingSetAfterLoad = server.WorkingSetBytes;
        privateMemoryAfterLoad = server.PrivateMemoryBytes;
        peakWorkingSet = workingSetAfterLoad;
        peakPrivateMemory = privateMemoryAfterLoad;
        serverProcessId = server.ProcessId;

        try
        {
            for (var index = 0; index < corpus.Count; index++)
            {
                var example = corpus[index];
                var deterministic = SpokenTextFormatter.Format(
                    ReadingUnitAnalyzer.Prepare(example.OriginalText));
                var decisions = new List<LocalCleanupUnitDecision>(deterministic.Units.Count);
                var candidateUnits = new List<ReadingUnit>(deterministic.Units.Count);
                foreach (var unit in deterministic.Units)
                {
                    var decision = await server.EvaluateAsync(
                        example,
                        unit,
                        CancellationToken.None);
                    var applied = ApplyDecision(unit, decision);
                    decisions.Add(applied.Decision);
                    candidateUnits.Add(applied.Unit);
                    peakWorkingSet = Math.Max(peakWorkingSet, server.WorkingSetBytes);
                    peakPrivateMemory = Math.Max(peakPrivateMemory, server.PrivateMemoryBytes);
                }

                var candidate = deterministic with { Units = candidateUnits };
                var guardFailure = ValidateCandidate(example, deterministic, candidate);
                if (guardFailure is not null)
                {
                    candidate = deterministic;
                    decisions = RejectAppliedReplacements(decisions, guardFailure).ToList();
                }

                results.Add(
                    new LocalCleanupCaseResult(
                        index + 1,
                        example,
                        deterministic,
                        candidate,
                        decisions,
                        guardFailure));
                Console.WriteLine(
                    $"[{index + 1:00}/{corpus.Count:00}] {example.Id}: "
                    + $"{decisions.Count(decision => decision.ModelAction == "replace")} replace, "
                    + $"{decisions.Count(decision => decision.Applied)} applied, "
                    + $"{decisions.Sum(decision => decision.ElapsedMilliseconds):0} ms");
            }

            cancellationProbe = await server.RunCancellationProbeAsync(CancellationToken.None);
            peakWorkingSet = Math.Max(peakWorkingSet, server.WorkingSetBytes);
            peakPrivateMemory = Math.Max(peakPrivateMemory, server.PrivateMemoryBytes);
        }
        finally
        {
            var shutdownStopwatch = Stopwatch.StartNew();
            await server.DisposeAsync();
            shutdownStopwatch.Stop();
            shutdownMilliseconds = shutdownStopwatch.Elapsed.TotalMilliseconds;
            releasedAfterShutdown = !IsProcessRunning(serverProcessId);
        }

        await GenerateAudioAsync(results, fullOutputDirectory);
        var metrics = BuildMetrics(
            results,
            runtimeVersion,
            modelPath,
            modelHash,
            startupMilliseconds,
            workingSetAfterLoad,
            privateMemoryAfterLoad,
            peakWorkingSet,
            peakPrivateMemory,
            shutdownMilliseconds,
            releasedAfterShutdown,
            cancellationProbe);
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    generatedUtc = DateTimeOffset.UtcNow,
                    metrics,
                    cases = results.Select(ToManifestCase)
                },
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "index.html"),
            LocalTextCleanupReviewPage.Build(results, metrics),
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "README.md"),
            BuildMarkdown(results, metrics),
            new UTF8Encoding(false));

        Console.WriteLine($"Local cleanup review page: {Path.Combine(fullOutputDirectory, "index.html")}");
        Console.WriteLine(
            $"Model load {metrics.StartupMilliseconds:0} ms; "
            + $"warm median {metrics.WarmMedianMilliseconds:0} ms; "
            + $"peak private memory {metrics.PeakPrivateMemoryMegabytes:0} MB; "
            + $"shutdown {metrics.ShutdownMilliseconds:0} ms.");
    }

    private static DecisionApplication ApplyDecision(
        ReadingUnit unit,
        LocalModelDecision modelDecision)
    {
        var action = modelDecision.Action.Trim().ToLowerInvariant();
        if (action == "keep")
        {
            return new DecisionApplication(
                unit,
                new LocalCleanupUnitDecision(
                    unit.Index,
                    unit.Kind,
                    action,
                    modelDecision.Reason,
                    modelDecision.SpokenText,
                    unit.SpokenText,
                    false,
                    null,
                    modelDecision.ElapsedMilliseconds,
                    modelDecision.PromptTokens,
                    modelDecision.CompletionTokens,
                    modelDecision.TokensPerSecond,
                    modelDecision.RawJson));
        }

        if (action != "replace" || string.IsNullOrWhiteSpace(modelDecision.SpokenText))
        {
            const string message = "The model returned an invalid action or empty replacement.";
            return Guarded(unit, modelDecision, action, message);
        }

        var replacement = modelDecision.SpokenText.Trim();
        if (string.Equals(replacement, unit.SpokenText.Trim(), StringComparison.Ordinal))
        {
            return Guarded(
                unit,
                modelDecision,
                action,
                "The proposed replacement was identical to the deterministic text.");
        }

        var maximumLength = Math.Max(unit.SpokenText.Length * 2, unit.SpokenText.Length + 160);
        if (replacement.Length > maximumLength)
        {
            return Guarded(
                unit,
                modelDecision,
                action,
                $"The replacement expanded from {unit.SpokenText.Length} to {replacement.Length} characters.");
        }

        return new DecisionApplication(
            unit with { SpokenText = replacement },
            new LocalCleanupUnitDecision(
                unit.Index,
                unit.Kind,
                action,
                modelDecision.Reason,
                modelDecision.SpokenText,
                replacement,
                true,
                null,
                modelDecision.ElapsedMilliseconds,
                modelDecision.PromptTokens,
                modelDecision.CompletionTokens,
                modelDecision.TokensPerSecond,
                modelDecision.RawJson));
    }

    private static DecisionApplication Guarded(
        ReadingUnit unit,
        LocalModelDecision modelDecision,
        string action,
        string message) =>
        new(
            unit,
            new LocalCleanupUnitDecision(
                unit.Index,
                unit.Kind,
                action,
                modelDecision.Reason,
                modelDecision.SpokenText,
                unit.SpokenText,
                false,
                message,
                modelDecision.ElapsedMilliseconds,
                modelDecision.PromptTokens,
                modelDecision.CompletionTokens,
                modelDecision.TokensPerSecond,
                modelDecision.RawJson));

    private static string? ValidateCandidate(
        LocalTextCleanupCase example,
        PreparedDocument deterministic,
        PreparedDocument candidate)
    {
        var spokenText = candidate.BuildSpokenText();
        if (string.IsNullOrWhiteSpace(spokenText))
        {
            return "The candidate removed all speakable text.";
        }

        var maximumLength = Math.Max(
            deterministic.BuildSpokenText().Length * 2,
            deterministic.BuildSpokenText().Length + 300);
        if (spokenText.Length > maximumLength)
        {
            return "The complete candidate expanded beyond the conservative length limit.";
        }

        var protectedContentViolations = LocalProtectedTermMatcher.FindViolations(
            spokenText,
            example.ProtectedSequences);
        return protectedContentViolations.Count == 0
            ? null
            : "Protected content was lost or changed: "
              + string.Join(" ", protectedContentViolations);
    }

    private static async Task GenerateAudioAsync(
        IReadOnlyList<LocalCleanupCaseResult> results,
        string outputDirectory)
    {
        var modelPath = await new KokoroModelStore().GetModelPathAsync(CancellationToken.None);
        var voice = KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice);
        using var synthesizer = new KokoroWavSynthesizer(modelPath);
        foreach (var result in results)
        {
            var prefix = $"{result.Number:00}-{result.Example.Id}";
            result.OriginalFile = prefix + "-a-original.wav";
            result.DeterministicFile = prefix + "-b-deterministic.wav";
            result.ModelFile = prefix + "-c-model.wav";

            var originalAudio = WrapPcmAsWave(
                await synthesizer.SynthesizeAsync(
                    SanitizeForKokoro(result.Example.OriginalText),
                    voice));
            var deterministicAudio = WrapPcmAsWave(
                await SynthesizeDocumentAsync(synthesizer, voice, result.Deterministic));
            var modelAudio = result.Candidate.BuildSpokenText() == result.Deterministic.BuildSpokenText()
                ? deterministicAudio
                : WrapPcmAsWave(
                    await SynthesizeDocumentAsync(synthesizer, voice, result.Candidate));
            ValidateAudio(originalAudio, result.Example.Id, "original");
            ValidateAudio(deterministicAudio, result.Example.Id, "deterministic");
            ValidateAudio(modelAudio, result.Example.Id, "model");
            await File.WriteAllBytesAsync(
                ResolveOutputFilePath(outputDirectory, result.OriginalFile),
                originalAudio);
            await File.WriteAllBytesAsync(
                ResolveOutputFilePath(outputDirectory, result.DeterministicFile),
                deterministicAudio);
            await File.WriteAllBytesAsync(
                ResolveOutputFilePath(outputDirectory, result.ModelFile),
                modelAudio);
            result.OriginalDurationSeconds = DurationSeconds(originalAudio);
            result.DeterministicDurationSeconds = DurationSeconds(deterministicAudio);
            result.ModelDurationSeconds = DurationSeconds(modelAudio);
        }
    }

    private static async Task<byte[]> SynthesizeDocumentAsync(
        KokoroWavSynthesizer synthesizer,
        KokoroVoice voice,
        PreparedDocument document)
    {
        using var result = new MemoryStream();
        foreach (var chunk in document.BuildSpeechChunks())
        {
            var text = SanitizeForKokoro(chunk.Text).Trim();
            if (text.Length > 0)
            {
                var audio = await synthesizer.SynthesizeAsync(text, voice);
                await result.WriteAsync(audio);
            }

            if (chunk.PauseAfterMilliseconds > 0)
            {
                var silenceBytes = KokoroPlayback.waveFormat.AverageBytesPerSecond
                                   * chunk.PauseAfterMilliseconds
                                   / 1000;
                silenceBytes -= silenceBytes % KokoroPlayback.waveFormat.BlockAlign;
                await result.WriteAsync(new byte[silenceBytes]);
            }
        }

        return result.ToArray();
    }

    private static string SanitizeForKokoro(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.OtherSymbol
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.Control)
            {
                result.Append(' ');
            }
            else
            {
                result.Append(rune.ToString());
            }
        }

        return result.ToString();
    }

    private static byte[] WrapPcmAsWave(byte[] pcm)
    {
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, KokoroPlayback.waveFormat))
        {
            writer.Write(pcm, 0, pcm.Length);
        }

        return output.ToArray();
    }

    private static void ValidateAudio(byte[] audio, string id, string variant)
    {
        if (audio.Length < 1000)
        {
            throw new InvalidOperationException(
                $"Local cleanup case '{id}' returned invalid {variant} audio.");
        }
    }

    private static double DurationSeconds(byte[] audio)
    {
        using var stream = new MemoryStream(audio, writable: false);
        using var reader = new WaveFileReader(stream);
        return Math.Round(reader.TotalTime.TotalSeconds, 3);
    }

    private static LocalCleanupMetrics BuildMetrics(
        IReadOnlyList<LocalCleanupCaseResult> results,
        string runtimeVersion,
        string modelPath,
        string modelHash,
        double startupMilliseconds,
        long workingSetAfterLoad,
        long privateMemoryAfterLoad,
        long peakWorkingSet,
        long peakPrivateMemory,
        double shutdownMilliseconds,
        bool releasedAfterShutdown,
        LocalCancellationProbe cancellationProbe)
    {
        var decisions = results.SelectMany(result => result.Decisions).ToArray();
        var requestTimes = decisions.Select(decision => decision.ElapsedMilliseconds).ToArray();
        var warmTimes = requestTimes.Skip(1).Order().ToArray();
        return new LocalCleanupMetrics(
            runtimeVersion.Trim(),
            Path.GetFileName(modelPath),
            new FileInfo(modelPath).Length,
            modelHash,
            InferenceThreads,
            startupMilliseconds,
            requestTimes.FirstOrDefault(),
            Percentile(warmTimes, 0.5),
            Percentile(warmTimes, 0.95),
            decisions.Sum(decision => decision.PromptTokens),
            decisions.Sum(decision => decision.CompletionTokens),
            decisions.Where(decision => decision.TokensPerSecond > 0)
                .Select(decision => decision.TokensPerSecond)
                .DefaultIfEmpty()
                .Average(),
            workingSetAfterLoad / 1024d / 1024d,
            privateMemoryAfterLoad / 1024d / 1024d,
            peakWorkingSet / 1024d / 1024d,
            peakPrivateMemory / 1024d / 1024d,
            shutdownMilliseconds,
            releasedAfterShutdown,
            decisions.Count(decision => decision.ModelAction == "replace"),
            decisions.Count(decision => decision.Applied),
            CountGuardedReplacements(decisions),
            results.Count(result => result.Example.ExpectKeep
                                    && result.Decisions.Any(decision => decision.Applied)),
            cancellationProbe);
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
    }

    private static object ToManifestCase(LocalCleanupCaseResult result) => new
    {
        result.Number,
        result.Example,
        complexity = result.Deterministic.Complexity,
        deterministicText = result.Deterministic.BuildSpokenText(),
        modelText = result.Candidate.BuildSpokenText(),
        result.GuardFailure,
        result.Decisions,
        result.OriginalFile,
        result.DeterministicFile,
        result.ModelFile,
        result.OriginalDurationSeconds,
        result.DeterministicDurationSeconds,
        result.ModelDurationSeconds
    };

    private static string BuildMarkdown(
        IReadOnlyList<LocalCleanupCaseResult> results,
        LocalCleanupMetrics metrics)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine("# Local text cleanup evaluation");
        markdown.AppendLine();
        markdown.AppendLine($"Model: <code>{WebUtility.HtmlEncode(metrics.ModelFile)}</code>");
        markdown.AppendLine();
        markdown.AppendLine($"Load: {metrics.StartupMilliseconds:0} ms");
        markdown.AppendLine();
        markdown.AppendLine($"Warm median: {metrics.WarmMedianMilliseconds:0} ms");
        markdown.AppendLine();
        markdown.AppendLine($"Peak private memory: {metrics.PeakPrivateMemoryMegabytes:0} MB");
        markdown.AppendLine();
        markdown.AppendLine("Open `index.html` for text, decisions, and A/B/C audio.");
        foreach (var result in results)
        {
            markdown.AppendLine();
            markdown.AppendLine(
                $"<h2>{result.Number:00}. {WebUtility.HtmlEncode(result.Example.Title)}</h2>");
            markdown.AppendLine();
            markdown.AppendLine($"<p>{WebUtility.HtmlEncode(result.Example.ReviewGoal)}</p>");
            markdown.AppendLine();
            markdown.AppendLine($"- [A, original]({result.OriginalFile})");
            markdown.AppendLine($"- [B, deterministic]({result.DeterministicFile})");
            markdown.AppendLine($"- [C, local model]({result.ModelFile})");
        }

        return markdown.ToString();
    }

    internal static string ResolveOutputFilePath(string outputDirectory, string fileName)
    {
        var outputRoot = Path.GetFullPath(outputDirectory);
        var requiredPrefix = Path.EndsInDirectorySeparator(outputRoot)
            ? outputRoot
            : outputRoot + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(outputRoot, fileName));
        if (!candidate.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to write evaluation output outside '{outputRoot}': {candidate}");
        }

        return candidate;
    }

    private static IReadOnlyList<LocalTextCleanupCase> FilterCorpus(
        IReadOnlyList<LocalTextCleanupCase> corpus,
        string? caseIds)
    {
        if (string.IsNullOrWhiteSpace(caseIds))
        {
            return corpus;
        }

        var requested = caseIds.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = corpus.Where(example => requested.Contains(example.Id)).ToArray();
        if (selected.Length != requested.Count)
        {
            throw new InvalidOperationException(
                "The local cleanup corpus does not contain every requested case: " + caseIds);
        }

        return selected;
    }

    internal static IReadOnlyList<LocalCleanupUnitDecision> RejectAppliedReplacements(
        IReadOnlyList<LocalCleanupUnitDecision> decisions,
        string guardMessage) =>
        decisions
            .Select(
                decision => decision.Applied
                            && string.Equals(
                                decision.ModelAction,
                                "replace",
                                StringComparison.OrdinalIgnoreCase)
                    ? decision with
                    {
                        Applied = false,
                        GuardMessage = guardMessage
                    }
                    : decision)
            .ToArray();

    internal static int CountGuardedReplacements(
        IEnumerable<LocalCleanupUnitDecision> decisions) =>
        decisions.Count(
            decision => string.Equals(
                            decision.ModelAction,
                            "replace",
                            StringComparison.OrdinalIgnoreCase)
                        && decision.GuardMessage is not null);

    internal static async Task<string> ReadRuntimeVersionAsync(
        string executablePath,
        IReadOnlyList<string>? arguments = null,
        TimeSpan? timeout = null,
        Action<int>? processStarted = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments ?? ["--version"])
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not read llama.cpp version.");
        processStarted?.Invoke(process.Id);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutCancellation = new CancellationTokenSource(
            timeout ?? RuntimeVersionTimeout);
        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(timeoutCancellation.Token),
                    outputTask,
                    errorTask)
                .WaitAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            await TerminateRuntimeVersionProcessAsync(process, outputTask, errorTask);
            throw new TimeoutException(
                $"llama.cpp --version did not exit within "
                + $"{(timeout ?? RuntimeVersionTimeout).TotalSeconds:0.###} seconds.");
        }

        var output = await outputTask;
        var error = await errorTask;
        return (output + Environment.NewLine + error).Trim();
    }

    private static async Task TerminateRuntimeVersionProcessAsync(
        Process process,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited after the timeout fired.
            }
        }

        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), outputTask, errorTask)
                .WaitAsync(RuntimeTerminationTimeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "llama.cpp --version did not terminate after it timed out.");
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
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

    private sealed record DecisionApplication(
        ReadingUnit Unit,
        LocalCleanupUnitDecision Decision);
}

internal sealed record LocalCleanupUnitDecision(
    int UnitIndex,
    ReadingUnitKind UnitKind,
    string ModelAction,
    string Reason,
    string? ProposedText,
    string AppliedText,
    bool Applied,
    string? GuardMessage,
    double ElapsedMilliseconds,
    int PromptTokens,
    int CompletionTokens,
    double TokensPerSecond,
    string RawJson);

internal sealed class LocalCleanupCaseResult
{
    public LocalCleanupCaseResult(
        int number,
        LocalTextCleanupCase example,
        PreparedDocument deterministic,
        PreparedDocument candidate,
        IReadOnlyList<LocalCleanupUnitDecision> decisions,
        string? guardFailure)
    {
        Number = number;
        Example = example;
        Deterministic = deterministic;
        Candidate = candidate;
        Decisions = decisions;
        GuardFailure = guardFailure;
    }

    public int Number { get; }
    public LocalTextCleanupCase Example { get; }
    public PreparedDocument Deterministic { get; }
    public PreparedDocument Candidate { get; }
    public IReadOnlyList<LocalCleanupUnitDecision> Decisions { get; }
    public string? GuardFailure { get; }
    public string OriginalFile { get; set; } = string.Empty;
    public string DeterministicFile { get; set; } = string.Empty;
    public string ModelFile { get; set; } = string.Empty;
    public double OriginalDurationSeconds { get; set; }
    public double DeterministicDurationSeconds { get; set; }
    public double ModelDurationSeconds { get; set; }
}

internal sealed record LocalCleanupMetrics(
    string RuntimeVersion,
    string ModelFile,
    long ModelBytes,
    string ModelSha256,
    int InferenceThreads,
    double StartupMilliseconds,
    double FirstRequestMilliseconds,
    double WarmMedianMilliseconds,
    double WarmP95Milliseconds,
    int PromptTokens,
    int CompletionTokens,
    double MeanGenerationTokensPerSecond,
    double WorkingSetAfterLoadMegabytes,
    double PrivateMemoryAfterLoadMegabytes,
    double PeakWorkingSetMegabytes,
    double PeakPrivateMemoryMegabytes,
    double ShutdownMilliseconds,
    bool ProcessReleasedAfterShutdown,
    int ProposedReplacements,
    int AppliedReplacements,
    int GuardedReplacements,
    int UnexpectedControlChanges,
    LocalCancellationProbe CancellationProbe);
