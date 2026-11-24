using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using PulseReader.Monitoring;

internal static class MonitorExitDiagnosticChecks
{
    public static void Run()
    {
#if DEBUG
        var directory = Path.Combine(Path.GetTempPath(), $"pulse-reader-worker-diagnostics-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "worker.jsonl");
        const string secret = "selected-secret-content";
        const string secretPath = @"C:\sensitive-user-directory\private-document.txt";
        var failure = CaptureFailure($"{secret}: {secretPath}");
        try
        {
            var diagnostics = WindowMonitoringDiagnosticLog.CreateFromArguments(
                [WindowMonitoringDiagnosticLog.ArgumentName, path])
                ?? throw new InvalidOperationException("Diagnostic arguments were not recognised.");
            using (diagnostics)
            {
                diagnostics.RecordWorkerFailure("snapshot-read", failure);
                diagnostics.RecordWorkerFailure("snapshot-read", failure);
                diagnostics.RecordWorkerStopped("worker-failed", "snapshot-read", started: true);
                diagnostics.RecordWorkerStopped("startup-failed", "automation-create", started: false);
                diagnostics.RecordWorkerFailure("bounded-fixture", CaptureDeepFailure(secret));
            }

            // Cleanup can race disposal. These calls must not write or throw.
            diagnostics.RecordWorkerFailure("after-dispose", failure);
            diagnostics.RecordWorkerStopped("after-dispose", "cleanup", started: true);

            var lines = File.ReadAllLines(path);
            Assert(lines.Length == 6, "Worker records were missing or disposal allowed extra writes.");
            using var first = JsonDocument.Parse(lines[1]);
            using var second = JsonDocument.Parse(lines[2]);
            var root = first.RootElement;
            Assert(root.GetProperty("kind").GetString() == "worker-failure"
                   && root.GetProperty("stage").GetString() == "snapshot-read",
                "Worker failure did not retain its kind and stage.");
            AssertException(root.GetProperty("exception"), failure);
            Assert(root.GetProperty("exception").GetProperty("messageHash").GetString()
                   == second.RootElement.GetProperty("exception").GetProperty("messageHash").GetString(),
                "Identical exception messages did not correlate within one diagnostic run.");
            var inner = root.GetProperty("innerExceptions");
            Assert(inner.GetArrayLength() == 1, "Worker failure lost its inner exception.");
            AssertException(inner[0], failure.InnerException!);
            Assert(inner[0].GetProperty("hResult").GetString() == "0x80040201",
                "Worker failure lost the UIA HRESULT.");
            Assert(inner[0].GetProperty("frames").EnumerateArray().Any(frame =>
                    frame.GetProperty("method").GetString() == nameof(ThrowPrivateFailure)
                    && frame.GetProperty("declaringType").GetString() == typeof(MonitorExitDiagnosticChecks).FullName
                    && frame.GetProperty("ilOffset").ValueKind == JsonValueKind.Number),
                "Worker failure did not retain safe method and IL-offset evidence.");
            Assert(!root.GetProperty("innerExceptionsTruncated").GetBoolean(),
                "A short exception chain was marked as truncated.");

            using var stopped = JsonDocument.Parse(lines[3]);
            Assert(stopped.RootElement.GetProperty("kind").GetString() == "worker-stopped"
                   && stopped.RootElement.GetProperty("reason").GetString() == "worker-failed"
                   && stopped.RootElement.GetProperty("stage").GetString() == "snapshot-read"
                   && stopped.RootElement.GetProperty("started").GetBoolean(),
                "Worker completion lost its reason, stage, or started state.");
            using var startupStopped = JsonDocument.Parse(lines[4]);
            Assert(!startupStopped.RootElement.GetProperty("started").GetBoolean(),
                "Worker startup failure was recorded as having started.");
            using var bounded = JsonDocument.Parse(lines[5]);
            Assert(bounded.RootElement.GetProperty("innerExceptions").GetArrayLength() == 4
                   && bounded.RootElement.GetProperty("innerExceptionsTruncated").GetBoolean(),
                "Worker failure did not bound the inner exception chain.");
            Assert(bounded.RootElement.GetProperty("exception").GetProperty("frames").GetArrayLength() == 32
                   && bounded.RootElement.GetProperty("exception").GetProperty("framesTruncated").GetBoolean(),
                "Worker failure did not bound stack frame output.");

            foreach (var line in lines)
            {
                using var entry = JsonDocument.Parse(line);
                AssertPrivateValuesAbsent(entry.RootElement, secret, "sensitive-user-directory", "private-document.txt", "private-source-path");
            }
            Console.WriteLine("PASS: worker diagnostics retain bounded failure and exit evidence without private messages or source paths.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
#endif
    }

#if DEBUG
    private static void AssertException(JsonElement entry, Exception exception)
    {
        Assert(entry.GetProperty("type").GetString() == exception.GetType().FullName
               && entry.GetProperty("hResult").GetString() == $"0x{exception.HResult:X8}"
               && entry.GetProperty("messageLength").GetInt32() == exception.Message.Length
               && entry.GetProperty("messageHash").GetString()?.Length == 20,
            "Worker failure lost safe exception metadata.");
    }

    private static void AssertPrivateValuesAbsent(JsonElement element, params string[] privateValues)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString()!;
            Assert(!privateValues.Any(secret => value.Contains(secret, StringComparison.Ordinal)),
                "Worker diagnostics exposed private exception content or a source path.");
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                AssertPrivateValuesAbsent(property.Value, privateValues);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                AssertPrivateValuesAbsent(child, privateValues);
            }
        }
    }

    private static Exception CaptureFailure(string message)
    {
        try
        {
            try
            {
                ThrowPrivateFailure(message);
                throw new InvalidOperationException("The failure fixture unexpectedly returned.");
            }
            catch (COMException inner)
            {
                throw new InvalidOperationException(message, inner);
            }
        }
        catch (InvalidOperationException failure)
        {
            return failure;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrivateFailure(string message)
    {
#line 1 "C:\private-source-path\private-document.txt"
        throw new COMException(message, unchecked((int)0x80040201));
#line default
    }

    private static Exception CaptureDeepFailure(string message)
    {
        Exception inner = new COMException(message);
        for (var index = 0; index < 8; index++)
        {
            inner = new InvalidOperationException(message, inner);
        }
        try
        {
            ThrowDeepFailure(64, new InvalidOperationException(message, inner));
            throw new InvalidOperationException("The deep failure fixture unexpectedly returned.");
        }
        catch (InvalidOperationException failure)
        {
            return failure;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDeepFailure(int depth, Exception failure)
    {
        if (depth == 0)
        {
            throw failure;
        }
        ThrowDeepFailure(depth - 1, failure);
        GC.KeepAlive(failure);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
#endif
}
