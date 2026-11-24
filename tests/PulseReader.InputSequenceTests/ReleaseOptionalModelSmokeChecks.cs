using System.Diagnostics;
using System.Text.Json;
using PulseReader.Configuration;
using PulseReader.Hotkeys;
using PulseReader.Settings;
using PulseReader.Setup;
using PulseReader.Speech;
using PulseReader.TextPreparation;

// Explicit opt-in only. The normal test suite must never invoke this download.
internal static class ReleaseOptionalModelSmokeChecks
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ColdRequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WarmRequestTimeout = TimeSpan.FromMinutes(1);

    public static async Task RunAsync(string reportDirectory)
    {
        AssertGuestAndReportDirectory(reportDirectory);
        Directory.CreateDirectory(reportDirectory);
        using var report = new SmokeReport(reportDirectory);
        using var timeout = new CancellationTokenSource(OverallTimeout);
        ObservedClassifier? classifier = null;
        try
        {
            var setup = new ModelSetupService(); // All production URLs, hashes and embedded manifest.
            var store = new ContextualPronunciationModelStore();
            var configuration = new ConfigurationService();
            Assert(!File.Exists(configuration.ConfigPath),
                "This fixture requires a fresh guest configuration; existing configuration was left untouched.");
            Assert(!Path.Exists(store.GetAvailability().InstallRoot) && !store.GetAvailability().IsInstalled,
                "The optional installation must be absent before the opt-in test.");
            AssertCoreAbsent(setup);
            var initial = configuration.LoadOrCreate();
            Assert(initial.UsedDefaults && initial.Warning is null
                && !initial.Configuration.ContextualPronunciationEnabled
                && !AppConfiguration.Default.ContextualPronunciationEnabled,
                "Optional pronunciation was not disabled by default.");
            report.Write("initial-defaults", new { configuration.ConfigPath, optionalInstalled = false, enabled = false });

            await InstallThroughSettingsAsync(configuration, initial, setup, store, report, timeout.Token)
                .WaitAsync(timeout.Token);
            var verification = Stopwatch.StartNew();
            var installation = await store.ResolveVerifiedAsync(timeout.Token);
            Assert(store.GetAvailability().IsInstalled, "Installation was not available without restarting.");
            Assert(configuration.LoadOrCreate().Configuration.ContextualPronunciationEnabled,
                "Enabling the optional model was not persisted.");
            AssertNotices(installation);
            AssertCoreAbsent(setup);
            report.Write("verified-installation", new
            {
                installation.Root, installation.ModelPath, installation.ServerPath,
                ContextualPronunciationModelStore.ModelSha256,
                ContextualPronunciationModelStore.RuntimeArchiveSha256,
                ContextualPronunciationModelStore.InstallationManifestSha256,
                runtimeFiles = installation.Manifest.RuntimeFiles,
                verificationSeconds = verification.Elapsed.TotalSeconds,
                availableWithoutRestart = true, persistedEnabled = true, coreModelAbsent = true
            });

            var memory = new WindowsContextualPronunciationMemoryMonitor().Capture();
            report.Write("classifier-memory-gate", new
            {
                memory, canStart = ContextualPronunciationMemoryPolicy.CanStart(memory),
                ContextualPronunciationMemoryPolicy.EstimatedClassifierBytes,
                ContextualPronunciationMemoryPolicy.MinimumAvailableBytes,
                coldRequestTimeoutSeconds = ColdRequestTimeout.TotalSeconds,
                warmRequestTimeoutSeconds = WarmRequestTimeout.TotalSeconds,
                productionStartupTimeoutSeconds = 30
            });
            Assert(ContextualPronunciationMemoryPolicy.CanStart(memory),
                "Sandbox memory is insufficient for the production classifier gate. It requires at least 5.5 GiB available; increase guest RAM and use a fresh guest.");

            using (var service = new ContextualPronunciationService(store,
                classifierFactory: verified => classifier = new ObservedClassifier(verified, report),
                requestTimeout: WarmRequestTimeout, coldRequestTimeout: ColdRequestTimeout))
            {
                service.SetEnabled(true);
                var source = SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(
                    "The presenter will go live in five minutes.\nI live near the railway station."));
                var targets = ContextualPronunciationRules.FindTargets(source);
                Assert(targets.Count == 2 && targets.Select(target => target.UnitIndex).Distinct().Count() == 2,
                    "The two fixture sentences did not produce distinct production reading units.");
                var updates = new List<ContextualPronunciationProgress>();
                var stopwatch = Stopwatch.StartNew();
                await service.ApplyProgressivelyAsync(source, (update, _) =>
                {
                    updates.Add(update);
                    report.Write("reading-unit", new
                    {
                        update.ResolvedUnitIndex, update.IsFinal,
                        spoken = update.Document.BuildSpokenText(), elapsedSeconds = stopwatch.Elapsed.TotalSeconds
                    });
                    return ValueTask.CompletedTask;
                }, timeout.Token);
                Assert(classifier is not null && classifier.Labels.SequenceEqual(
                    new[] { "REAL_TIME_OR_ACTIVE", "RESIDE_OR_REMAIN_ALIVE" }, StringComparer.Ordinal),
                    "The real classifier did not classify both senses correctly. Unclassified or timed-out text is a failure.");
                Assert(updates.Count == 2 && !updates[0].IsFinal && updates[1].IsFinal
                    && updates.Select(update => update.ResolvedUnitIndex).SequenceEqual(targets.Select(target => target.UnitIndex)),
                    "Production progressive delivery did not resolve both reading units.");
                var result = updates[^1].Document;
                Assert(result.Units.Single(unit => unit.Index == targets[0].UnitIndex).SpokenText
                    == "The presenter will go [live](/lˈIv/) in five minutes.",
                    "The real-time live pronunciation was not rewritten correctly.");
                Assert(result.Units.Single(unit => unit.Index == targets[1].UnitIndex).SpokenText
                    == "I live near the railway station.",
                    "The reside sense must retain the ordinary live spelling.");
                report.Write("classification-complete", new { elapsedSeconds = stopwatch.Elapsed.TotalSeconds });
            }
            var completedClassifier = classifier
                ?? throw new InvalidOperationException("The production classifier was never created.");
            Assert(completedClassifier.Disposed
                && completedClassifier.ProcessIds.Count > 0
                && completedClassifier.ProcessIds.All(id => !IsProcessRunning(id)),
                "Disposing the production service retained a classifier process.");
            AssertCoreAbsent(setup);
            report.Write("passed", new { completedClassifier.ProcessIds, coreModelAbsent = true, timeoutMinutes = 30 });
            Console.WriteLine("PASS: Sandbox opt-in downloaded and verified the pinned optional model, persisted the setting, classified both live senses, and stopped its process without downloading Kokoro.");
        }
        catch (Exception exception)
        {
            report.Write("failed", new { exception = exception.ToString(), classifierProcessIds = classifier?.ProcessIds });
            throw;
        }
    }

    private static Task InstallThroughSettingsAsync(ConfigurationService configuration,
        ConfigurationLoadResult initial, ModelSetupService setup, ContextualPronunciationModelStore store,
        SmokeReport report, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Exception? installFailure = null;
                var downloads = 0;
                var saves = 0;
                var fullyVerified = false;
                using var settings = new SettingsForm(initial.Configuration, initial.Hotkey,
                    next =>
                    {
                        try
                        {
                            Assert(!next.ContextualPronunciationEnabled || fullyVerified,
                                "The checkbox saved enabled before full installation verification.");
                            saves++;
                            Assert(configuration.TrySave(next, out var error), error ?? "Configuration save failed.");
                            report.Write("setting-saved", new { next.ContextualPronunciationEnabled, saves, fullyVerified });
                            return null;
                        }
                        catch (Exception exception)
                        {
                            installFailure ??= exception;
                            return exception.Message;
                        }
                    }, _ => null, _ => Task.FromResult(SpeechStartResult.Success()),
                    () => { }, () => null, () => null,
                    contextualPronunciationAvailability: store.GetAvailability(),
                    installContextualPronunciation: () =>
                    {
                        downloads++;
                        report.Write("checkbox-opt-in", new { downloads });
                        using var dialog = new ModelSetupForm(true, async (uiProgress, token) =>
                        {
                            try
                            {
                                await setup.EnsureContextualPronunciationAsync(
                                    new ReportingProgress(value =>
                                    {
                                        uiProgress.Report(value);
                                        report.Progress(value);
                                    }), token);
                                await store.ResolveVerifiedAsync(token);
                                Assert(!configuration.LoadOrCreate().Configuration.ContextualPronunciationEnabled,
                                    "The setting became enabled while the installation callback was still active.");
                                AssertCoreAbsent(setup);
                                fullyVerified = true;
                                report.Write("setup-callback-verified", new { fullyVerified });
                            }
                            catch (Exception exception)
                            {
                                installFailure = exception;
                                report.Write("setup-callback-failed", new { exception = exception.ToString() });
                                throw;
                            }
                        }, cancellationToken);
                        PutOffscreen(dialog);
                        using var poll = new System.Windows.Forms.Timer { Interval = 1000 };
                        poll.Tick += (_, _) =>
                        {
                            try
                            {
                                Assert(!configuration.LoadOrCreate().Configuration.ContextualPronunciationEnabled,
                                    "Enabled was written before the setup dialog succeeded.");
                                AssertCoreAbsent(setup);
                            }
                            catch (Exception exception) { installFailure ??= exception; }
                            if (installFailure is not null || cancellationToken.IsCancellationRequested)
                                dialog.Close();
                        };
                        poll.Start();
                        var result = dialog.ShowDialog();
                        report.Write("setup-dialog-closed", new { result = result.ToString(), fullyVerified });
                        return Task.FromResult(result == DialogResult.OK && installFailure is null && fullyVerified);
                    }, getContextualPronunciationAvailability: store.GetAvailability);
                PutOffscreen(settings);
                var checkbox = settings.Controls.OfType<SettingsScrollViewport>().Single().ContentControls
                    .OfType<ThemePanel>().SelectMany(panel => panel.Controls.OfType<GlowCheckBox>())
                    .Single(box => box.Text == "Use local model for ambiguous words");
                Assert(checkbox.Enabled && !checkbox.Checked && downloads == 0 && saves == 0,
                    "Opening settings downloaded the optional model or enabled it eagerly.");
                Assert(!store.GetAvailability().IsInstalled, "Constructing settings installed the optional model eagerly.");
                AssertCoreAbsent(setup);
                report.Write("settings-before-toggle", new { downloads, saves, checkbox.Checked, checkbox.Enabled });
                cancellationToken.ThrowIfCancellationRequested();
                checkbox.Checked = true; // The production CheckedChanged handler owns setup and saving.
                if (installFailure is not null) throw new InvalidOperationException("Real optional setup failed.", installFailure);
                cancellationToken.ThrowIfCancellationRequested();
                Assert(downloads == 1 && saves == 1 && fullyVerified && checkbox.Checked
                    && configuration.LoadOrCreate().Configuration.ContextualPronunciationEnabled,
                    "The production checkbox did not save enabled exactly once after verified setup.");
                completed.TrySetResult();
            }
            catch (Exception exception) { completed.TrySetException(exception); }
        }) { IsBackground = true, Name = "Optional release setup STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }

    private static void AssertGuestAndReportDirectory(string reportDirectory)
    {
        Assert(OperatingSystem.IsWindows()
            && string.Equals(Environment.UserName, "WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Environment.GetEnvironmentVariable("USERNAME"), "WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase),
            "This opt-in download is restricted to Windows Sandbox WDAGUtilityAccount; no host execution is allowed.");
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Reports");
        Assert(string.Equals(Path.GetFullPath(reportDirectory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
            "The optional smoke report directory must be exactly the guest Desktop\\Reports.");
    }

    private static void AssertCoreAbsent(ModelSetupService setup) =>
        Assert(!File.Exists(setup.KokoroPath)
            && !Directory.EnumerateFiles(AppContext.BaseDirectory, "*.onnx", SearchOption.AllDirectories).Any()
            && (!Directory.Exists(Path.GetDirectoryName(setup.KokoroPath))
                || !Directory.EnumerateFiles(Path.GetDirectoryName(setup.KokoroPath)!, "*", SearchOption.AllDirectories).Any()),
            "Classifier-only setup downloaded or included a Kokoro/core ONNX model.");

    private static void AssertNotices(ContextualPronunciationInstallation installation)
    {
        var directory = Path.Combine(installation.Root, "licenses");
        foreach (var name in new[] { "Apache-2.0-LICENSE.txt", "llama.cpp-LICENSE.txt", "LLVM-OpenMP-LICENSE.txt", "MODEL-SOURCES.txt" })
        {
            var path = Path.Combine(directory, name);
            Assert(File.Exists(path) && new FileInfo(path).Length > 0,
                "The verified optional installation is missing a required notice: " + name);
            if (name == "MODEL-SOURCES.txt") continue;
            using var resource = typeof(ModelSetupService).Assembly.GetManifestResourceStream("PulseReader.Setup." + name)
                ?? throw new InvalidOperationException("The expected notice resource is missing: " + name);
            using var expected = new MemoryStream();
            resource.CopyTo(expected);
            Assert(File.ReadAllBytes(path).SequenceEqual(expected.ToArray()),
                "The installed optional notice differs from the production resource: " + name);
        }
        var sources = File.ReadAllText(Path.Combine(directory, "MODEL-SOURCES.txt"));
        Assert(sources.Contains("4edb920b6f14e3b9284d4502a6485103d72cde05", StringComparison.Ordinal)
            && sources.Contains("b10662", StringComparison.Ordinal), "Optional notices do not identify the pinned sources.");
    }

    private static bool IsProcessRunning(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void PutOffscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-10000, -10000);
        form.ShowInTaskbar = false;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ReportingProgress(Action<SetupProgress> report) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => report(value);
    }

    // Observe real labels so an unchanged reside sentence cannot conceal fallback.
    private sealed class ObservedClassifier(ContextualPronunciationInstallation installation, SmokeReport report)
        : IContextualSenseClassifier
    {
        private readonly ContextualPronunciationServer _server = new(
            installation.ServerPath, installation.ModelPath, installation.LogDirectory);
        public List<string?> Labels { get; } = [];
        public List<int> ProcessIds { get; } = [];
        public bool Disposed { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var start = _server.StartAsync(cancellationToken);
                if (_server.ProcessId > 0) ProcessIds.Add(_server.ProcessId);
                await start;
                report.Write("real-server-started", new { _server.ProcessId, elapsedSeconds = stopwatch.Elapsed.TotalSeconds });
            }
            catch (Exception exception)
            {
                report.Write("real-server-start-failed", new
                {
                    exception = exception.ToString(), ProcessIds, elapsedSeconds = stopwatch.Elapsed.TotalSeconds
                });
                throw;
            }
        }

        public async Task<string?> ClassifyAsync(ContextualPronunciationTarget target, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var label = await _server.ClassifyAsync(target, cancellationToken);
                Labels.Add(label);
                report.Write("real-classification", new
                {
                    target.UnitIndex, target.Context, label, elapsedSeconds = stopwatch.Elapsed.TotalSeconds
                });
                return label;
            }
            catch (Exception exception)
            {
                report.Write("real-classification-failed", new
                {
                    target.UnitIndex, target.Context, exception = exception.ToString(),
                    elapsedSeconds = stopwatch.Elapsed.TotalSeconds
                });
                throw;
            }
        }

        public void Dispose()
        {
            _server.Dispose();
            Disposed = true;
            report.Write("real-server-disposed", new { ProcessIds, processesStopped = ProcessIds.All(id => !IsProcessRunning(id)) });
        }
    }

    private sealed class SmokeReport : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly StreamWriter _events;
        private readonly string _latestPath;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private string? _lastProgressStage;
        private long _lastProgressTimestamp;

        public SmokeReport(string directory)
        {
            _latestPath = Path.Combine(directory, "optional-model-progress.json");
            _events = new StreamWriter(new FileStream(Path.Combine(directory, "optional-model-events.jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }

        public void Progress(SetupProgress value)
        {
            lock (_gate)
            {
                var stage = value.AssetName + "/" + value.Stage;
                if (stage == _lastProgressStage
                    && Stopwatch.GetElapsedTime(_lastProgressTimestamp) < TimeSpan.FromSeconds(1)) return;
                _lastProgressStage = stage;
                _lastProgressTimestamp = Stopwatch.GetTimestamp();
                Write("setup-progress", value);
            }
        }

        public void Write(string stage, object details)
        {
            lock (_gate)
            {
                var json = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTime.UtcNow, elapsedSeconds = _elapsed.Elapsed.TotalSeconds, stage, details
                });
                _events.WriteLine(json);
                File.WriteAllText(_latestPath, json + Environment.NewLine);
                Console.WriteLine($"OPTIONAL MODEL: {stage} ({_elapsed.Elapsed.TotalSeconds:0.0}s)");
            }
        }

        public void Dispose() { lock (_gate) _events.Dispose(); }
    }
}
