using PulseReader.Startup;

internal static class StartupRegistrationChecks
{
    public static void Run()
    {
        const string executablePath = @"C:\Program Files\Pulse Reader\PulseReader.exe";
        const string expectedCommand =
            "\"C:\\Program Files\\Pulse Reader\\PulseReader.exe\" --startup";

        Assert(
            WindowsStartupRegistrationService.BuildCommand(executablePath) == expectedCommand,
            "The Windows startup command did not quote the executable path safely.");
        Assert(
            PulseReader.Program.IsStartupLaunch(["--STARTUP"])
            && !PulseReader.Program.IsStartupLaunch(["--other"]),
            "The application did not recognize startup launches case-insensitively.");

        var registry = new FakeStartupRegistry();
        var service = new WindowsStartupRegistrationService(registry, executablePath);
        var initialState = service.GetState();
        Assert(
            !initialState.IsEnabled && !initialState.HasRegistration && initialState.Warning is null,
            "A missing Run entry was not reported as disabled.");

        Assert(service.SetEnabled(true) is null, "A valid startup entry could not be enabled.");
        Assert(
            registry.Read(WindowsStartupRegistrationService.ValueName) == expectedCommand
            && service.GetState() is { IsEnabled: true, HasRegistration: true, Warning: null },
            "Enabling startup did not persist and verify the expected command.");

        registry.Write(
            WindowsStartupRegistrationService.ValueName,
            "\"C:\\Old Pulse Reader\\PulseReader.exe\" --startup");
        var staleState = service.GetState();
        Assert(
            !staleState.IsEnabled && staleState.HasRegistration && staleState.Warning is not null,
            "A stale startup path was not distinguished from the current executable.");
        Assert(
            service.SetEnabled(true) is null
            && registry.Read(WindowsStartupRegistrationService.ValueName) == expectedCommand,
            "Re-enabling startup did not repair a stale executable path.");

        var readFailureRegistry = new FakeStartupRegistry
        {
            ReadFailure = new IOException("Read failed after the write for test.")
        };
        var readFailureService = new WindowsStartupRegistrationService(
            readFailureRegistry,
            executablePath);
        Assert(
            readFailureService.SetEnabled(true) is null
            && readFailureRegistry.Peek(WindowsStartupRegistrationService.ValueName) == expectedCommand,
            "A verification read failure overruled a successful startup registration write.");
        Assert(
            readFailureService.SetEnabled(false) is null
            && readFailureRegistry.Peek(WindowsStartupRegistrationService.ValueName) is null,
            "A verification read failure overruled a successful startup registration deletion.");

        Assert(service.SetEnabled(false) is null, "A valid startup entry could not be disabled.");
        Assert(
            registry.Read(WindowsStartupRegistrationService.ValueName) is null
            && service.GetState() is { IsEnabled: false, HasRegistration: false },
            "Disabling startup did not remove only the Pulse Reader value.");

        registry.Failure = new UnauthorizedAccessException("Access denied for test.");
        Assert(
            service.SetEnabled(true)?.Contains("could not update", StringComparison.OrdinalIgnoreCase) == true,
            "A registry write failure did not return a user-facing error.");

        var overlongPath = @"C:\" + new string('a', 250) + "\\PulseReader.exe";
        AssertThrows<InvalidOperationException>(
            () => WindowsStartupRegistrationService.BuildCommand(overlongPath),
            "An overlong Windows Run command was accepted.");

        Console.WriteLine("PASS: Windows startup registration is quoted, reversible, and test-isolated.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class FakeStartupRegistry : IStartupRegistry
    {
        private readonly Dictionary<string, string> _values =
            new(StringComparer.OrdinalIgnoreCase);

        public Exception? Failure { get; set; }

        public Exception? ReadFailure { get; set; }

        public string? Read(string valueName)
        {
            ThrowIfFailed();
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return _values.GetValueOrDefault(valueName);
        }

        public string? Peek(string valueName) => _values.GetValueOrDefault(valueName);

        public void Write(string valueName, string command)
        {
            ThrowIfFailed();
            _values[valueName] = command;
        }

        public void Delete(string valueName)
        {
            ThrowIfFailed();
            _values.Remove(valueName);
        }

        private void ThrowIfFailed()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }
}
