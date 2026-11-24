using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PulseReader.Speech;

namespace PulseReader.Setup;

internal enum InstallationScope { User, Machine }

// Scope is installer metadata, never a preference or a command-line destination.
internal sealed record InstallationLayout(InstallationScope Scope, string ApplicationRoot,
    string DataRoot, string UserDataRoot)
{
    internal const string ScopeFileName = "installation-scope.json";
    internal static InstallationLayout Current => Resolve();
    internal string KokoroPath => Path.Combine(DataRoot, "Models", "kokoro.onnx");
    internal string ContextualRoot => Path.Combine(DataRoot, "ContextualPronunciation");
    internal string OptionalRuntimeRoot => Path.Combine(ApplicationRoot, "OptionalRuntime");
    internal string ContextualRuntimeRoot => Scope == InstallationScope.Machine
        ? Path.Combine(OptionalRuntimeRoot, ContextualPronunciationModelStore.InstallationManifestSha256)
        : Path.Combine(ContextualRoot, "runtime");
    internal string ContextualLogRoot => Path.Combine(UserDataRoot, "ContextualPronunciation", "logs");
    internal string ExecutablePath => Path.Combine(ApplicationRoot, "PulseReader.exe");

    internal static InstallationLayout Resolve(string? applicationBaseDirectory = null,
        string? localApplicationDataPath = null, string? programFilesPath = null,
        string? commonApplicationDataPath = null)
    {
        var app = Path.GetFullPath(applicationBaseDirectory ?? AppContext.BaseDirectory).TrimEnd('\\', '/');
        var user = Path.Combine(localApplicationDataPath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReader");
        var marker = Path.Combine(app, ScopeFileName);
        SetupPaths.AssertSafe(marker);
        byte[] bytes;
        try
        {
            using var input = File.OpenRead(marker);
            if (input.Length > 256) throw new InvalidDataException("The installation scope marker is invalid.");
            bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
        }
        catch (FileNotFoundException) { return new(InstallationScope.User, app, user, user); }
        catch (DirectoryNotFoundException) { return new(InstallationScope.User, app, user, user); }
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("SchemaVersion", out var schema) || !schema.TryGetInt32(out var version)
            || version != 1 || !root.TryGetProperty("Scope", out var scope)
            || scope.ValueKind != JsonValueKind.String || scope.GetString() is not ("user" or "machine"))
            throw new InvalidDataException("The installation scope marker is invalid.");
        if (scope.GetString() == "user") return new(InstallationScope.User, app, user, user);
        var canonical = Path.Combine(programFilesPath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PulseReader");
        if (!app.Equals(Path.GetFullPath(canonical).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An all-users installation must use the protected Program Files location.");
        return new(InstallationScope.Machine, app,
            Path.Combine(commonApplicationDataPath
                ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PulseReader"), user);
    }

    internal void AssertTrustedMachineInstallation(bool requireAdministrator)
    {
        if (Scope != InstallationScope.Machine || this != Current
            || !string.Equals(Environment.ProcessPath, ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Shared setup must run from the installed Pulse Reader executable.");
        if (requireAdministrator && !new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Shared model setup requires an administrator token.");
        AssertProtected(ApplicationRoot);
        AssertProtected(ExecutablePath);
        AssertProtected(Path.Combine(ApplicationRoot, ScopeFileName));
        AssertProtected(DataRoot);
        AssertProtected(OptionalRuntimeRoot);
        AssertProtectedTree(ApplicationRoot);
        AssertProtectedTree(DataRoot);
    }

    private static void AssertProtectedTree(string root)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var inspected = 0;
        while (pending.TryPop(out var entry))
        {
            if (++inspected > 16384 || entry.Depth > 32)
                throw new InvalidDataException("Shared installation exceeds its inspection limits.");
            AssertProtected(entry.Path);
            if (Directory.Exists(entry.Path))
                foreach (var child in Directory.EnumerateFileSystemEntries(entry.Path))
                {
                    if (pending.Count + inspected >= 16384)
                        throw new InvalidDataException("Shared installation exceeds its inspection limits.");
                    pending.Push((child, entry.Depth + 1));
                }
        }
    }

    internal static void AssertProtected(string path)
    {
        SetupPaths.AssertSafe(path);
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        static bool Trusted(string sid) => sid is "S-1-5-18" or "S-1-5-32-544"
            or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted(owner.Value))
            throw new UnauthorizedAccessException("Shared setup requires an administrator-owned installation directory.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && GrantsMutationRights(rule.FileSystemRights)
                && !Trusted(((SecurityIdentifier)rule.IdentityReference).Value))
                throw new UnauthorizedAccessException("Shared setup refuses a location writable by ordinary users.");
    }

    internal static bool GrantsMutationRights(FileSystemRights rights)
    {
        const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership | (FileSystemRights)0x50000000;
        return (rights & mutation) != 0;
    }

    internal void NormalizeSharedOwners()
    {
        if (Scope != InstallationScope.Machine) throw new InvalidOperationException("A shared layout is required.");
        var administrator = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (var root in new[] { DataRoot, OptionalRuntimeRoot })
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            var count = 0;
            while (pending.TryPop(out var entry))
            {
                if (++count > 16384 || entry.Depth > 32) throw new InvalidDataException("Shared ownership exceeds its inspection limits.");
                SetupPaths.AssertSafe(entry.Path);
                if (Directory.Exists(entry.Path))
                {
                    var directory = new DirectoryInfo(entry.Path);
                    var security = directory.GetAccessControl();
                    security.SetOwner(administrator);
                    directory.SetAccessControl(security);
                    foreach (var child in Directory.EnumerateFileSystemEntries(entry.Path))
                    {
                        if (pending.Count + count >= 16384)
                            throw new InvalidDataException("Shared ownership exceeds its inspection limits.");
                        pending.Push((child, entry.Depth + 1));
                    }
                }
                else
                {
                    var file = new FileInfo(entry.Path);
                    var security = file.GetAccessControl();
                    security.SetOwner(administrator);
                    file.SetAccessControl(security);
                }
            }
        }
    }
}
