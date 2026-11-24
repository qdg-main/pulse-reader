namespace PulseReader.Setup;

internal static class StartupFailureBoundary
{
    internal static int Run(Func<int> start, bool isStartupLaunch,
        Action<Exception> writeDiagnostic, Action<string> showError)
    {
        try
        {
            return start();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or System.Text.Json.JsonException
            or System.Security.SecurityException)
        {
            writeDiagnostic(exception);
            if (!isStartupLaunch)
                showError("Pulse Reader could not start. Check that the installation is complete "
                    + "and accessible to your Windows account.\n\n" + exception.Message);
            return 1;
        }
    }
}
