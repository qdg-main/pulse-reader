using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PulseReader.Speech;

namespace PulseReader.Setup;

internal sealed class ModelSetupService
{
    internal static readonly SetupAsset KokoroAsset = new("Kokoro speech model",
        new Uri("https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx"),
        // GitHub v2.0.0 asset digest, also verified against the existing installed model.
        "0CFD5E79AAB70A3D8C1A57DC639835110DDB32C9F5FF4FDD1F4DB202EA43BB05", 325508342, 325508342);
    private static readonly SetupAsset RuntimeAsset = new("Local pronunciation runtime",
        new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b10662/llama-b10662-bin-win-cpu-x64.zip"),
        ContextualPronunciationModelStore.RuntimeArchiveSha256, 512L * 1024 * 1024);
    private static readonly SetupAsset PronunciationAsset = new("Optional pronunciation model",
        new Uri("https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF/resolve/4edb920b6f14e3b9284d4502a6485103d72cde05/Qwen3-4B-Instruct-2507-Q4_K_M.gguf"),
        ContextualPronunciationModelStore.ModelSha256, 3L * 1024 * 1024 * 1024);

    private readonly VerifiedAssetDownloader _downloader;
    private readonly SetupAsset _kokoro;
    private readonly SetupAsset _runtime;
    private readonly SetupAsset _pronunciation;
    private readonly Func<byte[]> _manifestSource;
    private readonly string _manifestHash;
    private readonly string _optionalRoot;
    private readonly InstallationLayout _layout;
    private readonly bool _sharedHelper;
    private readonly string? _sharedRuntime;
    internal string KokoroPath { get; }
    // Temporary fixtures can exercise enumeration permutations without choosing timestamps.
    internal IComparer<string>? RecoveryCandidateComparer { get; init; }

    public ModelSetupService(string? localApplicationDataPath = null)
        : this(InstallationLayout.Resolve(localApplicationDataPath: localApplicationDataPath),
            new VerifiedAssetDownloader(), KokoroAsset, RuntimeAsset,
            PronunciationAsset, ReadManifestResource, ContextualPronunciationModelStore.InstallationManifestSha256) { }

    internal ModelSetupService(InstallationLayout layout, bool sharedHelper = false)
        : this(layout, new VerifiedAssetDownloader(), KokoroAsset, RuntimeAsset, PronunciationAsset,
            ReadManifestResource, ContextualPronunciationModelStore.InstallationManifestSha256, sharedHelper) { }

    internal ModelSetupService(string? localApplicationDataPath, VerifiedAssetDownloader downloader,
        SetupAsset kokoro, SetupAsset runtime, SetupAsset pronunciation,
        Func<byte[]> manifestSource, string manifestHash)
        : this(new InstallationLayout(InstallationScope.User, AppContext.BaseDirectory,
            Path.Combine(localApplicationDataPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReader"),
            Path.Combine(localApplicationDataPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReader")),
            downloader, kokoro, runtime, pronunciation, manifestSource, manifestHash) { }

    internal ModelSetupService(InstallationLayout layout, VerifiedAssetDownloader downloader,
        SetupAsset kokoro, SetupAsset runtime, SetupAsset pronunciation,
        Func<byte[]> manifestSource, string manifestHash, bool sharedHelper = false)
    {
        _layout = layout;
        _sharedHelper = sharedHelper;
        KokoroPath = layout.KokoroPath;
        _optionalRoot = layout.ContextualRoot;
        _sharedRuntime = layout.Scope == InstallationScope.Machine
            ? SetupPaths.Child(layout.OptionalRuntimeRoot, manifestHash) : null;
        _downloader = downloader;
        _kokoro = kokoro;
        _runtime = runtime;
        _pronunciation = pronunciation;
        _manifestSource = manifestSource;
        _manifestHash = manifestHash;
    }

    public bool IsKokoroInstalled
    {
        get
        {
            try
            {
                SetupPaths.AssertSafe(KokoroPath);
                if (!File.Exists(KokoroPath)
                    || _kokoro.Length is not null && new FileInfo(KokoroPath).Length != _kokoro.Length) return false;
                using var stream = File.OpenRead(KokoroPath);
                return Convert.ToHexString(SHA256.HashData(stream)).Equals(_kokoro.Sha256,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public async Task EnsureKokoroAsync(IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        if (_layout.Scope == InstallationScope.Machine && !_sharedHelper)
        {
            _layout.AssertTrustedMachineInstallation(false);
            if (await VerifyKokoroInstalledAsync(cancellationToken).ConfigureAwait(false)) return;
            await SharedModelSetup.EnsureAsync(_layout, "kokoro", progress, cancellationToken).ConfigureAwait(false);
            if (!await VerifyKokoroInstalledAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Shared speech setup did not produce a verified model.");
            return;
        }
        await _downloader.EnsureAsync(_kokoro, KokoroPath, progress, cancellationToken).ConfigureAwait(false);
        await WriteNoticesAsync(Path.GetDirectoryName(KokoroPath)!, false, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> VerifyKokoroInstalledAsync(CancellationToken cancellationToken) =>
        VerifiedAssetDownloader.MatchesAsync(KokoroPath, _kokoro.Sha256, cancellationToken, _kokoro.Length);

    public async Task EnsureContextualPronunciationAsync(IProgress<SetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_layout.Scope == InstallationScope.Machine && !_sharedHelper)
        {
            await SharedModelSetup.EnsureAsync(_layout, "contextual", progress, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var lease = await VerifiedAssetDownloader.LockAsync(_optionalRoot + ".install-lock",
            cancellationToken).ConfigureAwait(false);
        var bytes = _manifestSource();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(_manifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The embedded pronunciation manifest failed checksum verification.");
        var manifest = JsonSerializer.Deserialize<ContextualPronunciationInstallationManifest>(bytes)
            ?? throw new InvalidDataException("Missing installation manifest.");
        if (manifest.SchemaVersion != 1 || manifest.RuntimeFiles.Count == 0
            || !manifest.ModelSha256.Equals(_pronunciation.Sha256, StringComparison.OrdinalIgnoreCase)
            || !manifest.RuntimeArchiveSha256.Equals(_runtime.Sha256, StringComparison.OrdinalIgnoreCase)
            || manifest.ModelFileName != Path.GetFileName(_pronunciation.Uri.LocalPath))
            throw new InvalidDataException("The installation manifest does not match the pinned assets.");
        progress?.Report(new("Optional pronunciation installation", 0, null, "Verifying"));
        if (await IsOptionalVerifiedAsync(_optionalRoot, manifest, cancellationToken).ConfigureAwait(false))
        {
            await WriteNoticesAsync(_optionalRoot, true, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Optional pronunciation installation", 0, null, "Installed"));
            return;
        }

        if (await TryRecoverStagedInstallationAsync(manifest, cancellationToken).ConfigureAwait(false))
        {
            await WriteNoticesAsync(_optionalRoot, true, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Optional pronunciation installation", 0, null, "Installed"));
            return;
        }

        var attempt = Guid.NewGuid().ToString("N");
        var staging = _optionalRoot + ".staging-" + attempt;
        var runtimeStaging = _sharedRuntime is null ? Path.Combine(staging, "runtime")
            : _sharedRuntime + ".staging-" + Guid.NewGuid().ToString("N");
        var backup = _optionalRoot + ".backup-" + attempt;
        var runtimeBackup = _sharedRuntime + ".backup-" + Guid.NewGuid().ToString("N");
        SetupPaths.AssertSafe(staging);
        Directory.CreateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, "runtime.zip");
            await _downloader.EnsureAsync(_runtime, archive, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(_runtime.Name, 0, null, "Extracting"));
            await ExtractRuntimeAsync(archive, runtimeStaging, manifest.RuntimeFiles,
                cancellationToken).ConfigureAwait(false);
            await _downloader.EnsureAsync(_pronunciation,
                SetupPaths.Child(staging, "models/" + manifest.ModelFileName), progress, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(staging, ContextualPronunciationModelStore.InstallationManifestFileName),
                bytes, cancellationToken).ConfigureAwait(false);
            await WriteNoticesAsync(staging, true, cancellationToken).ConfigureAwait(false);
            File.Delete(archive);
            File.Delete(archive + ".install-lock");
            // Verify the entire installation, including the manifest and every runtime
            // file, before publishing either protected destination.
            if (!await IsOptionalVerifiedAsync(staging, manifest, cancellationToken, runtimeStaging).ConfigureAwait(false))
                throw new InvalidDataException("The staged pronunciation installation failed verification.");
            cancellationToken.ThrowIfCancellationRequested();
            if (_sharedRuntime is not null)
            {
                SetupPaths.AssertSafe(_sharedRuntime);
                if (Directory.Exists(_sharedRuntime))
                {
                    // Immutable content-addressed runtime may be left by an interrupted
                    // model commit. Reuse only after checking every pinned file.
                    if (await IsRuntimeVerifiedAsync(_sharedRuntime, manifest, cancellationToken).ConfigureAwait(false))
                        SetupPaths.DeleteTree(runtimeStaging);
                    else
                    {
                        SetupPaths.AssertSafe(runtimeBackup);
                        Directory.Move(_sharedRuntime, runtimeBackup);
                        try { Directory.Move(runtimeStaging, _sharedRuntime); }
                        catch
                        {
                            if (!Directory.Exists(_sharedRuntime)) Directory.Move(runtimeBackup, _sharedRuntime);
                            throw;
                        }
                    }
                }
                else Directory.Move(runtimeStaging, _sharedRuntime);
            }
            PublishOptionalInstallation(staging, _optionalRoot, backup);
            // Once committed, cancellation cannot undo a complete verified installation.
            if (Directory.Exists(backup))
            {
                try { SetupPaths.DeleteTree(backup); }
                catch (IOException) { /* Keep a recoverable backup if old files are in use. */ }
                catch (UnauthorizedAccessException) { }
            }
            if (_sharedRuntime is not null && Directory.Exists(runtimeBackup))
            {
                try { SetupPaths.DeleteTree(runtimeBackup); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            progress?.Report(new("Optional pronunciation installation", 0, null, "Installed"));
        }
        finally
        {
            // A failed migration or rollback can leave saved logs in staging. Keep
            // that directory for recovery instead of deleting user data on failure.
            if (Directory.Exists(staging) && !Path.Exists(Path.Combine(staging, "logs")))
                SetupPaths.DeleteTree(staging);
            if (_sharedRuntime is not null && Directory.Exists(runtimeStaging)) SetupPaths.DeleteTree(runtimeStaging);
        }
    }

    internal static void PublishOptionalInstallation(string staging, string destination, string backup,
        Action<string, string>? publish = null)
    {
        SetupPaths.AssertSafe(staging);
        SetupPaths.AssertSafe(destination);
        SetupPaths.AssertSafe(backup);
        var savedLogs = SetupPaths.Child(backup, "logs");
        var stagedLogs = SetupPaths.Child(staging, "logs");
        if (Directory.Exists(destination))
        {
            AssertTreeSafe(destination);
            Directory.Move(destination, backup);
        }
        else if (Directory.Exists(backup)) AssertTreeSafe(backup);
        var logsMoved = false;
        try
        {
            if (Path.Exists(savedLogs))
            {
                if (!Directory.Exists(savedLogs) || Path.Exists(stagedLogs))
                    throw new IOException("Optional setup cannot preserve the existing logs without a path conflict.");
                Directory.Move(savedLogs, stagedLogs);
                logsMoved = true;
            }
            (publish ?? Directory.Move)(staging, destination);
        }
        catch (Exception publicationFailure)
        {
            try
            {
                if (logsMoved && Directory.Exists(staging)) Directory.Move(stagedLogs, savedLogs);
                if (Directory.Exists(backup) && !Path.Exists(destination)) Directory.Move(backup, destination);
            }
            catch (Exception rollbackFailure)
            {
                throw new IOException("Optional setup could not restore the previous installation. "
                    + "Its files and logs were retained in the staging or backup directory.",
                    new AggregateException(publicationFailure, rollbackFailure));
            }
            throw;
        }
    }

    private async Task<bool> TryRecoverStagedInstallationAsync(
        ContextualPronunciationInstallationManifest manifest, CancellationToken cancellationToken)
    {
        SetupPaths.AssertSafe(_optionalRoot);
        if (Path.Exists(_optionalRoot)) return false;
        var parent = Path.GetDirectoryName(_optionalRoot)!;
        var prefix = Path.GetFileName(_optionalRoot) + ".staging-";
        var candidates = Directory.EnumerateDirectories(parent, prefix + "*")
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.StartsWith(prefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(name[prefix.Length..], "N", out _);
            })
            .Take(17).ToArray();
        if (candidates.Length > 16)
            throw new InvalidDataException("Too many interrupted model setup directories to inspect safely.");
        if (RecoveryCandidateComparer is not null) Array.Sort(candidates, RecoveryCandidateComparer);
        var inspected = new List<(string Stage, string Backup, bool Paired, bool Verified)>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backup = _optionalRoot + ".backup-" + Path.GetFileName(candidate)[prefix.Length..];
            var paired = false;
            try
            {
                var attributes = File.GetAttributes(backup);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("An interrupted repair backup is not a regular directory. Retained data was not changed.");
                paired = true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (paired) AssertRecoveryTreeSafe(backup, cancellationToken);
            var verified = false;
            try
            {
                AssertRecoveryTreeSafe(candidate, cancellationToken);
                verified = await IsOptionalVerifiedAsync(candidate, manifest, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            inspected.Add((candidate, backup, paired, verified));
        }
        // A paired repair carries the previous live root. An unpaired failed
        // conflict may also be verified, but its logs cannot establish precedence.
        var pairedRepairs = inspected.Where(value => value.Paired).ToArray();
        if (pairedRepairs.Length > 1)
            throw new InvalidDataException("Multiple interrupted repairs have matching backups. Recovery is ambiguous; retained data was not changed.");
        var eligible = pairedRepairs.Length == 1 ? pairedRepairs : inspected.Where(value => value.Verified).ToArray();
        if (eligible.Length > 1)
            throw new InvalidDataException("Multiple verified unpaired installations remain. Recovery is ambiguous; retained data was not changed.");
        if (eligible.Length == 0)
        {
            // Missing or unverified staging does not make saved backup logs
            // disposable. Refuse fresh publication until their ownership is clear.
            AssertNoOrphanedBackupLogs(parent, cancellationToken);
            return false;
        }
        var selected = eligible[0];
        if (!selected.Verified)
            throw new InvalidDataException("An interrupted repair has a matching backup but its staging installation is not verified. Retained data was not changed.");
        if (!selected.Paired) AssertNoOrphanedBackupLogs(parent, cancellationToken);
        if (Path.Exists(SetupPaths.Child(selected.Backup, "logs")) && Path.Exists(SetupPaths.Child(selected.Stage, "logs")))
            throw new InvalidDataException("Both sides of an interrupted repair contain logs. Recovery is ambiguous; retained data was not changed.");
        cancellationToken.ThrowIfCancellationRequested();
        SetupPaths.AssertSafe(_optionalRoot);
        if (Path.Exists(_optionalRoot)) return false;
        PublishOptionalInstallation(selected.Stage, _optionalRoot, selected.Backup);
        // Other backups and staging directories remain untouched.
        return true;
    }

    private void AssertNoOrphanedBackupLogs(string parent, CancellationToken cancellationToken)
    {
        var prefix = Path.GetFileName(_optionalRoot) + ".backup-";
        var backups = Directory.EnumerateDirectories(parent, prefix + "*")
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)
                && Guid.TryParseExact(Path.GetFileName(path)[prefix.Length..], "N", out _)).Take(17).ToArray();
        if (backups.Length > 16) throw new InvalidDataException("Too many interrupted repair backups to inspect safely.");
        foreach (var backup in backups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.Exists(SetupPaths.Child(backup, "logs")))
                throw new InvalidDataException("A backup contains logs without a verified paired repair. Recovery is ambiguous; retained data was not changed.");
        }
    }

    private static void AssertRecoveryTreeSafe(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var inspected = 0;
        while (pending.TryPop(out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspected > 4096 || entry.Depth > 32)
                throw new InvalidDataException("Interrupted setup contents exceed inspection limits.");
            SetupPaths.AssertSafe(entry.Path);
            if (!Directory.Exists(entry.Path)) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(entry.Path))
            {
                if (pending.Count + inspected >= 4096)
                    throw new InvalidDataException("Interrupted setup contents exceed inspection limits.");
                pending.Push((child, entry.Depth + 1));
            }
        }
    }

    private async Task<bool> IsOptionalVerifiedAsync(string root,
        ContextualPronunciationInstallationManifest manifest, CancellationToken cancellationToken,
        string? runtimeOverride = null)
    {
        SetupPaths.AssertSafe(root);
        if (!Directory.Exists(root)) return false;
        AssertTreeSafe(root, cancellationToken);
        var runtimeRoot = runtimeOverride ?? _sharedRuntime ?? Path.Combine(root, "runtime");
        if (!await IsRuntimeVerifiedAsync(runtimeRoot, manifest, cancellationToken).ConfigureAwait(false)) return false;
        return await VerifiedAssetDownloader.MatchesAsync(Path.Combine(root,
                ContextualPronunciationModelStore.InstallationManifestFileName), _manifestHash, cancellationToken)
                .ConfigureAwait(false)
            && await VerifiedAssetDownloader.MatchesAsync(SetupPaths.Child(root, "models/" + manifest.ModelFileName),
                _pronunciation.Sha256, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsRuntimeVerifiedAsync(string runtimeRoot,
        ContextualPronunciationInstallationManifest manifest, CancellationToken cancellationToken)
    {
        SetupPaths.AssertSafe(runtimeRoot);
        if (!Directory.Exists(runtimeRoot)) return false;
        AssertTreeSafe(runtimeRoot, cancellationToken);
        var allowedPaths = manifest.RuntimeFiles.Select(file => SetupPaths.Child(runtimeRoot, file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories))
        {
            if (allowedPaths.Contains(path)) continue;
            // Compatibility with installations made by the pinned developer setup script.
            if (path == Path.Combine(runtimeRoot, ".source-sha256")
                && File.ReadAllText(path).Equals(_runtime.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            return false;
        }
        foreach (var file in manifest.RuntimeFiles)
            if (!await VerifiedAssetDownloader.MatchesAsync(SetupPaths.Child(runtimeRoot, file.Path),
                    file.Sha256, cancellationToken, file.Length).ConfigureAwait(false)) return false;
        return true;
    }

    internal static async Task ExtractRuntimeAsync(string archivePath, string destination,
        IReadOnlyList<ContextualPronunciationRuntimeFile> manifest, CancellationToken cancellationToken)
    {
        SetupPaths.AssertSafe(archivePath);
        SetupPaths.AssertSafe(destination);
        var expected = manifest.ToDictionary(file => file.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        if (expected.Count != manifest.Count || manifest.Any(file => file.Length < 0 || file.Length > 512L * 1024 * 1024)
            || manifest.Sum(file => file.Length) > 1024L * 1024 * 1024)
            throw new InvalidDataException("The runtime manifest exceeds its extraction limits.");
        foreach (var path in expected.Keys) SetupPaths.Child(destination, path);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 10000)
            throw new InvalidDataException("Runtime archive contains too many entries.");
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            var isDirectory = relative.EndsWith('/');
            SetupPaths.Child(destination, isDirectory ? relative.TrimEnd('/') : relative);
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || unixType == 0xa000)
                throw new InvalidDataException("Runtime archive contains a link.");
            if (isDirectory) continue;
            if (!expected.TryGetValue(relative, out var file)) continue;
            if (!found.Add(relative) || entry.Length != file.Length)
                throw new InvalidDataException("Runtime archive entry does not match the manifest.");
            var path = SetupPaths.Child(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var source = entry.Open())
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    copied += count;
                    if (copied > file.Length) throw new InvalidDataException("Runtime entry exceeded its size limit.");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if (copied != file.Length) throw new InvalidDataException("Runtime archive entry is incomplete.");
            }
            if (!await VerifiedAssetDownloader.MatchesAsync(path, file.Sha256, cancellationToken, file.Length)
                    .ConfigureAwait(false))
                throw new InvalidDataException("Runtime archive entry failed checksum verification.");
        }
        if (found.Count != expected.Count) throw new InvalidDataException("Runtime archive is incomplete.");
    }

    private static void AssertTreeSafe(string root, CancellationToken cancellationToken = default) =>
        AssertRecoveryTreeSafe(root, cancellationToken);

    private static async Task WriteNoticesAsync(string root, bool optional, CancellationToken cancellationToken)
    {
        using var lease = await VerifiedAssetDownloader.LockAsync(Path.Combine(root, ".notices.install-lock"),
            cancellationToken).ConfigureAwait(false);
        var directory = SetupPaths.Child(root, "licenses");
        Directory.CreateDirectory(directory);
        var names = optional
            ? new[] { "Apache-2.0-LICENSE.txt", "llama.cpp-LICENSE.txt", "LLVM-OpenMP-LICENSE.txt" }
            : ["Apache-2.0-LICENSE.txt"];
        foreach (var name in names)
        {
            using var resource = typeof(ModelSetupService).Assembly.GetManifestResourceStream("PulseReader.Setup." + name)
                ?? throw new InvalidOperationException("A model licence resource is missing.");
            var path = SetupPaths.Child(directory, name);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            await resource.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        var attribution = optional
            ? "Qwen3-4B-Instruct-2507 by Qwen, Apache-2.0.\n" +
              "Q4_K_M conversion by LM Studio community; revision 4edb920b6f14e3b9284d4502a6485103d72cde05.\n" +
              "https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507\n" +
              "https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF\n" +
              "llama.cpp b10662 by ggml contributors, MIT; LLVM OpenMP licence retained.\n" +
              "https://github.com/ggml-org/llama.cpp/tree/b10662\n"
            : "Kokoro v1.0 by hexgrad, Apache-2.0.\n" +
              "https://huggingface.co/hexgrad/Kokoro-82M\n" +
              "ONNX conversion distributed in KokoroSharpBinaries v2.0.0.\n" +
              "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/tag/v2.0.0\n";
        await File.WriteAllTextAsync(SetupPaths.Child(directory, "MODEL-SOURCES.txt"), attribution,
            cancellationToken).ConfigureAwait(false);
    }

    private static byte[] ReadManifestResource()
    {
        using var stream = typeof(ModelSetupService).Assembly.GetManifestResourceStream(
            "PulseReader.Setup.contextual-pronunciation-manifest.json")
            ?? throw new InvalidOperationException("The pinned pronunciation manifest resource is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        // Source control may terminate the JSON line; installation retains the original pinned bytes.
        var length = bytes.Length;
        if (length > 0 && bytes[length - 1] == '\n')
        {
            length--;
            if (length > 0 && bytes[length - 1] == '\r') length--;
        }
        return bytes[..length];
    }
}
