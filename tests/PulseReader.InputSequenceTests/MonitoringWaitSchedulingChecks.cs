using PulseReader.Monitoring;

internal static class MonitoringWaitSchedulingChecks
{
    internal static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var watchdog = now.AddSeconds(2);
        var expiredProbe = now.AddSeconds(-10);
        Equal(WindowTextMonitoringSession.GetBaseWaitTimeout(now, watchdog, expiredProbe, true, true), TimeSpan.FromSeconds(2),
            "An expired disabled Codex editor probe forced a zero-timeout capture loop.");
        Equal(WindowTextMonitoringSession.GetBaseWaitTimeout(now.AddSeconds(1), watchdog, expiredProbe, true, true), TimeSpan.FromSeconds(1),
            "A disabled editor probe remained scheduled on the next iteration.");
        Equal(WindowTextMonitoringSession.GetBaseWaitTimeout(now, watchdog, expiredProbe, true, false), TimeSpan.Zero,
            "A due foreground generic editor probe was delayed.");
        Equal(WindowTextMonitoringSession.GetBaseWaitTimeout(now, watchdog, now.AddMilliseconds(125), true, false), TimeSpan.FromMilliseconds(125),
            "Foreground generic editor polling lost its deadline.");
        foreach (var codex in new[] { false, true })
            Equal(WindowTextMonitoringSession.GetBaseWaitTimeout(now, watchdog, expiredProbe, false, codex), TimeSpan.FromSeconds(2),
                "A background editor probe forced unnecessary capture work.");
        Equal(WindowTextMonitoringSession.GetBaseWaitTimeout(now, now.AddSeconds(-1), expiredProbe, true, true), TimeSpan.Zero,
            "A due Codex watchdog was delayed.");
        Console.WriteLine("PASS: disabled Codex editor probes do not spin the capture loop; watchdog and generic editor deadlines remain active.");
    }

    private static void Equal(TimeSpan actual, TimeSpan expected, string message)
    { if (actual != expected) throw new InvalidOperationException(message); }
}
