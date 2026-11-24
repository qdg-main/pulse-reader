using PulseReader.Monitoring;

internal static class MonitorStartupCancellationChecks
{
    internal static async Task RunAsync()
    {
        WindowTextMonitoringSession? delayedSession = null;
        using (delayedSession = new WindowTextMonitoringSession(new IntPtr(4), workerBodyForChecks: capture =>
        {
            capture.Read("synthetic-slow-startup", () =>
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(3500));
                return 0;
            });
            delayedSession!.CompleteStartup(WindowMonitoringStartResult.Success);
        }))
        {
            var delayedResult = await delayedSession.StartAsync(CancellationToken.None);
            Require(delayedResult.Started, "A successful capture taking longer than three seconds was rejected before the eight-second deadline.");
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stopped = new TaskCompletionSource<MonitoringWorkerStoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuedAfterTimeout = false;
        using var session = new WindowTextMonitoringSession(new IntPtr(1), workerBodyForChecks: capture =>
        {
            capture.Read("synthetic-blocked-provider", () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Synthetic provider was not released.");
                return 0;
            });
            continuedAfterTimeout = true;
        });
        session.WorkerStopped += (_, result) => stopped.TrySetResult(result);
        try
        {
            var result = await session.StartAsync(CancellationToken.None);
            Require(entered.IsSet && !result.Started && result.Error!.Contains("quickly enough", StringComparison.Ordinal),
                "Blocked capture did not return the real startup timeout.");
            Require(!session.CompleteStartup(WindowMonitoringStartResult.Success), "A late capture converted timed-out startup into success.");
            var retryRan = false;
            using var retry = new WindowTextMonitoringSession(new IntPtr(2), workerBodyForChecks: _ => retryRan = true);
            var retryResult = await retry.StartAsync(CancellationToken.None);
            Require(!retryResult.Started && retryResult.Error!.Contains("previous accessibility capture", StringComparison.Ordinal) && !retryRan,
                "A retry started more provider work while the timed-out worker was alive.");
            release.Set();
            var workerResult = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!continuedAfterTimeout && !workerResult.WasStarted && workerResult.Reason == "cancelled",
                "Timed-out capture established state/subscriptions or was reported as a successful latch.");
            using var nextLease = await MonitoringCaptureGate.TryEnterAsync(CancellationToken.None);
            Require(nextLease is not null, "Capture admission did not recover after the old worker finished cleanup.");
        }
        finally { release.Set(); }

        using var callerEntered = new ManualResetEventSlim();
        using var callerRelease = new ManualResetEventSlim();
        var callerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerContinued = false;
        using var callerCancellation = new CancellationTokenSource();
        using var callerSession = new WindowTextMonitoringSession(new IntPtr(3), workerBodyForChecks: capture =>
        {
            capture.Read("synthetic-caller-cancel", () =>
            {
                callerEntered.Set();
                if (!callerRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Caller fixture was not released.");
                return 0;
            });
            callerContinued = true;
        });
        callerSession.WorkerStopped += (_, _) => callerStopped.TrySetResult();
        try
        {
            var start = callerSession.StartAsync(callerCancellation.Token);
            Require(callerEntered.Wait(TimeSpan.FromSeconds(2)), "Caller-cancellation capture did not enter its provider.");
            callerCancellation.Cancel();
            try { await start; throw new InvalidOperationException("Caller cancellation was ignored."); }
            catch (OperationCanceledException) { }
            callerRelease.Set();
            await callerStopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!callerContinued, "Caller cancellation only stopped waiting while capture continued.");
        }
        finally { callerRelease.Set(); }
        Console.WriteLine("PASS: startup timeout/caller cancellation reject late work and prevent overlapping capture retries until worker cleanup completes.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
