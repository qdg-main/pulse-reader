using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PulseReader.Setup;

namespace PulseReader.Updates;

internal sealed class UpdateHandoff
{
    internal static string CreateOperationDirectory()
    {
        AssertNormalUser();
        var path = Path.Combine(OperationsRoot, Guid.NewGuid().ToString("N"));
        using var pins = new UpdatePathSafety.Pins();
        pins.Directory(path, create: true);
        return path;
    }

    internal static string OperationsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReader", "Updates");

    internal async Task<bool> StartAsync(PreparedUpdate update, string acceptedTermsSha256,
        CancellationToken cancellationToken = default)
    {
        AssertNormalUser();
        UpdateHandoffProtocol.Validate(update, acceptedTermsSha256);
        using var parent = Process.GetCurrentProcess();
        if (!string.Equals(Environment.ProcessPath, Path.Combine(update.Installation.ApplicationRoot, "PulseReader.exe"),
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update must start from the identified application executable.");
        using var lease = await UpdateFileLease.OpenAsync(update.AssetPath, update.Offer.Asset.Length,
            update.Offer.Asset.Sha256, cancellationToken).ConfigureAwait(false);
        using var shadow = UpdateShadowPayload.Copy(update.Installation.ApplicationRoot, update.OperationDirectory,
            cancellationToken);
        var pipeName = "PulseReader.Update." + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo(Path.Combine(shadow.Root, "PulseReader.exe"))
        { UseShellExecute = false, WorkingDirectory = shadow.Root, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var child = Process.Start(start) ?? throw new IOException("The update helper could not be started.");
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            UpdateHandoffProtocol.RequirePeer(pipe, child.Id, server: false);
            await UpdateHandoffProtocol.WriteAsync(pipe, new UpdateHandoffPacket(update, acceptedTermsSha256), timeout.Token)
                .ConfigureAwait(false);
            var reply = await UpdateHandoffProtocol.ReadAsync<UpdateHandoffReply>(pipe, timeout.Token).ConfigureAwait(false);
            if (reply.Status != "READY") throw new IOException(reply.Error ?? "The helper rejected this update.");
            await UpdateHandoffProtocol.WriteAsync(pipe, new UpdateHandoffReply("ACCEPT", null), timeout.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The update helper did not become ready within 45 seconds. The application is still running."); }
    }

    internal static void AssertNormalUser()
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Start Pulse Reader as your ordinary user to apply an update.");
    }
}

internal sealed record UpdateHandoffPacket(PreparedUpdate Update, string AcceptedTermsSha256);
internal sealed record UpdateHandoffReply(string Status, string? Error);

internal static class UpdateHandoffProtocol
{
    private const int MaximumPacketBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 16 };
    internal static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    internal static void Validate(PreparedUpdate update, string acceptedTermsSha256, string? operationsRoot = null)
    {
        var installation = update.Installation;
        var offer = update.Offer;
        if (!Enum.IsDefined(installation.Kind) || !Enum.IsDefined(installation.Scope)
            || !UpdateVersionPolicy.IsVariant(installation.Variant)
            || offer.Version != UpdateVersionPolicy.Normalize(offer.Version)
            || offer.Version <= UpdateVersionPolicy.Normalize(installation.Version)
            || offer.ReleaseId <= 0 || offer.Asset.Id <= 0)
            throw new InvalidDataException("The prepared update identity is invalid.");
        var name = UpdateVersionPolicy.AssetName(offer.Version, installation.Variant, installation.Kind);
        if (offer.Asset.Name != name
            || offer.ReleasePage.AbsoluteUri != $"https://github.com/{UpdateConstants.RepositoryPath}/releases/tag/v{offer.Version}"
            || offer.Asset.DownloadUri.AbsoluteUri != $"https://github.com/{UpdateConstants.RepositoryPath}/releases/download/v{offer.Version}/{name}"
            || offer.Asset.Length <= 0 || offer.Asset.Length > (installation.Kind == UpdatePackageKind.Installer
                ? UpdateConstants.MaximumInstallerBytes : UpdateConstants.MaximumPortableBytes)
            || !IsHash(offer.Asset.Sha256) || !IsHash(offer.TermsSha256) || !IsHash(acceptedTermsSha256)
            || !offer.TermsSha256.Equals(acceptedTermsSha256, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(offer.TermsText) || offer.TermsText.Contains('\0')
            || Encoding.UTF8.GetByteCount(offer.TermsText) > 256 * 1024
            || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(offer.TermsText)))
                .Equals(offer.TermsSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared update does not match the accepted release and terms.");
        var operation = UpdatePathSafety.Root(update.OperationDirectory);
        var root = UpdatePathSafety.Root(operationsRoot ?? UpdateHandoff.OperationsRoot);
        if (!Path.GetDirectoryName(operation)!.Equals(root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(operation), "N", out _)
            || !update.AssetPath.Equals(Path.Combine(operation, name), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update package is outside its private operation directory.");
        _ = UpdatePathSafety.Root(installation.ApplicationRoot);
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length is <= 0 or > MaximumPacketBytes) throw new InvalidDataException("Update IPC packet exceeds its limit.");
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumPacketBytes) throw new InvalidDataException("Update IPC packet exceeds its limit.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Invalid update IPC packet.");
    }

    internal static void RequirePeer(PipeStream stream, int expectedPid, bool server)
    {
        uint actual;
        var okay = server ? GetNamedPipeServerProcessId(stream.SafePipeHandle, out actual)
            : GetNamedPipeClientProcessId(stream.SafePipeHandle, out actual);
        if (!okay || actual != (uint)expectedPid) throw new UnauthorizedAccessException("The update IPC peer identity is invalid.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle handle, out uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle handle, out uint pid);
}

internal sealed class UpdateShadowPayload : IDisposable
{
    private readonly UpdatePathSafety.Pins _pins = new();
    private readonly List<UpdatePathSafety.LockedFile> _files = [];
    private readonly List<UpdatePathSafety.LockedFile> _sources = [];
    private UpdatePathSafety.LockedFile? _ownership;
    internal string Root { get; }
    private UpdateShadowPayload(string root) { Root = root; }

    internal static UpdateShadowPayload Copy(string applicationRoot, string operation, CancellationToken token)
    {
        var shadow = new UpdateShadowPayload(Path.Combine(operation, "helper"));
        try
        {
            shadow._pins.Directory(applicationRoot);
            shadow._pins.Directory(shadow.Root, true);
            shadow._ownership = new UpdatePathSafety.LockedFile(Path.Combine(applicationRoot, "payload-files.txt"), movable: false);
            var paths = ReadList(applicationRoot, shadow._pins);
            long total = 0;
            foreach (var relative in paths)
            {
                token.ThrowIfCancellationRequested();
                var source = UpdatePathSafety.Under(applicationRoot, relative);
                shadow._pins.Parents(source);
                var input = new UpdatePathSafety.LockedFile(source, movable: false);
                shadow._sources.Add(input);
                if (input.Stream.Length > 512L * 1024 * 1024 || (total += input.Stream.Length) > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("The helper payload exceeds its size limit.");
            }
            for (var index = 0; index < paths.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var relative = paths[index];
                var input = shadow._sources[index];
                var destination = UpdatePathSafety.Under(shadow.Root, relative);
                shadow._pins.Parents(destination, true);
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536];
                    int read;
                    while ((read = input.Stream.Read(buffer)) != 0)
                    { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                }
                shadow._files.Add(new UpdatePathSafety.LockedFile(destination, movable: false));
            }
            return shadow;
        }
        catch { shadow.Dispose(); throw; }
    }

    internal static UpdateShadowPayload Pin(string helperRoot)
    {
        var shadow = new UpdateShadowPayload(helperRoot);
        try
        {
            shadow._pins.Directory(helperRoot);
            shadow._ownership = new UpdatePathSafety.LockedFile(Path.Combine(helperRoot, "payload-files.txt"), movable: false);
            long total = 0;
            foreach (var relative in ReadList(helperRoot, shadow._pins))
            {
                var path = UpdatePathSafety.Under(helperRoot, relative);
                shadow._pins.Parents(path);
                var file = new UpdatePathSafety.LockedFile(path, movable: false);
                shadow._files.Add(file);
                if (file.Stream.Length > 512L * 1024 * 1024 || (total += file.Stream.Length) > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("The helper payload exceeds its size limit.");
            }
            return shadow;
        }
        catch { shadow.Dispose(); throw; }
    }

    internal static string[] ReadList(string root, UpdatePathSafety.Pins pins)
    {
        var list = Path.Combine(root, "payload-files.txt");
        pins.Parents(list);
        using var input = new UpdatePathSafety.LockedFile(list, movable: false);
        if (input.Stream.Length > UpdateConstants.MaximumMetadataBytes) throw new InvalidDataException("Helper ownership list is too large.");
        using var reader = new StreamReader(input.Stream, new UTF8Encoding(false, true), true);
        var paths = reader.ReadToEnd().Split('\n').Select(p => p.TrimEnd('\r')).Where(p => p.Length > 0)
            .Select(UpdatePathSafety.Relative).ToArray();
        if (paths.Length is 0 or > 4096 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length
            || !paths.Contains("PulseReader.exe", StringComparer.Ordinal)
            || !paths.Contains("PulseReader.dll", StringComparer.Ordinal)
            || !paths.Contains("payload-files.txt", StringComparer.Ordinal)
            || !paths.Contains("dependency-inventory.json", StringComparer.Ordinal)
            || paths.Any(IsForbidden))
            throw new InvalidDataException("The helper ownership list includes unsupported or user-owned paths.");
        return paths;
    }

    private static bool IsForbidden(string path)
    {
        var first = path.Split('/')[0];
        return new[] { "Models", "ContextualPronunciation", "OptionalRuntime", "Cache", "Logs", "Data", "Updates" }
                .Contains(first, StringComparer.OrdinalIgnoreCase)
            || first.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || first.Equals(InstallationLayout.ScopeFileName, StringComparison.OrdinalIgnoreCase)
            || first.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
            || first.Equals("config.json", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    { foreach (var file in _files) file.Dispose(); foreach (var source in _sources) source.Dispose(); _ownership?.Dispose(); _pins.Dispose(); }
}
