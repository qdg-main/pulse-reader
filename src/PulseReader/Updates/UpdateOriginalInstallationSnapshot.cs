using System.Security.Cryptography;
using PulseReader.Setup;

namespace PulseReader.Updates;

// This snapshot does not roll an installer back. It permits reopening an old
// build only when the installer demonstrably left all original bytes intact.
internal sealed class UpdateOriginalInstallationSnapshot : IDisposable
{
    private const long MaximumFileBytes = 512L * 1024 * 1024;
    private const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    private readonly UpdatePathSafety.Pins _pins = new();
    private readonly List<UpdatePathSafety.LockedFile> _shadowFiles = [];
    private readonly Dictionary<string, Fingerprint> _original = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileAttributes> _namespace;
    private readonly string _applicationRoot;
    internal string OriginalTermsSha256 { get; }

    private UpdateOriginalInstallationSnapshot(string applicationRoot, string shadowRoot)
    {
        _applicationRoot = UpdatePathSafety.Root(applicationRoot);
        try
        {
            _pins.Directory(_applicationRoot);
            _pins.Directory(shadowRoot);
            long total = 0;
            foreach (var relative in UpdateShadowPayload.ReadList(shadowRoot, _pins))
            {
                var shadowPath = UpdatePathSafety.Under(shadowRoot, relative);
                _pins.Parents(shadowPath);
                var shadow = new UpdatePathSafety.LockedFile(shadowPath, movable: false);
                _shadowFiles.Add(shadow);
                if (shadow.Stream.Length > MaximumFileBytes || (total += shadow.Stream.Length) > MaximumTotalBytes)
                    throw new InvalidDataException("Original installer payload exceeds its comparison limit.");
                _original.Add(relative, Fingerprint.Read(shadow.Stream));
            }
            foreach (var name in new[] { InstallationLayout.ScopeFileName, "unins000.exe", "unins000.dat" })
            {
                var path = UpdatePathSafety.Under(_applicationRoot, name);
                _pins.Parents(path);
                // Target file locks are released before Inno starts, allowing
                // actual replacement. The original fingerprints remain fixed.
                using var file = new UpdatePathSafety.LockedFile(path, movable: false);
                if (file.Stream.Length > MaximumFileBytes || (total += file.Stream.Length) > MaximumTotalBytes)
                    throw new InvalidDataException("Original installer metadata exceeds its comparison limit.");
                _original.Add(name, Fingerprint.Read(file.Stream));
            }
            if (!_original.TryGetValue("Distribution-Terms.txt", out var terms)
                || !_original.ContainsKey("distribution-terms.json"))
                throw new InvalidDataException("Original installed distribution terms are missing from the owned payload.");
            OriginalTermsSha256 = terms.Sha256;
            using var namespacePins = new UpdatePathSafety.Pins();
            _namespace = ReadNamespace(namespacePins);
            using var unchanged = VerifyOriginalAndLease();
        }
        catch { Dispose(); throw; }
    }

    internal static UpdateOriginalInstallationSnapshot Capture(string applicationRoot, string shadowRoot)
        => new(applicationRoot, shadowRoot);

    internal (bool Updated, IDisposable Lease) SelectFailureLease(bool installerCompleted,
        Func<IDisposable> verifyUpdated)
    {
        // A nonzero installer may publish new identity/terms before failing on
        // a dependency. Those few new files never authorize a new-build launch.
        if (installerCompleted)
        {
            try { return (true, verifyUpdated()); }
            catch { /* Only the exact original snapshot can authorize fallback. */ }
        }
        return (false, VerifyOriginalAndLease());
    }

    // Keep this returned lease alive through identity/terms checks and process
    // launch. It blocks payload writes/deletes and directory relinks throughout.
    internal IDisposable VerifyOriginalAndLease()
    {
        var lease = new OriginalLease();
        try
        {
            foreach (var (relative, expected) in _original)
            {
                var path = UpdatePathSafety.Under(_applicationRoot, relative);
                lease.Pins.Parents(path);
                var file = new UpdatePathSafety.LockedFile(path, movable: false);
                lease.Files.Add(file);
                if (file.Stream.Length != expected.Length || Fingerprint.Read(file.Stream) != expected)
                    throw new InvalidDataException("Installer failure changed an original owned file: " + relative);
            }
            var current = ReadNamespace(lease.Pins);
            if (_namespace.Count != current.Count || _namespace.Any(entry =>
                !current.TryGetValue(entry.Key, out var kind) || kind != entry.Value))
                throw new InvalidDataException("Installer failure changed the original application namespace; old relaunch is withheld.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static IDisposable LeaseCurrentPayload(string applicationRoot)
    {
        var lease = new OriginalLease();
        try
        {
            lease.Pins.Directory(applicationRoot);
            long total = 0;
            foreach (var relative in UpdateShadowPayload.ReadList(applicationRoot, lease.Pins)
                .Concat([InstallationLayout.ScopeFileName, "unins000.exe", "unins000.dat"]))
            {
                var path = UpdatePathSafety.Under(applicationRoot, relative);
                lease.Pins.Parents(path);
                var file = new UpdatePathSafety.LockedFile(path, movable: false);
                lease.Files.Add(file);
                if (file.Stream.Length > MaximumFileBytes || (total += file.Stream.Length) > MaximumTotalBytes)
                    throw new InvalidDataException("Installed payload exceeds its final verification limit.");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private Dictionary<string, FileAttributes> ReadNamespace(UpdatePathSafety.Pins pins)
    {
        var result = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((_applicationRoot, 0));
        var count = 0;
        while (pending.TryPop(out var current))
        {
            if (current.Depth > 32) throw new InvalidDataException("Original application namespace exceeds its depth limit.");
            pins.Directory(current.Directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Directory))
            {
                if (++count > 16384) throw new InvalidDataException("Original application namespace exceeds its entry limit.");
                var relative = UpdatePathSafety.Relative(Path.GetRelativePath(_applicationRoot, path));
                if (IsUserData(relative)) continue;
                var attributes = File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint);
                result.Add(relative, attributes);
                // Record links without following or reading their targets.
                if (attributes == FileAttributes.Directory) pending.Push((path, current.Depth + 1));
            }
        }
        return result;
    }

    private static bool IsUserData(string path)
    {
        var first = path.Split('/')[0];
        return new[] { "Models", "ContextualPronunciation", "OptionalRuntime", "Cache", "Logs", "Data", "Updates" }
                .Contains(first, StringComparer.OrdinalIgnoreCase)
            || first.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
            || first.Equals("config.json", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    { foreach (var file in _shadowFiles) file.Dispose(); _pins.Dispose(); }

    private sealed record Fingerprint(long Length, string Sha256)
    {
        internal static Fingerprint Read(Stream stream)
        {
            stream.Position = 0;
            var value = new Fingerprint(stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
            stream.Position = 0;
            return value;
        }
    }

    private sealed class OriginalLease : IDisposable
    {
        internal UpdatePathSafety.Pins Pins { get; } = new();
        internal List<UpdatePathSafety.LockedFile> Files { get; } = [];
        public void Dispose() { foreach (var file in Files) file.Dispose(); Pins.Dispose(); }
    }
}
