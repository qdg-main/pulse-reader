using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using PulseReader.Setup;
using PulseReader.Updates;

internal static class UpdateHandoffChecks
{
    public static async Task RunAsync()
    {
        using var fixture = new Fixture();
        await CheckLeaseAsync(fixture);
        await CheckPipeAsync(fixture);
        await CheckFramingAsync();
        CheckMetadata(fixture);
        CheckShadowCopy(fixture);
        CheckPortableTerms();
        CheckInstallerArguments(fixture);
        CheckRecoveryMetadataAndLog(fixture);
        CheckOriginalInstallerSnapshot(fixture);
        Console.WriteLine("PASS: update handoff checks cover live CurrentUserOnly IPC peer identity, bounded framing, no-write/no-delete package leases and ancestor pins, release/path/terms rejection, bounded helper ownership copy, portable terms and scope-specific installer arguments without launching applications or installers.");
    }

    private static async Task CheckLeaseAsync(Fixture fixture)
    {
        var package = fixture.Update;
        using (var lease = await UpdateFileLease.OpenAsync(package.AssetPath, package.Offer.Asset.Length, package.Offer.Asset.Sha256))
        {
            Require(lease.Stream.CanRead, "Verified package must remain readable.");
            Fails<IOException>(() => { using var file = new FileStream(package.AssetPath, FileMode.Open, FileAccess.Write, FileShare.Read); });
            Fails<IOException>(() => File.Delete(package.AssetPath));
            Fails<IOException>(() => Directory.Move(package.OperationDirectory, package.OperationDirectory + "-moved"));
        }
        using (var writable = new FileStream(package.AssetPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            Require(writable.CanWrite, "Package lock must release on disposal.");
        await FailsAsync<InvalidDataException>(async () =>
        { using var lease = await UpdateFileLease.OpenAsync(package.AssetPath, package.Offer.Asset.Length + 1, package.Offer.Asset.Sha256); });
        await FailsAsync<InvalidDataException>(async () =>
        { using var lease = await UpdateFileLease.OpenAsync(package.AssetPath, package.Offer.Asset.Length, new string('A', 64)); });
    }

    private static async Task CheckPipeAsync(Fixture fixture)
    {
        var name = "PulseReader.Update." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connection = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connection;
        UpdateHandoffProtocol.RequirePeer(server, Environment.ProcessId, server: false);
        UpdateHandoffProtocol.RequirePeer(client, Environment.ProcessId, server: true);
        Fails<UnauthorizedAccessException>(() => UpdateHandoffProtocol.RequirePeer(server, int.MaxValue, server: false));
        var packet = new UpdateHandoffPacket(fixture.Update, fixture.Update.Offer.TermsSha256);
        var read = UpdateHandoffProtocol.ReadAsync<UpdateHandoffPacket>(client, timeout.Token);
        await UpdateHandoffProtocol.WriteAsync(server, packet, timeout.Token);
        Require(await read == packet, "Prepared Version, Uri and accepted terms must survive bounded JSON IPC.");
        var ready = UpdateHandoffProtocol.ReadAsync<UpdateHandoffReply>(server, timeout.Token);
        await UpdateHandoffProtocol.WriteAsync(client, new UpdateHandoffReply("READY", null), timeout.Token);
        Require((await ready).Status == "READY", "Helper readiness must travel in the reverse direction.");
    }

    private static async Task CheckFramingAsync()
    {
        foreach (var length in new[] { -1, 0, 1024 * 1024 + 1 })
        {
            using var stream = new MemoryStream(BitConverter.GetBytes(length));
            await FailsAsync<InvalidDataException>(() => UpdateHandoffProtocol.ReadAsync<UpdateHandoffReply>(stream, default));
        }
        using var truncated = new MemoryStream(BitConverter.GetBytes(10).Concat(new byte[2]).ToArray());
        await FailsAsync<EndOfStreamException>(() => UpdateHandoffProtocol.ReadAsync<UpdateHandoffReply>(truncated, default));
    }

    private static void CheckMetadata(Fixture fixture)
    {
        var original = fixture.Update;
        void Validate(PreparedUpdate update) => UpdateHandoffProtocol.Validate(update, original.Offer.TermsSha256, fixture.OperationsRoot);
        Validate(original);
        Fails<InvalidDataException>(() => Validate(original with { OperationDirectory = fixture.Root }));
        Fails<InvalidDataException>(() => Validate(original with { AssetPath = Path.Combine(fixture.Root, original.Offer.Asset.Name) }));
        Fails<InvalidDataException>(() => Validate(original with
        { Offer = original.Offer with { Asset = original.Offer.Asset with { DownloadUri = new Uri("https://example.com/package.exe") } } }));
        Fails<InvalidDataException>(() => Validate(original with
        { Offer = original.Offer with { TermsText = "Changed without a matching hash" } }));
        Fails<InvalidDataException>(() => UpdateHandoffProtocol.Validate(original, new string('A', 64), fixture.OperationsRoot));
        Fails<InvalidDataException>(() => Validate(original with
        { Offer = original.Offer with { Version = original.Installation.Version } }));
        Fails<InvalidDataException>(() => Validate(original with
        { Installation = original.Installation with { Variant = "win-arm64" } }));
    }

    private static void CheckShadowCopy(Fixture fixture)
    {
        var source = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(source);
        string[] paths = ["PulseReader.exe", "PulseReader.dll", "dependency-inventory.json", "payload-files.txt", "runtimes/win-x64/native/runtime.dll"];
        foreach (var path in paths)
        {
            var target = Path.Combine(source, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, path);
        }
        File.WriteAllLines(Path.Combine(source, "payload-files.txt"), paths);
        File.WriteAllText(Path.Combine(source, "settings.json"), "User state is excluded.");
        File.WriteAllText(Path.Combine(source, "unins000.exe"), "Uninstaller is excluded.");
        var operation = Path.Combine(fixture.Root, "copy-op");
        Directory.CreateDirectory(operation);
        using (var shadow = UpdateShadowPayload.Copy(source, operation, default))
        using (var childPins = UpdateShadowPayload.Pin(shadow.Root))
        {
            Require(File.Exists(Path.Combine(shadow.Root, paths[4])), "Owned runtime files must accompany a self-contained helper.");
            Require(!File.Exists(Path.Combine(shadow.Root, "settings.json")), "Unlisted user state must not be copied.");
            Require(!File.Exists(Path.Combine(shadow.Root, "unins000.exe")), "Uninstaller must not be copied.");
            Fails<IOException>(() => File.Delete(Path.Combine(shadow.Root, "PulseReader.exe")));
        }
        File.WriteAllLines(Path.Combine(source, "payload-files.txt"), paths.Append("settings.json"));
        var denied = Path.Combine(fixture.Root, "denied-copy");
        Directory.CreateDirectory(denied);
        Fails<InvalidDataException>(() => { using var shadow = UpdateShadowPayload.Copy(source, denied, default); });
        File.WriteAllLines(Path.Combine(source, "payload-files.txt"), paths.Append("../escape.exe"));
        Fails<InvalidDataException>(() => { using var shadow = UpdateShadowPayload.Copy(source, denied, default); });
    }

    private static void CheckPortableTerms()
    {
        using var stream = new MemoryStream();
        const string terms = "Terms the original user accepted.";
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("Distribution-Terms.txt").Open(), new UTF8Encoding(false)))
            writer.Write(terms);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terms)));
        UpdateHelperRunner.VerifyPortableTerms(stream, hash);
        Require(stream.Position == 0, "Portable verification must rewind the pinned package.");
        Fails<InvalidDataException>(() => UpdateHelperRunner.VerifyPortableTerms(stream, new string('A', 64)));
    }

    private static void CheckInstallerArguments(Fixture fixture)
    {
        foreach (var scope in new[] { InstallationScope.User, InstallationScope.Machine })
        {
            var update = fixture.Update with { Installation = fixture.Update.Installation with { Scope = scope, Kind = UpdatePackageKind.Installer } };
            var start = UpdateHelperRunner.InstallerStartInfo(update, update.Offer.TermsSha256);
            var machine = scope == InstallationScope.Machine;
            Require(start.UseShellExecute == machine && start.Verb == (machine ? "runas" : ""), "Only the machine installer may request UAC.");
            Require(start.ArgumentList.Contains(machine ? "/ALLUSERS" : "/CURRENTUSER"), "Installer must retain the original installation scope.");
            Require(start.ArgumentList.Contains("/PULSEREADERUPDATE=1")
                && start.ArgumentList.Contains("/UPDATEACCEPTEDTERMS=" + update.Offer.TermsSha256)
                && start.ArgumentList.Contains("/NORESTART")
                && start.ArgumentList.Contains("/RESTARTEXITCODE=3010")
                && !start.ArgumentList.Any(a => a.StartsWith("/DIR", StringComparison.OrdinalIgnoreCase)),
                "Installer guard, exact consent and canonical destination are mandatory.");
        }
    }

    private static void CheckRecoveryMetadataAndLog(Fixture fixture)
    {
        var packet = new UpdateHandoffPacket(fixture.Update, fixture.Update.Offer.TermsSha256);
        var record = UpdateRecoveryRecord.Create(fixture.Update.OperationDirectory, packet);
        using (var input = new UpdatePathSafety.LockedFile(Path.Combine(fixture.Update.OperationDirectory, "recovery.json"), movable: false))
        {
            Require(UpdateRecoveryRecord.Read(input.Stream, record.Nonce) == record,
                "Retained recovery metadata must bind the original packet and capability.");
            input.Stream.Position = 0;
            Fails<UnauthorizedAccessException>(() => UpdateRecoveryRecord.Read(input.Stream, Guid.NewGuid().ToString("N")));
        }
        Fails<IOException>(() => UpdateRecoveryRecord.Create(fixture.Update.OperationDirectory, packet));
        var log = Path.Combine(fixture.Update.OperationDirectory, "fixture.log");
        UpdateOperationLog.Append(log, "First fixture record.");
        UpdateOperationLog.Append(log, "Second fixture record.");
        Require(File.ReadAllLines(log).Length == 2, "Operation log must append records to a regular file.");
        Require(UpdateHelperRunner.IsHelperInvocation(["--recover-update", record.Nonce]),
            "Recovery must route before normal application state, mutex and consent initialization.");
    }

    private static void CheckOriginalInstallerSnapshot(Fixture fixture)
    {
        var app = Path.Combine(fixture.Root, "snapshot-app");
        var operation = Path.Combine(fixture.Root, "snapshot-operation");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(operation);
        string[] owned = ["PulseReader.exe", "PulseReader.dll", "dependency-inventory.json", "payload-files.txt",
            "Distribution-Terms.txt", "distribution-terms.json", "runtimes/native/runtime.dll"];
        foreach (var relative in owned)
        {
            var path = Path.Combine(app, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Original owned fixture bytes: " + relative);
        }
        File.WriteAllLines(Path.Combine(app, "payload-files.txt"), owned);
        string[] metadata = [InstallationLayout.ScopeFileName, "unins000.exe", "unins000.dat"];
        foreach (var relative in metadata) File.WriteAllText(Path.Combine(app, relative), "Original metadata: " + relative);
        File.WriteAllText(Path.Combine(app, "unknown-user-file.txt"), "Unowned user bytes must not be read for payload comparison.");
        var model = Path.Combine(app, "Models", "large-user-model.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllText(model, "User model excluded from namespace traversal.");
        using (var copied = UpdateShadowPayload.Copy(app, operation, default)) { }
        using var shadow = UpdateShadowPayload.Pin(Path.Combine(operation, "helper"));
        using var snapshot = UpdateOriginalInstallationSnapshot.Capture(app, shadow.Root);
        using (var coherent = snapshot.VerifyOriginalAndLease())
        {
            Fails<IOException>(() => File.Delete(Path.Combine(app, "PulseReader.dll")));
            Fails<IOException>(() => Directory.Move(Path.Combine(app, "runtimes"), Path.Combine(app, "runtimes-moved")));
        }
        var updatedVerificationCalled = false;
        var earlyRejected = snapshot.SelectFailureLease(installerCompleted: false, () =>
        { updatedVerificationCalled = true; return new MemoryStream(); });
        using (earlyRejected.Lease)
            Require(!earlyRejected.Updated && !updatedVerificationCalled,
                "A failed installer must only consider the exact original snapshot, even if new identity metadata appears valid.");
        var partialNewPaths = new[] { "PulseReader.dll", "dependency-inventory.json", "Distribution-Terms.txt", "runtimes/native/runtime.dll" };
        var originalBytes = partialNewPaths.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(app, path)));
        foreach (var relative in partialNewPaths)
            File.WriteAllText(Path.Combine(app, relative), "Partial new installer bytes: " + relative);
        Fails<InvalidDataException>(() =>
        {
            var mixed = snapshot.SelectFailureLease(installerCompleted: false, () =>
            { updatedVerificationCalled = true; return new MemoryStream(); });
            mixed.Lease.Dispose();
        });
        Require(!updatedVerificationCalled, "Nonzero installer completion must never authorize new metadata or a changed dependency.");
        foreach (var (relative, bytes) in originalBytes) File.WriteAllBytes(Path.Combine(app, relative), bytes);
        foreach (var relative in owned.Concat(metadata))
        {
            var path = Path.Combine(app, relative);
            var original = File.ReadAllBytes(path);
            File.WriteAllBytes(path, original.Select(value => (byte)(value ^ 1)).ToArray());
            Fails<InvalidDataException>(() => { using var coherent = snapshot.VerifyOriginalAndLease(); });
            File.WriteAllBytes(path, original);
            File.Delete(path);
            Fails<IOException>(() => { using var coherent = snapshot.VerifyOriginalAndLease(); });
            File.WriteAllBytes(path, original);
        }
        var added = Path.Combine(app, "injected-new-runtime.dll");
        File.WriteAllText(added, "A failed installer must not add an unowned loadable binary.");
        Fails<InvalidDataException>(() => { using var coherent = snapshot.VerifyOriginalAndLease(); });
        File.Delete(added);
        File.WriteAllText(model, "User models may change independently without being hashed by this comparison.");
        using var final = snapshot.VerifyOriginalAndLease();
        Require(snapshot.OriginalTermsSha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(app, "Distribution-Terms.txt")))),
            "Original terms must be bound to the exact original owned file.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Fails<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task FailsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PulseReader-handoff-checks-" + Guid.NewGuid().ToString("N"));
        internal string OperationsRoot => Path.Combine(Root, "Updates");
        internal PreparedUpdate Update { get; }
        internal Fixture()
        {
            var operation = Path.Combine(OperationsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operation);
            var installation = new UpdateInstallation(new Version(1, 0, 0), "win-x64", UpdatePackageKind.Portable,
                InstallationScope.User, Path.Combine(Root, "app"));
            const string terms = "Fixture distribution terms.";
            var name = UpdateVersionPolicy.AssetName(new Version(1, 1, 0), installation.Variant, installation.Kind);
            var path = Path.Combine(operation, name);
            byte[] bytes = [1, 2, 3, 4, 5];
            File.WriteAllBytes(path, bytes);
            var offer = new UpdateOffer(1, new Version(1, 1, 0),
                new Uri($"https://github.com/{UpdateConstants.RepositoryPath}/releases/tag/v1.1.0"),
                new UpdateAsset(1, name, new Uri($"https://github.com/{UpdateConstants.RepositoryPath}/releases/download/v1.1.0/{name}"),
                    bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terms))), terms);
            Update = new(installation, offer, operation, path);
        }
        public void Dispose() { Directory.Delete(Root, recursive: true); }
    }
}
