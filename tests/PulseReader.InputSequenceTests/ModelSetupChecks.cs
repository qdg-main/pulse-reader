using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReader.Setup;
using PulseReader.Speech;

internal static class ModelSetupChecks
{
    public static async Task RunAsync()
    {
        await CheckDownloadsAsync();
        await CheckCancellationAsync();
        await CheckOptionalAsync();
        CheckOptionalPublicationFailure();
        CheckOptionalRollbackFailureRetainsLogs();
        CheckOptionalLogMigrationConflict();
        await CheckOptionalRepairConflictAsync();
        await CheckInterruptedCommitRecoveryAsync();
        await CheckInterruptedLogHandoffRecoveryAsync();
        await CheckRecoveryAfterEarlierConflictAsync();
        await CheckAmbiguousRecoveryAsync();
        await CheckOrphanedLogsWithoutVerifiedStageAsync();
        Console.WriteLine("PASS: repair recovery prefers one verified pair in every order and leaves ambiguous or unverified histories unchanged.");
        Console.WriteLine("PASS: optional repair preserves exact logs, rejects conflicts, restores failed publication and recovers interrupted log handoff.");
        await CheckArchiveSafetyAsync();
        Console.WriteLine("PASS: model setup verifies pins, supports cancellation/retry, serializes downloads, and safely stages optional runtime extraction.");
    }

    private static void CheckOptionalPublicationFailure()
    {
        using var fixture = new Fixture();
        var destination = Path.Combine(fixture.Root, "optional");
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        var backup = destination + ".backup-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(destination, "logs"));
        Directory.CreateDirectory(staging);
        var originalLog = Path.Combine(destination, "logs", "diagnostics.bin");
        byte[] bytes = [255, 0, 13, 10, 7];
        File.WriteAllBytes(originalLog, bytes);
        File.WriteAllText(Path.Combine(destination, "old-model.bin"), "old model");
        File.WriteAllText(Path.Combine(staging, "new-model.bin"), "new model");
        try
        {
            ModelSetupService.PublishOptionalInstallation(staging, destination, backup, (_, _) =>
            {
                Assert(!Directory.Exists(destination) && File.Exists(Path.Combine(staging, "logs", "diagnostics.bin")),
                    "Publication failure fixture did not reach the real log handoff.");
                throw new IOException("Fixture publication interruption.");
            });
            throw new InvalidOperationException("The publication interruption was not reported.");
        }
        catch (IOException) { }
        Assert(File.ReadAllBytes(originalLog).SequenceEqual(bytes)
            && File.ReadAllText(Path.Combine(destination, "old-model.bin")) == "old model"
            && !Directory.Exists(backup) && !Directory.Exists(Path.Combine(staging, "logs")),
            "Failed publication did not restore the old installation and exact log bytes.");
        ModelSetupService.PublishOptionalInstallation(staging, destination, backup);
        Assert(File.ReadAllBytes(originalLog).SequenceEqual(bytes)
            && File.ReadAllText(Path.Combine(destination, "new-model.bin")) == "new model"
            && Directory.EnumerateFiles(fixture.Root, "diagnostics.bin", SearchOption.AllDirectories).Count() == 1,
            "Publication retry duplicated, overwrote or misplaced saved logs.");
    }

    private static void CheckOptionalLogMigrationConflict()
    {
        using var fixture = new Fixture();
        var destination = Path.Combine(fixture.Root, "optional");
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        var backup = destination + ".backup-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(destination, "logs"));
        Directory.CreateDirectory(Path.Combine(staging, "logs"));
        var originalLog = Path.Combine(destination, "logs", "diagnostics.bin");
        var conflictingLog = Path.Combine(staging, "logs", "diagnostics.bin");
        File.WriteAllText(originalLog, "original diagnostic bytes");
        File.WriteAllText(conflictingLog, "conflicting diagnostic bytes");
        try
        {
            ModelSetupService.PublishOptionalInstallation(staging, destination, backup);
            throw new InvalidOperationException("The log migration conflict was not reported.");
        }
        catch (IOException) { }
        Assert(File.ReadAllText(originalLog) == "original diagnostic bytes"
            && File.ReadAllText(conflictingLog) == "conflicting diagnostic bytes" && !Directory.Exists(backup),
            "Log migration conflict overwrote data or failed to restore the old root.");
    }

    private static void CheckOptionalRollbackFailureRetainsLogs()
    {
        using var fixture = new Fixture();
        var destination = Path.Combine(fixture.Root, "optional");
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        var backup = destination + ".backup-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(destination, "logs"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(destination, "logs", "original.log"), "original diagnostic bytes");
        try
        {
            ModelSetupService.PublishOptionalInstallation(staging, destination, backup, (_, _) =>
            {
                Directory.CreateDirectory(Path.Combine(backup, "logs"));
                File.WriteAllText(Path.Combine(backup, "logs", "conflict.log"), "rollback conflict bytes");
                throw new IOException("Fixture publication interruption.");
            });
            throw new InvalidOperationException("The rollback conflict was not reported.");
        }
        catch (IOException exception)
        {
            Assert(exception.Message.Contains("retained", StringComparison.Ordinal),
                "Rollback failure did not explain where the retained data remains.");
        }
        Assert(File.ReadAllText(Path.Combine(staging, "logs", "original.log")) == "original diagnostic bytes"
            && File.ReadAllText(Path.Combine(backup, "logs", "conflict.log")) == "rollback conflict bytes",
            "Rollback failure overwrote or deleted retained logs.");
    }

    private static async Task CheckOptionalRepairConflictAsync()
    {
        using var fixture = new Fixture();
        var model = Encoding.UTF8.GetBytes("classifier");
        var runtime = Encoding.UTF8.GetBytes("server");
        var archive = Zip(("llama-server.exe", runtime));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ContextualPronunciationInstallationManifest(
            1, Hash(archive), "classifier.bin", Hash(model),
            [new("llama-server.exe", runtime.Length, Hash(runtime))]));
        using var client = Client(uri => new ByteArrayContent(uri.AbsolutePath == "/runtime.zip" ? archive : model));
        var setup = new ModelSetupService(fixture.Root, new VerifiedAssetDownloader(client), Asset("kokoro.onnx", model),
            Asset("runtime.zip", archive), Asset("classifier.bin", model), () => manifest, Hash(manifest));
        var root = Path.Combine(fixture.Root, "PulseReader", "ContextualPronunciation");
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        var log = Path.Combine(root, "logs", "original.log");
        File.WriteAllText(log, "original production diagnostics");
        string? conflict = null;
        await ThrowsAsync<IOException>(() => setup.EnsureContextualPronunciationAsync(new InlineProgress(value =>
        {
            if (value.Stage != "Extracting") return;
            var staging = Directory.EnumerateDirectories(Path.GetDirectoryName(root)!, "ContextualPronunciation.staging-*").Single();
            Directory.CreateDirectory(Path.Combine(staging, "logs"));
            conflict = Path.Combine(staging, "logs", "conflict.log");
            File.WriteAllText(conflict, "conflicting diagnostic bytes");
        }), CancellationToken.None));
        Assert(File.ReadAllText(log) == "original production diagnostics"
            && conflict is not null && File.ReadAllText(conflict) == "conflicting diagnostic bytes"
            && !Directory.EnumerateDirectories(Path.GetDirectoryName(root)!, "ContextualPronunciation.backup-*").Any(),
            "The complete setup failure or staging cleanup lost logs or failed to restore the original root.");
        await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
        Assert(File.ReadAllText(log) == "original production diagnostics" && File.Exists(conflict!)
            && Directory.EnumerateFiles(fixture.Root, "original.log", SearchOption.AllDirectories).Count() == 1,
            "Retry after a migration conflict overwrote, duplicated or misplaced saved logs.");
    }

    private static async Task CheckInterruptedLogHandoffRecoveryAsync()
    {
        using var fixture = new Fixture();
        var model = Encoding.UTF8.GetBytes("classifier");
        var runtime = Encoding.UTF8.GetBytes("server");
        var archive = Zip(("llama-server.exe", runtime));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ContextualPronunciationInstallationManifest(
            1, Hash(archive), "classifier.bin", Hash(model),
            [new("llama-server.exe", runtime.Length, Hash(runtime))]));
        var root = Path.Combine(fixture.Root, "PulseReader", "ContextualPronunciation");
        var attempt = Guid.NewGuid().ToString("N");
        var staging = root + ".staging-" + attempt;
        var backup = root + ".backup-" + attempt;
        Directory.CreateDirectory(Path.Combine(staging, "models"));
        Directory.CreateDirectory(Path.Combine(staging, "runtime"));
        File.WriteAllBytes(Path.Combine(staging, "models", "classifier.bin"), model);
        File.WriteAllBytes(Path.Combine(staging, "runtime", "llama-server.exe"), runtime);
        File.WriteAllBytes(Path.Combine(staging, "installation-manifest.json"), manifest);
        Directory.CreateDirectory(Path.Combine(backup, "logs"));
        byte[] bytes = [255, 0, 13, 10, 3, 9];
        File.WriteAllBytes(Path.Combine(backup, "logs", "original.log"), bytes);
        using var client = Client(_ => throw new InvalidOperationException("Recovery must not download."));
        var setup = new ModelSetupService(fixture.Root, new VerifiedAssetDownloader(client), Asset("kokoro.onnx", model),
            Asset("runtime.zip", archive), Asset("classifier.bin", model), () => manifest, Hash(manifest));
        await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
        Assert(File.ReadAllBytes(Path.Combine(root, "logs", "original.log")).SequenceEqual(bytes)
            && !Directory.Exists(staging)
            && Directory.EnumerateFiles(fixture.Root, "original.log", SearchOption.AllDirectories).Count() == 1,
            "Recovery after old-root backup but before log handoff lost or duplicated saved logs.");
    }

    private static async Task CheckRecoveryAfterEarlierConflictAsync()
    {
        foreach (var oldFirst in new[] { true, false })
        foreach (var handoffCompleted in new[] { false, true })
        {
            using var fixture = new Fixture();
            var model = Encoding.UTF8.GetBytes("classifier");
            var runtime = Encoding.UTF8.GetBytes("server");
            var archive = Zip(("llama-server.exe", runtime));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new ContextualPronunciationInstallationManifest(
                1, Hash(archive), "classifier.bin", Hash(model), [new("llama-server.exe", runtime.Length, Hash(runtime))]));
            var root = Path.Combine(fixture.Root, "PulseReader", "ContextualPronunciation");
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            byte[] original = [255, 0, 13, 10, 8, 4];
            byte[] stale = [1, 2, 3];
            var log = Path.Combine(root, "logs", "production.log");
            File.WriteAllBytes(log, original);
            using var client = Client(uri => new ByteArrayContent(uri.AbsolutePath == "/runtime.zip" ? archive : model));
            string? oldStage = null;
            var setup = new ModelSetupService(fixture.Root, new VerifiedAssetDownloader(client), Asset("kokoro.onnx", model),
                Asset("runtime.zip", archive), Asset("classifier.bin", model), () => manifest, Hash(manifest))
            {
                RecoveryCandidateComparer = Comparer<string>.Create((left, right) =>
                    ((left == oldStage) ? (oldFirst ? 0 : 1) : (oldFirst ? 1 : 0))
                    .CompareTo((right == oldStage) ? (oldFirst ? 0 : 1) : (oldFirst ? 1 : 0)))
            };
            await ThrowsAsync<IOException>(() => setup.EnsureContextualPronunciationAsync(new InlineProgress(value =>
            {
                if (value.Stage != "Extracting") return;
                oldStage = Directory.EnumerateDirectories(Path.GetDirectoryName(root)!, "ContextualPronunciation.staging-*").Single();
                Directory.CreateDirectory(Path.Combine(oldStage, "logs"));
                File.WriteAllBytes(Path.Combine(oldStage, "logs", "production.log"), stale);
            }), CancellationToken.None));
            await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
            var attempt = Guid.NewGuid().ToString("N");
            var currentStage = root + ".staging-" + attempt;
            var currentBackup = root + ".backup-" + attempt;
            WriteVerifiedStage(currentStage, model, runtime, manifest);
            Directory.Move(root, currentBackup);
            if (handoffCompleted) Directory.Move(Path.Combine(currentBackup, "logs"), Path.Combine(currentStage, "logs"));
            await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
            Assert(File.Exists(log) && File.ReadAllBytes(log).SequenceEqual(original),
                "Recovery chose an older failed-conflict stage and stranded current production logs in backup.");
            Assert(File.ReadAllBytes(Path.Combine(oldStage!, "logs", "production.log")).SequenceEqual(stale)
                && Directory.Exists(oldStage!) && !Directory.Exists(currentStage),
                "Recovery changed the unrelated older conflicting stage or failed to publish the paired repair.");
        }
    }

    private static async Task CheckAmbiguousRecoveryAsync()
    {
        foreach (var mode in new[] { "paired", "unpaired", "corrupt-paired", "file-backup", "orphan-backup", "paired-log-conflict" })
        foreach (var reverse in new[] { false, true })
        {
            using var fixture = new Fixture();
            var model = Encoding.UTF8.GetBytes("classifier");
            var runtime = Encoding.UTF8.GetBytes("server");
            var archive = Zip(("llama-server.exe", runtime));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new ContextualPronunciationInstallationManifest(
                1, Hash(archive), "classifier.bin", Hash(model), [new("llama-server.exe", runtime.Length, Hash(runtime))]));
            var root = Path.Combine(fixture.Root, "PulseReader", "ContextualPronunciation");
            var firstAttempt = Guid.NewGuid().ToString("N");
            var first = root + ".staging-" + firstAttempt;
            WriteVerifiedStage(first, model, runtime, manifest);
            var firstBackup = root + ".backup-" + (mode == "orphan-backup" ? Guid.NewGuid().ToString("N") : firstAttempt);
            if (mode == "file-backup") File.WriteAllText(firstBackup, "not a directory");
            else if (mode != "unpaired")
            {
                Directory.CreateDirectory(Path.Combine(firstBackup, "logs"));
                File.WriteAllText(Path.Combine(firstBackup, "logs", "production.log"), "first current diagnostics");
            }
            if (mode == "corrupt-paired") File.WriteAllText(Path.Combine(first, "models", "classifier.bin"), "corrupt");
            if (mode == "paired-log-conflict")
            {
                Directory.CreateDirectory(Path.Combine(first, "logs"));
                File.WriteAllText(Path.Combine(first, "logs", "production.log"), "conflicting staged diagnostics");
            }
            if (mode is not ("file-backup" or "orphan-backup" or "paired-log-conflict"))
            {
                var secondAttempt = Guid.NewGuid().ToString("N");
                var second = root + ".staging-" + secondAttempt;
                WriteVerifiedStage(second, model, runtime, manifest);
                if (mode == "paired")
                {
                    Directory.CreateDirectory(Path.Combine(root + ".backup-" + secondAttempt, "logs"));
                    File.WriteAllText(Path.Combine(root + ".backup-" + secondAttempt, "logs", "production.log"), "second current diagnostics");
                }
            }
            var before = Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            using var client = Client(_ => throw new InvalidOperationException("Ambiguous recovery must not download."));
            var setup = new ModelSetupService(fixture.Root, new VerifiedAssetDownloader(client), Asset("kokoro.onnx", model),
                Asset("runtime.zip", archive), Asset("classifier.bin", model), () => manifest, Hash(manifest))
            { RecoveryCandidateComparer = Comparer<string>.Create((left, right) => reverse
                ? StringComparer.Ordinal.Compare(right, left) : StringComparer.Ordinal.Compare(left, right)) };
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => setup.EnsureContextualPronunciationAsync(null, cancelled.Token));
                Assert(!Path.Exists(root) && before.All(pair => File.Exists(pair.Key)
                    && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "Cancelled recovery changed retained data.");
            }
            await ThrowsAsync<InvalidDataException>(() => setup.EnsureContextualPronunciationAsync(null, CancellationToken.None));
            Assert(!Path.Exists(root) && before.All(pair => File.Exists(pair.Key)
                && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "Ambiguous or unverified paired recovery published a stage or changed retained data.");
        }
    }

    private static async Task CheckOrphanedLogsWithoutVerifiedStageAsync()
    {
        foreach (var invalidStage in new[] { false, true })
        {
            using var fixture = new Fixture();
            var model = Encoding.UTF8.GetBytes("classifier");
            var runtime = Encoding.UTF8.GetBytes("server");
            var archive = Zip(("llama-server.exe", runtime));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new ContextualPronunciationInstallationManifest(
                1, Hash(archive), "classifier.bin", Hash(model), [new("llama-server.exe", runtime.Length, Hash(runtime))]));
            var root = Path.Combine(fixture.Root, "PulseReader", "ContextualPronunciation");
            var backup = root + ".backup-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(backup, "logs"));
            var log = Path.Combine(backup, "logs", "production.log");
            byte[] saved = [0, 255, 13, 10, 7];
            File.WriteAllBytes(log, saved);
            string? stage = null;
            if (invalidStage)
            {
                stage = root + ".staging-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, "installation-manifest.json"), "incomplete stage");
            }
            var requests = 0;
            using var client = Client(uri =>
            {
                requests++;
                return new ByteArrayContent(uri.AbsolutePath == "/runtime.zip" ? archive : model);
            });
            var setup = new ModelSetupService(fixture.Root, new VerifiedAssetDownloader(client), Asset("kokoro.onnx", model),
                Asset("runtime.zip", archive), Asset("classifier.bin", model), () => manifest, Hash(manifest));
            var rejected = false;
            try { await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None); }
            catch (InvalidDataException exception)
            {
                rejected = exception.Message.Contains("backup", StringComparison.OrdinalIgnoreCase);
            }
            Assert(rejected && requests == 0 && !Path.Exists(root) && File.ReadAllBytes(log).SequenceEqual(saved),
                "Recovery installed a fresh model and stranded backup logs when no staging candidate verified.");
            if (stage is not null)
                Assert(File.ReadAllText(Path.Combine(stage, "installation-manifest.json")) == "incomplete stage",
                    "Rejected orphan-log recovery changed an invalid staging directory.");
        }
    }

    private static void WriteVerifiedStage(string stage, byte[] model, byte[] runtime, byte[] manifest)
    {
        Directory.CreateDirectory(Path.Combine(stage, "models"));
        Directory.CreateDirectory(Path.Combine(stage, "runtime"));
        File.WriteAllBytes(Path.Combine(stage, "models", "classifier.bin"), model);
        File.WriteAllBytes(Path.Combine(stage, "runtime", "llama-server.exe"), runtime);
        File.WriteAllBytes(Path.Combine(stage, "installation-manifest.json"), manifest);
    }

    private static async Task CheckDownloadsAsync()
    {
        using var fixture = new Fixture();
        var good = Encoding.UTF8.GetBytes("small valid speech model");
        var asset = Asset("kokoro.onnx", good);
        var attempts = 0;
        using var client = Client(_ => new ByteArrayContent(++attempts == 1 ? Encoding.UTF8.GetBytes("corrupt") : good));
        var downloader = new VerifiedAssetDownloader(client);
        var path = Path.Combine(fixture.Root, "model.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("previous installation"));
        await ThrowsAsync<InvalidDataException>(() => downloader.EnsureAsync(asset, path, null, CancellationToken.None));
        Assert(File.ReadAllText(path) == "previous installation", "A corrupt download replaced an existing installation.");
        Assert(!Directory.EnumerateFiles(fixture.Root, "*.download").Any(), "A failed download left temporary bytes.");
        var reports = new List<SetupProgress>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            new VerifiedAssetDownloader(client).EnsureAsync(asset, path,
                new InlineProgress(reports.Add), CancellationToken.None)));
        Assert(attempts == 2, "Concurrent requests downloaded the same model more than once.");
        Assert(File.ReadAllBytes(path).SequenceEqual(good), "Retry did not install verified bytes.");
        Assert(reports.Any(p => p.Stage == "Downloading" && p.BytesReceived == good.Length)
               && reports.Any(p => p.Stage == "Installed"), "Progress omitted download or completion.");

        using var broken = Client(_ => new StreamContent(new FailingStream()));
        await ThrowsAsync<IOException>(() => new VerifiedAssetDownloader(broken).EnsureAsync(
            asset, Path.Combine(fixture.Root, "interrupted.bin"), null, CancellationToken.None));
        Assert(!File.Exists(Path.Combine(fixture.Root, "interrupted.bin"))
               && !Directory.EnumerateFiles(fixture.Root, "*.download").Any(), "Interrupted bytes counted as installed.");
    }

    private static async Task CheckCancellationAsync()
    {
        using var fixture = new Fixture();
        var data = new byte[160000];
        using var client = Client(_ => new ByteArrayContent(data));
        using var cancellation = new CancellationTokenSource();
        var asset = Asset("kokoro.onnx", data);
        var service = CreateService(fixture.Root, client, asset);
        await ThrowsAsync<OperationCanceledException>(() => service.EnsureKokoroAsync(
            new InlineProgress(p => { if (p.Stage == "Downloading" && p.BytesReceived > 0) cancellation.Cancel(); }),
            cancellation.Token));
        Assert(!service.IsKokoroInstalled, "Cancelled speech setup counted as installed.");
        Assert(!Directory.EnumerateFiles(Path.Combine(fixture.Root, "PulseReader", "Models"), "*.download").Any(),
            "Cancelled setup left a partial download.");
        await service.EnsureKokoroAsync(null, CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.EnsureKokoroAsync(null, CancellationToken.None)));
        Assert(service.IsKokoroInstalled && await service.VerifyKokoroInstalledAsync(CancellationToken.None),
            "Speech setup could not retry after cancellation.");
        Assert(File.Exists(Path.Combine(fixture.Root, "PulseReader", "Models", "licenses", "Apache-2.0-LICENSE.txt")),
            "Kokoro setup did not retain its licence.");
    }

    private static async Task CheckOptionalAsync()
    {
        using var fixture = new Fixture();
        var speech = Encoding.UTF8.GetBytes("speech");
        var model = Encoding.UTF8.GetBytes("classifier");
        var runtime = Encoding.UTF8.GetBytes("server");
        var archive = Zip(("llama-server.exe", runtime));
        var files = new[] { new ContextualPronunciationRuntimeFile("llama-server.exe", runtime.Length, Hash(runtime)) };
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ContextualPronunciationInstallationManifest(
            1, Hash(archive), "classifier.bin", Hash(model), files));
        var requests = new List<string>();
        var failModel = true;
        using var client = Client(uri =>
        {
            requests.Add(uri.AbsolutePath);
            return new ByteArrayContent(uri.AbsolutePath switch
            {
                "/kokoro.onnx" => speech,
                "/runtime.zip" => archive,
                "/classifier.bin" => failModel ? Encoding.UTF8.GetBytes("corrupt model") : model,
                _ => throw new InvalidOperationException("Unexpected fixture request.")
            });
        });
        var setup = new ModelSetupService(fixture.Root, new VerifiedAssetDownloader(client), Asset("kokoro.onnx", speech),
            Asset("runtime.zip", archive), Asset("classifier.bin", model), () => manifest, Hash(manifest));
        await setup.EnsureKokoroAsync(null, CancellationToken.None);
        Assert(requests.SequenceEqual(new[] { "/kokoro.onnx" }), "Core setup requested the optional classifier.");
        var optionalRoot = Path.Combine(fixture.Root, "PulseReader", "ContextualPronunciation");
        Directory.CreateDirectory(optionalRoot);
        File.WriteAllText(Path.Combine(optionalRoot, "old.txt"), "old installation");
        var logPath = Path.Combine(optionalRoot, "logs", "llama-server.stderr.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        byte[] logBytes = [0, 1, 2, 255, 13, 10, 42];
        File.WriteAllBytes(logPath, logBytes);
        await ThrowsAsync<InvalidDataException>(() => setup.EnsureContextualPronunciationAsync(null, CancellationToken.None));
        Assert(File.ReadAllText(Path.Combine(optionalRoot, "old.txt")) == "old installation",
            "Failed optional setup altered the existing installation.");
        Assert(File.ReadAllBytes(logPath).SequenceEqual(logBytes), "Failed optional setup changed saved logs.");
        Assert(!Directory.EnumerateDirectories(Path.GetDirectoryName(optionalRoot)!, "*.staging-*").Any(),
            "Failed optional setup left staging.");
        failModel = false;
        using (var cancellation = new CancellationTokenSource())
        {
            await ThrowsAsync<OperationCanceledException>(() => setup.EnsureContextualPronunciationAsync(
                new InlineProgress(p => { if (p.AssetName == "classifier.bin" && p.Stage == "Downloading") cancellation.Cancel(); }),
                cancellation.Token));
        }
        Assert(File.Exists(Path.Combine(optionalRoot, "old.txt")), "Cancelled optional setup changed the existing installation.");
        Assert(File.ReadAllBytes(logPath).SequenceEqual(logBytes), "Cancelled optional setup changed saved logs.");
        await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
        Assert(File.Exists(logPath) && File.ReadAllBytes(logPath).SequenceEqual(logBytes),
            "Successful optional repair deleted or changed the existing production logs.");
        Assert(Directory.EnumerateFiles(fixture.Root, "llama-server.stderr.log", SearchOption.AllDirectories).Count() == 1,
            "Successful optional repair duplicated saved logs.");
        Assert(File.ReadAllBytes(Path.Combine(optionalRoot, "installation-manifest.json")).SequenceEqual(manifest),
            "Installed manifest bytes changed.");
        Assert(File.ReadAllBytes(Path.Combine(optionalRoot, "runtime", "llama-server.exe")).SequenceEqual(runtime),
            "Installed runtime differs from its manifest.");
        Assert(File.Exists(Path.Combine(optionalRoot, "licenses", "llama.cpp-LICENSE.txt"))
            && File.Exists(Path.Combine(optionalRoot, "licenses", "LLVM-OpenMP-LICENSE.txt"))
            && File.ReadAllText(Path.Combine(optionalRoot, "licenses", "MODEL-SOURCES.txt")).Contains("Qwen3-4B-Instruct-2507"),
            "Optional setup did not retain runtime/model licences and source attribution.");
        var count = requests.Count;
        await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
        Assert(count == requests.Count, "A verified optional installation downloaded again.");
    }

    private static async Task CheckInterruptedCommitRecoveryAsync()
    {
        using var fixture = new Fixture();
        var model = Encoding.UTF8.GetBytes("recovered classifier");
        var runtime = Encoding.UTF8.GetBytes("recovered server");
        var archive = Zip(("llama-server.exe", runtime));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ContextualPronunciationInstallationManifest(
            1, Hash(archive), "classifier.bin", Hash(model),
            [new ContextualPronunciationRuntimeFile("llama-server.exe", runtime.Length, Hash(runtime))]));
        var optionalRoot = Path.Combine(fixture.Root, "PulseReader", "ContextualPronunciation");
        var staging = optionalRoot + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(staging, "runtime"));
        Directory.CreateDirectory(Path.Combine(staging, "models"));
        File.WriteAllBytes(Path.Combine(staging, "runtime", "llama-server.exe"), runtime);
        File.WriteAllBytes(Path.Combine(staging, "models", "classifier.bin"), model);
        File.WriteAllBytes(Path.Combine(staging, "installation-manifest.json"), manifest);
        Directory.CreateDirectory(Path.Combine(staging, "logs"));
        File.WriteAllText(Path.Combine(staging, "logs", "retained.log"), "interrupted repair diagnostics");
        var backup = optionalRoot + ".backup-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "old.txt"), "previous installation");
        var unknown = optionalRoot + ".staging-not-a-guid";
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "unknown.txt"), "untouched");
        var invalid = optionalRoot + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, "installation-manifest.json"), "invalid manifest");
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            throw new InvalidOperationException("Recovery must not request a download.");
        });
        var setup = new ModelSetupService(fixture.Root, new VerifiedAssetDownloader(client), Asset("kokoro.onnx", model),
            Asset("runtime.zip", archive), Asset("classifier.bin", model), () => manifest, Hash(manifest));
        await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
        Assert(requests == 0 && Directory.Exists(optionalRoot) && !Directory.Exists(staging),
            "Interrupted commit did not recover its fully verified staging directory without downloading.");
        Assert(File.ReadAllBytes(Path.Combine(optionalRoot, "models", "classifier.bin")).SequenceEqual(model),
            "Recovered installation changed model bytes.");
        Assert(File.ReadAllText(Path.Combine(optionalRoot, "logs", "retained.log")) == "interrupted repair diagnostics",
            "Interrupted publication recovery changed its retained logs.");
        Assert(File.ReadAllText(Path.Combine(backup, "old.txt")) == "previous installation"
               && File.ReadAllText(Path.Combine(unknown, "unknown.txt")) == "untouched"
               && File.ReadAllText(Path.Combine(invalid, "installation-manifest.json")) == "invalid manifest",
            "Recovery altered a backup or unverified staging directory.");
        await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
        Assert(requests == 0, "Recovered installation did not remain verified on retry.");
    }

    private static async Task CheckArchiveSafetyAsync()
    {
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes("server");
        var files = new[] { new ContextualPronunciationRuntimeFile("llama-server.exe", bytes.Length, Hash(bytes)) };
        foreach (var malicious in new[] { "../escaped.dll", "/rooted.dll", "C:/rooted.dll", "nested/../../escaped.dll" })
        {
            var path = Path.Combine(fixture.Root, "malicious.zip");
            File.WriteAllBytes(path, Zip((malicious, bytes), ("llama-server.exe", bytes)));
            await ThrowsAsync<InvalidDataException>(() => ModelSetupService.ExtractRuntimeAsync(path,
                Path.Combine(fixture.Root, "runtime-" + Guid.NewGuid().ToString("N")), files, CancellationToken.None));
        }
        var archivePath = Path.Combine(fixture.Root, "extra.zip");
        File.WriteAllBytes(archivePath, Zip(("llama-server.exe", bytes), ("unlisted.dll", bytes)));
        var output = Path.Combine(fixture.Root, "only-manifest");
        await ModelSetupService.ExtractRuntimeAsync(archivePath, output, files, CancellationToken.None);
        Assert(!File.Exists(Path.Combine(output, "unlisted.dll")), "Extraction installed an unlisted archive file.");
        File.WriteAllBytes(archivePath, Zip(("llama-server.exe", bytes), ("llama-server.exe", bytes)));
        await ThrowsAsync<InvalidDataException>(() => ModelSetupService.ExtractRuntimeAsync(archivePath,
            Path.Combine(fixture.Root, "duplicates"), files, CancellationToken.None));
        File.WriteAllBytes(archivePath, Zip(("llama-server.exe", Encoding.UTF8.GetBytes("wrong!"))));
        await ThrowsAsync<InvalidDataException>(() => ModelSetupService.ExtractRuntimeAsync(archivePath,
            Path.Combine(fixture.Root, "checksum"), files, CancellationToken.None));
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry("llama-server.exe");
                entry.ExternalAttributes = unchecked((int)0xa0000000);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
            File.WriteAllBytes(archivePath, memory.ToArray());
        }
        await ThrowsAsync<InvalidDataException>(() => ModelSetupService.ExtractRuntimeAsync(archivePath,
            Path.Combine(fixture.Root, "symlink"), files, CancellationToken.None));
    }

    private static ModelSetupService CreateService(string root, HttpClient client, SetupAsset speech) =>
        new(root, new VerifiedAssetDownloader(client), speech, speech, speech,
            () => throw new InvalidOperationException("Optional setup must remain dormant."), "unused");
    private static SetupAsset Asset(string fileName, byte[] bytes) =>
        new(fileName, new Uri("https://fixture.invalid/" + fileName), Hash(bytes), 1024 * 1024);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static HttpClient Client(Func<Uri, HttpContent> content) => new(new FakeHandler(content));
    private static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                using var stream = zip.CreateEntry(file.Name).Open();
                stream.Write(file.Bytes);
            }
        return memory.ToArray();
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class InlineProgress(Action<SetupProgress> action) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => action(value);
    }
    private sealed class FakeHandler(Func<Uri, HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content(request.RequestUri!) });
        }
    }
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Fixture interruption.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Fixture interruption."));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PulseReader-model-tests-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => SetupPaths.DeleteTree(Root);
    }
}
