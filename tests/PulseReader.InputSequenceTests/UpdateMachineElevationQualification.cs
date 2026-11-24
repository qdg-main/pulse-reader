using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PulseReader.Configuration;
using PulseReader.Setup;
using PulseReader.Updates;

internal static class UpdateMachineElevationQualification
{
    private const string Guest = @"C:\PulseReaderUpdateQualification";
    private const string Inputs = Guest + @"\Inputs";
    private const string Reports = Guest + @"\Reports";
    private const string Marker = "machine-elevation-fixture.json";
    private static string AppRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PulseReader");
    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;

    internal static async Task<int?> TryRunAsync(string[] args)
    {
        var controller = args is ["--sandbox-machine-controller"];
        var verifyApproved = args is ["--sandbox-machine-verify-approved"];
        var marked = File.Exists(Path.Combine(AppContext.BaseDirectory, Marker));
        if (!controller && !verifyApproved && !marked) return null;
        Require(Environment.UserName == "PulseUpdateFixture"
            && Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) == @"C:\Users\PulseUpdateFixture"
            && Environment.GetFolderPath(Environment.SpecialFolder.Windows) == @"C:\Windows"
            && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), "Machine fixture requires the exact disposable ordinary user.");
        UpdatePathSafety.AssertRegular(Guest + @"\guest-only.marker", false);
        UpdatePathSafety.AssertRegular(@"C:\PulseReaderUacQualification\restart.marker", false);
        if (verifyApproved)
        {
            try { VerifyApproved(); }
            catch (Exception error) { Write("machine-post-retry-validation.json", new { Passed = false, Error = error.ToString() }); throw; }
            return 0;
        }
        if (controller) { await ControllerAsync(); return 0; }
        if (UpdateHelperRunner.IsHelperInvocation(args)) return await UpdateHelperRunner.RunHelperAsync(args);
        Require(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\').Equals(AppRoot, StringComparison.OrdinalIgnoreCase), "Machine fixture must run from its canonical Program Files target.");
        if (args is ["--sandbox-machine-parent", var phase] && phase is "cancel" or "approve")
        {
            var update = Prepare();
            Write("machine-operation.json", new { Phase = phase, update.OperationDirectory, ProcessId = Environment.ProcessId, Sid });
            Require(await new UpdateHandoff().StartAsync(update, update.Offer.TermsSha256), "Machine helper rejected authenticated handoff.");
            return 0;
        }
        Require(args.Length == 0, "Unexpected machine fixture arguments.");
        Write("machine-relaunch.json", new { Sid, User = Environment.UserName, Administrator = false, ProcessId = Environment.ProcessId,
            Version = FileVersionInfo.GetVersionInfo(Path.Combine(AppRoot, "PulseReader.dll")).FileVersion });
        return 0;
    }

    private static void VerifyApproved()
    {
        Require(UpdateInstallationDiscovery.TryRead(AppRoot, new Version(1, 0, 1), out var installed, out var reason)
            && installed!.Scope == InstallationScope.Machine && installed.Kind == UpdatePackageKind.Installer && installed.Variant == "win-x64", reason ?? "Approved machine identity mismatch.");
        var actual = VerifyPayload(AppRoot, Path.Combine(Inputs, "NewPayload"));
        var expectedConfig = new ConfigurationService(Path.Combine(Reports, "expected-config"));
        Require(expectedConfig.TrySave(new AppConfiguration { SpeechBackend = SpeechBackend.Windows, WindowsSpeechVolume = 75,
            ContextualPronunciationEnabled = false, CheckForUpdatesAutomatically = false }, out var error), error ?? "Cannot recreate seeded fixture config.");
        var actualConfigHash = Hash(new ConfigurationService().ConfigPath);
        Require(actualConfigHash == Hash(expectedConfig.ConfigPath), "Original-user config differs from its exact seeded fixture bytes.");
        var personal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReader", "Models", "machine-sentinel.bin");
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PulseReader", "Models", "machine-shared-sentinel.bin");
        Require(Hash(personal) == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Disposable machine update sentinel."))), "Original-user model sentinel changed.");
        Require(Hash(shared) == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Disposable shared machine-model preservation sentinel."))), "Shared model sentinel changed.");
        var termsHash = Hash(Path.Combine(AppRoot, "Distribution-Terms.txt"));
        Require(new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256()?.Equals(termsHash, StringComparison.OrdinalIgnoreCase) == true, "New terms were not accepted for the original user.");
        var relaunch = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Reports, "machine-relaunch.json")));
        Require(relaunch.GetProperty("Sid").GetString() == Sid && !relaunch.GetProperty("Administrator").GetBoolean()
            && relaunch.GetProperty("Version").GetString() == "1.0.1.0", "Approved update relaunched as the wrong/elevated user or wrong version.");
        Write("machine-post-retry-validation.json", new { Passed = true, Sid, Scope = "Machine", Variant = "win-x64", MatchedOwnedFiles = actual.Count,
            ConfigSha256 = actualConfigHash, PersonalAndSharedSentinelsPreserved = true, TermsSha256 = termsHash, Relaunched = relaunch, Utc = DateTimeOffset.UtcNow });
    }

    private static PreparedUpdate Prepare()
    {
        Require(UpdateInstallationDiscovery.TryRead(AppRoot, new Version(1, 0, 0), out var installation, out var reason)
            && installation!.Scope == InstallationScope.Machine && installation.Kind == UpdatePackageKind.Installer && installation.Variant == "win-x64", reason ?? "Wrong machine fixture identity.");
        var source = Path.Combine(Inputs, "PulseReader-1.0.1-win-x64-Setup.exe");
        UpdatePathSafety.AssertRegular(source, false);
        var operation = UpdateHandoff.CreateOperationDirectory();
        var name = Path.GetFileName(source);
        var destination = Path.Combine(operation, name);
        File.Copy(source, destination);
        var terms = File.ReadAllText(Path.Combine(Inputs, "UpdateTerms.txt"), new UTF8Encoding(false, true));
        var offer = new UpdateOffer(77, new Version(1, 0, 1), new Uri($"https://github.com/{UpdateConstants.RepositoryPath}/releases/tag/v1.0.1"),
            new UpdateAsset(1, name, new Uri($"https://github.com/{UpdateConstants.RepositoryPath}/releases/download/v1.0.1/{name}"),
                new FileInfo(destination).Length, Hash(destination)), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terms))), terms);
        return new(installation!, offer, operation, destination);
    }

    private static async Task ControllerAsync()
    {
        var config = new ConfigurationService();
        Require(config.TrySave(new AppConfiguration { SpeechBackend = SpeechBackend.Windows, WindowsSpeechVolume = 75,
            ContextualPronunciationEnabled = false, CheckForUpdatesAutomatically = false }, out var saveError), saveError ?? "Cannot seed guest settings.");
        var configHash = Hash(config.ConfigPath);
        var sentinel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReader", "Models", "machine-sentinel.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "Disposable machine update sentinel.");
        var sentinelHash = Hash(sentinel);
        var sharedSentinel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PulseReader", "Models", "machine-shared-sentinel.bin");
        var sharedHash = Hash(sharedSentinel);
        var receipts = new RegistryDistributionTermsReceiptStore();
        var oldTerms = Hash(Path.Combine(AppRoot, "Distribution-Terms.txt"));
        receipts.WriteAcceptedSha256(oldTerms);
        var before = Snapshot();
        var cases = new List<object>();
        try
        {
            foreach (var phase in new[] { "cancel", "approve" })
            {
                if (phase == "approve")
                {
                    Write("machine-stage.json", new { Phase = "awaiting-approval-ready", Action = "Wait for the human to be ready before showing UAC", Sid, Utc = DateTimeOffset.UtcNow });
                    await UntilAsync(() => File.Exists(Path.Combine(Reports, "machine-approval-ready.marker")), TimeSpan.FromMinutes(20));
                }
                var observed = Path.Combine(Reports, "machine-relaunch.json");
                if (File.Exists(observed)) File.Delete(observed);
                Write("machine-stage.json", new { Phase = phase, Action = phase == "cancel" ? "Cancel the guest UAC prompt" : "Approve using the temporary guest administrator", Sid, Utc = DateTimeOffset.UtcNow });
                using (var parent = Start(Path.Combine(AppRoot, "PulseReader.exe"), "--sandbox-machine-parent", phase))
                {
                    await ExitAsync(parent, TimeSpan.FromMinutes(3));
                    Require(parent.ExitCode == 0, "Machine parent did not exit cleanly after READY.");
                }
                await UntilAsync(() => File.Exists(observed), TimeSpan.FromMinutes(20));
                var relaunched = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(observed));
                await UntilAsync(() =>
                {
                    foreach (var process in Process.GetProcessesByName("PulseReader"))
                    {
                        using (process)
                            if (!process.HasExited && process.MainModule?.FileName?.Equals(Path.Combine(AppRoot, "PulseReader.exe"), StringComparison.OrdinalIgnoreCase) == true)
                                return false;
                    }
                    return true;
                }, TimeSpan.FromMinutes(1));
                Require(relaunched.GetProperty("Sid").GetString() == Sid && !relaunched.GetProperty("Administrator").GetBoolean(), "Machine update relaunched as the wrong or elevated user.");
                Require(Hash(config.ConfigPath) == configHash && Hash(sentinel) == sentinelHash, "Machine update changed original-user settings or model sentinel.");
                Require(Hash(sharedSentinel) == sharedHash, "Machine update changed the shared ProgramData model sentinel.");
                var operation = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Reports, "machine-operation.json"))).GetProperty("OperationDirectory").GetString()!;
                Require(Path.GetDirectoryName(operation)!.Equals(UpdateHandoff.OperationsRoot, StringComparison.OrdinalIgnoreCase), "Unexpected machine operation root.");
                var logPath = Path.Combine(operation, "update-helper.log");
                var log = File.ReadAllText(logPath);
                if (phase == "cancel")
                {
                    Require(relaunched.GetProperty("Version").GetString() == "1.0.0.0" && receipts.ReadAcceptedSha256() == oldTerms, "Cancelled UAC changed version or terms receipt.");
                    var after = Snapshot();
                    Require(before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var hash) && hash == p.Value), "Cancelled UAC changed the original installed payload.");
                    Require(!File.Exists(Path.Combine(operation, "install.log")) && log.Contains("canceled by the user", StringComparison.OrdinalIgnoreCase), "Expected explicit UAC cancellation before installer launch.");
                }
                else
                {
                    Require(relaunched.GetProperty("Version").GetString() == "1.0.1.0" && log.Contains("Update confirmed"), "Approved machine update did not confirm version 1.0.1.");
                    Require(receipts.ReadAcceptedSha256()?.Equals(Hash(Path.Combine(AppRoot, "Distribution-Terms.txt")), StringComparison.OrdinalIgnoreCase) == true, "New acceptance was not saved for the original user.");
                    Require(UpdateInstallationDiscovery.TryRead(AppRoot, new Version(1, 0, 1), out var installed, out _)
                        && installed!.Scope == InstallationScope.Machine && installed.Kind == UpdatePackageKind.Installer && installed.Variant == "win-x64", "Machine scope/flavour changed.");
                    Write("machine-after-payload.json", VerifyPayload(AppRoot, Path.Combine(Inputs, "NewPayload")));
                    File.Copy(Path.Combine(operation, "install.log"), Path.Combine(Reports, "machine-install.log"));
                }
                File.Copy(logPath, Path.Combine(Reports, "machine-" + phase + "-helper.log"));
                cases.Add(new { Phase = phase, Relaunched = relaunched, OriginalSid = Sid, SettingsPreserved = true });
                Write("machine-progress.json", new { PassedCases = cases });
            }
            Write("machine-result.json", new { Passed = true, Cases = cases, Limitation = "Controlled installer packages and test apphost; real helper, machine scope, UAC and user credentials. No public HTTP delivery." });
        }
        catch (Exception error) { Write("machine-result.json", new { Passed = false, Error = error.ToString(), Cases = cases }); throw; }
    }

    private static Dictionary<string, string> Snapshot() => File.ReadAllLines(Path.Combine(AppRoot, "payload-files.txt"))
        .Where(p => p.Length != 0).ToDictionary(p => p, p => Hash(UpdatePathSafety.Under(AppRoot, UpdatePathSafety.Relative(p))), StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, string> VerifyPayload(string applicationRoot, string expectedRoot)
    {
        var expectedPaths = File.ReadAllLines(Path.Combine(expectedRoot, "payload-files.txt"))
            .Where(p => p.Length != 0).Select(UpdatePathSafety.Relative).ToArray();
        var actualPaths = File.ReadAllLines(Path.Combine(applicationRoot, "payload-files.txt"))
            .Where(p => p.Length != 0).Select(UpdatePathSafety.Relative).ToArray();
        Require(expectedPaths.Length > 0 && expectedPaths.Contains("payload-files.txt", StringComparer.Ordinal)
            && expectedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == expectedPaths.Length
            && actualPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == actualPaths.Length
            && expectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actualPaths),
            "Approved machine ownership list differs from the complete expected payload.");
        var actual = expectedPaths.ToDictionary(p => p,
            p => Hash(UpdatePathSafety.Under(applicationRoot, p)), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in actual)
            Require(entry.Value == Hash(UpdatePathSafety.Under(expectedRoot, UpdatePathSafety.Relative(entry.Key))), "Approved machine payload mismatch: " + entry.Key);
        return actual;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Write(string name, object value)
    {
        if (Path.GetFileName(name) != name) throw new InvalidDataException("Unexpected report filename.");
        var target = Path.Combine(Reports, name);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static Process Start(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppRoot };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("Cannot start machine fixture process.");
    }
    private static async Task ExitAsync(Process process, TimeSpan timeout) { using var cts = new CancellationTokenSource(timeout); await process.WaitForExitAsync(cts.Token); }
    private static async Task UntilAsync(Func<bool> ready, TimeSpan timeout)
    { var end = DateTimeOffset.UtcNow + timeout; while (!ready()) { Require(DateTimeOffset.UtcNow < end, "Timed out waiting for human UAC action; no installer was killed."); await Task.Delay(200); } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
