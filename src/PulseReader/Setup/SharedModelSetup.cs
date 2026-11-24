using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace PulseReader.Setup;

// This protocol accepts only a fixed model kind and a random session ID. No paths,
// URLs, executable names, or downloaded commands cross the privilege boundary.
internal static class SharedModelSetup
{
    private static readonly TimeSpan MaximumSetupTime = TimeSpan.FromHours(4);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(45);
    private static string PipeName(Guid session) => "PulseReader-model-setup-" + session.ToString("N");

    internal static async Task EnsureAsync(InstallationLayout layout, string kind,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        layout.AssertTrustedMachineInstallation(false);
        cancellationToken.ThrowIfCancellationRequested();
        var session = Guid.NewGuid();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Required for UAC's alternative-administrator-credentials flow. Both ends
        // additionally attest the peer PID against the protected installed binary.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(PipeName(session), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            4096, 4096, security);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(MaximumSetupTime);
        Process? child = null;
        try
        {
            progress?.Report(new("Shared model setup", 0, null, "Administrator approval"));
            // ShellExecute may wait for the UAC UI. Keep it off the caller's UI thread;
            // the pipe lifetime still closes on cancellation if consent arrives later.
            var start = new ProcessStartInfo(layout.ExecutablePath)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = layout.ApplicationRoot,
                Arguments = $"--setup-shared-model {kind} {session:N}", WindowStyle = ProcessWindowStyle.Hidden
            };
            child = await StartElevatedAsync(start, lifetime.Token).ConfigureAwait(false);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connect.CancelAfter(ConnectionTimeout);
            await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || peer != child.Id)
                throw new UnauthorizedAccessException("Shared setup connected to an unexpected process.");
            while (true)
            {
                var message = await ReadAsync(pipe, lifetime.Token).ConfigureAwait(false);
                if (message.Type == "progress" && message.Progress is not null) progress?.Report(message.Progress);
                else if (message.Type == "complete")
                {
                    if (message.Code != 0) throw new InvalidOperationException(message.Error ?? "Shared model setup failed.");
                    await child.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
                    if (child.ExitCode != 0) throw new InvalidOperationException("Shared model setup did not exit successfully.");
                    return;
                }
                else throw new InvalidDataException("Shared model setup sent an invalid message.");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Administrator approval was cancelled.", exception, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Shared model setup timed out. Please retry."); }
        finally
        {
            // Closing the pipe is the reliable cancellation signal even when the
            // normal reader cannot terminate an alternate user's elevated process.
            pipe.Dispose();
            if (child is not null)
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                catch (TimeoutException) { /* Helper also enforces its own bounded lifetime. */ }
                finally { child.Dispose(); }
            }
        }
    }

    private static async Task<Process> StartElevatedAsync(ProcessStartInfo start, CancellationToken token)
    {
        var pending = Task.Run(() => Process.Start(start)
            ?? throw new InvalidOperationException("The administrator setup process could not be started."));
        try { return await pending.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            // Consent can finish after cancellation. The abandoned server has been
            // disposed; the helper cannot connect and exits without installing.
            _ = pending.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                else _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    internal static async Task<int> RunHelperAsync(string kind, string sessionGuid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateKind(kind);
            if (!Guid.TryParseExact(sessionGuid, "N", out var session)) return 2;
            var layout = InstallationLayout.Current;
            layout.AssertTrustedMachineInstallation(true);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(MaximumSetupTime);
            using var pipe = new NamedPipeClientStream(".", PipeName(session), PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            using (var connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                connection.CancelAfter(ConnectionTimeout);
                await pipe.ConnectAsync(connection.Token).ConfigureAwait(false);
            }
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var peer)) return 3;
            using (var process = Process.GetProcessById(checked((int)peer)))
                if (!string.Equals(process.MainModule?.FileName, layout.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    return 3;
            // No setup happens before the installed reader has been attested.
            return await ServeAsync(pipe, kind, new ModelSetupService(layout, sharedHelper: true), lifetime,
                layout.NormalizeSharedOwners)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return 4; }
        catch { return 1; }
    }

    internal static async Task<int> ServeAsync(Stream pipe, string kind, ModelSetupService setup,
        CancellationTokenSource lifetime, Action? finalizeOwnership = null)
    {
        ValidateKind(kind);
        var messages = Channel.CreateBounded<Message>(new BoundedChannelOptions(64)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
        var monitor = MonitorDisconnectAsync(pipe, lifetime);
        var writer = WriteMessagesAsync(pipe, messages.Reader, lifetime);
        var code = 0;
        string? error = null;
        try
        {
            var progress = new InlineProgress(value => messages.Writer.TryWrite(new("progress", value)));
            if (kind == "kokoro") await setup.EnsureKokoroAsync(progress, lifetime.Token).ConfigureAwait(false);
            else await setup.EnsureContextualPronunciationAsync(progress, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { code = 4; error = "Shared model setup was cancelled."; }
        catch (Exception exception) { code = 1; error = exception.Message[..Math.Min(exception.Message.Length, 1024)]; }
        try { finalizeOwnership?.Invoke(); }
        catch (Exception exception) { code = 1; error = exception.Message[..Math.Min(exception.Message.Length, 1024)]; }
        messages.Writer.TryWrite(new("complete", Code: code, Error: error));
        messages.Writer.TryComplete();
        try { await writer.ConfigureAwait(false); }
        finally
        {
            lifetime.Cancel();
            await monitor.ConfigureAwait(false);
        }
        return code;
    }

    private static async Task MonitorDisconnectAsync(Stream pipe, CancellationTokenSource lifetime)
    {
        try
        {
            // The reader sends no commands. Any input or EOF cancels setup.
            await pipe.ReadAsync(new byte[1], lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { lifetime.Cancel(); }
    }

    private static async Task WriteMessagesAsync(Stream pipe, ChannelReader<Message> messages,
        CancellationTokenSource lifetime)
    {
        try
        {
            await foreach (var message in messages.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                await WriteAsync(pipe, message, lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        { lifetime.Cancel(); }
    }

    internal sealed record Message(string Type, SetupProgress? Progress = null, int Code = 0, string? Error = null);
    internal static async Task WriteAsync(Stream stream, Message message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > 4096) throw new InvalidDataException("Shared setup message is too large.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    internal static async Task<Message> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length is < 1 or > 4096) throw new InvalidDataException("Shared setup message size is invalid.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Message>(body) ?? throw new InvalidDataException("Invalid shared setup message.");
    }
    private static void ValidateKind(string kind)
    {
        if (kind is not ("kokoro" or "contextual")) throw new ArgumentException("Unknown shared model kind.", nameof(kind));
    }
    private sealed class InlineProgress(Action<SetupProgress> report) : IProgress<SetupProgress>
    { public void Report(SetupProgress value) => report(value); }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
