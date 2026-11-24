using PulseReader.Setup;

internal static class StartupFailureChecks
{
    internal static void Run()
    {
        using var fixture = new Fixture();
        foreach (var startupLaunch in new[] { false, true })
        {
            CheckMarkerFailure(fixture, new string(' ', 257), startupLaunch,
                "The installation scope marker is invalid.");
            CheckMarkerFailure(fixture, "{\"SchemaVersion\":2,\"Scope\":\"user\"}", startupLaunch,
                "The installation scope marker is invalid.");
            CheckMarkerFailure(fixture, "{\"SchemaVersion\":1,\"Scope\":\"machine\"}", startupLaunch,
                "An all-users installation must use the protected Program Files location.");
            CheckMarkerFailure(fixture, "{", startupLaunch, null);
        }

        var diagnosticCount = 0;
        var reportCount = 0;
        var exitCode = StartupFailureBoundary.Run(() => 0, false,
            _ => diagnosticCount++, _ => reportCount++);
        Assert(exitCode == 0 && diagnosticCount == 0 && reportCount == 0,
            "Successful startup was reported as a failure.");
        exitCode = StartupFailureBoundary.Run(() => 7, false,
            _ => diagnosticCount++, _ => reportCount++);
        Assert(exitCode == 7, "The startup boundary changed a caller's failure exit code.");

        var unexpected = new NullReferenceException("Unexpected programming error probe.");
        try
        {
            StartupFailureBoundary.Run(() => throw unexpected, false,
                _ => diagnosticCount++, _ => reportCount++);
            throw new InvalidOperationException("The startup boundary suppressed an unexpected programming error.");
        }
        catch (NullReferenceException exception) when (ReferenceEquals(exception, unexpected)) { }
        Assert(diagnosticCount == 0 && reportCount == 0,
            "An unexpected programming error was treated as an installation failure.");
        Console.WriteLine("PASS: real invalid scope markers return failure with diagnostics, manual startup reports, and quiet sign-in startup.");
    }

    private static void CheckMarkerFailure(Fixture fixture, string marker, bool startupLaunch,
        string? expectedMessage)
    {
        File.WriteAllText(Path.Combine(fixture.ApplicationRoot, InstallationLayout.ScopeFileName), marker);
        var diagnostics = new List<Exception>();
        var reports = new List<string>();
        var startupCompleted = false;
        int exitCode;
        try
        {
            exitCode = StartupFailureBoundary.Run(() =>
            {
                // This is the layout resolution performed during the application
                // context's ModelSetupService field initialization, using fixture roots.
                var layout = InstallationLayout.Resolve(fixture.ApplicationRoot,
                    Path.Combine(fixture.Root, "User"), Path.Combine(fixture.Root, "ProgramFiles"),
                    Path.Combine(fixture.Root, "ProgramData"));
                _ = new ModelSetupService(layout);
                startupCompleted = true;
                return 0;
            }, startupLaunch, diagnostics.Add, reports.Add);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The startup error boundary let an invalid installation scope marker escape.", exception);
        }

        Assert(exitCode != 0 && !startupCompleted,
            "Invalid installation metadata was allowed to complete startup successfully.");
        Assert(diagnostics.Count == 1, "Startup failure omitted or duplicated the diagnostic.");
        var diagnostic = diagnostics[0];
        Assert(diagnostic is InvalidDataException or System.Text.Json.JsonException,
            "The fixture did not reach installation metadata validation.");
        Assert(diagnostic.ToString().Contains(nameof(InstallationLayout.Resolve), StringComparison.Ordinal),
            "Startup diagnostics omitted the real metadata validation stack.");
        if (expectedMessage is not null)
            Assert(diagnostic.Message == expectedMessage, "Startup diagnostics omitted the metadata failure reason.");
        Assert(reports.Count == (startupLaunch ? 0 : 1),
            "Manual startup must report an installation failure and sign-in startup must stay quiet.");
        if (!startupLaunch)
            Assert(reports[0].StartsWith("Pulse Reader could not start.", StringComparison.Ordinal)
                && reports[0].Contains("installation is complete", StringComparison.Ordinal)
                && reports[0].Contains(diagnostic.Message, StringComparison.Ordinal),
                "The manual startup report omitted installation guidance or the failure reason.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PulseReaderStartupChecks-" + Guid.NewGuid().ToString("N"));
        internal string ApplicationRoot => Path.Combine(Root, "PortableApp");
        internal Fixture() => Directory.CreateDirectory(ApplicationRoot);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
