using System.Runtime.InteropServices;
using PulseReader.Monitoring;

internal static class MonitorWorkerLifecycleChecks
{
    public static void Run()
    {
        foreach (var mode in new[] { "runtime", "startup", "cleanup", "unexpected", "cancelled", "managed", "unavailable" })
        {
            using var session = new WindowTextMonitoringSession(new IntPtr(1));
            var stopped = new List<MonitoringWorkerStoppedEventArgs>();
            var cleaned = false;
            var detachedBeforeCleanup = false;
            var originalVersion = session.ContextVersion;
            session.WorkerStopped += (_, e) => stopped.Add(e);
            session.ExecuteWorker(() => {
                if (mode == "unavailable") session.CompleteStartup(WindowMonitoringStartResult.Failure("No accessible surface."));
                else if (mode != "startup") session.CompleteStartup(WindowMonitoringStartResult.Success);
                if (mode is "runtime" or "startup")
                    throw new COMException("Synthetic provider fault.", unchecked((int)0x80040201));
                if (mode == "managed") throw new FormatException("Synthetic unexpected managed fault.");
                if (mode == "cancelled") session.Dispose();
            }, () => {
                cleaned = true;
                detachedBeforeCleanup = session.IsContextSuspended && session.ContextVersion > originalVersion
                    && stopped.Count == 1;
                if (mode == "cleanup") throw new IOException("Synthetic cleanup fault.");
            });
            var expectedReason = mode == "cancelled" ? "cancelled" : mode is "unexpected" or "cleanup" ? "unexpected-exit"
                : mode == "unavailable" ? "startup-failed" : "failure";
            if (!cleaned || stopped.Count != 1 || stopped[0].Reason != expectedReason
                || stopped[0].WasStarted != (mode is not ("startup" or "unavailable")) || !session.IsContextSuspended
                || session.ContextVersion <= originalVersion)
                throw new InvalidOperationException("A capture-worker exit left a stale latch or lost its reason: " + mode);
            if (!detachedBeforeCleanup)
                throw new InvalidOperationException("Worker termination waited for provider cleanup before invalidating/notifying the latch: " + mode);
            var emitted = 0;
            session.TextAvailable += (_, _) => emitted++;
            var changedAt = DateTimeOffset.MinValue;
            session.AppendChanges([new("No stale sentence should be emitted.", true, true)], ref changedAt);
            if (emitted != 0) throw new InvalidOperationException("An exited monitor still published stale text.");
        }
        Console.WriteLine("PASS: unexpected monitor worker failures notify their owner and invalidate the latch.");
    }
}
