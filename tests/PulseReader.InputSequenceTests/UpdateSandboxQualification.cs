using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PulseReader.Setup;
using PulseReader.Updates;

// This entry point exists only in the test assembly. The production executable
// does not acquire fixture behavior or interruption switches.
internal static class UpdateSandboxQualification
{
    private const string Marker = "update-qualification-fixture.json";

    internal static async Task<int?> TryRunAsync(string[] args)
    {
        var marked = File.Exists(Path.Combine(AppContext.BaseDirectory, Marker));
        var controller = args.Length == 4 && args[0] is "--sandbox-update-controller" or "--sandbox-installer-controller";
        if (!marked && !controller) return null;
        AssertGuest();
        if (controller)
        {
            if (args[0] == "--sandbox-installer-controller")
                await RunInstallerControllerAsync(args[1], args[2], args[3]);
            else await RunControllerAsync(args[1], args[2], args[3]);
            return 0;
        }
        if (UpdateHelperRunner.IsHelperInvocation(args))
            return await UpdateHelperRunner.RunHelperAsync(args);
        if (args.Length == 2 && args[0] == "--sandbox-update-parent")
        {
            await RunParentAsync(args[1]);
            return 0;
        }
        if (args.Length == 0)
        {
            var root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            var version = FileVersionInfo.GetVersionInfo(Path.Combine(root, "PulseReader.dll")).FileVersion;
            var outcome = new { ProcessId = Environment.ProcessId, ProcessPath = Environment.ProcessPath,
                Root = root, Version = version, NormalUser = !IsAdministrator(), Utc = DateTimeOffset.UtcNow };
            File.WriteAllText(Path.Combine(root, "relaunch-observed.json"), JsonSerializer.Serialize(outcome));
            return 0;
        }
        throw new InvalidOperationException("Unsupported disposable update fixture invocation.");
    }

    private static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    private static void AssertGuest()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (root != "C:\\" || Environment.UserName != "PulseUpdateFixture"
            || !Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .StartsWith("C:\\Users\\PulseUpdateFixture", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(@"C:\PulseReaderUpdateQualification\guest-only.marker") || IsAdministrator())
            throw new UnauthorizedAccessException("Live update qualification requires the disposable Sandbox standard account.");
    }

    private static async Task RunParentAsync(string requestPath)
    {
        var request = Read<ParentRequest>(requestPath);
        AssertFixtureRoot(request.ApplicationRoot, request.Kind);
        if (!request.ApplicationRoot.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The qualification parent must run from its exact application root.");
        var update = Prepare(request.ApplicationRoot, request.PackagePath, request.Kind, request.TermsPath);
        File.WriteAllText(Path.Combine(request.ApplicationRoot, "operation-observed.json"), JsonSerializer.Serialize(new
        { update.OperationDirectory, ParentPid = Environment.ProcessId, ParentPath = Environment.ProcessPath }));
        var ready = await new UpdateHandoff().StartAsync(update, update.Offer.TermsSha256);
        if (!ready) throw new InvalidOperationException("The live helper did not accept handoff.");
        // Returning to the process boundary is a graceful parent exit. No fixture
        // code kills the parent/helper or impersonates the actual updater.
        File.WriteAllText(Path.Combine(request.ApplicationRoot, "ready-observed.json"), JsonSerializer.Serialize(new
        { Ready = true, ParentPid = Environment.ProcessId, Utc = DateTimeOffset.UtcNow }));
    }

    private static PreparedUpdate Prepare(string root, string packagePath,
        UpdatePackageKind kind = UpdatePackageKind.Portable, string? termsPath = null)
    {
        if (!UpdateInstallationDiscovery.TryRead(root, new Version(1, 0, 0), out var installation, out var reason))
            throw new InvalidDataException(reason);
        if (installation!.Kind != kind) throw new InvalidDataException("Qualification package kind does not match the installed fixture.");
        var operation = UpdateHandoff.CreateOperationDirectory();
        var name = UpdateVersionPolicy.AssetName(new Version(1, 0, 1), "win-x64", kind);
        var destination = Path.Combine(operation, name);
        File.Copy(packagePath, destination, overwrite: false);
        string terms;
        if (kind == UpdatePackageKind.Portable)
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(destination);
            using var reader = new StreamReader(zip.GetEntry("Distribution-Terms.txt")!.Open(), new UTF8Encoding(false, true));
            terms = reader.ReadToEnd();
        }
        else terms = File.ReadAllText(termsPath ?? throw new InvalidDataException("Installer fixture terms are required."), new UTF8Encoding(false, true));
        var bytesHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)));
        var termsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terms)));
        var offer = new UpdateOffer(1, new Version(1, 0, 1),
            new Uri($"https://github.com/{UpdateConstants.RepositoryPath}/releases/tag/v1.0.1"),
            new UpdateAsset(1, name, new Uri($"https://github.com/{UpdateConstants.RepositoryPath}/releases/download/v1.0.1/{name}"),
                new FileInfo(destination).Length, bytesHash), termsHash, terms);
        return new(installation!, offer, operation, destination);
    }

    private static async Task RunControllerAsync(string oldPayload, string packagePath, string reports)
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<string>();
        var appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseReaderUpdateQualification", "app");
        AssertFixtureRoot(appRoot);
        Directory.CreateDirectory(reports);
        void Stage(string stage) => File.WriteAllText(Path.Combine(reports, "stage.json"), JsonSerializer.Serialize(new
        { Stage = stage, Utc = DateTimeOffset.UtcNow, PassedCases = cases }));
        void Pass(string value) { cases.Add(value); Stage(value); }
        try
        {
            Stage("Preparing live parent and preserved data");
            CopyPayload(oldPayload, appRoot);
            var sentinels = SeedPreservedData(appRoot);
            var request = Path.Combine(reports, "parent-request.json");
            File.WriteAllText(request, JsonSerializer.Serialize(new ParentRequest(appRoot, packagePath)));
            using var parent = Start(Path.Combine(appRoot, "PulseReader.exe"), "--sandbox-update-parent", request);
            await WaitExitAsync(parent, TimeSpan.FromMinutes(3));
            Require(parent.ExitCode == 0, "The live parent must exit cleanly after authenticated READY.");
            Require(File.Exists(Path.Combine(appRoot, "ready-observed.json")), "Parent exited without recording authenticated READY.");
            var operationJson = Read<JsonElement>(Path.Combine(appRoot, "operation-observed.json"));
            var operation = operationJson.GetProperty("OperationDirectory").GetString()!;
            Require(Path.GetDirectoryName(operation)!.Equals(UpdateHandoff.OperationsRoot, StringComparison.OrdinalIgnoreCase),
                "The helper must run from its private operation GUID.");
            var recordPath = Path.Combine(operation, "recovery.json");
            var recovery = Read<UpdateRecoveryRecord>(recordPath);
            await WaitForFileAsync(Path.Combine(appRoot, "relaunch-observed.json"), TimeSpan.FromMinutes(5));
            await WaitNoFixtureApplicationsAsync(appRoot, operation);
            var relaunch = Read<JsonElement>(Path.Combine(appRoot, "relaunch-observed.json"));
            Require(relaunch.GetProperty("NormalUser").GetBoolean(), "The helper must relaunch as the original normal user.");
            Require(relaunch.GetProperty("Version").GetString() == "1.0.1.0", "The real replacement DLL must be version 1.0.1.");
            Require(UpdateInstallationDiscovery.TryRead(appRoot, new Version(1, 0, 1), out var updated, out _)
                && updated!.Variant == "win-x64" && updated.Kind == UpdatePackageKind.Portable, "Post-update identity mismatch.");
            VerifyVersionAndPayloadHash(appRoot, packagePath, "PulseReader.dll", new Version(1, 0, 1));
            Require(new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256() == recovery.Packet.AcceptedTermsSha256,
                "Only confirmed new terms must receive the original user's receipt.");
            AssertSentinels(sentinels);
            var log = File.ReadAllText(Path.Combine(operation, "update-helper.log"));
            Require(log.Contains("Package and original application verified") && log.Contains("Update confirmed")
                && log.Contains("--recover-update " + recovery.Nonce), "The live helper did not record verification, success and retained recovery.");
            File.Copy(Path.Combine(operation, "update-helper.log"), Path.Combine(reports, "live-helper.log"));
            File.Copy(recordPath, Path.Combine(reports, "retained-recovery.json"));
            Pass("Real shadow helper and graceful parent exit replaced 1.0.0 with 1.0.1 and relaunched normally");
            Pass("Real package bytes, accepted terms receipt and user sentinels verified");

            Stage("Creating a retained interrupted portable journal");
            // After every parent/helper/relaunch process has exited, reset only
            // the known fixture payload. User sentinels are retained throughout.
            CopyPayload(oldPayload, appRoot, overwrite: true);
            File.Delete(Path.Combine(appRoot, "relaunch-observed.json"));
            Require(FileVersionInfo.GetVersionInfo(Path.Combine(appRoot, "PulseReader.dll")).FileVersion == "1.0.0.0",
                "Recovery fixture did not restore the old real DLL.");
            var interrupted = false;
            try
            {
                await new PortableUpdateTransaction(checkpoint =>
                {
                    if (checkpoint == "BackedUp:PulseReader.dll") throw new PortableUpdateInterruptionException();
                }).ApplyAsync(recovery.Packet.Update, default);
            }
            catch (PortableUpdateInterruptionException) { interrupted = true; }
            Require(interrupted && File.Exists(PortableUpdateTransaction.JournalPath(appRoot))
                && !File.Exists(Path.Combine(appRoot, "PulseReader.dll")), "The interrupted journal must leave the live DLL unavailable for standalone recovery.");
            AssertSentinels(sentinels);
            File.Copy(PortableUpdateTransaction.JournalPath(appRoot), Path.Combine(reports, "interrupted-journal.json"));
            Pass("A retained real transaction journal and backup survived an abrupt-interruption checkpoint");

            Stage("Running retained standalone recovery helper");
            using var helper = Start(Path.Combine(operation, "helper", "PulseReader.exe"), "--recover-update", recovery.Nonce);
            await WaitExitAsync(helper, TimeSpan.FromMinutes(5));
            Require(helper.ExitCode == 0, "Standalone retained recovery helper failed.");
            await WaitForFileAsync(Path.Combine(appRoot, "relaunch-observed.json"), TimeSpan.FromMinutes(1));
            await WaitNoFixtureApplicationsAsync(appRoot, operation);
            relaunch = Read<JsonElement>(Path.Combine(appRoot, "relaunch-observed.json"));
            Require(relaunch.GetProperty("Version").GetString() == "1.0.0.0" && relaunch.GetProperty("NormalUser").GetBoolean(),
                "Uncommitted journal recovery must restore and relaunch the coherent old real DLL.");
            Require(!File.Exists(PortableUpdateTransaction.JournalPath(appRoot)), "Recovered journal was not cleaned up.");
            VerifyVersionAndPayloadHash(appRoot, oldPayload, "PulseReader.dll", new Version(1, 0, 0));
            AssertSentinels(sentinels);
            File.Copy(Path.Combine(operation, "update-helper.log"), Path.Combine(reports, "recovery-helper.log"));
            Pass("Actual retained helper recovered an unavailable application and relaunched 1.0.0 normally");
            File.WriteAllText(Path.Combine(reports, "result.json"), JsonSerializer.Serialize(new
            {
                Passed = true, GuestNormalUser = Environment.UserName, Utc = DateTimeOffset.UtcNow,
                ElapsedSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds, Cases = cases,
                AppRoot = appRoot, OperationDirectory = operation,
                QualificationLimit = "Uses a test apphost and synthetic fixed-repository metadata; no GitHub requests, real UI, installer or physical power loss."
            }));
        }
        catch (Exception error)
        {
            ExportFailureEvidence(appRoot, reports);
            File.WriteAllText(Path.Combine(reports, "result.json"), JsonSerializer.Serialize(new
            { Passed = false, Error = error.ToString(), Cases = cases, Utc = DateTimeOffset.UtcNow, AppRoot = appRoot }));
            throw;
        }
    }

    private static async Task RunInstallerControllerAsync(string initialSetup, string updateSetup, string reports)
    {
        var started = DateTimeOffset.UtcNow;
        var cases = new List<string>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PulseReader");
        AssertFixtureRoot(root, UpdatePackageKind.Installer);
        var inputs = Path.GetDirectoryName(initialSetup)!;
        var oldPayload = Path.Combine(inputs, "OldPayload");
        var newPayload = Path.Combine(inputs, "NewPayload");
        var termsPath = Path.Combine(inputs, "UpdateTerms.txt");
        var oldTermsHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(oldPayload, "Distribution-Terms.txt"))));
        var newTermsHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(termsPath)));
        Directory.CreateDirectory(reports);
        void Stage(string value) => File.WriteAllText(Path.Combine(reports, "stage.json"), JsonSerializer.Serialize(new
        { Stage = value, Utc = DateTimeOffset.UtcNow, PassedCases = cases }));
        void Pass(string value) { cases.Add(value); Stage(value); }
        try
        {
            Stage("Installing synthetic 1.0.0 as the disposable standard user");
            Require(!Directory.Exists(root), "The installer qualification requires a fresh canonical user target.");
            using (var initial = Start(initialSetup, "/CURRENTUSER", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                "/ACCEPTDISTRIBUTIONTERMS=yes", "/LOG=" + Path.Combine(reports, "initial-install.log")))
            {
                await WaitExitAsync(initial, TimeSpan.FromMinutes(5));
                Require(initial.ExitCode == 0, "Initial per-user installer failed.");
            }
            await WaitForFileAsync(Path.Combine(root, "unins000.dat"), TimeSpan.FromMinutes(2));
            Require(UpdateInstallationDiscovery.TryRead(root, new Version(1, 0, 0), out var installation, out var reason)
                && installation!.Kind == UpdatePackageKind.Installer && installation.Scope == InstallationScope.User
                && installation.Variant == "win-x64", "Initial canonical per-user identity is invalid: " + reason);
            VerifyVersionAndPayloadHash(root, oldPayload, "PulseReader.dll", new Version(1, 0, 0));
            Require(new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256() == oldTermsHash,
                "Initial per-user installer did not record exact old terms in the disposable user's HKCU.");
            var sentinels = SeedPreservedData(root);
            var before = SnapshotFiles(root);
            Pass("Initial per-user installer produced the canonical user target and real 1.0.0 identity");

            Stage("Rejecting wrong update terms before any old payload changes");
            using (var rejected = Start(updateSetup, "/CURRENTUSER", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                "/PULSEREADERUPDATE=1", "/UPDATEACCEPTEDTERMS=" + new string('A', 64),
                "/LOG=" + Path.Combine(reports, "rejected-update.log")))
            {
                await WaitExitAsync(rejected, TimeSpan.FromMinutes(2));
                Require(rejected.ExitCode != 0, "Installer accepted an incorrect update terms hash.");
            }
            var after = SnapshotFiles(root);
            Require(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value),
                "Rejected update changed an original installation or user file.");
            Require(new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256() == oldTermsHash,
                "Rejected update changed the original terms receipt.");
            Require(File.ReadAllText(Path.Combine(reports, "rejected-update.log"))
                .Contains("Update installation requires acceptance of the exact new distribution terms."),
                "Wrong-hash rejection was not attributed to the compiled production installer guard.");
            Pass("Incorrect update terms were rejected before payload, uninstaller, scope or user receipt changed");

            Stage("Driving compiled terms rejection through the actual helper and reopening unchanged 1.0.0");
            var inconsistentTerms = Path.Combine(reports, "accepted-inconsistent-offer-terms.txt");
            File.WriteAllText(inconsistentTerms, "Synthetic accepted offer terms deliberately differ from the compiled installer terms.\n", new UTF8Encoding(false));
            var request = Path.Combine(reports, "parent-request.json");
            File.WriteAllText(request, JsonSerializer.Serialize(new ParentRequest(root, updateSetup,
                UpdatePackageKind.Installer, inconsistentTerms)));
            using (var parent = Start(Path.Combine(root, "PulseReader.exe"), "--sandbox-update-parent", request))
            {
                await WaitExitAsync(parent, TimeSpan.FromMinutes(3));
                Require(parent.ExitCode == 0, "Guard-failure parent did not gracefully exit after READY.");
            }
            var failedObserved = Read<JsonElement>(Path.Combine(root, "operation-observed.json"));
            var failedOperation = failedObserved.GetProperty("OperationDirectory").GetString()!;
            await WaitForFileAsync(Path.Combine(root, "relaunch-observed.json"), TimeSpan.FromMinutes(3));
            await WaitNoFixtureApplicationsAsync(root, failedOperation);
            var restored = Read<JsonElement>(Path.Combine(root, "relaunch-observed.json"));
            Require(restored.GetProperty("NormalUser").GetBoolean() && restored.GetProperty("Version").GetString() == "1.0.0.0",
                "Early installer rejection did not reopen the exact original build normally.");
            after = SnapshotFiles(root);
            Require(before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value)
                && after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase).All(path =>
                    path is "operation-observed.json" or "ready-observed.json" or "relaunch-observed.json"),
                "Helper guard rejection changed original payload/user bytes or added unexpected install files.");
            Require(new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256() == oldTermsHash,
                "Helper guard rejection persisted inconsistent accepted offer terms.");
            var failedLog = File.ReadAllText(Path.Combine(failedOperation, "update-helper.log"));
            Require(failedLog.Contains("every original owned payload, scope marker and uninstaller byte unchanged")
                && File.ReadAllText(Path.Combine(failedOperation, "install.log"))
                    .Contains("Update installation requires acceptance of the exact new distribution terms."),
                "The production helper did not attribute rejection and verify the complete unchanged original install.");
            File.Copy(Path.Combine(failedOperation, "update-helper.log"), Path.Combine(reports, "guard-failure-helper.log"));
            File.Copy(Path.Combine(failedOperation, "install.log"), Path.Combine(reports, "guard-failure-install.log"));
            AssertSentinels(sentinels);
            Pass("Actual helper rejected inconsistent compiled terms and relaunched exact unchanged 1.0.0 normally");
            File.Delete(Path.Combine(root, "relaunch-observed.json"));

            Stage("Applying 1.0.1 through the real authenticated shadow helper");
            File.WriteAllText(request, JsonSerializer.Serialize(new ParentRequest(root, updateSetup,
                UpdatePackageKind.Installer, termsPath)));
            using (var parent = Start(Path.Combine(root, "PulseReader.exe"), "--sandbox-update-parent", request))
            {
                await WaitExitAsync(parent, TimeSpan.FromMinutes(3));
                Require(parent.ExitCode == 0, "Installed parent did not exit cleanly after READY.");
            }
            Require(File.Exists(Path.Combine(root, "ready-observed.json")), "Installed parent did not record READY.");
            var observed = Read<JsonElement>(Path.Combine(root, "operation-observed.json"));
            var operation = observed.GetProperty("OperationDirectory").GetString()!;
            await WaitForFileAsync(Path.Combine(root, "relaunch-observed.json"), TimeSpan.FromMinutes(5));
            await WaitNoFixtureApplicationsAsync(root, operation);
            var relaunched = Read<JsonElement>(Path.Combine(root, "relaunch-observed.json"));
            Require(relaunched.GetProperty("NormalUser").GetBoolean()
                && relaunched.GetProperty("Version").GetString() == "1.0.1.0",
                "Installer helper did not relaunch the real new DLL as the original normal user.");
            Require(UpdateInstallationDiscovery.TryRead(root, new Version(1, 0, 1), out var updated, out reason)
                && updated!.Kind == UpdatePackageKind.Installer && updated.Scope == InstallationScope.User
                && updated.Variant == "win-x64", "Updated canonical user identity is invalid: " + reason);
            VerifyVersionAndPayloadHash(root, newPayload, "PulseReader.dll", new Version(1, 0, 1));
            Require(new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256() == newTermsHash,
                "Confirmed installer update did not preserve acceptance of the exact new terms for the original user.");
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "Distribution-Terms.txt")))) == newTermsHash,
                "Installed distribution terms differ from the accepted installer offer.");
            UpdatePathSafety.AssertRegular(Path.Combine(root, "unins000.exe"), false);
            UpdatePathSafety.AssertRegular(Path.Combine(root, "unins000.dat"), false);
            AssertSentinels(sentinels);
            var log = File.ReadAllText(Path.Combine(operation, "update-helper.log"));
            Require(log.Contains("Starting the installer") && log.Contains("Update confirmed"),
                "Live installer helper did not confirm replacement and receipt persistence.");
            File.Copy(Path.Combine(operation, "update-helper.log"), Path.Combine(reports, "live-helper.log"));
            File.Copy(Path.Combine(operation, "install.log"), Path.Combine(reports, "helper-install.log"));
            Pass("Production helper upgraded the same canonical user install from real 1.0.0 to 1.0.1 and relaunched normally");
            Pass("New inventory, scope marker, uninstaller, exact DLL/terms, HKCU receipt and user sentinels verified");
            File.WriteAllText(Path.Combine(reports, "result.json"), JsonSerializer.Serialize(new
            {
                Passed = true, Kind = "Installer", GuestNormalUser = Environment.UserName, Utc = DateTimeOffset.UtcNow,
                ElapsedSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds, Cases = cases,
                AppRoot = root, OperationDirectory = operation,
                QualificationLimit = "Synthetic Inno packages use the shipping installer script and a test apphost; no HTTP, real UI, machine install, UAC or alternative credentials."
            }));
        }
        catch (Exception error)
        {
            ExportFailureEvidence(root, reports);
            File.WriteAllText(Path.Combine(reports, "result.json"), JsonSerializer.Serialize(new
            { Passed = false, Kind = "Installer", Error = error.ToString(), Cases = cases, Utc = DateTimeOffset.UtcNow, AppRoot = root }));
            throw;
        }
    }

    private static Dictionary<string, string> SnapshotFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            StringComparer.OrdinalIgnoreCase);

    private static void ExportFailureEvidence(string root, string reports)
    {
        try
        {
            var observedPath = Path.Combine(root, "operation-observed.json");
            if (!File.Exists(observedPath)) return;
            File.Copy(observedPath, Path.Combine(reports, "failed-operation.json"), overwrite: true);
            var observed = Read<JsonElement>(observedPath);
            var operation = observed.GetProperty("OperationDirectory").GetString()!;
            if (!Path.GetDirectoryName(operation)!.Equals(UpdateHandoff.OperationsRoot, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(operation), "N", out _)) return;
            foreach (var name in new[] { "update-helper.log", "install.log", "recovery.json" })
            {
                var path = Path.Combine(operation, name);
                if (!File.Exists(path)) continue;
                UpdatePathSafety.AssertRegular(path, false);
                File.Copy(path, Path.Combine(reports, "failed-" + name), overwrite: true);
            }
        }
        catch { /* Failure evidence must not replace the original error. */ }
    }

    private static void AssertFixtureRoot(string root, UpdatePackageKind kind = UpdatePackageKind.Portable)
    {
        var expected = kind == UpdatePackageKind.Installer
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PulseReader")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReaderUpdateQualification", "app");
        if (!Path.GetFullPath(root).TrimEnd('\\').Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Fixture application mutation is limited to the exact disposable guest root.");
    }

    private static void CopyPayload(string source, string destination, bool overwrite = false)
    {
        AssertFixtureRoot(destination);
        var sourcePaths = File.ReadAllLines(Path.Combine(source, "payload-files.txt"))
            .Where(path => path.Length != 0).Select(UpdatePathSafety.Relative).ToArray();
        if (overwrite)
        {
            // Remove only the previous fixture distribution's owned extras.
            // Unknown files and every seeded user/model sentinel remain outside
            // these lists and are verified before and after recovery.
            var currentList = Path.Combine(destination, "payload-files.txt");
            foreach (var current in File.ReadAllLines(currentList).Where(path => path.Length != 0)
                .Select(UpdatePathSafety.Relative).Except(sourcePaths, StringComparer.OrdinalIgnoreCase))
            {
                var target = UpdatePathSafety.Under(destination, current);
                UpdatePathSafety.AssertRegular(target, directory: false);
                File.Delete(target);
            }
        }
        foreach (var relative in sourcePaths)
        {
            var target = UpdatePathSafety.Under(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(UpdatePathSafety.Under(source, relative), target, overwrite);
        }
    }

    private static Dictionary<string, string> SeedPreservedData(string root)
    {
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReader");
        string[] paths = [Path.Combine(root, "settings.json"), Path.Combine(root, "Models", "fixture-model.bin"),
            Path.Combine(root, "ContextualPronunciation", "user-state.txt"), Path.Combine(root, "unknown-user-file.txt"),
            Path.Combine(user, "settings.json"), Path.Combine(user, "Models", "fixture-user-model.bin")];
        var result = new Dictionary<string, string>();
        foreach (var path in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Preserved guest fixture state: " + path);
            result.Add(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return result;
    }

    private static void AssertSentinels(Dictionary<string, string> sentinels)
    {
        foreach (var (path, hash) in sentinels)
            Require(File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == hash,
                "Portable update changed a user sentinel: " + path);
    }

    private static void VerifyVersionAndPayloadHash(string root, string source, string relative, Version expected)
    {
        var actual = Path.Combine(root, relative);
        var info = FileVersionInfo.GetVersionInfo(actual);
        Require(new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) == expected,
            "Real compiled DLL version mismatch.");
        string hash;
        if (Directory.Exists(source)) hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, relative))));
        else
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(source);
            using var stream = zip.GetEntry(relative)!.Open();
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actual))) == hash,
            "Installed DLL bytes differ from the selected payload.");
    }

    private static Process Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Guest fixture process could not be started.");
    }

    private static async Task WaitExitAsync(Process process, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("Guest fixture process timed out; it was not killed."); }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Guest fixture observation was not produced: " + path);
            await Task.Delay(250);
        }
    }

    private static async Task WaitNoFixtureApplicationsAsync(string appRoot, string operation)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        while (true)
        {
            var active = false;
            foreach (var process in Process.GetProcessesByName("PulseReader"))
            {
                using (process)
                {
                    if (process.HasExited) continue;
                    var path = process.MainModule?.FileName ?? throw new IOException("Cannot inspect a guest fixture process.");
                    active |= path.Equals(Path.Combine(appRoot, "PulseReader.exe"), StringComparison.OrdinalIgnoreCase)
                        || path.Equals(Path.Combine(operation, "helper", "PulseReader.exe"), StringComparison.OrdinalIgnoreCase);
                }
            }
            if (!active) return;
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Guest fixture helper/relaunch did not exit; no process was killed.");
            await Task.Delay(250);
        }
    }

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path))
        ?? throw new InvalidDataException("Missing or invalid guest fixture metadata.");
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed record ParentRequest(string ApplicationRoot, string PackagePath,
        UpdatePackageKind Kind = UpdatePackageKind.Portable, string? TermsPath = null);
}
