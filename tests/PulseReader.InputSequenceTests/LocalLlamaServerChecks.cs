internal static class LocalLlamaServerChecks
{
    public static async Task RunAsync()
    {
        const string context = "The axes hung beside the chart whose axes were unlabeled.";
        const string expectedFirst =
            "The <target>axes</target> hung beside the chart whose <other-occurrence/> were unlabeled.";
        const string expectedSecond =
            "The <other-occurrence/> hung beside the chart whose <target>axes</target> were unlabeled.";

        if (!string.Equals(
                LocalLlamaServer.MarkTargetOccurrence(context, "axes", 1),
                expectedFirst,
                StringComparison.Ordinal)
            || !string.Equals(
                LocalLlamaServer.MarkTargetOccurrence(context, "axes", 2),
                expectedSecond,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Local-model target marking did not isolate the requested occurrence.");
        }

        var preservedTerms = LocalProtectedTermMatcher.FindMissingTerms(
            "Use af heart at speed 1.05 when enabled is true, then call Run.",
            ["af_heart", "1.05", "true", "Run"],
            new HashSet<string>(StringComparer.Ordinal) { "af_heart" });
        var alteredTerms = LocalProtectedTermMatcher.FindMissingTerms(
            "Use af heart at speed 10.5 when enabled is untrue, then inspect runtime.",
            ["af_heart", "1.05", "true", "Run"],
            new HashSet<string>(StringComparer.Ordinal) { "af_heart" });
        if (preservedTerms.Count != 0
            || !alteredTerms.SequenceEqual(
                new[] { "1.05", "true", "Run" },
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Local-model protected-term matching did not preserve value and token boundaries.");
        }

        var probeExecutable = Path.ChangeExtension(
            typeof(LocalLlamaServerChecks).Assembly.Location,
            ".exe");
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "PulseReader.LocalLlamaServerChecks",
            Guid.NewGuid().ToString("N"));
        try
        {
            await using var server = new LocalLlamaServer(
                probeExecutable,
                Path.Combine(outputDirectory, "startup-cleanup-probe.gguf"),
                outputDirectory,
                threads: 1);
            using var startupCancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(30));
            var startupTask = server.StartAsync(startupCancellation.Token);
            var childProcessId = server.ProcessId;
            // Cancel after observing the child, not during synchronous Process.Start.
            // A busy machine can otherwise finish cleanup before this test captures its ID.
            startupCancellation.Cancel();
            try
            {
                await startupTask;
                throw new InvalidOperationException(
                    "The local-model cleanup probe unexpectedly became healthy.");
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation forces cleanup while the probe child is still running.
            }

            if (childProcessId <= 0 || server.ProcessId != 0 || IsProcessRunning(childProcessId))
            {
                throw new InvalidOperationException(
                    "A cancelled local-model startup retained its child process.");
            }
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }

        Console.WriteLine(
            "PASS: local-model target marking, protected terms, and failed-startup cleanup validate.");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
