using System.Text.Json;
using PulseReader.Monitoring;

internal static class MonitoringPerformanceDiagnosticChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseReader-performance-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bounded = Path.Combine(root, "bounded.jsonl");
        var performance = Path.Combine(root, "performance.jsonl");
        var modePath = Path.Combine(root, "mode.jsonl");
        try
        {
            using (var writer = new BoundedDiagnosticWriter(bounded, 1024))
            {
                for (var index = 0; index < 100; index++)
                    writer.Write(new { kind = "fixture", index, padding = new string('x', 100) });
                Require(writer.FailureCount == 0, "Bounded diagnostics failed during normal rotation.");
                writer.Write(new { padding = new string('x', 4096) });
            }
            var retained = new[] { bounded, bounded + ".1", bounded + ".2" };
            Require(retained.All(File.Exists), "Expected exactly the current log and two retained archives.");
            foreach (var file in retained)
            {
                Require(new FileInfo(file).Length <= 1024, "A diagnostic file exceeded its byte cap.");
                foreach (var line in File.ReadAllLines(file)) using (JsonDocument.Parse(line)) { }
            }
            Require(File.ReadAllText(bounded).Contains("diagnostic-record-omitted", StringComparison.Ordinal),
                "An oversized record was not explicitly marked as omitted.");
#if DEBUG
            using (var mode = WindowMonitoringDiagnosticLog.CreateFromArguments(
                [WindowMonitoringDiagnosticLog.PerformanceOnlyArgumentName, modePath]))
                Require(mode is not null && !mode.IncludeStructure, "Performance-only mode still requests structural capture.");
            using (var diagnostics = new MonitoringPerformanceDiagnostics(performance, startTimer: false))
            {
                diagnostics.Attached(IntPtr.Zero);
                using var first = diagnostics.TrackWorker(IntPtr.Zero);
                first.SetStage("read-snapshot");
                diagnostics.Signal(); diagnostics.Signal();
                diagnostics.Snapshot(new WindowTextSnapshot([new("fixture-key", "Private fixture words.")]));
                diagnostics.Queue(new(false, true, 3, HasManualPlayback: true,
                    ManualPlacement: new(new Rectangle(0, 0, 1920, 1080), new Rectangle(10, 20, 800, 600), new IntPtr(123)),
                    IsManualPlaybackActive: true));
                diagnostics.Sample();
                diagnostics.Detached();
                // A worker blocked in provider cleanup must remain visible even
                // after the UI has unlatched. The sampler does no UIA calls.
                first.SetStage("cleanup");
                diagnostics.Sample();
                using (var late = diagnostics.TrackWorker(IntPtr.Zero)) diagnostics.Sample();
                first.Dispose();
                diagnostics.Sample();
            }
            var entries = File.ReadAllLines(performance).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var samples = entries.Where(entry => entry.RootElement.GetProperty("kind").GetString() == "performance-sample")
                    .Select(entry => entry.RootElement).ToArray();
                Require(samples.Length == 4, "Performance samples were lost or failed.");
                var state = samples[0].GetProperty("state");
                Require(state.GetProperty("attached").GetBoolean() && state.GetProperty("activeWorkers").GetInt32() == 1
                    && state.GetProperty("signalsSinceSample").GetInt64() == 2
                    && state.GetProperty("snapshotsSinceSample").GetInt64() == 1
                    && state.GetProperty("blocks").GetInt32() == 1
                    && state.GetProperty("queue").GetProperty("PendingItemCount").GetInt32() == 3
                    && state.GetProperty("queue").GetProperty("HasManualPlacement").GetBoolean()
                    && state.GetProperty("queue").GetProperty("IsManualPlaybackActive").GetBoolean()
                    && !state.GetProperty("queue").TryGetProperty("ManualPlacement", out _),
                    "Performance sample lost source/worker/event/snapshot/queue accounting.");
                var detached = samples[1].GetProperty("state");
                Require(!detached.GetProperty("attached").GetBoolean() && detached.GetProperty("activeWorkers").GetInt32() == 1
                    && detached.GetProperty("workers")[0].GetProperty("stage").GetString() == "cleanup"
                    && detached.GetProperty("signalsSinceSample").GetInt64() == 0
                    && detached.GetProperty("snapshotsSinceSample").GetInt64() == 0,
                    "Detachment hid an active cleanup worker or failed to reset sample counters.");
                Require(!samples[2].GetProperty("state").GetProperty("attached").GetBoolean()
                    && samples[2].GetProperty("state").GetProperty("activeWorkers").GetInt32() == 2,
                    "A late worker incorrectly changed the attachment state.");
                Require(samples[3].GetProperty("state").GetProperty("activeWorkers").GetInt32() == 0,
                    "Completed worker remained registered.");
                Require(!File.ReadAllText(performance).Contains("Private fixture words", StringComparison.Ordinal)
                    && samples[0].GetProperty("processes").GetArrayLength() >= 1,
                    "Performance sampling exposed text or failed to measure the reader process.");
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
#endif
            Console.WriteLine("PASS: bounded diagnostic rotation and performance worker/counter/privacy/recovery sampling.");
        }
        finally
        {
            foreach (var path in new[] { bounded, performance, modePath, modePath + ".performance.jsonl" })
                foreach (var suffix in new[] { "", ".1", ".2" }) File.Delete(path + suffix);
            Directory.Delete(root);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
