using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PulseReader.Setup;

internal enum UninstallDataChoice { Preserve, Dependencies, Everything }
internal enum UninstallRegistryCleanupResult { Cleaned, SkippedOffline }
internal sealed record UninstallProfile(string Sid, string ProfileDirectory);

// Profile and hive access are injectable: checks never inspect or change the host's profiles.
internal interface IUninstallProfileAccess
{
    IReadOnlyList<UninstallProfile> GetProfiles();
    UninstallRegistryCleanupResult RemoveOwnedRegistryValues(UninstallProfile profile, string applicationDirectory, bool removeReceipt);
}

internal sealed record UninstallCleanupEnvironment(string ApplicationDirectory, string LocalApplicationData,
    string ProgramFiles, string ProgramData, IUninstallProfileAccess Profiles, Action<string> AssertNoRunningClients,
    Action<string>? DirectoryPinned = null);

internal static class UninstallDataCleanup
{
    internal const string MarkerFileName = "installation-scope.json";
    // CLI: --uninstall-data preserve|dependencies|everything [--quiet]
    // The explicit choice is the confirmation, including all-users file removal.
    // No marker/root override is accepted. Silent uninstall defaults to preserve in Inno Setup.
    // Return 20 for an invalid request, 21 for incomplete/failed cleanup; never treat either as success.
    internal static int Run(string[] args)
    {
        var quiet = args.Contains("--quiet", StringComparer.Ordinal);
        try
        {
            var choice = ParseArguments(args);
            Execute(choice, new(AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                new WindowsUninstallProfileAccess(), AssertNoRunningClients));
            return 0;
        }
        catch (Exception exception)
        {
            var message = "Pulse Reader data cleanup did not complete. The application has not been removed. "
                + "Close Pulse Reader in every affected Windows session and retry. Some requested data may "
                + "already have been removed.\n\n" + exception.Message;
            Console.Error.WriteLine(message);
            if (!quiet) MessageBox.Show(message, "Pulse Reader uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return exception is ArgumentException ? 20 : 21;
        }
    }

    internal static UninstallDataChoice ParseArguments(string[] args)
    {
        if (args.Length < 2 || args[0] != "--uninstall-data")
            throw new ArgumentException("Expected --uninstall-data preserve|dependencies|everything.");
        var choice = args[1] switch
        {
            "preserve" => UninstallDataChoice.Preserve,
            "dependencies" => UninstallDataChoice.Dependencies,
            "everything" => UninstallDataChoice.Everything,
            _ => throw new ArgumentException("Unknown uninstall data choice.")
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arg in args.Skip(2))
        {
            if (!seen.Add(arg) || arg != "--quiet")
                throw new ArgumentException("Unknown or duplicate uninstall data argument.");
        }
        return choice;
    }

    internal static void Execute(UninstallDataChoice choice, UninstallCleanupEnvironment environment)
    {
        var app = Path.GetFullPath(environment.ApplicationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        SetupPaths.AssertSafe(app);
        var markerPath = SetupPaths.Child(app, MarkerFileName);
        using var stream = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 1024) throw new InvalidDataException("Invalid installation scope record.");
        using var marker = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
        var fields = marker.RootElement.ValueKind == JsonValueKind.Object
            ? marker.RootElement.EnumerateObject().ToArray() : [];
        if (fields.Length != 2 || fields.Count(p => p.Name == "SchemaVersion") != 1
            || fields.Count(p => p.Name == "Scope") != 1
            || !marker.RootElement.GetProperty("SchemaVersion").TryGetInt32(out var schema) || schema != 1
            || marker.RootElement.GetProperty("Scope").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Unsupported installation scope record.");
        var scope = marker.RootElement.GetProperty("Scope").GetString();
        var machine = scope == "machine";
        if (scope is not ("user" or "machine")) throw new InvalidDataException("Unknown installation scope.");
        var canonical = machine ? Path.Combine(environment.ProgramFiles, "PulseReader")
            : Path.Combine(environment.LocalApplicationData, "Programs", "PulseReader");
        if (!SamePath(app, canonical))
            throw new InvalidDataException("The installation scope does not match its canonical directory or current user.");
        environment.AssertNoRunningClients(app);

        var profiles = machine ? environment.Profiles.GetProfiles() : Array.Empty<UninstallProfile>();
        var userRoots = machine ? profiles.Select(profile => ProfileDataRoot(profile.ProfileDirectory)).ToArray()
            : [Path.Combine(environment.LocalApplicationData, "PulseReader")];
        var dataRoot = machine ? Path.Combine(environment.ProgramData, "PulseReader") : userRoots[0];
        var targets = new List<string>();
        var emptyContainers = new List<string>
        {
            Path.Combine(dataRoot, "ContextualPronunciation", "logs"),
            Path.Combine(dataRoot, "ContextualPronunciation"), Path.Combine(dataRoot, "Models"), dataRoot
        };
        var discoveredEntries = 0;
        if (choice == UninstallDataChoice.Everything)
        {
            targets.AddRange(userRoots);
            if (machine) targets.Add(dataRoot);
        }
        else if (choice == UninstallDataChoice.Dependencies)
        {
            targets.Add(Path.Combine(dataRoot, "Models"));
            targets.Add(Path.Combine(dataRoot, "ContextualPronunciation.install-lock"));
            AddOptionalDependencyTargets(Path.Combine(dataRoot, "ContextualPronunciation"), targets, emptyContainers, ref discoveredEntries);
            AddInterruptedSetupTargets(dataRoot, targets, emptyContainers, ref discoveredEntries);
        }
        if (machine && choice != UninstallDataChoice.Preserve)
            targets.Add(Path.Combine(app, "OptionalRuntime"));
        if (machine) emptyContainers.Add(Path.Combine(app, "OptionalRuntime"));
        // Pin existing ancestry and every enumerated directory. A user cannot replace an ancestor
        // with a junction between validation and deletion. Missing entries are never rediscovered.
        using var pins = new DirectoryPins(environment.DirectoryPinned);
        var plans = targets.Distinct(StringComparer.OrdinalIgnoreCase).Select(pins.Plan).Where(p => p is not null).ToArray();
        foreach (var plan in plans) pins.Delete(plan!);
        // Only known storage containers, deepest first. No traversal or file removal for pruning.
        foreach (var container in emptyContainers.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length))
            pins.PruneEmptyDirectory(container);

        // Only the named app receipt and startup value are owned. Other values in either key survive.
        // For a user installation, HKCU is safe only after the canonical-current-user check above.
        if (machine)
        {
            var errors = new List<string>();
            var cleaned = 0;
            var skippedOffline = 0;
            foreach (var profile in profiles)
            {
                try
                {
                    var result = environment.Profiles.RemoveOwnedRegistryValues(profile, app, choice == UninstallDataChoice.Everything);
                    if (result == UninstallRegistryCleanupResult.SkippedOffline) skippedOffline++;
                    else if (result == UninstallRegistryCleanupResult.Cleaned) cleaned++;
                    else throw new InvalidOperationException("An unknown registry cleanup result was returned.");
                }
                catch (Exception exception) { errors.Add($"{profile.Sid}: {exception.Message}"); }
            }
            Console.WriteLine($"Pulse Reader registry cleanup: {cleaned} loaded profile(s) processed; "
                + $"{skippedOffline} offline profile(s) left unchanged (startup entries and distribution terms acceptance records).");
            if (errors.Count != 0) throw new IOException("Some user registries could not be cleaned:\n" + string.Join("\n", errors));
        }
        else if (environment.Profiles.RemoveOwnedRegistryValues(new("current", ""), app, choice == UninstallDataChoice.Everything)
            != UninstallRegistryCleanupResult.Cleaned) throw new IOException("The current user's registry cleanup did not complete.");
    }

    private static string ProfileDataRoot(string profileDirectory)
    {
        if (!Path.IsPathFullyQualified(profileDirectory)) throw new InvalidDataException("A profile path is not absolute.");
        SetupPaths.AssertSafe(profileDirectory);
        return SetupPaths.Child(profileDirectory, "AppData/Local/PulseReader");
    }

    private static void AddInterruptedSetupTargets(string root, List<string> targets, List<string> emptyContainers, ref int discoveredEntries)
    {
        SetupPaths.AssertSafe(root);
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            CountDiscoveryEntry(ref discoveredEntries);
            var name = Path.GetFileName(path);
            foreach (var prefix in new[] { "ContextualPronunciation.staging-", "ContextualPronunciation.backup-" })
                if (name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _))
                    AddOptionalDependencyTargets(path, targets, emptyContainers, ref discoveredEntries);
        }
    }

    private static void AddOptionalDependencyTargets(string optionalRoot, List<string> targets, List<string> emptyContainers, ref int discoveredEntries)
    {
        SetupPaths.AssertSafe(optionalRoot);
        CountDiscoveryEntry(ref discoveredEntries);
        emptyContainers.Add(Path.Combine(optionalRoot, "logs"));
        CountDiscoveryEntry(ref discoveredEntries);
        emptyContainers.Add(optionalRoot);
        // Keep logs and any user settings even when older versions placed them under this directory.
        foreach (var relative in new[] { "models", "runtime", "runtime.zip", "runtime.zip.install-lock",
            "installation-manifest.json", "licenses", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", ".notices.install-lock" })
        {
            CountDiscoveryEntry(ref discoveredEntries);
            targets.Add(Path.Combine(optionalRoot, relative));
        }
        if (!Directory.Exists(optionalRoot)) return;
        foreach (var path in Directory.EnumerateFiles(optionalRoot))
        {
            CountDiscoveryEntry(ref discoveredEntries);
            if (Path.GetFileName(path).StartsWith("runtime.zip.", StringComparison.Ordinal)
                && Path.GetFileName(path).EndsWith(".download", StringComparison.Ordinal)) targets.Add(path);
        }
    }

    private static void CountDiscoveryEntry(ref int discoveredEntries)
    {
        if (++discoveredEntries > 65536)
            throw new InvalidDataException("Uninstall dependency discovery exceeds 65536 entries. No files have been removed.");
    }

    private sealed record DeletionPlan(string Path, bool Directory, IReadOnlyList<DeletionPlan> Children);

    private sealed class DirectoryPins(Action<string>? pinned) : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> _leases = new(StringComparer.OrdinalIgnoreCase);
        private int _plannedEntries;

        internal DeletionPlan? Plan(string path) => Plan(path, 0);

        private DeletionPlan? Plan(string path, int depth)
        {
            if (depth > 64 || ++_plannedEntries > 65536)
                throw new InvalidDataException("Uninstall data exceeds the safe inspection limits (64 levels or 65536 entries). No files have been removed.");
            path = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(path)!;
            if (!PinAncestors(parent)) return null;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Uninstall cleanup refuses a reparse point: {path}");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!Pin(path)) return null;
                var children = Directory.EnumerateFileSystemEntries(path).Select(child => Plan(child, depth + 1))
                    .Where(p => p is not null).Select(p => p!).ToArray();
                return new(path, true, children);
            }
            using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return new(path, false, []);
        }

        internal bool PinAncestors(string directory)
        {
            if (_leases.ContainsKey(directory)) return true;
            var parent = Path.GetDirectoryName(directory);
            if (parent is not null && !PinAncestors(parent)) return false;
            return Pin(directory);
        }


        private bool Pin(string directory)
        {
            if (_leases.ContainsKey(directory)) return true;
            // Generic read includes FILE_LIST_DIRECTORY. Attribute-only handles do not participate
            // in Windows sharing checks and would allow rename despite a missing share-delete flag.
            // Avoid MAX_PATH returning PATH_NOT_FOUND for a real deep directory, which must not
            // be misclassified as absent and skipped during the before-deletion inspection.
            var nativeDirectory = directory.StartsWith(@"\\?\", StringComparison.Ordinal) ? directory
                : directory.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + directory[2..]
                : @"\\?\" + directory;
            var handle = CreateFileW(nativeDirectory, 0x80000020, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error is 2 or 3) return false;
                throw new IOException($"Cannot pin uninstall directory {directory}: {new Win32Exception(error).Message}");
            }
            try
            {
                if (!GetFileInformationByHandle(handle, out var information))
                    throw new IOException($"Cannot inspect pinned directory {directory}.", new Win32Exception(Marshal.GetLastWin32Error()));
                if ((information.Attributes & 0x400) != 0 || (information.Attributes & 0x10) == 0)
                    throw new IOException($"Uninstall cleanup refuses a redirected or non-directory ancestor: {directory}");
                _leases.Add(directory, handle);
                pinned?.Invoke(directory);
                return true;
            }
            catch { if (!_leases.ContainsKey(directory)) handle.Dispose(); throw; }
        }

        internal void Delete(DeletionPlan plan)
        {
            if (plan.Directory)
            {
                foreach (var child in plan.Children) Delete(child);
                // Only non-recursive removal follows release. The parent remains pinned; if a final
                // entry were replaced with a link, this removes the link and never visits its target.
                if (_leases.Remove(plan.Path, out var handle)) handle.Dispose();
                Directory.Delete(plan.Path, recursive: false);
            }
            else File.Delete(plan.Path); // Final entry deletion does not follow a replacement file link.
        }

        internal void PruneEmptyDirectory(string path)
        {
            path = Path.GetFullPath(path);
            if (!PinAncestors(Path.GetDirectoryName(path)!)) return;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new IOException($"Refusing an unsafe empty-container prune: {path}");
            if (!Pin(path)) return;
            if (Directory.EnumerateFileSystemEntries(path).Any()) return;
            if (_leases.Remove(path, out var handle)) handle.Dispose();
            Directory.Delete(path, recursive: false); // Newly inserted data prevents removal atomically.
        }

        public void Dispose()
        {
            foreach (var handle in _leases.Values) handle.Dispose();
            _leases.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileHandleInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileHandleInformation information);


    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    internal static bool StartupPointsToApp(string command, string app)
    {
        var value = command.Trim();
        var end = value.StartsWith('"') ? value.IndexOf('"', 1) : value.IndexOf(' ');
        var executable = value.StartsWith('"') ? (end < 1 ? "" : value[1..end])
            : (end < 0 ? value : value[..end]);
        return executable.Length > 0 && Path.IsPathFullyQualified(executable)
            && SamePath(executable, Path.Combine(app, "PulseReader.exe"));
    }

    private static void AssertNoRunningClients(string app)
    {
        foreach (var process in Process.GetProcessesByName("PulseReader"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch (Exception exception) { throw new IOException($"Cannot inspect Pulse Reader process {process.Id}; close it and retry.", exception); }
                if (executable is null || SamePath(executable, Path.Combine(app, "PulseReader.exe")))
                    throw new IOException($"Pulse Reader process {process.Id} is still running. Close it in its Windows session and retry.");
            }
        }
    }
}

internal sealed class WindowsUninstallProfileAccess : IUninstallProfileAccess
{
    public IReadOnlyList<UninstallProfile> GetProfiles()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")
            ?? throw new IOException("The Windows profile list cannot be inspected.");
        var profiles = new List<UninstallProfile>();
        foreach (var sid in key.GetSubKeyNames())
        {
            // Normal local/domain and Azure AD user SIDs; service/special profiles and .bak keys are excluded.
            if ((!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
                || sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
            _ = new SecurityIdentifier(sid);
            using var profile = key.OpenSubKey(sid) ?? throw new IOException($"Cannot inspect profile {sid}.");
            if (profile.GetValue("ProfileImagePath") is not string path || string.IsNullOrWhiteSpace(path))
                throw new IOException($"Missing profile directory for {sid}.");
            path = Environment.ExpandEnvironmentVariables(path);
            if (path.Contains('%') || !Path.IsPathFullyQualified(path)) throw new IOException($"Unsafe profile directory for {sid}.");
            SetupPaths.AssertSafe(path);
            profiles.Add(new(sid, path));
        }
        return profiles;
    }

    public UninstallRegistryCleanupResult RemoveOwnedRegistryValues(UninstallProfile profile, string applicationDirectory, bool removeReceipt)
    {
        if (profile.Sid == "current")
        {
            CleanHive(Registry.CurrentUser, applicationDirectory, removeReceipt);
            return UninstallRegistryCleanupResult.Cleaned;
        }
        // Distinguish an absent/offline hive from access errors explicitly; do not load NTUSER.DAT.
        var status = RegOpenKeyExW(Registry.Users.Handle, profile.Sid, 0, 0x2001F, out var handle);
        if (status == 2) { handle.Dispose(); return UninstallRegistryCleanupResult.SkippedOffline; }
        if (status != 0)
        {
            handle.Dispose();
            throw new IOException($"Cannot open a loaded user registry for {profile.Sid}: {new Win32Exception(status).Message}");
        }
        using (handle)
        using (var loaded = RegistryKey.FromHandle(handle)) CleanHive(loaded, applicationDirectory, removeReceipt);
        return UninstallRegistryCleanupResult.Cleaned;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(SafeRegistryHandle hive, string subKey, uint options, uint access, out SafeRegistryHandle key);

    private static void CleanHive(RegistryKey hive, string app, bool receipt)
    {
        using (var startup = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            if (startup?.GetValue("Pulse Reader", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command
                && UninstallDataCleanup.StartupPointsToApp(command, app)) startup.DeleteValue("Pulse Reader", throwOnMissingValue: false);
        if (receipt)
        {
            using var key = hive.OpenSubKey(@"Software\QuantumDotGames\PulseReader", writable: true);
            key?.DeleteValue("AcceptedDistributionTermsSha256", throwOnMissingValue: false);
        }
    }

}
