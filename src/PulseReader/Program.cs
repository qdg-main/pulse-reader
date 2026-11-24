namespace PulseReader;

using PulseReader.Monitoring;
using PulseReader.Settings;
using PulseReader.Setup;
using PulseReader.Startup;
using PulseReader.Updates;

internal static class Program
{
    private const string SingleInstanceMutexName =
        @"Local\PulseReader.7A6B7D15-2B8D-4B2F-9A04-899F056E35CD";

    [STAThread]
    private static void Main(string[] args)
    {
        if (UpdateHelperRunner.IsHelperInvocation(args))
        {
            Environment.ExitCode = UpdateHelperRunner.RunHelperAsync(args).GetAwaiter().GetResult();
            return;
        }
        // Installer helpers must not create a user profile, show consent, or
        // contend with the reader's per-session single-instance mutex.
        if (args.Length > 0 && args[0] is "--setup-shared-model" or "--uninstall-data")
        {
            try
            {
                Environment.ExitCode = args[0] switch
                {
                    "--uninstall-data" => UninstallDataCleanup.Run(args),
                    _ => args.Length == 3
                        ? SharedModelSetup.RunHelperAsync(args[1], args[2]).GetAwaiter().GetResult()
                        : 2
                };
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine(exception);
                Console.Error.WriteLine(exception);
                Environment.ExitCode = 1;
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        var isStartupLaunch = IsStartupLaunch(args);

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var isFirstInstance);

        if (!isFirstInstance)
        {
            if (!isStartupLaunch)
            {
                MessageBox.Show(
                    "Pulse Reader is already running in the notification area.",
                    "Pulse Reader",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return;
        }

        try
        {
            Environment.ExitCode = StartupFailureBoundary.Run(() =>
            {
                var consent = DistributionTermsConsent.Check(
                    AppContext.BaseDirectory,
                    isStartupLaunch,
                    new RegistryDistributionTermsReceiptStore(),
                    terms =>
                    {
                        using var form = new DistributionTermsForm(terms.Text);
                        return form.ShowDialog() == DialogResult.OK;
                    });
                if (!consent.CanStart)
                {
                    if (consent.Status != DistributionTermsConsentStatus.Declined)
                    {
                        System.Diagnostics.Trace.WriteLine(consent.Error);
                        if (!isStartupLaunch)
                            MessageBox.Show(consent.Error, "Pulse Reader distribution terms",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return consent.Status == DistributionTermsConsentStatus.Declined ? 0 : 1;
                }

                using var diagnostics = WindowMonitoringDiagnosticLog.CreateFromArguments(args);
                using var applicationContext = new PulseReaderApplicationContext(diagnostics);
                Application.Run(applicationContext);
                return 0;
            }, isStartupLaunch,
                exception => System.Diagnostics.Trace.WriteLine(exception),
                message => MessageBox.Show(message, "Pulse Reader",
                    MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        finally
        {
            singleInstanceMutex.ReleaseMutex();
        }
    }

    internal static bool IsStartupLaunch(IEnumerable<string> args) =>
        args.Contains(
            WindowsStartupRegistrationService.StartupArgument,
            StringComparer.OrdinalIgnoreCase);
}
