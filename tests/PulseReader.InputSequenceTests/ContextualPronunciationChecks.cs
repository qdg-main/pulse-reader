using PulseReader.Speech;
using PulseReader.TextPreparation;

internal static class ContextualPronunciationChecks
{
    public static async Task RunAsync()
    {
        CheckMemoryPolicy();
        CheckApprovedRulesAndPerOccurrenceReplacement();
        await CheckProgressiveUnitDeliveryAsync();
        await CheckAdaptiveRetentionAndPressureReleaseAsync();
        await CheckColdStartBudgetAndRestartAsync();
        await CheckFailureTimeoutCancellationAndGuardingAsync();
        Console.WriteLine(
            "PASS: contextual pronunciation scopes approved words, preserves failures, and adapts local-model residency to memory pressure.");
    }

    private static async Task CheckProgressiveUnitDeliveryAsync()
    {
        var releaseSecondUnit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var classifier = new FakeClassifier(
            async (target, cancellationToken) =>
            {
                if (target.Rule.Word == "live")
                {
                    await releaseSecondUnit.Task.WaitAsync(cancellationToken);
                }

                return target.Rule.PhonemeLabel;
            });
        using var service = CreateService(classifier);
        service.SetEnabled(true);
        var source = Prepare(
            "An unaffected opening sentence.\n"
            + "The pipe contains lead.\n"
            + "Another unaffected sentence.\n"
            + "The presenter will go live in five minutes.");
        var progress = new List<ContextualPronunciationProgress>();
        var firstUnitDelivered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = service.ApplyProgressivelyAsync(
            source,
            (update, _) =>
            {
                progress.Add(update);
                firstUnitDelivered.TrySetResult();
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        await firstUnitDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(!processing.IsCompleted && progress.Count == 1 && !progress[0].IsFinal,
            "Contextual pronunciation waited for later reading units before publishing the first resolved unit.");
        Assert(
            progress[0].Document.BuildSpokenText().Contains("[lead](/lˈɛd/)", StringComparison.Ordinal)
            && !progress[0].Document.BuildSpokenText().Contains("[live](/lˈIv/)", StringComparison.Ordinal),
            "The first progressive update did not contain exactly the resolved pronunciation work.");

        releaseSecondUnit.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(
            progress.Count == 2
            && progress[1].IsFinal
            && progress[1].Document.BuildSpokenText().Contains("[live](/lˈIv/)", StringComparison.Ordinal),
            "Contextual pronunciation did not publish the final resolved reading unit.");
    }

    private static void CheckMemoryPolicy()
    {
        const ulong gibibyte = 1024UL * 1024 * 1024;
        var healthy = new ContextualPronunciationMemorySnapshot(32 * gibibyte, 12 * gibibyte, 62);
        var insufficientStartup = healthy with { AvailablePhysicalBytes = 5 * gibibyte };
        var retained = healthy with { AvailablePhysicalBytes = 4 * gibibyte };
        var pressured = healthy with { AvailablePhysicalBytes = 2 * gibibyte };
        var criticalLoad = healthy with { MemoryLoadPercent = 90 };

        Assert(ContextualPronunciationMemoryPolicy.CanStart(healthy),
            "Healthy system memory did not allow the optional classifier to start.");
        Assert(!ContextualPronunciationMemoryPolicy.CanStart(insufficientStartup),
            "Classifier startup did not preserve space for its measured footprint.");
        Assert(!ContextualPronunciationMemoryPolicy.ShouldRelease(retained),
            "A resident classifier was released despite adequate remaining memory.");
        Assert(ContextualPronunciationMemoryPolicy.ShouldRelease(pressured),
            "Low available memory did not release the resident classifier.");
        Assert(ContextualPronunciationMemoryPolicy.ShouldRelease(criticalLoad),
            "Critical system memory load did not release the resident classifier.");
    }

    private static void CheckApprovedRulesAndPerOccurrenceReplacement()
    {
        const string text =
            "Wind the cable. The pipe contains lead. Reduce the bass. "
            + "Sharpen both axes. I live nearby and broadcast live.";
        var document = Prepare(text);
        var targets = ContextualPronunciationRules.FindTargets(document);
        Assert(targets.Count == 6, "The approved contextual target set changed.");
        Assert(
            targets.Select(target => target.Rule.Word).SequenceEqual(
                ["wind", "lead", "bass", "axes", "live", "live"],
                StringComparer.Ordinal),
            "Contextual targets did not retain reading and occurrence order.");
        Assert(
            ContextualPronunciationRules.MarkTargetOccurrence(
                "I live nearby and broadcast live.",
                "live",
                2)
            == "nearby and broadcast <target>live</target>.",
            "The requested occurrence was not isolated from the other spelling.");
        Assert(
            ContextualPronunciationRules.MarkTargetOccurrence(
                "I live nearby and broadcast live.",
                "live",
                1)
            == "I <target>live</target> nearby and broadcast",
            "The first occurrence did not receive an isolated local context window.");

        var labels = targets
            .Select(target => target.Rule.Word == "live" && target.Occurrence == 1
                ? "RESIDE_OR_REMAIN_ALIVE"
                : target.Rule.PhonemeLabel)
            .ToArray();
        var spoken = ContextualPronunciationRules.ApplyLabels(document, targets, labels)
            .BuildSpokenText();
        Assert(spoken.Contains("[axes](/ˈæksɪz/)", StringComparison.Ordinal),
            "The cutting-tool pronunciation was not inserted.");
        Assert(spoken.Contains("[bass](/bˈAs/)", StringComparison.Ordinal),
            "The audio pronunciation was not inserted.");
        Assert(spoken.Contains("[lead](/lˈɛd/)", StringComparison.Ordinal),
            "The metal pronunciation was not inserted.");
        Assert(spoken.Contains("[Wind](/wˈInd/)", StringComparison.Ordinal),
            "The turning pronunciation did not preserve source casing.");
        Assert(
            spoken.Contains("I live nearby and broadcast [live](/lˈIv/).", StringComparison.Ordinal),
            "Per-occurrence live pronunciation changed the wrong spelling.");
        var voice = KokoroSpeechService.CreateVoice(KokoroSpeechSettings.Default);
        var plan = KokoroSpeechService.CreatePlaybackPlan(
            ContextualPronunciationRules.ApplyLabels(document, targets, labels),
            voice,
            KokoroSpeechService.CreatePipelineConfig(KokoroSpeechSettings.DefaultSpeed));
        Assert(plan.Segments.Count > 0,
            "Contextual phoneme literals did not pass through the production Kokoro plan.");
    }

    private static async Task CheckAdaptiveRetentionAndPressureReleaseAsync()
    {
        var classifier = new FakeClassifier(target => target.Rule.PhonemeLabel);
        var resolveCalls = 0;
        var memory = HealthyMemory();
        using var service = CreateService(
            classifier,
            () => resolveCalls++,
            captureMemory: () => memory,
            memoryCheckInterval: TimeSpan.FromMilliseconds(40));
        service.SetEnabled(true);

        var source = Prepare("The pipe contains lead.");
        var prepared = await service.ApplyAsync(source, CancellationToken.None);
        Assert(resolveCalls == 1 && classifier.StartCalls == 1,
            "The local classifier was not lazily started exactly once.");
        Assert(
            prepared.BuildSpokenText().Contains("[lead](/lˈɛd/)", StringComparison.Ordinal),
            "A valid closed-label response did not apply the deterministic pronunciation.");

        await Task.Delay(120);
        Assert(classifier.DisposeCalls == 0,
            "The contextual-pronunciation process was evicted while memory remained healthy.");

        memory = PressuredMemory();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (classifier.DisposeCalls == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert(classifier.DisposeCalls == 1,
            "Memory pressure did not release the contextual-pronunciation process.");
    }

    private static async Task CheckFailureTimeoutCancellationAndGuardingAsync()
    {
        var source = Prepare("The pipe contains lead.");

        var failed = new FakeClassifier(_ => throw new InvalidOperationException("probe"));
        using (var service = CreateService(failed))
        {
            service.SetEnabled(true);
            var result = await service.ApplyAsync(source, CancellationToken.None);
            Assert(ReferenceEquals(result, source),
                "A failed classifier changed the deterministic prepared document.");
            Assert(failed.DisposeCalls == 1,
                "A failed classifier process was retained.");
        }

        var invalidLabel = new FakeClassifier(_ => null);
        using (var service = CreateService(invalidLabel))
        {
            service.SetEnabled(true);
            var result = await service.ApplyAsync(source, CancellationToken.None);
            Assert(ReferenceEquals(result, source),
                "A non-label classifier response changed the deterministic prepared document.");
        }

        var timedOut = new FakeClassifier(
            async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return "METAL";
            });
        using (var service = CreateService(
                   timedOut,
                   requestTimeout: TimeSpan.FromMilliseconds(30),
                   coldRequestTimeout: TimeSpan.FromMilliseconds(30)))
        {
            service.SetEnabled(true);
            var result = await service.ApplyAsync(source, CancellationToken.None);
            Assert(ReferenceEquals(result, source),
                "A timed-out classifier changed the deterministic prepared document.");
            Assert(timedOut.DisposeCalls == 1,
                "A timed-out classifier process was retained.");
        }

        var canceled = new FakeClassifier(
            async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return "METAL";
            });
        using (var service = CreateService(canceled))
        {
            service.SetEnabled(true);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            try
            {
                await service.ApplyAsync(source, cancellation.Token);
                throw new InvalidOperationException(
                    "Caller cancellation was converted into successful preparation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Expected. The speech request owns cancellation.
            }

            Assert(canceled.DisposeCalls == 1,
                "Caller cancellation retained the classifier process.");
        }

        var manyTargetsClassifier = new FakeClassifier(target => target.Rule.PhonemeLabel);
        var resolveCalls = 0;
        using (var service = CreateService(manyTargetsClassifier, () => resolveCalls++))
        {
            service.SetEnabled(true);
            var manyTargets = Prepare(string.Join(" ", Enumerable.Repeat("Go live.", 9)));
            var manyTargetsResult = await service.ApplyAsync(manyTargets, CancellationToken.None);
            Assert(
                manyTargetsResult.BuildSpokenText().Split("[live](/lˈIv/)").Length - 1 == 9
                && resolveCalls == 1,
                "A selection with more than eight contextual targets was not fully processed.");

            service.SetEnabled(false);
            var disabledResult = await service.ApplyAsync(source, CancellationToken.None);
            Assert(ReferenceEquals(disabledResult, source) && resolveCalls == 1,
                "Disabled contextual pronunciation started or changed processing.");
        }

        CheckMissingAndUntrustedInstallation();
    }

    private static async Task CheckColdStartBudgetAndRestartAsync()
    {
        var classifiers = new List<FakeClassifier>();
        var resolveCalls = 0;
        var memory = HealthyMemory();
        using var service = new ContextualPronunciationService(
            classifierFactory: _ =>
            {
                var classifier = new FakeClassifier(
                    async (target, cancellationToken) =>
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);
                        return target.Rule.PhonemeLabel;
                    });
                classifiers.Add(classifier);
                return classifier;
            },
            availability: new ContextualPronunciationAvailability(true, "test", null),
            resolveInstallation: _ =>
            {
                resolveCalls++;
                return Task.FromResult(FakeInstallation());
            },
            captureMemory: () => memory,
            memoryCheckInterval: TimeSpan.FromMilliseconds(40),
            requestTimeout: TimeSpan.FromMilliseconds(20),
            coldRequestTimeout: TimeSpan.FromMilliseconds(200));
        service.SetEnabled(true);

        var source = Prepare("The pipe contains lead.");
        var first = await service.ApplyAsync(source, CancellationToken.None);
        Assert(
            first.BuildSpokenText().Contains("[lead](/lˈɛd/)", StringComparison.Ordinal),
            "The first classification did not receive the cold-start request budget.");

        memory = PressuredMemory();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while ((classifiers.Count != 1 || classifiers[0].DisposeCalls == 0)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert(classifiers.Count == 1 && classifiers[0].DisposeCalls == 1,
            "The first classifier did not shut down under memory pressure before restart testing.");

        memory = HealthyMemory();
        var restarted = await service.ApplyAsync(source, CancellationToken.None);
        Assert(
            resolveCalls == 2
            && classifiers.Count == 2
            && classifiers[1].StartCalls == 1
            && restarted.BuildSpokenText().Contains("[lead](/lˈɛd/)", StringComparison.Ordinal),
            "The classifier did not reverify and restart with a fresh cold-start request budget.");

        var warmTimeout = await service.ApplyAsync(source, CancellationToken.None);
        Assert(ReferenceEquals(warmTimeout, source) && classifiers[1].DisposeCalls == 1,
            "A warm classification incorrectly retained the longer cold-start budget.");
    }

    private static void CheckMissingAndUntrustedInstallation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PulseReader.ContextualPronunciationChecks",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ContextualPronunciationModelStore(roots: [root]);
            Assert(!store.GetAvailability().IsInstalled,
                "A missing contextual-pronunciation installation was accepted.");

            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, ContextualPronunciationModelStore.InstallationManifestFileName),
                "{}");
            Assert(!store.GetAvailability().IsInstalled,
                "An untrusted contextual-pronunciation manifest was accepted.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ContextualPronunciationService CreateService(
        FakeClassifier classifier,
        Action? onResolve = null,
        Func<ContextualPronunciationMemorySnapshot?>? captureMemory = null,
        TimeSpan? memoryCheckInterval = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? coldRequestTimeout = null) =>
        new(
            classifierFactory: _ => classifier,
            availability: new ContextualPronunciationAvailability(true, "test", null),
            resolveInstallation: _ =>
            {
                onResolve?.Invoke();
                return Task.FromResult(FakeInstallation());
            },
            captureMemory: captureMemory ?? (() => HealthyMemory()),
            memoryCheckInterval: memoryCheckInterval ?? TimeSpan.FromMinutes(1),
            requestTimeout: requestTimeout ?? TimeSpan.FromSeconds(1),
            coldRequestTimeout: coldRequestTimeout ?? TimeSpan.FromSeconds(1));

    private static ContextualPronunciationMemorySnapshot HealthyMemory() =>
        new(32UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, 62);

    private static ContextualPronunciationMemorySnapshot PressuredMemory() =>
        new(32UL * 1024 * 1024 * 1024, 2UL * 1024 * 1024 * 1024, 94);

    private static ContextualPronunciationInstallation FakeInstallation() =>
        new(
            "test",
            "test",
            "test-server.exe",
            "test-model.gguf",
            "test-logs",
            new ContextualPronunciationInstallationManifest(
                1,
                ContextualPronunciationModelStore.RuntimeArchiveSha256,
                ContextualPronunciationModelStore.ModelFileName,
                ContextualPronunciationModelStore.ModelSha256,
                [new ContextualPronunciationRuntimeFile("llama-server.exe", 1, "00")]));

    private static PreparedDocument Prepare(string text) =>
        SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(text));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeClassifier : IContextualSenseClassifier
    {
        private readonly Func<ContextualPronunciationTarget, CancellationToken, Task<string?>>
            _classify;

        public FakeClassifier(Func<ContextualPronunciationTarget, string?> classify)
            : this((target, _) => Task.FromResult(classify(target)))
        {
        }

        public FakeClassifier(
            Func<ContextualPronunciationTarget, CancellationToken, Task<string?>> classify)
        {
            _classify = classify;
        }

        public int StartCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task<string?> ClassifyAsync(
            ContextualPronunciationTarget target,
            CancellationToken cancellationToken) =>
            _classify(target, cancellationToken);

        public void Dispose()
        {
            DisposeCalls++;
        }
    }
}
