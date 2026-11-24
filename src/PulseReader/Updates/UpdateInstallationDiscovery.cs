using System.Text.Json;
using PulseReader.Setup;

namespace PulseReader.Updates;

internal static class UpdateInstallationDiscovery
{
    internal static bool TryRead(string applicationRoot, Version runningVersion,
        out UpdateInstallation? installation, out string? reason,
        string? localApplicationData = null, string? programFiles = null)
    {
        installation = null;
        reason = null;
        try
        {
            var app = Path.GetFullPath(applicationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            SetupPaths.AssertSafe(app);
            var inventory = Path.Combine(app, "dependency-inventory.json");
            if (!Exists(inventory))
            {
                reason = "This development or unpackaged copy has no release inventory.";
                return false;
            }
            using var document = ReadDocument(inventory, UpdateConstants.MaximumMetadataBytes);
            var root = document.RootElement;
            var schema = Property(root, "SchemaVersion");
            if (!schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1)
                throw new InvalidDataException("The release inventory schema is unsupported.");
            var versionText = Property(root, "AppVersion").GetString();
            if (!UpdateVersionPolicy.TryParseStable(versionText, false, out var version)
                || version != UpdateVersionPolicy.Normalize(runningVersion))
                throw new InvalidDataException("The release inventory does not match the running application version.");
            var variant = Property(root, "Variant").GetString();
            if (!UpdateVersionPolicy.IsVariant(variant))
                throw new InvalidDataException("The release inventory package flavour is unsupported.");
            var marker = Path.Combine(app, InstallationLayout.ScopeFileName);
            var scope = InstallationScope.User;
            var kind = UpdatePackageKind.Portable;
            if (Exists(marker))
            {
                using var scopeDocument = ReadDocument(marker, 256);
                var scopeRoot = scopeDocument.RootElement;
                if (scopeRoot.ValueKind != JsonValueKind.Object || scopeRoot.EnumerateObject().Count() != 2
                    || !Property(scopeRoot, "SchemaVersion").TryGetInt32(out var scopeSchema) || scopeSchema != 1)
                    throw new InvalidDataException("The installation scope record is invalid.");
                scope = Property(scopeRoot, "Scope").GetString() switch
                {
                    "user" => InstallationScope.User,
                    "machine" => InstallationScope.Machine,
                    _ => throw new InvalidDataException("The installation scope record is invalid.")
                };
                var expected = scope == InstallationScope.User
                    ? Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs", "PulseReader")
                    : Path.Combine(programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PulseReader");
                if (!string.Equals(app, Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The installed copy is outside the canonical location for its scope.");
                // These are package identity checks, not publisher signature verification.
                AssertRegularFile(Path.Combine(app, "unins000.exe"));
                AssertRegularFile(Path.Combine(app, "unins000.dat"));
                kind = UpdatePackageKind.Installer;
            }
            installation = new(version!, variant!, kind, scope, app);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            reason = "Automatic updates are unavailable because installation identity could not be verified. " + exception.Message;
            return false;
        }
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void AssertRegularFile(string path)
    {
        SetupPaths.AssertSafe(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(path).Length <= 0)
            throw new InvalidDataException("The installed copy is missing a regular uninstaller file.");
    }

    private static JsonDocument ReadDocument(string path, int maximumBytes)
    {
        SetupPaths.AssertSafe(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("The installation identity metadata exceeds its size limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("The installation identity changed during inspection.");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static JsonElement Property(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count(p => p.NameEquals(name)) != 1)
            throw new InvalidDataException($"Installation identity must contain exactly one {name} field.");
        return root.GetProperty(name);
    }
}
