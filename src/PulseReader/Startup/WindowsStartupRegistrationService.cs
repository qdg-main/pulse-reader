using Microsoft.Win32;
using System.Security;

namespace PulseReader.Startup;

internal sealed record StartupRegistrationState(
    bool IsEnabled,
    bool HasRegistration,
    string? Warning);

internal interface IStartupRegistry
{
    string? Read(string valueName);

    void Write(string valueName, string command);

    void Delete(string valueName);
}

internal sealed class WindowsStartupRegistrationService
{
    internal const string StartupArgument = "--startup";
    internal const string ValueName = "Pulse Reader";
    internal const int MaximumCommandLength = 260;

    private readonly IStartupRegistry _registry;
    private readonly string? _command;
    private readonly string? _initializationError;

    public WindowsStartupRegistrationService()
        : this(
            new CurrentUserRunRegistry(),
            Environment.ProcessPath ?? Application.ExecutablePath)
    {
    }

    internal WindowsStartupRegistrationService(
        IStartupRegistry registry,
        string executablePath)
    {
        _registry = registry;

        try
        {
            _command = BuildCommand(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or IOException
                                          or SecurityException
                                          or NotSupportedException)
        {
            _initializationError = exception.Message;
        }
    }

    public StartupRegistrationState GetState()
    {
        if (_initializationError is not null || _command is null)
        {
            return new StartupRegistrationState(false, false, _initializationError);
        }

        try
        {
            var registeredCommand = _registry.Read(ValueName);
            if (registeredCommand is null)
            {
                return new StartupRegistrationState(false, false, null);
            }

            if (string.Equals(registeredCommand, _command, StringComparison.OrdinalIgnoreCase))
            {
                return new StartupRegistrationState(true, true, null);
            }

            return new StartupRegistrationState(
                false,
                true,
                "The Windows startup entry points to a different Pulse Reader location. Enable it again to update the entry.");
        }
        catch (Exception exception) when (IsRegistryException(exception))
        {
            return new StartupRegistrationState(
                false,
                false,
                $"Pulse Reader could not read the Windows startup setting: {exception.Message}");
        }
    }

    public string? SetEnabled(bool enabled)
    {
        if (_initializationError is not null || _command is null)
        {
            return _initializationError ?? "Pulse Reader could not determine its executable path.";
        }

        try
        {
            if (enabled)
            {
                _registry.Write(ValueName, _command);
            }
            else
            {
                _registry.Delete(ValueName);
            }

            return null;
        }
        catch (Exception exception) when (IsRegistryException(exception))
        {
            return $"Pulse Reader could not update the Windows startup setting: {exception.Message}";
        }
    }

    internal static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "Pulse Reader needs an absolute executable path for Windows startup.",
                nameof(executablePath));
        }

        if (executablePath.Contains('"'))
        {
            throw new ArgumentException(
                "The Pulse Reader executable path contains an unsupported quote character.",
                nameof(executablePath));
        }

        var command = $"\"{Path.GetFullPath(executablePath)}\" {StartupArgument}";
        if (command.Length > MaximumCommandLength)
        {
            throw new InvalidOperationException(
                $"The Pulse Reader startup command exceeds Windows' {MaximumCommandLength}-character limit.");
        }

        return command;
    }

    private static bool IsRegistryException(Exception exception) =>
        exception is UnauthorizedAccessException
            or SecurityException
            or IOException
            or PlatformNotSupportedException
            or ObjectDisposedException;

    private sealed class CurrentUserRunRegistry : IStartupRegistry
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public string? Read(string valueName)
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return runKey?.GetValue(
                valueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        public void Write(string valueName, string command)
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            runKey.SetValue(valueName, command, RegistryValueKind.String);
        }

        public void Delete(string valueName)
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
