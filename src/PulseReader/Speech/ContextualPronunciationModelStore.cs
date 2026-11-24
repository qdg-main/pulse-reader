using System.Security.Cryptography;
using System.Text.Json;
using PulseReader.Setup;

namespace PulseReader.Speech;

internal sealed class ContextualPronunciationModelStore
{
    internal const string ModelFileName = "Qwen3-4B-Instruct-2507-Q4_K_M.gguf";
    internal const string ModelSha256 =
        "8CDB57CBB880D313736A9BC4E3D3D2485F145B5E19CF33783746E753E82641FC";
    internal const string RuntimeArchiveSha256 =
        "BB56503314A53EEB3044504E44275D5A3B55FD4AC8BB1E33679B40423404B33A";
    internal const string InstallationManifestFileName = "installation-manifest.json";

    internal const string InstallationManifestSha256 =
        "0F6CE5FC5599E5E5C2F1FD1A101D034284CEF896C03A7C86D5D538584A84882F";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyList<string> _roots;
    private readonly InstallationLayout? _layout;

    public ContextualPronunciationModelStore(
        string? localApplicationDataPath = null,
        string? applicationBaseDirectory = null,
        IReadOnlyList<string>? roots = null)
    {
        if (roots is not null) _roots = roots;
        else
        {
            _layout = InstallationLayout.Resolve(applicationBaseDirectory, localApplicationDataPath);
            _roots = _layout.Scope == InstallationScope.Machine ? [_layout.ContextualRoot]
                : CreateDefaultRoots(localApplicationDataPath, applicationBaseDirectory ?? AppContext.BaseDirectory);
        }
        if (_roots.Count == 0) throw new ArgumentException("At least one model root is required.", nameof(roots));
    }

    internal ContextualPronunciationModelStore(InstallationLayout layout)
    { _layout = layout; _roots = [layout.ContextualRoot]; }

    public ContextualPronunciationAvailability GetAvailability()
    {
        string? unavailable = null;
        foreach (var root in _roots)
        {
            if (TryReadInstallation(root, out _, out var error, out var state))
            {
                return new ContextualPronunciationAvailability(true, root, null);
            }
            if (state == ContextualPronunciationInstallationState.Unavailable) unavailable = error;
        }

        return new ContextualPronunciationAvailability(
            false,
            _roots[0],
            unavailable ?? "Install the optional local pronunciation model before enabling this setting.")
        { State = unavailable is null ? ContextualPronunciationInstallationState.MissingOrInvalid
            : ContextualPronunciationInstallationState.Unavailable };
    }

    public async Task<ContextualPronunciationInstallation> ResolveVerifiedAsync(
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var root in _roots)
        {
            if (!TryReadInstallation(root, out var installation, out var error, out _))
            {
                if (error is not null)
                {
                    failures.Add(error);
                }

                continue;
            }

            try
            {
                await VerifyFilesAsync(installation!, cancellationToken);
                return installation!;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException)
            {
                failures.Add(exception.Message);
            }
        }

        throw new InvalidOperationException(
            failures.Count == 0
                ? "The optional contextual-pronunciation model is not installed."
                : string.Join(Environment.NewLine, failures));
    }

    private static async Task VerifyFilesAsync(
        ContextualPronunciationInstallation installation,
        CancellationToken cancellationToken)
    {
        await VerifyFileAsync(
            installation.ModelPath,
            ModelSha256,
            cancellationToken);
        foreach (var file in installation.Manifest.RuntimeFiles)
        {
            var path = ResolveChildPath(installation.RuntimeDirectory, file.Path);
            await VerifyFileAsync(path, file.Sha256, cancellationToken, file.Length);
        }
    }

    private static async Task VerifyFileAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken,
        long? expectedLength = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists
            || (info.Attributes & FileAttributes.ReparsePoint) != 0
            || expectedLength is not null && info.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"The contextual-pronunciation installation contains an invalid file: {path}");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The contextual-pronunciation installation failed checksum verification: {path}");
        }
    }

    private bool TryReadInstallation(
        string root,
        out ContextualPronunciationInstallation? installation,
        out string? error,
        out ContextualPronunciationInstallationState state)
    {
        installation = null;
        error = null;
        state = ContextualPronunciationInstallationState.MissingOrInvalid;
        try
        {
            if (_layout?.Scope == InstallationScope.Machine) _layout.AssertTrustedMachineInstallation(false);
            var fullRoot = Path.GetFullPath(root);
            var manifestPath = Path.Combine(fullRoot, InstallationManifestFileName);
            SetupPaths.AssertSafe(manifestPath);
            _ = File.GetAttributes(manifestPath); // File.Exists masks access and transient I/O errors.
            if (new FileInfo(manifestPath).Length > 256 * 1024)
                throw new InvalidDataException("The contextual-pronunciation manifest is too large.");
            var manifestBytes = File.ReadAllBytes(manifestPath);
            var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            if (!string.Equals(
                    manifestHash,
                    InstallationManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = $"The contextual-pronunciation manifest is not the pinned version: {manifestPath}";
                return false;
            }

            var manifest = JsonSerializer.Deserialize<ContextualPronunciationInstallationManifest>(
                manifestBytes,
                JsonOptions);
            if (manifest is null
                || manifest.SchemaVersion != 1
                || !string.Equals(
                    manifest.RuntimeArchiveSha256,
                    RuntimeArchiveSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    manifest.ModelFileName,
                    ModelFileName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    manifest.ModelSha256,
                    ModelSha256,
                    StringComparison.OrdinalIgnoreCase)
                || manifest.RuntimeFiles.Count == 0)
            {
                error = $"The contextual-pronunciation manifest is invalid: {manifestPath}";
                return false;
            }

            var runtimeDirectory = _layout?.Scope == InstallationScope.Machine
                ? _layout.ContextualRuntimeRoot : Path.Combine(fullRoot, "runtime");
            var modelPath = Path.Combine(fullRoot, "models", ModelFileName);
            var serverPath = ResolveChildPath(runtimeDirectory, "llama-server.exe");
            SetupPaths.AssertSafe(modelPath);
            SetupPaths.AssertSafe(runtimeDirectory);
            _ = File.GetAttributes(modelPath);
            _ = File.GetAttributes(serverPath);
            if (new FileInfo(modelPath).Length == 0)
            {
                error = $"The contextual-pronunciation installation is incomplete: {fullRoot}";
                return false;
            }
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.RuntimeFiles)
            {
                var path = ResolveChildPath(runtimeDirectory, file.Path);
                SetupPaths.AssertSafe(path);
                _ = File.GetAttributes(path);
                if (!allowed.Add(path) || new FileInfo(path).Length != file.Length)
                {
                    error = "The contextual-pronunciation runtime is incomplete or invalid.";
                    return false;
                }
            }
            foreach (var path in EnumerateSafeRuntimeFiles(runtimeDirectory))
            {
                if (allowed.Contains(path)) continue;
                if (path.Equals(Path.Combine(runtimeDirectory, ".source-sha256"), StringComparison.OrdinalIgnoreCase)
                    && File.ReadAllText(path).Equals(RuntimeArchiveSha256, StringComparison.OrdinalIgnoreCase)) continue;
                error = "The contextual-pronunciation runtime contains an unverified file.";
                return false;
            }

            installation = new ContextualPronunciationInstallation(
                fullRoot,
                runtimeDirectory,
                serverPath,
                modelPath,
                _layout?.Scope == InstallationScope.Machine ? _layout.ContextualLogRoot : Path.Combine(fullRoot, "logs"),
                manifest);
            state = ContextualPronunciationInstallationState.Installed;
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException
            or JsonException or InvalidDataException or InvalidOperationException)
        {
            error = "The contextual-pronunciation installation is missing or invalid. " + exception.Message;
            return false;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException)
        {
            state = ContextualPronunciationInstallationState.Unavailable;
            error = $"Pulse Reader could not inspect the contextual-pronunciation installation at {root}. "
                + exception.Message;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateSafeRuntimeFiles(string root)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var count = 0;
        while (pending.TryPop(out var entry))
        {
            if (++count > 4096 || entry.Depth > 32) throw new InvalidDataException("The runtime exceeds inspection limits.");
            SetupPaths.AssertSafe(entry.Path);
            if (Directory.Exists(entry.Path))
                foreach (var child in Directory.EnumerateFileSystemEntries(entry.Path))
                {
                    if (pending.Count + count >= 4096) throw new InvalidDataException("The runtime exceeds inspection limits.");
                    pending.Push((child, entry.Depth + 1));
                }
            else yield return entry.Path;
        }
    }

    private static IReadOnlyList<string> CreateDefaultRoots(
        string? localApplicationDataPath,
        string applicationBaseDirectory)
    {
        var localAppData = localApplicationDataPath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string>
        {
            Path.Combine(localAppData, "PulseReader", "ContextualPronunciation")
        };

        var directory = new DirectoryInfo(Path.GetFullPath(applicationBaseDirectory));
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "PulseReader.slnx")))
            {
                continue;
            }

            roots.Add(Path.Combine(directory.FullName, "artifacts", "local-text-cleanup"));
            break;
        }

        return roots;
    }

    private static string ResolveChildPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("A contextual-pronunciation manifest path is rooted.");
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A contextual-pronunciation manifest path leaves its runtime directory.");
        }

        return fullPath;
    }
}

internal sealed record ContextualPronunciationAvailability(
    bool IsInstalled,
    string InstallRoot,
    string? Message)
{
    public ContextualPronunciationInstallationState State { get; init; } = IsInstalled
        ? ContextualPronunciationInstallationState.Installed
        : ContextualPronunciationInstallationState.MissingOrInvalid;
}

internal enum ContextualPronunciationInstallationState { Installed, MissingOrInvalid, Unavailable }

internal sealed record ContextualPronunciationInstallation(
    string Root,
    string RuntimeDirectory,
    string ServerPath,
    string ModelPath,
    string LogDirectory,
    ContextualPronunciationInstallationManifest Manifest);

internal sealed record ContextualPronunciationInstallationManifest(
    int SchemaVersion,
    string RuntimeArchiveSha256,
    string ModelFileName,
    string ModelSha256,
    IReadOnlyList<ContextualPronunciationRuntimeFile> RuntimeFiles);

internal sealed record ContextualPronunciationRuntimeFile(
    string Path,
    long Length,
    string Sha256);
