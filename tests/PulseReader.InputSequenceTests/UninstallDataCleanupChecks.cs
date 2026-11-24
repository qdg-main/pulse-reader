using PulseReader.Setup;
using System.Diagnostics;

internal static class UninstallDataCleanupChecks
{
    internal static void Run()
    {
        CheckArguments();
        CheckPreserve();
        CheckUserPreserve();
        CheckUserDependencies();
        CheckUserEverything();
        CheckMachineDependencies();
        CheckMachineEverything();
        CheckInvalidScope();
        CheckLockedFile();
        CheckPinnedDirectoryCannotMove();
        CheckJunction();
        CheckRunningClient();
        CheckProfileFailure();
        CheckMixedLoadedAndOfflineProfiles();
        CheckOfflineDoesNotSuppressRegistryErrors();
        CheckNativePrototypeRemoved();
        CheckDeepTreeIsRejectedBeforeDeletion();
        CheckEmptyDependencyContainersPruned();
        CheckEmptyRuntimePrunedButRetainedFilesSurvive();
        Console.WriteLine("PASS: uninstall choices preserve scoped data, wipe all users' files when selected, report offline registry skips, reject unsafe paths, and propagate registry errors.");
    }

    private static void CheckArguments()
    {
        Assert(UninstallDataCleanup.ParseArguments(["--uninstall-data", "preserve", "--quiet"])
            == UninstallDataChoice.Preserve, "Silent preserve parsing failed.");
        Assert(UninstallDataCleanup.ParseArguments(["--uninstall-data", "everything"])
            == UninstallDataChoice.Everything, "Explicit full cleanup parsing failed.");
        Assert(UninstallDataCleanup.ParseArguments(["--uninstall-data", "everything", "--quiet"])
            == UninstallDataChoice.Everything, "Explicit silent full cleanup parsing failed.");
        foreach (var args in new[] { new[] { "--uninstall-data" }, ["--uninstall-data", "all"],
            ["--uninstall-data", "preserve", "--confirm-all-users-data"],
            ["--uninstall-data", "everything", "--confirm-all-users-data"],
            ["--uninstall-data", "everything", "--unknown"],
            ["--uninstall-data", "everything", "--quiet", "--quiet"],
            ["--uninstall-data", "everything", "--root", "C:\\"] })
            Throws<ArgumentException>(() => UninstallDataCleanup.ParseArguments(args));
    }

    private static void CheckPreserve()
    {
        using var fixture = new Fixture(machine: true);
        fixture.Cleanup(UninstallDataChoice.Preserve);
        Assert(fixture.AllFilesExist(), "App-only uninstall changed user or shared downloads.");
        Assert(fixture.Hives.Calls.Count == 2 && fixture.Hives.Calls.All(call => !call.Receipt), "App-only removed a receipt or ignored a profile.");
    }

    private static void CheckUserDependencies()
    {
        using var fixture = new Fixture(machine: false);
        fixture.Cleanup(UninstallDataChoice.Dependencies);
        Assert(!File.Exists(fixture.UserModel) && !File.Exists(fixture.UserRuntime), "User dependencies survived cleanup.");
        Assert(File.Exists(fixture.UserConfig) && File.Exists(fixture.UserLog), "Option 2 removed config or logs.");
        Assert(File.Exists(fixture.SharedModel) && File.Exists(fixture.OtherConfig) && File.Exists(fixture.OtherModel), "User uninstall touched global or other-user data.");
        Assert(fixture.Hives.Calls.Single() == ("current", false), "User cleanup inspected another user's registry.");
    }

    private static void CheckUserPreserve()
    {
        using var fixture = new Fixture(machine: false);
        fixture.Hives.OfflineSids.Add("fixture-other");
        fixture.Cleanup(UninstallDataChoice.Preserve);
        Assert(fixture.AllFilesExist() && fixture.Hives.Calls.Single() == ("current", false),
            "Per-user preserve changed files or accessed another user's registry.");
        Assert(fixture.Hives.StartupRemovedSids.SetEquals(["current"]) && fixture.Hives.ReceiptRemovedSids.Count == 0,
            "Per-user preserve failed its own startup cleanup or removed acceptance records.");
    }

    private static void CheckUserEverything()
    {
        using var fixture = new Fixture(machine: false);
        fixture.Cleanup(UninstallDataChoice.Everything);
        Assert(!Directory.Exists(fixture.UserData), "User full wipe left owned data.");
        Assert(File.Exists(fixture.OtherConfig) && File.Exists(fixture.SharedModel) && File.Exists(fixture.Unrelated), "User full wipe escaped its data root.");
        Assert(fixture.Hives.Calls.Single() == ("current", true), "User full wipe did not remove its receipt.");
    }

    private static void CheckMachineDependencies()
    {
        using var fixture = new Fixture(machine: true);
        fixture.Cleanup(UninstallDataChoice.Dependencies);
        Assert(!File.Exists(fixture.SharedModel) && !File.Exists(fixture.MachineRuntime), "Shared model/runtime was retained.");
        Assert(File.Exists(fixture.UserModel) && File.Exists(fixture.OtherModel) && File.Exists(fixture.OtherConfig), "Machine dependency cleanup removed a private install's cache or config.");
        Assert(File.Exists(fixture.UserLog), "Machine dependency cleanup removed user logs.");
    }

    private static void CheckMachineEverything()
    {
        using var fixture = new Fixture(machine: true);
        fixture.Cleanup(UninstallDataChoice.Everything);
        Assert(!Directory.Exists(fixture.UserData) && !Directory.Exists(fixture.OtherData)
            && !File.Exists(fixture.SharedModel) && !File.Exists(fixture.MachineRuntime), "Global full wipe missed scoped data.");
        Assert(File.Exists(fixture.Unrelated), "Global full wipe removed unrelated profile data.");
        Assert(fixture.Hives.Calls.Count == 2 && fixture.Hives.Calls.All(call => call.Receipt), "Global full wipe missed acceptance records.");
        Assert(UninstallDataCleanup.StartupPointsToApp('"' + Path.Combine(fixture.App, "PulseReader.exe") + "\" --startup", fixture.App), "Own startup was not recognized.");
        Assert(!UninstallDataCleanup.StartupPointsToApp('"' + Path.Combine(fixture.Local, "Programs", "PulseReader", "PulseReader.exe") + "\" --startup", fixture.App), "Global uninstall would remove the per-user startup.");
    }

    private static void CheckInvalidScope()
    {
        using var fixture = new Fixture(machine: false);
        File.WriteAllText(Path.Combine(fixture.App, UninstallDataCleanup.MarkerFileName), "{\"SchemaVersion\":1,\"Scope\":\"machine\"}");
        Throws<InvalidDataException>(() => fixture.Cleanup(UninstallDataChoice.Everything));
        Assert(fixture.AllFilesExist(), "Mismatched marker mutated data.");
        File.WriteAllText(Path.Combine(fixture.App, UninstallDataCleanup.MarkerFileName), "{\"SchemaVersion\":1,\"Scope\":\"user\",\"Scope\":\"user\"}");
        Throws<InvalidDataException>(() => fixture.Cleanup(UninstallDataChoice.Preserve));
    }

    private static void CheckLockedFile()
    {
        using var fixture = new Fixture(machine: false);
        using var lease = new FileStream(fixture.UserLog, FileMode.Open, FileAccess.Read, FileShare.None);
        Throws<IOException>(() => fixture.Cleanup(UninstallDataChoice.Everything));
        Assert(fixture.AllFilesExist() && fixture.Hives.Calls.Count == 0, "Locked target allowed partial filesystem deletion.");
    }

    private static void CheckRunningClient()
    {
        using var fixture = new Fixture(machine: true);
        var environment = fixture.Environment with { AssertNoRunningClients = _ => throw new IOException("Fixture active client.") };
        Throws<IOException>(() => UninstallDataCleanup.Execute(UninstallDataChoice.Everything, environment));
        Assert(fixture.AllFilesExist() && fixture.Hives.Calls.Count == 0, "Running client allowed cleanup.");
    }

    private static void CheckPinnedDirectoryCannotMove()
    {
        using var fixture = new Fixture(machine: true);
        var attempted = false;
        var blocked = false;
        var environment = fixture.Environment with
        {
            DirectoryPinned = directory =>
            {
                if (!directory.Equals(fixture.UserData, StringComparison.OrdinalIgnoreCase)) return;
                attempted = true;
                try { Directory.Move(directory, directory + "-redirected"); }
                catch (IOException) { blocked = true; }
            }
        };
        UninstallDataCleanup.Execute(UninstallDataChoice.Everything, environment);
        Assert(attempted && blocked && File.Exists(fixture.Unrelated), "A pinned user-data ancestor could be replaced during elevated cleanup.");
    }

    private static void CheckJunction()
    {
        using var fixture = new Fixture(machine: true);
        var target = Path.GetDirectoryName(fixture.Unrelated)!;
        var link = Path.Combine(fixture.UserData, "redirect");
        var info = new ProcessStartInfo(Path.Combine(System.Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start fixture junction helper.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Cannot create fixture junction: " + output + error);
        try
        {
            Throws<IOException>(() => fixture.Cleanup(UninstallDataChoice.Everything));
            Assert(fixture.AllFilesExist() && fixture.Hives.Calls.Count == 0, "Junction cleanup escaped scope or deleted data before preflight finished.");
        }
        finally { Directory.Delete(link, recursive: false); }
    }

    private static void CheckProfileFailure()
    {
        using var fixture = new Fixture(machine: true);
        fixture.Hives.FailSid = "fixture-other";
        Throws<IOException>(() => fixture.Cleanup(UninstallDataChoice.Everything));
        Assert(fixture.Hives.Calls.Count == 2, "Registry failure prevented remaining profiles from being inspected.");
    }

    private static void CheckMixedLoadedAndOfflineProfiles()
    {
        foreach (var choice in Enum.GetValues<UninstallDataChoice>())
        {
            using var fixture = new Fixture(machine: true);
            fixture.Hives.OfflineSids.Add("fixture-other");
            var previousOutput = Console.Out;
            using var output = new StringWriter();
            Console.SetOut(output);
            try { fixture.Cleanup(choice); }
            finally { Console.SetOut(previousOutput); }
            Assert(output.ToString().Contains("1 offline profile(s) left unchanged", StringComparison.Ordinal),
                "Cleanup did not explicitly report the offline registry left behind.");
            Assert(fixture.Hives.StartupRemovedSids.SetEquals(["fixture-user"]), "Offline startup values were removed or a loaded startup was ignored.");
            Assert(!fixture.Hives.ReceiptRemovedSids.Contains("fixture-other"), "Offline acceptance records were removed.");
            Assert(fixture.Hives.ReceiptRemovedSids.Contains("fixture-user") == (choice == UninstallDataChoice.Everything),
                "Loaded acceptance receipt cleanup did not follow the selected option.");
            if (choice == UninstallDataChoice.Everything)
                Assert(!Directory.Exists(fixture.OtherData) && !Directory.Exists(fixture.UserData) && File.Exists(fixture.Unrelated),
                    "Offline registry skip retained that user's files or deleted unrelated files.");
            else
                Assert(File.Exists(fixture.UserConfig) && File.Exists(fixture.OtherConfig) && File.Exists(fixture.OtherModel),
                    "Preserve/dependencies changed an offline user's private files.");
            Assert(File.Exists(fixture.SharedModel) == (choice == UninstallDataChoice.Preserve), "Shared dependency removal did not follow the selected option.");
        }
    }

    private static void CheckOfflineDoesNotSuppressRegistryErrors()
    {
        using var fixture = new Fixture(machine: true);
        fixture.Hives.OfflineSids.Add("fixture-other");
        fixture.Hives.FailSid = "fixture-user";
        Throws<IOException>(() => fixture.Cleanup(UninstallDataChoice.Everything));
        Assert(fixture.Hives.Calls.Count == 2 && fixture.Hives.StartupRemovedSids.Count == 0,
            "A real registry error was treated as offline or prevented explicit offline handling.");
    }

    private static void CheckNativePrototypeRemoved()
    {
        Assert(typeof(UninstallDataCleanup).Assembly.GetType("PulseReader.Setup.UninstallHiveLoadHelper") is null,
            "The rejected native hive-loading prototype remains in the application.");
    }

    private static void CheckDeepTreeIsRejectedBeforeDeletion()
    {
        using var fixture = new Fixture(machine: true);
        var cursor = fixture.UserData;
        for (var depth = 0; depth < 65; depth++) cursor = Path.Combine(cursor, "x");
        Directory.CreateDirectory(cursor);
        var sentinel = Path.Combine(cursor, "sentinel.txt");
        File.WriteAllText(sentinel, "Keep these fixture bytes.");
        Throws<InvalidDataException>(() => fixture.Cleanup(UninstallDataChoice.Everything));
        Assert(fixture.AllFilesExist() && File.Exists(sentinel) && fixture.Hives.Calls.Count == 0,
            "Deep-tree inspection failure deleted files or changed registry values.");
    }

    private static void CheckEmptyDependencyContainersPruned()
    {
        using var fixture = new Fixture(machine: true);
        var sharedRoot = Path.GetDirectoryName(Path.GetDirectoryName(fixture.SharedModel))!;
        var optional = Path.Combine(sharedRoot, "ContextualPronunciation");
        var interrupted = optional + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(optional, "models"));
        Directory.CreateDirectory(Path.Combine(optional, "logs"));
        Directory.CreateDirectory(Path.Combine(interrupted, "runtime"));
        fixture.Cleanup(UninstallDataChoice.Dependencies);
        Assert(!Directory.Exists(optional) && !Directory.Exists(interrupted) && !Directory.Exists(sharedRoot),
            "Dependency cleanup left empty shared model containers behind.");
    }

    private static void CheckEmptyRuntimePrunedButRetainedFilesSurvive()
    {
        using var fixture = new Fixture(machine: true);
        var runtime = Path.GetDirectoryName(Path.GetDirectoryName(fixture.MachineRuntime))!;
        File.Delete(fixture.MachineRuntime);
        Directory.Delete(Path.GetDirectoryName(fixture.MachineRuntime)!);
        var sharedRoot = Path.GetDirectoryName(Path.GetDirectoryName(fixture.SharedModel))!;
        var optional = Path.Combine(sharedRoot, "ContextualPronunciation");
        Directory.CreateDirectory(Path.Combine(optional, "logs"));
        var retainedLog = Path.Combine(optional, "logs", "retain.log");
        var retainedConfig = Path.Combine(sharedRoot, "retain.json");
        File.WriteAllText(retainedLog, "Retain these log bytes.");
        File.WriteAllText(retainedConfig, "Retain these config bytes.");
        fixture.Cleanup(UninstallDataChoice.Preserve);
        Assert(!Directory.Exists(runtime), "App-only cleanup left an empty downloaded-runtime container.");
        Assert(File.Exists(fixture.SharedModel) && File.Exists(retainedLog) && File.Exists(retainedConfig)
            && File.Exists(fixture.UserConfig) && Directory.Exists(optional), "Empty-directory pruning deleted retained data.");
        fixture.Cleanup(UninstallDataChoice.Dependencies);
        Assert(!File.Exists(fixture.SharedModel) && File.Exists(retainedLog) && File.Exists(retainedConfig)
            && Directory.Exists(optional) && Directory.Exists(sharedRoot), "Dependency pruning deleted shared logs/config or their nonempty containers.");
    }


    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeProfiles : IUninstallProfileAccess
    {
        internal readonly List<(string Sid, bool Receipt)> Calls = [];
        internal required IReadOnlyList<UninstallProfile> Profiles;
        internal string? FailSid;
        internal readonly HashSet<string> OfflineSids = [];
        internal readonly HashSet<string> StartupRemovedSids = [];
        internal readonly HashSet<string> ReceiptRemovedSids = [];
        public IReadOnlyList<UninstallProfile> GetProfiles() => Profiles;
        public UninstallRegistryCleanupResult RemoveOwnedRegistryValues(UninstallProfile profile, string app, bool removeReceipt)
        {
            Calls.Add((profile.Sid, removeReceipt));
            if (profile.Sid == FailSid) throw new IOException("Fixture inaccessible user hive.");
            if (OfflineSids.Contains(profile.Sid)) return UninstallRegistryCleanupResult.SkippedOffline;
            StartupRemovedSids.Add(profile.Sid);
            if (removeReceipt) ReceiptRemovedSids.Add(profile.Sid);
            return UninstallRegistryCleanupResult.Cleaned;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PulseReaderUninstallChecks-" + Guid.NewGuid().ToString("N"));
        internal readonly FakeProfiles Hives;
        internal string Local { get; }
        internal string App { get; }
        internal string UserData { get; }
        internal string OtherData { get; }
        internal string UserConfig { get; }
        internal string UserModel { get; }
        internal string UserRuntime { get; }
        internal string UserLog { get; }
        internal string OtherConfig { get; }
        internal string OtherModel { get; }
        internal string SharedModel { get; }
        internal string MachineRuntime { get; }
        internal string Unrelated { get; }
        internal UninstallCleanupEnvironment Environment { get; }
        internal Fixture(bool machine)
        {
            var user = Path.Combine(_root, "Profiles", "user");
            var other = Path.Combine(_root, "Profiles", "other");
            Local = Path.Combine(user, "AppData", "Local");
            var programFiles = Path.Combine(_root, "ProgramFiles");
            var programData = Path.Combine(_root, "ProgramData");
            App = machine ? Path.Combine(programFiles, "PulseReader") : Path.Combine(Local, "Programs", "PulseReader");
            UserData = Path.Combine(Local, "PulseReader");
            OtherData = Path.Combine(other, "AppData", "Local", "PulseReader");
            UserConfig = Put(UserData, "config.json");
            UserModel = Put(UserData, "Models/kokoro.onnx");
            UserRuntime = Put(UserData, "ContextualPronunciation/runtime/llama-server.exe");
            UserLog = Put(UserData, "ContextualPronunciation/logs/log.txt");
            OtherConfig = Put(OtherData, "config.json");
            OtherModel = Put(OtherData, "Models/kokoro.onnx");
            SharedModel = Put(Path.Combine(programData, "PulseReader"), "Models/kokoro.onnx");
            MachineRuntime = Put(Path.Combine(programFiles, "PulseReader"), "OptionalRuntime/hash/llama-server.exe");
            Unrelated = Put(Path.Combine(other, "AppData", "Local", "OtherApp"), "settings.json");
            Directory.CreateDirectory(App);
            File.WriteAllText(Path.Combine(App, UninstallDataCleanup.MarkerFileName), "{\"SchemaVersion\":1,\"Scope\":\"" + (machine ? "machine" : "user") + "\"}");
            Hives = new() { Profiles = [new("fixture-user", user), new("fixture-other", other)] };
            Environment = new(App, Local, programFiles, programData, Hives, _ => { });
        }
        internal void Cleanup(UninstallDataChoice choice) => UninstallDataCleanup.Execute(choice, Environment);
        internal bool AllFilesExist() => new[] { UserConfig, UserModel, UserRuntime, UserLog, OtherConfig,
            OtherModel, SharedModel, MachineRuntime, Unrelated }.All(File.Exists);
        private static string Put(string root, string relative)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            return path;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
