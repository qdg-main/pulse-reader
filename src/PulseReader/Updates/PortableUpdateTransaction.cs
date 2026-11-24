using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using PulseReader.Setup;

namespace PulseReader.Updates;

internal sealed class PortableUpdateInterruptionException : IOException
{
    internal PortableUpdateInterruptionException() : base("Fixture interruption.") { }
}

internal sealed class PortableUpdateTransaction(Action<string>? checkpoint = null)
{
    private const int MaximumFiles = 4096;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumEntryBytes = 512L * 1024 * 1024;
    private const string Inventory = "dependency-inventory.json";
    private const string PayloadList = "payload-files.txt";
    private static readonly string[] RequiredApplicationFiles = ["PulseReader.exe", "PulseReader.dll", "PulseReader.deps.json", "PulseReader.runtimeconfig.json"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // The helper calls this after the application process has exited.
    internal Task ApplyAsync(PreparedUpdate update, CancellationToken cancellationToken)
    {
        Apply(update, cancellationToken);
        return Task.CompletedTask;
    }

    internal Task RecoverAsync(string applicationRoot, CancellationToken cancellationToken = default)
    {
        var target = UpdatePathSafety.Root(applicationRoot);
        using var pins = new UpdatePathSafety.Pins();
        pins.Directory(target);
        Recover(target, pins, cancellationToken);
        return Task.CompletedTask;
    }

    internal static string JournalPath(string applicationRoot)
    {
        var target = UpdatePathSafety.Root(applicationRoot);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..24];
        return Path.Combine(Path.GetDirectoryName(target)!, ".PulseReader-update-" + key + ".json");
    }

    private void Apply(PreparedUpdate update, CancellationToken token)
    {
        if (update.Installation.Kind != UpdatePackageKind.Portable || update.Installation.Scope != InstallationScope.User)
            throw new InvalidDataException("Only a portable installation can use this update transaction.");
        var target = UpdatePathSafety.Root(update.Installation.ApplicationRoot);
        using var pins = new UpdatePathSafety.Pins();
        pins.Directory(target);
        Recover(target, pins, token);
        token.ThrowIfCancellationRequested();
        CheckIdentity(target, update.Installation.Version, update.Installation.Variant);
        var oldPaths = ReadPayloadList(target);
        var operation = Guid.NewGuid().ToString("N");
        var parent = Path.GetDirectoryName(target)!;
        var stage = Path.Combine(parent, ".PulseReader-stage-" + operation);
        var backup = Path.Combine(parent, ".PulseReader-backup-" + operation);
        if (Directory.Exists(stage) || File.Exists(stage) || Directory.Exists(backup) || File.Exists(backup))
            throw new IOException("The update operation directory already exists.");
        pins.Directory(stage, true);
        pins.Directory(backup, true);
        var extracted = new List<string>();
        var extractedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Journal? journal = null;
        Exception? preparationFailure = null;
        var oldHandles = new Dictionary<string, UpdatePathSafety.LockedFile>(StringComparer.OrdinalIgnoreCase);
        var newHandles = new Dictionary<string, UpdatePathSafety.LockedFile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Extract(update, stage, pins, extracted, extractedHashes, token);
            CheckIdentity(stage, update.Offer.Version, update.Installation.Variant);
            CheckTerms(stage, update.Offer);
            var newPaths = ReadPayloadList(stage);
            if (!newPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(extracted))
                throw new InvalidDataException("The portable payload list does not exactly describe the ZIP files.");
            CheckRunnablePayload(stage, newPaths, update.Offer.Version);
            var entries = new List<Entry>();
            foreach (var path in oldPaths.Union(newPaths, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var live = UpdatePathSafety.Under(target, path);
                pins.Parents(live, true);
                var oldOwned = oldPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
                var newOwned = newPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(live) || (!oldOwned && File.Exists(live)))
                    throw new InvalidDataException("The update would replace an unowned file or directory: " + path);
                string? oldHash = null, oldIdentity = null, newHash = null, newIdentity = null;
                if (oldOwned)
                {
                    var handle = new UpdatePathSafety.LockedFile(live);
                    oldHandles.Add(path, handle);
                    oldHash = Hash(handle.Stream, token); oldIdentity = handle.Identity;
                    pins.Parents(UpdatePathSafety.Under(backup, path), true);
                }
                if (newOwned)
                {
                    var handle = new UpdatePathSafety.LockedFile(UpdatePathSafety.Under(stage, path));
                    newHandles.Add(path, handle);
                    newHash = Hash(handle.Stream, token); newIdentity = handle.Identity;
                    if (newHash != extractedHashes[path]) throw new InvalidDataException("An extracted payload file changed before publication.");
                }
                entries.Add(new(path, oldHash, oldIdentity, newHash, newIdentity));
            }
            CheckIdentity(oldHandles[Inventory].Stream, update.Installation.Version, update.Installation.Variant);
            if (!ReadPayloadList(oldHandles[PayloadList].Stream).SequenceEqual(oldPaths, StringComparer.Ordinal))
                throw new InvalidDataException("The installed payload ownership list changed during preparation.");
            CheckTerms(newHandles[DistributionTermsConsent.ContractFileName].Stream,
                newHandles[DistributionTermsConsent.TermsFileName].Stream, update.Offer);
            var preparedJournal = new Journal(1, target, operation, false, entries);
            Save(preparedJournal);
            journal = preparedJournal;
            checkpoint?.Invoke("Prepared");
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (oldHandles.TryGetValue(entry.Path, out var old))
                {
                    old.Move(UpdatePathSafety.Under(backup, entry.Path));
                    checkpoint?.Invoke("BackedUp:" + entry.Path);
                }
                token.ThrowIfCancellationRequested();
                if (newHandles.TryGetValue(entry.Path, out var next))
                {
                    next.Move(UpdatePathSafety.Under(target, entry.Path));
                    checkpoint?.Invoke("Published:" + entry.Path);
                }
            }
            token.ThrowIfCancellationRequested();
            CheckIdentity(newHandles[Inventory].Stream, update.Offer.Version, update.Installation.Variant);
            // All new handles still deny writes. The durable commit is the cancellation boundary.
            var committedJournal = journal with { Committed = true };
            Save(committedJournal);
            journal = committedJournal;
            checkpoint?.Invoke("Committed");
        }
        catch (PortableUpdateInterruptionException) { throw; }
        catch (Exception original)
        {
            preparationFailure = original;
            foreach (var handle in oldHandles.Values) handle.Dispose(); oldHandles.Clear();
            foreach (var handle in newHandles.Values) handle.Dispose(); newHandles.Clear();
            if (journal is not null && !journal.Committed)
            {
                try { Recover(target, pins, CancellationToken.None); }
                catch (Exception recovery)
                {
                    throw new IOException("The portable update failed and recovery retained the journal and backup.",
                        new AggregateException(original, recovery));
                }
            }
            throw;
        }
        finally
        {
            foreach (var handle in oldHandles.Values) handle.Dispose();
            foreach (var handle in newHandles.Values) handle.Dispose();
            // No journal means the live tree was never touched. Only known extracted files are removed.
            if (journal is null)
            {
                pins.Dispose();
                foreach (var (directory, paths, removeFiles) in new[]
                {
                    (stage, (IEnumerable<string>)extracted, true), (backup, (IEnumerable<string>)oldPaths, false)
                })
                {
                    try { CleanupDirectory(directory, paths, removeFiles); }
                    catch (Exception cleanup) when (preparationFailure is not null)
                    {
                        // Preserve the actual preflight failure. Retain unexpected directory content
                        // and attach the exact cleanup evidence rather than replacing the main error.
                        preparationFailure.Data["Retained portable update directory: " + directory] = cleanup.ToString();
                    }
                }
            }
        }
        pins.Dispose();
        // A committed installation is complete even if antivirus temporarily prevents cleanup.
        try { using var cleanupPins = new UpdatePathSafety.Pins(); cleanupPins.Directory(target); Recover(target, cleanupPins, CancellationToken.None); }
        catch (IOException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void Extract(PreparedUpdate update, string stage, UpdatePathSafety.Pins pins, List<string> extracted,
        Dictionary<string, string> extractedHashes, CancellationToken token)
    {
        pins.Parents(update.AssetPath);
        using var asset = new UpdatePathSafety.LockedFile(update.AssetPath, movable: false);
        if (asset.Stream.Length != update.Offer.Asset.Length || asset.Stream.Length > UpdateConstants.MaximumPortableBytes
            || !Hash(asset.Stream, token).Equals(update.Offer.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded portable ZIP failed its integrity check.");
        asset.Stream.Position = 0;
        using var zip = new ZipArchive(asset.Stream, ZipArchiveMode.Read, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(ZipArchiveEntry Entry, string Path)>();
        long expanded = 0;
        if (zip.Entries.Count > MaximumFiles * 2) throw new InvalidDataException("Too many ZIP entries.");
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var path = UpdatePathSafety.Relative(directory ? entry.FullName[..^1] : entry.FullName);
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000))
                || (!directory && (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0)
                || path.Equals(InstallationLayout.ScopeFileName, StringComparison.OrdinalIgnoreCase)
                || !names.Add(path)) throw new InvalidDataException("Unsafe or duplicate portable ZIP entry.");
            if (directory)
            {
                if (entry.Length != 0) throw new InvalidDataException("ZIP directory has content.");
                directoryNames.Add(path); continue;
            }
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes || (expanded += entry.Length) > MaximumExpandedBytes
                || files.Count >= MaximumFiles) throw new InvalidDataException("Portable ZIP expansion limit exceeded.");
            files.Add((entry, path));
        }
        foreach (var (_, path) in files)
        {
            var parts = path.Split('/');
            for (var count = 1; count < parts.Length; count++)
            {
                var ancestor = string.Join('/', parts.Take(count));
                if (names.Contains(ancestor) && !directoryNames.Contains(ancestor))
                    throw new InvalidDataException("ZIP file collides with a directory.");
            }
        }
        foreach (var (entry, path) in files)
        {
            token.ThrowIfCancellationRequested();
            var destination = UpdatePathSafety.Under(stage, path);
            pins.Parents(destination, true);
            using var source = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            extracted.Add(path);
            var buffer = new byte[65536]; long copied = 0; int read;
            while ((read = source.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if ((copied += read) > entry.Length) throw new InvalidDataException("ZIP entry exceeds its declared length.");
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            if (copied != entry.Length) throw new InvalidDataException("Truncated ZIP entry.");
            output.Flush(true);
            extractedHashes.Add(path, Convert.ToHexString(hash.GetHashAndReset()));
        }
    }

    private static void CheckIdentity(string root, Version version, string variant)
    {
        if (File.Exists(Path.Combine(root, InstallationLayout.ScopeFileName)) || Directory.Exists(Path.Combine(root, InstallationLayout.ScopeFileName)))
            throw new InvalidDataException("An installer scope marker is not allowed in a portable update.");
        using var inventory = new UpdatePathSafety.LockedFile(Path.Combine(root, Inventory), movable: false);
        CheckIdentity(inventory.Stream, version, variant);
    }

    private static void CheckRunnablePayload(string root, string[] paths, Version version)
    {
        if (RequiredApplicationFiles.Any(name => !paths.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("The portable update is missing required application files.");
        using (var executable = new UpdatePathSafety.LockedFile(Path.Combine(root, "PulseReader.exe"), movable: false))
        using (var pe = new PEReader(executable.Stream, PEStreamOptions.LeaveOpen))
        {
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader is not { AddressOfEntryPoint: > 0 }
                || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0
                || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0)
                throw new InvalidDataException("The portable application entry point is not a valid x64 executable.");
        }
        var assemblyPath = Path.Combine(root, "PulseReader.dll");
        using (var assembly = new UpdatePathSafety.LockedFile(assemblyPath, movable: false))
        using (var pe = new PEReader(assembly.Stream, PEStreamOptions.LeaveOpen))
        {
            if (!pe.HasMetadata) throw new InvalidDataException("The portable application assembly has no managed metadata.");
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) throw new InvalidDataException("The portable application payload is not an assembly.");
            var definition = metadata.GetAssemblyDefinition();
            var actual = definition.Version;
            var file = FileVersionInfo.GetVersionInfo(assemblyPath);
            if (metadata.GetString(definition.Name) != "PulseReader" || actual.Revision != 0
                || new Version(actual.Major, actual.Minor, actual.Build) != version
                || new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart) != version || file.FilePrivatePart != 0)
                throw new InvalidDataException("The portable application assembly version does not match the update offer.");
        }
        foreach (var name in new[] { "PulseReader.deps.json", "PulseReader.runtimeconfig.json" })
        {
            using var file = new UpdatePathSafety.LockedFile(Path.Combine(root, name), movable: false);
            if (file.Stream.Length is <= 0 or > UpdateConstants.MaximumMetadataBytes)
                throw new InvalidDataException("Invalid portable application runtime metadata.");
            using var json = JsonDocument.Parse(file.Stream);
            var obj = json.RootElement;
            if (obj.ValueKind != JsonValueKind.Object || obj.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != obj.EnumerateObject().Count())
                throw new InvalidDataException("Invalid portable application runtime metadata.");
            if (name.EndsWith("deps.json", StringComparison.Ordinal))
            {
                if (!obj.TryGetProperty("runtimeTarget", out var target) || target.ValueKind != JsonValueKind.Object
                    || !target.TryGetProperty("name", out var targetName) || targetName.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(targetName.GetString()) || !obj.TryGetProperty("targets", out var targets)
                    || targets.ValueKind != JsonValueKind.Object || !targets.TryGetProperty(targetName.GetString()!, out var selected)
                    || selected.ValueKind != JsonValueKind.Object || !selected.EnumerateObject().Any())
                    throw new InvalidDataException("The portable dependency metadata does not identify a runtime target.");
            }
            else
            {
                if (!obj.TryGetProperty("runtimeOptions", out var options) || options.ValueKind != JsonValueKind.Object
                    || !options.TryGetProperty("tfm", out var tfm) || tfm.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tfm.GetString())
                    || !(options.TryGetProperty("framework", out var framework) && ValidFramework(framework)
                        || options.TryGetProperty("frameworks", out var frameworks) && ValidFrameworks(frameworks)
                        || options.TryGetProperty("includedFrameworks", out var included) && ValidFrameworks(included)))
                    throw new InvalidDataException("The portable runtime configuration does not identify its frameworks.");
            }
        }
    }

    private static bool ValidFramework(JsonElement framework) => framework.ValueKind == JsonValueKind.Object
        && framework.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())
        && framework.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String && Version.TryParse(version.GetString(), out _);
    private static bool ValidFrameworks(JsonElement frameworks) => frameworks.ValueKind == JsonValueKind.Array && frameworks.GetArrayLength() > 0
        && frameworks.EnumerateArray().All(ValidFramework);

    private static void CheckIdentity(Stream stream, Version version, string variant)
    {
        stream.Position = 0;
        if (stream.Length > UpdateConstants.MaximumMetadataBytes) throw new InvalidDataException("Invalid package inventory.");
        using var json = JsonDocument.Parse(stream);
        var obj = json.RootElement;
        if (obj.ValueKind != JsonValueKind.Object || obj.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != obj.EnumerateObject().Count()
            || !obj.TryGetProperty("SchemaVersion", out var schema) || !schema.TryGetInt32(out var value) || value != 1
            || !obj.TryGetProperty("AppVersion", out var appVersion) || appVersion.ValueKind != JsonValueKind.String
            || !Version.TryParse(appVersion.GetString(), out var actual) || actual != version
            || !obj.TryGetProperty("Variant", out var flavour) || flavour.ValueKind != JsonValueKind.String
            || flavour.GetString() != variant || variant is not ("win-x64" or "win-x64-framework-dependent"))
            throw new InvalidDataException("The portable package identity does not match the expected version and variant.");
    }

    private static string[] ReadPayloadList(string root)
    {
        using var file = new UpdatePathSafety.LockedFile(Path.Combine(root, PayloadList), movable: false);
        return ReadPayloadList(file.Stream);
    }

    private static string[] ReadPayloadList(Stream stream)
    {
        stream.Position = 0;
        if (stream.Length > UpdateConstants.MaximumMetadataBytes) throw new InvalidDataException("Payload list is too large.");
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var list = reader.ReadToEnd();
        if (list.StartsWith('\uFEFF')) list = list[1..];
        var paths = list.Split('\n').Select(p => p.TrimEnd('\r')).Where(p => p.Length != 0).Select(UpdatePathSafety.Relative).ToArray();
        if (paths.Length is 0 or > MaximumFiles || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length
            || !paths.Contains(PayloadList, StringComparer.Ordinal) || !paths.Contains(Inventory, StringComparer.Ordinal)
            || paths.Any(p => p.Equals(InstallationLayout.ScopeFileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Invalid portable payload ownership list.");
        foreach (var path in paths) ValidateOwnedPath(path);
        return paths;
    }

    private static void ValidateOwnedPath(string path)
    {
        // Prevent an ownership list or damaged journal claiming settings or model data.
        if (path.Split('/')[0].Equals("Models", StringComparison.OrdinalIgnoreCase)
            || path.Split('/')[0].Equals("ContextualPronunciation", StringComparison.OrdinalIgnoreCase)
            || path.Equals("config.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals(InstallationLayout.ScopeFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A portable payload cannot own user configuration, models or installer scope.");
    }

    private static void CheckTerms(string root, UpdateOffer offer)
    {
        using var contract = new UpdatePathSafety.LockedFile(Path.Combine(root, DistributionTermsConsent.ContractFileName), movable: false);
        using var terms = new UpdatePathSafety.LockedFile(Path.Combine(root, DistributionTermsConsent.TermsFileName), movable: false);
        CheckTerms(contract.Stream, terms.Stream, offer);
    }

    private static void CheckTerms(Stream contract, Stream terms, UpdateOffer offer)
    {
        contract.Position = 0; terms.Position = 0;
        if (contract.Length is <= 0 or > DistributionTermsConsent.MaximumContractBytes
            || terms.Length is <= 0 or > DistributionTermsConsent.MaximumTermsBytes)
            throw new InvalidDataException("Invalid portable distribution terms size.");
        using var json = JsonDocument.Parse(contract);
        var root = json.RootElement;
        string[] required = ["SchemaVersion", "TermsFileName", "TermsSha256"];
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != required.Length
            || required.Any(name => root.EnumerateObject().Count(p => p.Name == name) != 1)
            || root.GetProperty("SchemaVersion").ValueKind != JsonValueKind.Number
            || !root.GetProperty("SchemaVersion").TryGetInt32(out var schema) || schema != 1
            || root.GetProperty("TermsFileName").ValueKind != JsonValueKind.String
            || root.GetProperty("TermsFileName").GetString() != DistributionTermsConsent.TermsFileName
            || root.GetProperty("TermsSha256").ValueKind != JsonValueKind.String
            || !string.Equals(root.GetProperty("TermsSha256").GetString(), offer.TermsSha256, StringComparison.OrdinalIgnoreCase)
            || !Hash(terms, CancellationToken.None).Equals(offer.TermsSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The portable distribution terms do not match the accepted update offer.");
        using var reader = new StreamReader(terms, StrictUtf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = reader.ReadToEnd();
        if (text.StartsWith('\uFEFF')) text = text[1..];
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || text != offer.TermsText)
            throw new InvalidDataException("The portable terms text differs from the accepted update offer.");
    }

    private static string Hash(Stream file, CancellationToken token)
    {
        file.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; int read;
        while ((read = file.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        file.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Save(Journal journal)
    {
        var path = JournalPath(journal.Target);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, journal); file.Flush(true); }
            UpdatePathSafety.ReplaceJournal(temporary, path);
        }
        catch
        {
            if (File.Exists(temporary)) { using var file = new UpdatePathSafety.LockedFile(temporary); file.Delete(); }
            throw;
        }
    }

    private static void Recover(string target, UpdatePathSafety.Pins pins, CancellationToken token)
    {
        var journalPath = JournalPath(target);
        if (!File.Exists(journalPath)) return;
        Journal journal;
        using (var input = new UpdatePathSafety.LockedFile(journalPath, movable: false))
        {
            if (input.Stream.Length > 4 * UpdateConstants.MaximumMetadataBytes) throw new InvalidDataException("Invalid update journal.");
            journal = JsonSerializer.Deserialize<Journal>(input.Stream) ?? throw new InvalidDataException("Invalid update journal.");
        }
        ValidateJournal(journal, target);
        var parent = Path.GetDirectoryName(target)!;
        var stage = Path.Combine(parent, ".PulseReader-stage-" + journal.Operation);
        var backup = Path.Combine(parent, ".PulseReader-backup-" + journal.Operation);
        if (Directory.Exists(stage)) pins.Directory(stage);
        if (Directory.Exists(backup)) pins.Directory(backup);
        var held = new List<UpdatePathSafety.LockedFile>();
        var actions = new List<Action>();
        try
        {
            // Preflight every path before changing anything. Conflicts retain all remaining evidence.
            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                token.ThrowIfCancellationRequested();
                var livePath = UpdatePathSafety.Under(target, entry.Path);
                var backupPath = UpdatePathSafety.Under(backup, entry.Path);
                var stagePath = UpdatePathSafety.Under(stage, entry.Path);
                pins.Parents(livePath, true);
                var live = journal.Committed ? null : OpenOptional(livePath, pins, held);
                var old = OpenOptional(backupPath, pins, held);
                var next = OpenOptional(stagePath, pins, held);
                if (old is not null && !Matches(old, entry.OldSha256, entry.OldIdentity)) throw Conflict(entry.Path);
                if (next is not null && !Matches(next, entry.NewSha256, entry.NewIdentity)) throw Conflict(entry.Path);
                if (journal.Committed)
                {
                    if (old is not null) actions.Add(old.Delete);
                    if (next is not null) throw Conflict(entry.Path);
                    continue;
                }
                if (old is not null)
                {
                    if (live is not null)
                    {
                        if (!Matches(live, entry.NewSha256, entry.NewIdentity) || next is not null) throw Conflict(entry.Path);
                        actions.Add(live.Delete);
                        // A pending delete requires its handle to close before the old file can be renamed.
                        actions.Add(live.Dispose);
                    }
                    actions.Add(() => old.Move(livePath));
                }
                else if (entry.OldSha256 is not null)
                {
                    if (live is null || !Matches(live, entry.OldSha256, entry.OldIdentity)) throw Conflict(entry.Path);
                }
                else if (live is not null)
                {
                    if (!Matches(live, entry.NewSha256, entry.NewIdentity) || next is not null) throw Conflict(entry.Path);
                    actions.Add(live.Delete);
                }
                if (next is not null) actions.Add(next.Delete);
            }
            foreach (var action in actions) action();
        }
        finally { foreach (var file in held) file.Dispose(); }
        pins.Dispose();
        CleanupDirectory(stage, journal.Entries.Select(e => e.Path), removeFiles: false);
        CleanupDirectory(backup, journal.Entries.Select(e => e.Path), removeFiles: false);
        using var journalFile = new UpdatePathSafety.LockedFile(journalPath);
        journalFile.Delete();
        // Re-pin ancestry after removing the operation directories.
        pins.Directory(target);
    }

    private static UpdatePathSafety.LockedFile? OpenOptional(string path, UpdatePathSafety.Pins pins, List<UpdatePathSafety.LockedFile> held)
    {
        if (Directory.Exists(path)) throw Conflict(path);
        if (!File.Exists(path)) return null;
        pins.Parents(path);
        var file = new UpdatePathSafety.LockedFile(path); held.Add(file); return file;
    }
    private static bool Matches(UpdatePathSafety.LockedFile file, string? hash, string? identity) =>
        hash is not null && file.Identity == identity && Hash(file.Stream, CancellationToken.None) == hash;
    private static IOException Conflict(string path) => new("Portable update recovery found changed or missing files; the journal and backup were retained. Conflicting path: " + path);

    private static void ValidateJournal(Journal journal, string target)
    {
        if (journal.Schema != 1 || journal.Target != target || !Guid.TryParseExact(journal.Operation, "N", out _)
            || journal.Entries is null || journal.Entries.Count is 0 or > MaximumFiles * 2
            || journal.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Entries.Count)
            throw new InvalidDataException("Invalid portable update journal.");
        foreach (var entry in journal.Entries)
        {
            ValidateOwnedPath(entry.Path);
            if (UpdatePathSafety.Relative(entry.Path) != entry.Path || (entry.OldSha256 is null && entry.NewSha256 is null))
                throw new InvalidDataException("Invalid portable update journal entry.");
            foreach (var hash in new[] { entry.OldSha256, entry.NewSha256 })
                if (hash is not null && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) throw new InvalidDataException("Invalid update journal hash.");
            if ((entry.OldSha256 is null) != (entry.OldIdentity is null) || (entry.NewSha256 is null) != (entry.NewIdentity is null))
                throw new InvalidDataException("Invalid update journal identity.");
        }
    }

    private static void CleanupDirectory(string root, IEnumerable<string> ownedPaths, bool removeFiles = true)
    {
        if (!Directory.Exists(root)) return;
        using var pins = new UpdatePathSafety.Pins();
        pins.Directory(root);
        foreach (var path in ownedPaths)
        {
            var full = UpdatePathSafety.Under(root, path);
            if (File.Exists(full))
            {
                if (!removeFiles) throw Conflict(path);
                pins.Parents(full); using var file = new UpdatePathSafety.LockedFile(full); file.Delete();
            }
        }
        var directories = ownedPaths.SelectMany(p =>
        {
            var parts = p.Split('/'); return Enumerable.Range(1, parts.Length - 1).Select(n => string.Join('/', parts.Take(n)));
        }).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length);
        foreach (var directory in directories)
        {
            var full = UpdatePathSafety.Under(root, directory);
            if (Directory.Exists(full))
            {
                pins.Parents(full);
                UpdatePathSafety.AssertRegular(full, true);
                pins.Release(full);
                Directory.Delete(full);
            }
        }
        UpdatePathSafety.AssertRegular(root, true);
        pins.Release(root);
        Directory.Delete(root);
    }

    internal sealed record Entry(string Path, string? OldSha256, string? OldIdentity, string? NewSha256, string? NewIdentity);
    internal sealed record Journal(int Schema, string Target, string Operation, bool Committed, List<Entry> Entries);
}
