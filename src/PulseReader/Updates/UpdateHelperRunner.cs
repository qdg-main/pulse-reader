using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using PulseReader.Setup;

namespace PulseReader.Updates;

internal static class UpdateHelperRunner
{
    internal static bool IsHelperInvocation(string[] args) => args.Length > 0 && args[0] is "--apply-update" or "--recover-update";

    internal static async Task<int> RunHelperAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length > 0 && args[0] == "--recover-update")
            return await RunRecoveryAsync(args, cancellationToken).ConfigureAwait(false);
        PreparedUpdate? update = null;
        var parentExited = false;
        var installerCompleted = false;
        var operation = "";
        UpdateFileLease? package = null;
        UpdateShadowPayload? shadow = null;
        UpdatePathSafety.Pins? logPins = null;
        UpdatePathSafety.Pins? applicationPins = null;
        UpdateOriginalInstallationSnapshot? originalInstallation = null;
        IDisposable? finalPayloadLease = null;
        try
        {
            UpdateHandoff.AssertNormalUser();
            if (args.Length != 4 || !IsHelperInvocation(args) || !ValidPipeName(args[1])
                || !int.TryParse(args[2], out var parentId) || parentId <= 0
                || !long.TryParse(args[3], out var parentTicks) || parentTicks <= 0)
                throw new InvalidDataException("Invalid update helper invocation.");
            var helperRoot = UpdatePathSafety.Root(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            var candidateOperation = Path.GetDirectoryName(helperRoot)!;
            if (Path.GetFileName(helperRoot) != "helper"
                || !Guid.TryParseExact(Path.GetFileName(candidateOperation), "N", out _)
                || !Path.GetDirectoryName(candidateOperation)!.Equals(UpdateHandoff.OperationsRoot, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Environment.ProcessPath, Path.Combine(helperRoot, "PulseReader.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update helper is outside its private operation directory.");
            operation = candidateOperation;
            logPins = new UpdatePathSafety.Pins();
            logPins.Directory(operation);
            using var parent = Process.GetProcessById(parentId);
            var parentPath = parent.MainModule?.FileName ?? throw new UnauthorizedAccessException("Cannot identify the original application.");
            if (parent.StartTime.ToUniversalTime().Ticks != parentTicks || parent.HasExited)
                throw new UnauthorizedAccessException("The original application process identity changed.");
            shadow = UpdateShadowPayload.Pin(helperRoot);
            await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(40));
                await pipe.ConnectAsync(handshake.Token).ConfigureAwait(false);
                UpdateHandoffProtocol.RequirePeer(pipe, parentId, server: true);
                var packet = await UpdateHandoffProtocol.ReadAsync<UpdateHandoffPacket>(pipe, handshake.Token).ConfigureAwait(false);
                update = packet.Update;
                UpdateHandoffProtocol.Validate(update, packet.AcceptedTermsSha256);
                if (!update.OperationDirectory.Equals(operation, StringComparison.OrdinalIgnoreCase)
                    || !parentPath.Equals(Path.Combine(update.Installation.ApplicationRoot, "PulseReader.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("The handoff does not belong to the original application.");
                applicationPins = new UpdatePathSafety.Pins();
                applicationPins.Directory(update.Installation.ApplicationRoot);
                AssertInstallation(update.Installation, update.Installation.Version);
                using (var original = new UpdatePathSafety.LockedFile(parentPath, movable: false))
                {
                    package = await UpdateFileLease.OpenAsync(update.AssetPath, update.Offer.Asset.Length,
                        update.Offer.Asset.Sha256, handshake.Token).ConfigureAwait(false);
                    if (update.Installation.Kind == UpdatePackageKind.Portable)
                    {
                        VerifyPortableTerms(package.Stream, update.Offer.TermsSha256);
                        var recovery = UpdateRecoveryRecord.Create(operation, packet);
                        WriteLog(operation, "If a restart or power loss interrupts replacement, close all copies and run: "
                            + Quote(Path.Combine(helperRoot, "PulseReader.exe")) + " --recover-update " + recovery.Nonce);
                    }
                    WriteLog(operation, "Package and original application verified. Waiting for graceful application exit.");
                    await UpdateHandoffProtocol.WriteAsync(pipe, new UpdateHandoffReply("READY", null), handshake.Token)
                        .ConfigureAwait(false);
                    var accepted = await UpdateHandoffProtocol.ReadAsync<UpdateHandoffReply>(pipe, handshake.Token).ConfigureAwait(false);
                    if (accepted.Status != "ACCEPT") throw new InvalidDataException("The original application did not accept handoff.");
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    wait.CancelAfter(TimeSpan.FromMinutes(2));
                    await parent.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    parentExited = true;
                }
                AssertNoOtherApplications(update.Installation.ApplicationRoot);
                AssertInstallation(update.Installation, update.Installation.Version);
                if (update.Installation.Kind == UpdatePackageKind.Portable)
                {
                    await new PortableUpdateTransaction().ApplyAsync(update, cancellationToken).ConfigureAwait(false);
                    AssertInstallation(update.Installation, update.Offer.Version);
                    VerifyInstalledTerms(update.Installation.ApplicationRoot, packet.AcceptedTermsSha256);
                }
                else
                {
                    originalInstallation = UpdateOriginalInstallationSnapshot.Capture(update.Installation.ApplicationRoot, helperRoot);
                    VerifyInstalledTerms(update.Installation.ApplicationRoot, originalInstallation.OriginalTermsSha256);
                    WriteLog(operation, "Starting the installer. Inno Setup may modify the installed build; no atomic installer rollback is available.");
                    await RunInstallerAsync(update, packet.AcceptedTermsSha256, cancellationToken).ConfigureAwait(false);
                    installerCompleted = true;
                    await ConfirmInstalledAsync(update, packet.AcceptedTermsSha256, cancellationToken).ConfigureAwait(false);
                    finalPayloadLease = UpdateOriginalInstallationSnapshot.LeaseCurrentPayload(update.Installation.ApplicationRoot);
                    AssertInstallation(update.Installation, update.Offer.Version);
                    VerifyInstalledTerms(update.Installation.ApplicationRoot, packet.AcceptedTermsSha256);
                }
                new RegistryDistributionTermsReceiptStore().WriteAcceptedSha256(packet.AcceptedTermsSha256.ToUpperInvariant());
                WriteLog(operation, "Update confirmed. Distribution terms acceptance saved for the original user. Relaunching Pulse Reader.");
                Relaunch(update.Installation.ApplicationRoot);
                return 0;
            }
        }
        catch (Exception exception)
        {
            // A WinExe helper must contain all failures; no unhandled-exception/WER dialog.
            TryLog(operation, "Update failed: " + exception.GetType().Name + ": " + exception.Message
                + " Review update-helper.log and install.log in this operation directory before retrying.");
            if (parentExited && update is not null)
            {
                try
                {
                    if (update.Installation.Kind == UpdatePackageKind.Portable)
                        await new PortableUpdateTransaction().RecoverAsync(update.Installation.ApplicationRoot).ConfigureAwait(false);
                    if (update.Installation.Kind == UpdatePackageKind.Installer)
                    {
                        finalPayloadLease?.Dispose(); finalPayloadLease = null;
                        if (originalInstallation is null)
                            throw new InvalidDataException("The original installer snapshot is unavailable; automatic relaunch is withheld.");
                        var selected = originalInstallation.SelectFailureLease(installerCompleted, () =>
                        {
                            var lease = UpdateOriginalInstallationSnapshot.LeaseCurrentPayload(update.Installation.ApplicationRoot);
                            try
                            {
                                AssertInstallation(update.Installation, update.Offer.Version);
                                VerifyInstalledTerms(update.Installation.ApplicationRoot, update.Offer.TermsSha256);
                                return lease;
                            }
                            catch { lease.Dispose(); throw; }
                        });
                        finalPayloadLease = selected.Lease;
                        if (!selected.Updated)
                        {
                            AssertInstallation(update.Installation, update.Installation.Version);
                            VerifyInstalledTerms(update.Installation.ApplicationRoot, originalInstallation.OriginalTermsSha256);
                            TryLog(operation, "The installer left every original owned payload, scope marker and uninstaller byte unchanged. Reopening the verified original build.");
                        }
                    }
                    else AssertCoherentForRelaunch(update);
                    Relaunch(update.Installation.ApplicationRoot);
                    TryLog(operation, "The coherent original or updated application was relaunched as the original user.");
                }
                catch (Exception recovery)
                { TryLog(operation, "Automatic relaunch was withheld: " + recovery.Message + " Repair this installation using its official package."); }
            }
            return 1;
        }
        finally
        { finalPayloadLease?.Dispose(); originalInstallation?.Dispose(); package?.Dispose(); shadow?.Dispose(); applicationPins?.Dispose(); logPins?.Dispose(); }
    }

    private static bool ValidPipeName(string name) => name.StartsWith("PulseReader.Update.", StringComparison.Ordinal)
        && Guid.TryParseExact(name["PulseReader.Update.".Length..], "N", out _);

    private static string Quote(string path) => "\"" + path + "\"";

    private static async Task<int> RunRecoveryAsync(string[] args, CancellationToken token)
    {
        var operation = "";
        try
        {
            UpdateHandoff.AssertNormalUser();
            if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out _))
                throw new InvalidDataException("Recovery requires the capability shown in the original operation log.");
            var helperRoot = UpdatePathSafety.Root(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            var candidate = Path.GetDirectoryName(helperRoot)!;
            if (Path.GetFileName(helperRoot) != "helper"
                || !Guid.TryParseExact(Path.GetFileName(candidate), "N", out _)
                || !Path.GetDirectoryName(candidate)!.Equals(UpdateHandoff.OperationsRoot, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Environment.ProcessPath, Path.Combine(helperRoot, "PulseReader.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovery must run from the retained operation helper.");
            operation = candidate;
            using var shadow = UpdateShadowPayload.Pin(helperRoot);
            using var pins = new UpdatePathSafety.Pins();
            pins.Directory(operation);
            using var metadata = new UpdatePathSafety.LockedFile(Path.Combine(operation, "recovery.json"), movable: false);
            var record = UpdateRecoveryRecord.Read(metadata.Stream, args[1]);
            var update = record.Packet.Update;
            UpdateHandoffProtocol.Validate(update, record.Packet.AcceptedTermsSha256);
            if (update.Installation.Kind != UpdatePackageKind.Portable
                || update.Installation.Scope != InstallationScope.User
                || !update.OperationDirectory.Equals(operation, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovery is limited to the original portable operation.");
            // Bind the saved application identity to the retained original helper build.
            var original = update.Installation with { ApplicationRoot = helperRoot };
            AssertInstallation(original, original.Version);
            AssertNoOtherApplications(update.Installation.ApplicationRoot);
            WriteLog(operation, "Authenticated portable recovery requested. No running application will be stopped.");
            await new PortableUpdateTransaction().RecoverAsync(update.Installation.ApplicationRoot, token).ConfigureAwait(false);
            AssertCoherentForRelaunch(update);
            if (UpdateInstallationDiscovery.TryRead(update.Installation.ApplicationRoot, update.Offer.Version, out _, out _))
            {
                AssertInstallation(update.Installation, update.Offer.Version);
                VerifyInstalledTerms(update.Installation.ApplicationRoot, record.Packet.AcceptedTermsSha256);
                new RegistryDistributionTermsReceiptStore().WriteAcceptedSha256(record.Packet.AcceptedTermsSha256.ToUpperInvariant());
            }
            WriteLog(operation, "Portable recovery completed. Relaunching the coherent application as the original user.");
            Relaunch(update.Installation.ApplicationRoot);
            return 0;
        }
        catch (Exception error)
        {
            TryLog(operation, "Portable recovery failed: " + error.Message
                + " Retain the operation directory and the .PulseReader-update journal/stage/backup paths beside the application for repair.");
            return 1;
        }
    }

    internal static void VerifyPortableTerms(Stream package, string acceptedHash)
    {
        package.Position = 0;
        using (var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (zip.Entries.Count > 4096) throw new InvalidDataException("The portable package contains too many files.");
            var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/').Equals("Distribution-Terms.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length != 1 || entries[0].FullName != "Distribution-Terms.txt"
                || entries[0].Length is <= 0 or > 256 * 1024)
                throw new InvalidDataException("The portable package is missing its exact distribution terms.");
            using var input = entries[0].Open();
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (memory.Length + read > 256 * 1024) throw new InvalidDataException("Portable distribution terms exceed their limit.");
                memory.Write(buffer, 0, read);
            }
            if (memory.Length != entries[0].Length
                || !Convert.ToHexString(SHA256.HashData(memory.GetBuffer().AsSpan(0, (int)memory.Length)))
                    .Equals(acceptedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The portable package distribution terms differ from the accepted offer.");
        }
        package.Position = 0;
    }

    internal static ProcessStartInfo InstallerStartInfo(PreparedUpdate update, string hash)
    {
        var elevated = update.Installation.Scope == InstallationScope.Machine;
        var start = new ProcessStartInfo(update.AssetPath)
        {
            UseShellExecute = elevated,
            WorkingDirectory = update.OperationDirectory,
            Verb = elevated ? "runas" : "",
            CreateNoWindow = !elevated
        };
        // ArgumentList is correctly quoted for both CreateProcess and ShellExecute.
        foreach (var argument in new[]
        {
            elevated ? "/ALLUSERS" : "/CURRENTUSER", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/RESTARTEXITCODE=3010",
            "/PULSEREADERUPDATE=1", "/UPDATEACCEPTEDTERMS=" + hash,
            "/LOG=" + Path.Combine(update.OperationDirectory, "install.log")
        }) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task RunInstallerAsync(PreparedUpdate update, string hash, CancellationToken token)
    {
        using var installer = Process.Start(InstallerStartInfo(update, hash)) ?? throw new IOException("The installer could not be started.");
        // Never kill an installer on cancellation: it may already have changed live files.
        await installer.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (installer.ExitCode == 3010)
            throw new IOException("A Windows restart is required to finish this update (installer exit 3010). See install.log.");
        if (installer.ExitCode != 0) throw new IOException($"The installer exited with code {installer.ExitCode}. See install.log.");
        token.ThrowIfCancellationRequested();
    }

    private static async Task ConfirmInstalledAsync(PreparedUpdate update, string hash, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        Exception? last = null;
        do
        {
            token.ThrowIfCancellationRequested();
            try
            {
                AssertInstallation(update.Installation, update.Offer.Version);
                VerifyInstalledTerms(update.Installation.ApplicationRoot, hash);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            { last = error; }
            await Task.Delay(500, token).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new IOException("The installer did not produce the requested version, variant, scope and accepted terms. See install.log.", last);
    }

    private static void AssertInstallation(UpdateInstallation expected, Version version)
    {
        using var pins = new UpdatePathSafety.Pins();
        pins.Directory(expected.ApplicationRoot);
        var held = new List<UpdatePathSafety.LockedFile>();
        try
        {
            var names = new List<string> { "dependency-inventory.json", "PulseReader.exe", "PulseReader.dll" };
            if (expected.Kind == UpdatePackageKind.Installer)
                names.AddRange([InstallationLayout.ScopeFileName, "unins000.exe", "unins000.dat"]);
            foreach (var name in names)
                held.Add(new UpdatePathSafety.LockedFile(Path.Combine(expected.ApplicationRoot, name), movable: false));
            if (!UpdateInstallationDiscovery.TryRead(expected.ApplicationRoot, version, out var actual, out var reason)
                || actual is null || actual.Variant != expected.Variant || actual.Scope != expected.Scope || actual.Kind != expected.Kind)
                throw new InvalidDataException(reason ?? "The installed package variant or scope changed.");
            var file = FileVersionInfo.GetVersionInfo(Path.Combine(expected.ApplicationRoot, "PulseReader.dll"));
            if (new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart) != version || file.FilePrivatePart > 0)
                throw new InvalidDataException("The installed executable assembly version does not match its release inventory.");
        }
        finally { foreach (var file in held) file.Dispose(); }
    }

    private static void VerifyInstalledTerms(string root, string hash)
    {
        using var pins = new UpdatePathSafety.Pins();
        var path = Path.Combine(root, "Distribution-Terms.txt");
        pins.Parents(path);
        using var contractFile = new UpdatePathSafety.LockedFile(Path.Combine(root, "distribution-terms.json"), movable: false);
        if (contractFile.Stream.Length is <= 0 or > 4096) throw new InvalidDataException("Invalid installed distribution terms contract.");
        using var contract = System.Text.Json.JsonDocument.Parse(contractFile.Stream, new System.Text.Json.JsonDocumentOptions { MaxDepth = 4 });
        var obj = contract.RootElement;
        if (obj.ValueKind != System.Text.Json.JsonValueKind.Object || obj.EnumerateObject().Count() != 3
            || obj.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 3
            || !obj.TryGetProperty("SchemaVersion", out var schema) || !schema.TryGetInt32(out var value) || value != 1
            || !obj.TryGetProperty("TermsFileName", out var name) || name.GetString() != "Distribution-Terms.txt"
            || !obj.TryGetProperty("TermsSha256", out var checksum)
            || !string.Equals(checksum.GetString(), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed distribution terms contract does not match the accepted release.");
        using var file = new UpdatePathSafety.LockedFile(path, movable: false);
        if (file.Stream.Length is <= 0 or > 256 * 1024
            || !Convert.ToHexString(SHA256.HashData(file.Stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed distribution terms do not match the accepted release.");
    }

    private static void AssertCoherentForRelaunch(PreparedUpdate update)
    {
        try { AssertInstallation(update.Installation, update.Installation.Version); }
        catch (InvalidDataException) { AssertInstallation(update.Installation, update.Offer.Version); }
    }

    private static void AssertNoOtherApplications(string root)
    {
        foreach (var process in Process.GetProcessesByName("PulseReader"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.HasExited) continue;
                    var path = process.MainModule?.FileName ?? throw new UnauthorizedAccessException("Cannot inspect a Pulse Reader process.");
                    if (path.Equals(Path.Combine(root, "PulseReader.exe"), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Another Pulse Reader process is using this installation. Close it before retrying.");
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                { throw new IOException("Another Pulse Reader process could not be inspected. Close all copies of this installation before retrying.", error); }
            }
        }
    }

    private static void Relaunch(string root)
    {
        UpdateHandoff.AssertNormalUser();
        AssertNoOtherApplications(root);
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "PulseReader.exe"))
        { UseShellExecute = false, WorkingDirectory = root })
            ?? throw new IOException("The updated application could not be relaunched. Start PulseReader.exe manually.");
    }

    private static void WriteLog(string operation, string message)
    {
        var path = Path.Combine(operation, "update-helper.log");
        using var pins = new UpdatePathSafety.Pins();
        pins.Parents(path);
        if (File.Exists(path)) UpdatePathSafety.AssertRegular(path, directory: false);
        UpdateOperationLog.Append(path, DateTimeOffset.UtcNow.ToString("O") + " " + message);
    }

    private static void TryLog(string operation, string message)
    {
        if (operation.Length == 0) return;
        try { WriteLog(operation, message); } catch { /* Preserve the original failure. */ }
    }
}
