using PulseReader.Updates;

internal static class UpdateSchedulingChecks
{
    internal static void Run()
    {
        var now = DateTimeOffset.Parse("2026-10-13T12:00:00Z");
        Require(UpdateCheckStateStore.IsDue(new(), now), "A new profile did not schedule an update check.");
        Require(!UpdateCheckStateStore.IsDue(new(LastAttempt: now - TimeSpan.FromHours(23)), now),
            "Update checks run more than once per day.");
        Require(UpdateCheckStateStore.IsDue(new(LastAttempt: now - TimeSpan.FromHours(24)), now),
            "The daily update check did not become due.");
        Require(!UpdateCheckStateStore.IsDue(new(RetryAfter: now + TimeSpan.FromMinutes(10)), now),
            "Rate-limit backoff was ignored.");
        Require(UpdateCheckStateStore.IsDue(new(LastAttempt: now + TimeSpan.FromDays(2)), now),
            "A backwards clock change prevented future checks indefinitely.");
        Require(!UpdateCheckStateStore.IsDeferred(new(RetryAfter: now + TimeSpan.FromDays(100)), now),
            "Unbounded local backoff data disabled updates indefinitely.");
        var root = Path.Combine(Path.GetTempPath(), "PulseReaderUpdateSchedule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "state.json");
            var store = new UpdateCheckStateStore(path);
            var state = new UpdateCheckState(now, now, now + TimeSpan.FromMinutes(1), "1.0.1");
            Require(store.Load() == new UpdateCheckState(), "A missing state file did not use safe defaults.");
            Require(store.TrySave(state) && store.Load() == state, "Daily state did not persist across restart.");
            File.WriteAllText(path, "not JSON");
            Require(store.Load() == new UpdateCheckState(), "Corrupt state escaped or disabled checks.");
            Require(store.TrySave(state with { AvailableVersion = "untrusted label" })
                && store.Load().AvailableVersion is null, "Unvalidated cached version text reached the menu.");
            Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "State saves left temporary files.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS: update checks persist their daily schedule, honour bounded backoff, and recover invalid state safely.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
