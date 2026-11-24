using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReader.Monitoring;

internal static class MonitoringLiveCaptureChecks
{
    public static async Task RunThinkingPanelAsync(IntPtr handle, string outputPrefix)
    {
        using var diagnostics = WindowMonitoringDiagnosticLog.CreateFromArguments(
            [WindowMonitoringDiagnosticLog.ArgumentName, outputPrefix + ".jsonl"]);
        using var session = new WindowTextMonitoringSession(handle, diagnostics);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = 0;
        string turn = "";
        session.ThinkingChanged += (_, e) =>
        {
            if (e.Snapshot is not { } thought) return;
            if (stage == 0)
            {
                if (thought.Retired || thought.Expanded || thought.Text.Length == 0) return;
                turn = thought.TurnKey;
                stage = 1;
                generationSeen.TrySetResult();
                Console.WriteLine("PANEL: collapsed preview captured but ineligible.");
            }
            else if (thought.TurnKey == turn)
            {
                if (stage == 1 && thought.Expanded && !thought.Retired && thought.Text.Length > 0)
                { stage = 2; Console.WriteLine("PANEL: expanded thinking eligible."); }
                else if (stage == 2 && !thought.Expanded && !thought.Retired)
                { stage = 3; Console.WriteLine("PANEL: collapsed thinking ineligible again."); }
                else if (stage == 3 && thought.Expanded && !thought.Retired)
                { stage = 4; Console.WriteLine("PANEL: reopened thinking eligible in the same generation."); }
                else if (stage == 4 && thought.Retired)
                { stage = 5; done.TrySetResult(); }
            }
        };
        var start = await session.StartAsync(CancellationToken.None);
        if (!start.Started) throw new InvalidOperationException(start.Error);
        Console.WriteLine("READY: generate with collapsed thinking, expand, collapse, reopen, then finish or stop generation.");
        if (await Task.WhenAny(generationSeen.Task, Task.Delay(TimeSpan.FromSeconds(75))) != generationSeen.Task)
            throw new InvalidOperationException("No new collapsed thinking generation appeared within the setup timeout.");
        if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(100))) != done.Task)
            throw new InvalidOperationException($"Live thinking panel transition check stopped at stage {stage}/5.");
        Console.WriteLine("PASS: live thinking eligibility followed collapse, expand, collapse, reopen and retirement in one generation.");
    }

    // A synthetic corpus only. Unknown captured text is never written as plaintext.
    private static readonly string[] Expected = [
        "Quartz markers identify the first working update.",
        "River markers identify the second working update.",
        "Silver markers introduce the final response.",
        "Timber gates.",
        "The first explanation describes the entrance to the garden.",
        "Umber walls.",
        "The second explanation describes the boundary of the courtyard.",
        "Velvet markers close this final response."
    ];

    public static async Task RunAsync(IntPtr handle, string outputPrefix, bool idleOnly = false, string[]? corpus = null,
        bool allowStreamedContinuation = false)
    {
        var expected = corpus ?? Expected;
        using var diagnostics = WindowMonitoringDiagnosticLog.CreateFromArguments(
            [WindowMonitoringDiagnosticLog.ArgumentName, outputPrefix + ".jsonl"]);
        using var session = new WindowTextMonitoringSession(handle, diagnostics);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var items = new List<(DateTimeOffset Time, string Text, bool Final)>();
        session.TextAvailable += (_, item) =>
        {
            lock (items) items.Add((DateTimeOffset.UtcNow, item.Text, item.BeginsCompletedResponse));
            if (item.Text == expected[^1]) complete.TrySetResult();
        };
        var start = await session.StartAsync(CancellationToken.None);
        if (!start.Started) throw new InvalidOperationException(start.Error);
        Console.WriteLine(idleOnly ? "READY: silent idle capture established; change sidebar state without sending a message."
            : "READY: silent monitoring capture established; send the controlled corpus.");
        var finished = idleOnly;
        if (idleOnly) await Task.Delay(TimeSpan.FromSeconds(45));
        else
        {
            finished = await Task.WhenAny(complete.Task, Task.Delay(TimeSpan.FromSeconds(75))) == complete.Task;
            if (finished) await Task.Delay(TimeSpan.FromSeconds(5));
        }
        (DateTimeOffset Time, string Text, bool Final)[] captured;
        lock (items) captured = items.ToArray();
        var report = captured.Select(item => new {
            item.Time,
            corpusIndex = Array.IndexOf(expected, item.Text),
            length = item.Text.Length,
            hash = Array.IndexOf(expected, item.Text) < 0 ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Text))),
            beginsFinalResponse = item.Final
        }).ToArray();
        await File.WriteAllTextAsync(outputPrefix + ".report.json", JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        if (idleOnly)
        {
            if (captured.Length != 0) throw new InvalidOperationException($"Idle monitoring captured {captured.Length} unwanted units.");
            Console.WriteLine("PASS: sidebar changes produced no speech units or replayed history.");
            return;
        }
        var finalStartIndex = corpus is null ? 2 : 0;
        var finalCount = captured.Count(item => item.Final);
        var validFinal = allowStreamedContinuation && finalCount == 0
            || finalCount == 1 && captured.Length > finalStartIndex && captured[finalStartIndex].Final;
        if (!finished || !captured.Select(item => item.Text).SequenceEqual(expected)
            || !validFinal || (allowStreamedContinuation && !session.HasCompletedResponse))
            throw new InvalidOperationException($"Controlled capture failed: {captured.Length} units; corpus indices [{string.Join(',', report.Select(item => item.corpusIndex))}]; final flags [{string.Join(',', report.Select(item => item.beginsFinalResponse))}].");
        Console.WriteLine($"PASS: {expected.Length} exact corpus units captured once in order; final-transition flags match the corpus; no trailing content.");
    }
}
