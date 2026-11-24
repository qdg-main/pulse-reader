using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PulseReader;
using PulseReader.Configuration;
using PulseReader.Settings;
using PulseReader.Setup;
using PulseReader.Tray;
using PulseReader.Updates;

// Test assembly only. Every entry, including a helper or default relaunch, is
// restricted to the disposable standard-user guest before it can mutate state.
internal static class UpdateInteractiveQualification
{
    private const string GuestRoot = @"C:\PulseReaderUpdateQualification";
    private const string Inputs = GuestRoot + @"\Inputs";
    private const string Reports = GuestRoot + @"\Reports";
    private const string Marker = "update-interactive-qualification-fixture.json";
    private const string RequestName = "update-interactive-request.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task<int?> TryRunAsync(string[] args)
    {
        var controller = args.Length == 5 && args[0] == "--sandbox-update-ui-controller";
        var marked = File.Exists(Path.Combine(AppContext.BaseDirectory, Marker));
        if (!controller && !marked && !(args.Length > 0 && args[0] == "--sandbox-update-ui-app")) return null;
        AssertGuest();
        if (controller)
        {
            Require(args[1] is "Portable" or "Installer", "Unsupported fixture package kind.");
            Exact(args[4], Reports);
            await ControllerAsync(Enum.Parse<UpdatePackageKind>(args[1]), args[2], args[3]);
            return 0;
        }
        Require(marked, "Interactive fixture apphost lacks its disposable payload marker.");
        if (UpdateHelperRunner.IsHelperInvocation(args)) return await UpdateHelperRunner.RunHelperAsync(args);
        Require(args.Length == 0 || args is ["--sandbox-update-ui-app"], "Unsupported interactive fixture invocation.");
        var root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
        var request = Read<Request>(Path.Combine(root, RequestName));
        Exact(root, AppRoot(request.Kind));
        Exact(request.ApplicationRoot, root);
        UnderInputs(request.PackagePath, false);
        Require(request.Variant is "win-x64" or "win-x64-framework-dependent", "Unsupported request variant.");
        return await UiAsync(request, relaunched: args.Length == 0);
    }

    private static void AssertGuest()
    {
        Require(Environment.UserName == "PulseUpdateFixture"
            && Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Equals(@"C:\Users\PulseUpdateFixture", StringComparison.OrdinalIgnoreCase)
            && Environment.GetFolderPath(Environment.SpecialFolder.Windows).Equals(@"C:\Windows", StringComparison.OrdinalIgnoreCase)
            && File.Exists(GuestRoot + @"\guest-only.marker")
            && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            "Interactive update qualification requires the exact disposable Sandbox standard account.");
        UpdatePathSafety.AssertRegular(GuestRoot, true);
        UpdatePathSafety.AssertRegular(GuestRoot + @"\guest-only.marker", false);
    }

    private static string AppRoot(UpdatePackageKind kind) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        kind == UpdatePackageKind.Installer ? @"Programs\PulseReader" : @"PulseReaderUpdateQualification\app");

    private static async Task ControllerAsync(UpdatePackageKind kind, string old, string package)
    {
        UnderInputs(old, kind == UpdatePackageKind.Portable);
        UnderInputs(package, false);
        Directory.CreateDirectory(Reports);
        UpdatePathSafety.AssertRegular(Reports, true);
        var root = AppRoot(kind);
        var input = Read<JsonElement>(Path.Combine(Inputs, "qualification-input.json"));
        var variant = input.GetProperty("Variant").GetString()!;
        var shippingRelaunch = input.TryGetProperty("ShippingRelaunch", out var shipping) && shipping.GetBoolean();
        var physicalCancel = input.TryGetProperty("PhysicalCancelProbe", out var physical) && physical.GetBoolean();
        Require(variant is "win-x64" or "win-x64-framework-dependent", "Unsupported fixture variant.");
        Require(Path.GetFileName(package) == UpdateVersionPolicy.AssetName(new Version(1, 0, 1), variant, kind), "Fixture package flavour/name mismatch.");
        Require(!Directory.Exists(root), "Interactive qualification requires a fresh application root.");
        try
        {
            if (kind == UpdatePackageKind.Portable)
            {
                Require(File.Exists(Path.Combine(old, Marker)), "Old payload is not an interactive fixture.");
                foreach (var relative in File.ReadAllLines(Path.Combine(old, "payload-files.txt")).Where(p => p.Length != 0))
                {
                    var source = UpdatePathSafety.Under(old, UpdatePathSafety.Relative(relative));
                    UpdatePathSafety.AssertRegular(source, false);
                    var destination = UpdatePathSafety.Under(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination);
                }
                new RegistryDistributionTermsReceiptStore().WriteAcceptedSha256(HashFile(Path.Combine(root, "Distribution-Terms.txt")));
            }
            else
            {
                using var install = Start(old, "/CURRENTUSER", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                    "/ACCEPTDISTRIBUTIONTERMS=yes", "/LOG=" + Path.Combine(Reports, "interactive-initial-install.log"));
                await WaitExit(install, TimeSpan.FromMinutes(5));
                Require(install.ExitCode == 0, "Initial guest per-user installer failed.");
            }
            Require(File.Exists(Path.Combine(root, Marker)), "Installed payload lacks interactive fixture marker.");
            Require(UpdateInstallationDiscovery.TryRead(root, new Version(1, 0, 0), out var identity, out var reason)
                && identity!.Kind == kind && identity.Scope == InstallationScope.User && identity.Variant == variant, reason ?? "Wrong initial identity.");
            var config = new ConfigurationService();
            Require(config.TrySave(new AppConfiguration { SpeechBackend = SpeechBackend.Windows,
                WindowsSpeechVolume = 75, SpeechSpeed = 1.1f, ContextualPronunciationEnabled = false,
                CheckForUpdatesAutomatically = true }, out var saveError), saveError ?? "Cannot seed settings.");
            var model = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReader", "Models", "interactive-sentinel.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            File.WriteAllText(model, "Disposable interactive updater preservation sentinel.\n", new UTF8Encoding(false));
            var receipt = new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256();
            Require(receipt?.Equals(HashFile(Path.Combine(root, "Distribution-Terms.txt")), StringComparison.OrdinalIgnoreCase) == true,
                "Initial terms receipt does not match installed old terms.");
            var request = new Request(root, package, kind, HashFile(model), receipt!, HashFile(Path.Combine(root, "PulseReader.dll")), variant, physicalCancel);
            Write(Path.Combine(root, RequestName), request);
            Write(Path.Combine(Reports, "interactive-initial.json"), new { request, Config = config.LoadOrCreate().Configuration, ProcessId = Environment.ProcessId });
            Write(Path.Combine(Reports, "interactive-before-payload.json"), SnapshotOwned(root));
            using var app = Start(Path.Combine(root, "PulseReader.exe"), "--sandbox-update-ui-app");
            Write(Path.Combine(Reports, "interactive-parent-process.json"), new { app.Id, Executable = Path.Combine(root, "PulseReader.exe") });
            await WaitExit(app, TimeSpan.FromMinutes(8));
            Require(app.ExitCode == 0, "Original interactive app exited with a fixture failure.");
            if (shippingRelaunch)
            {
                await VerifyShippingRelaunchAsync(request, app.Id);
                return;
            }
            await UntilAsync(() => File.Exists(Path.Combine(Reports, "interactive-relaunch.json"))
                || File.Exists(Path.Combine(Reports, "interactive-failure.json")), TimeSpan.FromMinutes(5));
            Require(!File.Exists(Path.Combine(Reports, "interactive-failure.json")), "Interactive app failed; see interactive-failure.json.");
            var after = Read<JsonElement>(Path.Combine(Reports, "interactive-relaunch.json"));
            Require(after.GetProperty("Passed").GetBoolean(), "Relaunched UI qualification failed.");
            Require(after.GetProperty("ProcessId").GetInt32() != app.Id, "No new process was relaunched.");
            using (var relaunched = Process.GetProcessById(after.GetProperty("ProcessId").GetInt32()))
            {
                // Hold the process handle before permitting its real Exit command,
                // so even an immediate shutdown retains an observable exit code.
                _ = relaunched.Handle;
                Require(!relaunched.HasExited && relaunched.MainModule?.FileName?.Equals(Path.Combine(root, "PulseReader.exe"), StringComparison.OrdinalIgnoreCase) == true,
                    "Relaunch handshake did not identify the live scoped application process.");
                Write(Path.Combine(Reports, "interactive-relaunch-exit-ready.json"), new { ProcessId = relaunched.Id });
                await WaitExit(relaunched, TimeSpan.FromMinutes(1));
                Require(relaunched.ExitCode == 0, "Relaunched application did not exit cleanly through its production Exit command.");
                Write(Path.Combine(Reports, "interactive-relaunch-exit.json"), new { ProcessId = relaunched.Id, ExitCode = relaunched.ExitCode });
            }
            Write(Path.Combine(Reports, "interactive-after-payload.json"), SnapshotOwned(root));
            var applying = Read<JsonElement>(Path.Combine(Reports, "interactive-applying.json"));
            var operation = applying.GetProperty("OperationDirectory").GetString()!;
            Require(Path.GetDirectoryName(operation)!.Equals(UpdateHandoff.OperationsRoot, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(Path.GetFileName(operation), "N", out _), "Unexpected helper operation root.");
            await UntilAsync(() => File.Exists(Path.Combine(operation, "update-helper.log"))
                && File.ReadAllText(Path.Combine(operation, "update-helper.log")).Contains("Update confirmed"), TimeSpan.FromMinutes(1));
            foreach (var name in new[] { "update-helper.log", "install.log" })
                if (File.Exists(Path.Combine(operation, name))) File.Copy(Path.Combine(operation, name), Path.Combine(Reports, "interactive-" + name));
            Write(Path.Combine(Reports, "interactive-result.json"), new { Passed = true, Kind = kind.ToString(), OldPid = app.Id,
                Relaunched = after, OperationDirectory = operation, PackageSha256 = HashFile(package), PhysicalCancelProbe = physicalCancel,
                Capture = "Actual live application forms, DrawToBitmap, explicitly labelled form captures; not desktop screenshots.",
                Limitation = "Synthetic release HTTP responses and synthetic versioned packages; real app context, controls, validation, handoff, helper and installer." });
        }
        catch (Exception error)
        {
            Write(Path.Combine(Reports, "interactive-result.json"), new { Passed = false, Error = error.ToString(), Kind = kind.ToString(), ApplicationRoot = root });
            throw;
        }
    }

    private static async Task VerifyShippingRelaunchAsync(Request request, int oldPid)
    {
        // The new package retains the genuine production apphost. No test entry
        // point or in-process application-context injection runs in that child.
        var expectedExe = Path.Combine(request.ApplicationRoot, "PulseReader.exe");
        Process? application = null;
        try
        {
            await UntilAsync(() =>
            {
                foreach (var candidate in Process.GetProcessesByName("PulseReader"))
                {
                    if (candidate.Id != oldPid && !candidate.HasExited
                        && candidate.MainModule?.FileName?.Equals(expectedExe, StringComparison.OrdinalIgnoreCase) == true)
                    { application = candidate; return true; }
                    candidate.Dispose();
                }
                return false;
            }, TimeSpan.FromMinutes(3));
            _ = application!.Handle;
            await Task.Delay(5500);
            Require(!application.HasExited, "The genuine shipping app exited during relaunch startup.");
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(request.ApplicationRoot, "PulseReader.dll"));
            Require(info.FileVersion == "1.0.1.0", "Shipping relaunch did not load the real 1.0.1 payload.");
            VerifyPreserved(request);
            var newTerms = HashFile(Path.Combine(request.ApplicationRoot, "Distribution-Terms.txt"));
            Require(new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256()?.Equals(newTerms, StringComparison.OrdinalIgnoreCase) == true,
                "Shipping relaunch did not retain confirmed new terms.");
            using (var duplicate = Start(expectedExe, "--startup"))
            {
                await WaitExit(duplicate, TimeSpan.FromSeconds(15));
                Require(duplicate.ExitCode == 0 && !application.HasExited, "The shipping single-instance startup path did not exit quietly without disrupting the resident app.");
            }
            var productionPid = application.Id;
            // Test cleanup only, after holding the exact process handle and
            // checking its path. This does not qualify graceful tray-menu Exit.
            application.Kill();
            await WaitExit(application, TimeSpan.FromSeconds(15));
            var receipts = new RegistryDistributionTermsReceiptStore();
            receipts.WriteAcceptedSha256(new string('A', 64));
            using (var unaccepted = Start(expectedExe, "--startup"))
            {
                await WaitExit(unaccepted, TimeSpan.FromSeconds(15));
                Require(unaccepted.ExitCode != 0, "Unaccepted shipping startup terms did not fail closed without user interaction.");
                Require(receipts.ReadAcceptedSha256() == new string('A', 64), "Unaccepted startup silently changed consent.");
            }
            receipts.WriteAcceptedSha256(newTerms);
            using (var startup = Start(expectedExe, "--startup"))
            {
                _ = startup.Handle;
                await Task.Delay(2000);
                Require(!startup.HasExited, "Accepted shipping sign-in startup did not remain running.");
                Require(startup.MainModule?.FileName?.Equals(expectedExe, StringComparison.OrdinalIgnoreCase) == true, "Unexpected startup process path.");
                startup.Kill();
                await WaitExit(startup, TimeSpan.FromSeconds(15));
            }
            Write(Path.Combine(Reports, "interactive-after-payload.json"), SnapshotOwned(request.ApplicationRoot));
            Write(Path.Combine(Reports, "interactive-result.json"), new
            {
                Passed = true, ShippingProgramMain = true, request.Variant, Kind = request.Kind.ToString(), OldPid = oldPid,
                ProductionPid = productionPid, Version = info.FileVersion, DllSha256 = HashFile(Path.Combine(request.ApplicationRoot, "PulseReader.dll")),
                DuplicateStartupExitCode = 0, UnacceptedStartupRejected = true, AcceptedStartupRan = true,
                Cleanup = "Exact guest-owned production processes were terminated after startup assertions; graceful production tray Exit was not tested.",
                Limitation = "Controlled release offer; no public HTTP delivery or machine-scope UAC."
            });
        }
        finally { application?.Dispose(); }
    }

    private static Task<int> UiAsync(Request request, bool relaunched)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var handler = new FixtureHandler(request);
                using var context = new PulseReaderApplicationContext(new UpdateClient(handler));
                var dispatcher = Field<Control>(context, "_uiDispatcher");
                var menu = Field<TrayMenuForm>(context, "_trayMenu");
                var settings = Field<SettingsForm>(context, "_settingsForm");
                using var observer = new System.Windows.Forms.Timer { Interval = 25 };
                var termsAction = "";
                var observedApplying = false;
                Exception? failure = null;
                observer.Tick += (_, _) =>
                {
                    if (failure is not null) return;
                    try
                    {
                        if (Application.OpenForms.OfType<DistributionTermsForm>().FirstOrDefault(f => f.Visible) is { } terms && termsAction.Length > 0)
                        {
                            var action = termsAction; termsAction = "";
                            Capture(terms, action + "-terms");
                            Write(Path.Combine(Reports, action + "-terms-ux.json"), new
                            {
                                DeclineLabel = terms.Controls.Find("DeclineDistributionTerms", true).Single().Text,
                                Description = terms.Controls.OfType<Label>().Single().Text,
                                Purpose = "Update-specific terms wording; cancelling keeps the current version running."
                            });
                            var decline = terms.Controls.Find("DeclineDistributionTerms", true).Single();
                            Require(decline.Text == "Cancel update" && decline.AccessibleName == "Cancel update"
                                && terms.Controls.OfType<Label>().Single().Text == "Review the distribution terms for this update. Cancelling leaves your current version running.",
                                "Actual updater terms dialog must explain that cancellation leaves the current app running.");
                            var rendered = terms.Controls.Find("DistributionTermsText", true).Single().Text;
                            var authenticated = handler.Terms;
                            var normalizedRendered = NormalizeRenderedLines(rendered);
                            var normalizedAuthenticated = NormalizeRenderedLines(authenticated);
                            var firstDifference = Enumerable.Range(0, Math.Min(normalizedRendered.Length, normalizedAuthenticated.Length))
                                .FirstOrDefault(i => normalizedRendered[i] != normalizedAuthenticated[i], -1);
                            Write(Path.Combine(Reports, action + "-terms-content.json"), new
                            {
                                AuthenticatedLength = authenticated.Length, RenderedLength = rendered.Length,
                                AuthenticatedUtf8Sha256 = Hash(Encoding.UTF8.GetBytes(authenticated)),
                                RenderedUtf8Sha256 = Hash(Encoding.UTF8.GetBytes(rendered)), handler.TermsHash,
                                NormalizedAuthenticatedLength = normalizedAuthenticated.Length, NormalizedRenderedLength = normalizedRendered.Length,
                                FirstNormalizedDifference = firstDifference,
                                LineEndingOnlyNormalization = "CRLF and CR converted to LF for RichTextBox display comparison only. Manifest and package hashes retain exact original bytes."
                            });
                            Require(Hash(Encoding.UTF8.GetBytes(authenticated)) == handler.TermsHash,
                                "Authenticated release terms no longer match their exact UTF-8 hash.");
                            Require(normalizedRendered == normalizedAuthenticated,
                                $"Rendered dialog terms differ beyond line endings (source length {authenticated.Length}, rendered {rendered.Length}, first normalized difference {firstDifference}).");
                            if (action == "decline" && request.PhysicalCancelProbe)
                                Write(Path.Combine(Reports, "physical-cancel-ready.json"), new { ProcessId = Environment.ProcessId, Utc = DateTimeOffset.UtcNow });
                            else if (action == "decline") ((Button)terms.Controls.Find("DeclineDistributionTerms", true).Single()).PerformClick();
                            else
                            {
                                ((CheckBox)terms.Controls.Find("DistributionTermsAgreement", true).Single()).Checked = true;
                                ((Button)terms.Controls.Find("AcceptDistributionTerms", true).Single()).PerformClick();
                            }
                        }
                        if (!observedApplying && Field<UpdateMenuMode>(context, "_updateMenuMode") == UpdateMenuMode.Applying)
                        {
                            observedApplying = true;
                            MenuCapture(menu, "applying");
                            var name = UpdateVersionPolicy.AssetName(new Version(1, 0, 1), request.Variant, request.Kind);
                            var operation = Directory.EnumerateDirectories(UpdateHandoff.OperationsRoot).Single(d => File.Exists(Path.Combine(d, name)));
                            Write(Path.Combine(Reports, "interactive-applying.json"), new { OperationDirectory = operation, ProcessId = Environment.ProcessId,
                                State = "Applying", Receipt = new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256(), Utc = DateTimeOffset.UtcNow });
                        }
                    }
                    catch (Exception error) { Fail(error); }
                };
                void Fail(Exception error)
                {
                    if (failure is not null) return;
                    failure = error;
                    observer.Stop();
                    termsAction = "";
                    Write(Path.Combine(Reports, "interactive-failure.json"), new { Error = error.ToString(), ProcessId = Environment.ProcessId, Relaunched = relaunched });
                    // A timer may fail while ShowDialog is nested inside the
                    // command callback. Cancel that dialog, then queue loop exit;
                    // Command below prevents the unwinding driver from continuing.
                    foreach (var terms in Application.OpenForms.OfType<DistributionTermsForm>().Where(f => f.Visible).ToArray())
                    {
                        terms.DialogResult = DialogResult.Cancel;
                        terms.Close();
                    }
                    dispatcher.BeginInvoke(() => context.ExitThread());
                }
                void Command(TrayMenuCommand command)
                {
                    if (failure is not null) throw new InvalidOperationException("Interactive fixture already failed; no further command is permitted.", failure);
                    menu.ActivateForTest(command);
                    if (failure is not null) throw new InvalidOperationException("Interactive observer failed during a nested command; automation stopped.", failure);
                }
                observer.Start();
                dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        var installed = Field<UpdateInstallation>(context, "_updateInstallation");
                        Require(installed.Kind == request.Kind && installed.Variant == request.Variant, "Actual app updater discovered wrong package kind or flavour.");
                        if (relaunched)
                        {
                            Require(installed.Version == new Version(1, 0, 1), "Actual new app context did not discover version 1.0.1.");
                            VerifyPreserved(request);
                            Require(new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256()?.Equals(handler.TermsHash, StringComparison.OrdinalIgnoreCase) == true,
                                "Successful helper did not persist exact new terms.");
                            Command(TrayMenuCommand.Settings);
                            Capture(settings, "relaunched-settings");
                            Field<GlowCheckBox>(settings, "_automaticUpdatesCheckBox").Focus();
                            Capture(settings, "relaunched-settings-updates");
                            settings.Hide();
                            Command(TrayMenuCommand.Update);
                            await WaitOperation(context);
                            Require(Field<UpdateMenuMode>(context, "_updateMenuMode") == UpdateMenuMode.Idle
                                && Field<UpdateOffer?>(context, "_updateOffer") is null && handler.PackageRequests == 0, "Current-version manual Check incorrectly offers/downloads an update.");
                            MenuCapture(menu, "up-to-date");
                            Write(Path.Combine(Reports, "interactive-relaunch.json"), new { Passed = true, ProcessId = Environment.ProcessId,
                                Version = installed.Version.ToString(), DllSha256 = HashFile(Path.Combine(request.ApplicationRoot, "PulseReader.dll")),
                                ConfigSha256 = HashFile(new ConfigurationService().ConfigPath), Receipt = new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256(),
                                Requests = handler.Requests, Utc = DateTimeOffset.UtcNow });
                            await UntilAsync(() => File.Exists(Path.Combine(Reports, "interactive-relaunch-exit-ready.json"))
                                && Read<JsonElement>(Path.Combine(Reports, "interactive-relaunch-exit-ready.json")).GetProperty("ProcessId").GetInt32() == Environment.ProcessId,
                                TimeSpan.FromMinutes(1));
                            Command(TrayMenuCommand.Exit);
                            return;
                        }
                        Require(installed.Version == new Version(1, 0, 0), "Initial real app context is not version 1.0.0.");
                        Command(TrayMenuCommand.Settings);
                        var automatic = Field<GlowCheckBox>(settings, "_automaticUpdatesCheckBox");
                        Require(automatic.Checked, "Initial automatic-updates setting was not enabled.");
                        automatic.Focus();
                        Click(automatic);
                        Require(!new ConfigurationService().LoadOrCreate().Configuration.CheckForUpdatesAutomatically, "Actual checkbox callback did not save opt-out.");
                        Capture(settings, "opt-out-settings");
                        Write(Path.Combine(Reports, "interactive-saved-config.json"), new { Hash = HashFile(new ConfigurationService().ConfigPath) });
                        settings.Hide();
                        MenuCapture(menu, "idle");
                        Require(!Field<System.Windows.Forms.Timer>(context, "_updateCheckTimer").Enabled,
                            "The saved opt-out did not disable the actual production update timer.");
                        await Task.Delay(TimeSpan.FromMilliseconds(5500));
                        Require(handler.Requests.Count == 0, "Automatic check ran before manual Check after opt-out.");
                        Stage("idle", context, handler);
                        Command(TrayMenuCommand.Update);
                        await WaitOperation(context);
                        Require(Field<UpdateMenuMode>(context, "_updateMenuMode") == UpdateMenuMode.Available && handler.PackageRequests == 0, "Manual Check did not expose a verified offer without downloading.");
                        Require(Field<bool>(Field<object>(context, "_trayIconAnimator"), "_updateAvailable"), "Actual tray animator did not enable its update badge.");
                        MenuCapture(menu, "available");
                        Stage("available", context, handler);
                        using (var icon = Field<NotifyIcon>(context, "_trayIcon").Icon!.ToBitmap()) icon.Save(Path.Combine(Reports, "available-actual-tray-icon.png"), ImageFormat.Png);
                        termsAction = "decline";
                        Command(TrayMenuCommand.Update);
                        await WaitOperation(context);
                        Require(handler.PackageRequests == 0, "Declining changed terms requested the package.");
                        VerifyUnchanged(request);
                        Stage("declined", context, handler);
                        termsAction = "accept-cancel";
                        Command(TrayMenuCommand.Update);
                        await UntilAsync(() => Field<UpdateMenuMode>(context, "_updateMenuMode") == UpdateMenuMode.Downloading
                            && LiveDownloadPercent(menu) is >= 1 and <= 5, TimeSpan.FromMinutes(1));
                        MenuCapture(menu, "downloading-before-cancel");
                        Stage("downloading-before-cancel", context, handler);
                        Command(TrayMenuCommand.Update);
                        await WaitOperation(context);
                        Require(Field<UpdateMenuMode>(context, "_updateMenuMode") == UpdateMenuMode.Available
                            && !Field<bool>(context, "_exiting"), "Cancelled download did not leave the actual old app running.");
                        Require(!Directory.EnumerateFiles(UpdateHandoff.OperationsRoot, "*.partial", SearchOption.AllDirectories).Any(), "Cancelled package left a partial download.");
                        VerifyUnchanged(request);
                        MenuCapture(menu, "cancelled-still-running");
                        Stage("cancelled", context, handler);
                        termsAction = "accept-complete";
                        Command(TrayMenuCommand.Update);
                        await WaitOperation(context);
                        Require(observedApplying, "The live Applying state was not captured before real handoff.");
                        // Production OnUpdateRequested awaits READY then gracefully exits
                        // this application context. The helper relaunches with zero args.
                    }
                    catch (Exception error) { Fail(error); }
                });
                Application.Run(context);
                completion.TrySetResult(failure is null ? 0 : 1);
            }
            catch (Exception error)
            {
                Write(Path.Combine(Reports, "interactive-failure.json"), new { Error = error.ToString(), ProcessId = Environment.ProcessId });
                completion.TrySetResult(1);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Stage(string name, object context, FixtureHandler handler) => Write(Path.Combine(Reports, "interactive-" + name + ".json"), new
    { Passed = true, State = Field<UpdateMenuMode>(context, "_updateMenuMode").ToString(), ProcessId = Environment.ProcessId,
        handler.PackageRequests, handler.BytesDelivered, handler.PackageLength, Requests = handler.Requests.ToArray(),
        Receipt = new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256(), Utc = DateTimeOffset.UtcNow });

    private static void VerifyUnchanged(Request request)
    {
        Require(HashFile(Path.Combine(request.ApplicationRoot, "PulseReader.dll")) == request.OldDllHash, "Decline/cancel changed old application DLL.");
        Require(new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256() == request.OldReceipt, "Decline/cancel persisted uninstalled terms.");
        VerifyPreserved(request);
    }

    private static void VerifyPreserved(Request request)
    {
        var config = new ConfigurationService();
        var actual = config.LoadOrCreate().Configuration;
        Require(actual.SpeechBackend == SpeechBackend.Windows && actual.WindowsSpeechVolume == 75 && actual.SpeechSpeed == 1.1f
            && !actual.ContextualPronunciationEnabled && !actual.CheckForUpdatesAutomatically, "Guest settings sentinel changed.");
        Require(HashFile(config.ConfigPath) == Read<JsonElement>(Path.Combine(Reports, "interactive-saved-config.json")).GetProperty("Hash").GetString(), "Saved configuration bytes changed.");
        Require(HashFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseReader", "Models", "interactive-sentinel.bin")) == request.ModelHash,
            "Guest model sentinel bytes changed.");
    }

    private static async Task WaitOperation(object context)
    {
        await UntilAsync(() => Field<Task<bool>?>(context, "_updateTask") is { IsCompleted: true }, TimeSpan.FromMinutes(5));
        await Field<Task<bool>>(context, "_updateTask");
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static string NormalizeRenderedLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static int LiveDownloadPercent(TrayMenuForm menu)
    {
        var description = Field<TrayMenuItemControl>(menu, "_updateItem").AccessibleDescription ?? "";
        var start = description.IndexOf(':');
        var end = description.IndexOf('%');
        return start >= 0 && end > start && int.TryParse(description[(start + 1)..end].Trim(), out var percent) ? percent : -1;
    }
    private static void Click(Control control) => control.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [EventArgs.Empty]);
    private static void MenuCapture(TrayMenuForm menu, string name)
    {
        menu.ShowAt(new Point(450, 350));
        Capture(menu, name);
        menu.HideMenu();
    }
    private static void Capture(Form form, string name)
    {
        Require(form.Visible && form.IsHandleCreated, "Cannot capture a form that is not live and visible.");
        form.Refresh();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(Reports, name + "-live-form-capture.png"), ImageFormat.Png);
        Write(Path.Combine(Reports, name + "-capture.json"), new { Source = "DrawToBitmap of actual visible production Form", form.Text,
            FormType = form.GetType().FullName, form.Bounds, ProcessId = Environment.ProcessId, Utc = DateTimeOffset.UtcNow });
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Request _request;
        private readonly Dictionary<string, byte[]> _bodies = new(StringComparer.Ordinal);
        private readonly byte[] _release;
        private readonly string _assetName;
        internal string Terms { get; }
        internal string TermsHash { get; }
        internal long PackageLength { get; }
        internal int PackageRequests;
        internal long BytesDelivered;
        internal List<string> Requests { get; } = [];

        internal FixtureHandler(Request request)
        {
            _request = request;
            _assetName = UpdateVersionPolicy.AssetName(new Version(1, 0, 1), request.Variant, request.Kind);
            PackageLength = new FileInfo(request.PackagePath).Length;
            if (request.Kind == UpdatePackageKind.Portable)
            {
                using var archive = ZipFile.OpenRead(request.PackagePath);
                Require(archive.GetEntry(Marker) is not null, "New portable package lacks fixture marker.");
                using var reader = new StreamReader(archive.GetEntry("Distribution-Terms.txt")!.Open(), new UTF8Encoding(false, true));
                Terms = reader.ReadToEnd();
            }
            else
            {
                var path = Path.Combine(Path.GetDirectoryName(request.PackagePath)!, "UpdateTerms.txt");
                UnderInputs(path, false);
                Terms = File.ReadAllText(path, new UTF8Encoding(false, true));
            }
            TermsHash = Hash(Encoding.UTF8.GetBytes(Terms));
            Require(!TermsHash.Equals(request.OldReceipt, StringComparison.OrdinalIgnoreCase), "Qualification requires changed new terms.");
            var packages = new JsonArray();
            foreach (var variant in new[] { "win-x64", "win-x64-framework-dependent" })
            {
                var entry = new JsonObject { ["Variant"] = variant };
                foreach (var kind in new[] { UpdatePackageKind.Installer, UpdatePackageKind.Portable })
                {
                    var name = UpdateVersionPolicy.AssetName(new Version(1, 0, 1), variant, kind);
                    var selected = name == _assetName;
                    var dummy = Encoding.UTF8.GetBytes("Unselected fixture asset " + name);
                    if (!selected) _bodies[name] = dummy;
                    entry[kind.ToString()] = new JsonObject { ["Name"] = name, ["Length"] = selected ? PackageLength : dummy.Length,
                        ["Sha256"] = selected ? HashFile(request.PackagePath).ToLowerInvariant() : Hash(dummy) };
                }
                packages.Add(entry);
            }
            _bodies[UpdateConstants.ManifestName] = Encoding.UTF8.GetBytes(new JsonObject { ["SchemaVersion"] = 1, ["Version"] = "1.0.1",
                ["TermsSha256"] = TermsHash, ["TermsText"] = Terms, ["Packages"] = packages }.ToJsonString());
            _bodies[UpdateConstants.ChecksumsName] = Encoding.UTF8.GetBytes(string.Join("\n", _bodies.Select(p => Hash(p.Value) + "  " + p.Key))
                + "\n" + HashFile(request.PackagePath).ToLowerInvariant() + "  " + _assetName + "\n");
            var assets = new JsonArray();
            var id = 100;
            foreach (var pair in _bodies) assets.Add(Asset(pair.Key, pair.Value.LongLength, Hash(pair.Value), ++id));
            assets.Add(Asset(_assetName, PackageLength, HashFile(request.PackagePath).ToLowerInvariant(), ++id));
            _release = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = 77, ["tag_name"] = "v1.0.1", ["draft"] = false,
                ["prerelease"] = false, ["html_url"] = "https://github.com/" + UpdateConstants.RepositoryPath + "/releases/tag/v1.0.1", ["assets"] = assets }.ToJsonString());
        }

        private static JsonObject Asset(string name, long length, string hash, int id) => new()
        { ["id"] = id, ["name"] = name, ["size"] = length, ["digest"] = "sha256:" + hash, ["state"] = "uploaded",
            ["browser_download_url"] = "https://github.com/" + UpdateConstants.RepositoryPath + "/releases/download/v1.0.1/" + name };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(uri);
            Require(request.Method == HttpMethod.Get && request.Headers.Authorization is null, "Unexpected fixture request method/authentication.");
            HttpContent content;
            if (uri == UpdateConstants.LatestRelease.AbsoluteUri || uri == "https://api.github.com/repos/" + UpdateConstants.RepositoryPath + "/releases/77")
                content = new ByteArrayContent(_release);
            else if (uri == "https://github.com/" + UpdateConstants.RepositoryPath + "/releases/download/v1.0.1/" + _assetName)
            {
                Interlocked.Increment(ref PackageRequests);
                Interlocked.Exchange(ref BytesDelivered, 0);
                content = new StreamContent(new SlowStream(new FileStream(_request.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                    PackageLength, count => Interlocked.Add(ref BytesDelivered, count)));
                content.Headers.ContentLength = PackageLength;
            }
            else
            {
                var name = Uri.UnescapeDataString(request.RequestUri.Segments.Last());
                Require(uri == "https://github.com/" + UpdateConstants.RepositoryPath + "/releases/download/v1.0.1/" + name
                    && _bodies.ContainsKey(name), "Fixture refused an unexpected URL; no external HTTP is permitted.");
                content = new ByteArrayContent(_bodies[name]);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class SlowStream(Stream inner, long length, Action<int> delivered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Keep the real progress/cancel state observable at the beginning;
            // do not spend several minutes throttling a self-contained runtime.
            await Task.Delay(inner.Position < length / 20 ? 100 : 1, cancellationToken);
            var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, (int)Math.Clamp(length / 200, 4096, 81920))], cancellationToken);
            delivered(count);
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private static void UnderInputs(string path, bool directory)
    {
        Require(Path.GetFullPath(path).StartsWith(Inputs + "\\", StringComparison.OrdinalIgnoreCase), "Fixture inputs must be copied beneath the fixed guest Inputs root.");
        UpdatePathSafety.AssertRegular(path, directory);
    }
    private static void Exact(string actual, string expected) => Require(Path.GetFullPath(actual).TrimEnd('\\').Equals(expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "Unexpected fixture path: " + actual);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static Dictionary<string, string> SnapshotOwned(string root) => File.ReadAllLines(Path.Combine(root, "payload-files.txt"))
        .Where(p => p.Length != 0).ToDictionary(p => p, p => HashFile(UpdatePathSafety.Under(root, UpdatePathSafety.Relative(p))), StringComparer.OrdinalIgnoreCase);
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid fixture request/report.");
    private static void Write(string path, object value)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Require(directory.Equals(Reports, StringComparison.OrdinalIgnoreCase)
            || (Path.GetFileName(path) == RequestName && (directory.Equals(AppRoot(UpdatePackageKind.Portable), StringComparison.OrdinalIgnoreCase)
                || directory.Equals(AppRoot(UpdatePackageKind.Installer), StringComparison.OrdinalIgnoreCase))),
            "Fixture JSON publication escaped its exact guest report/application roots.");
        UpdatePathSafety.AssertRegular(directory, true);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }
    private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static Process Start(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Cannot start scoped guest fixture process.");
    }
    private static async Task WaitExit(Process process, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("Fixture process timed out; no process was killed."); }
    }
    private static async Task UntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            Require(DateTimeOffset.UtcNow < deadline, "Timed out awaiting real interactive update state; no process was killed.");
            await Task.Delay(25);
        }
    }
    private sealed record Request(string ApplicationRoot, string PackagePath, UpdatePackageKind Kind, string ModelHash, string OldReceipt, string OldDllHash, string Variant = "win-x64", bool PhysicalCancelProbe = false);
}
