internal static class FailureReportingChecks
{
    public static async Task RunAsync()
    {
        const string assertionMessage = "Intentional boundary regression assertion.";
        using var error = new StringWriter();
        var exitCode = await TestProcessBoundary.RunAsync(
            () => throw new InvalidOperationException(
                "Intentional boundary regression probe.",
                new InvalidOperationException(assertionMessage)),
            error);
        var output = error.ToString();

        Assert(
            exitCode == TestProcessBoundary.FailureExitCode,
            "The test-process boundary did not return its nonzero failure exit code.");
        Assert(
            output.Contains(assertionMessage, StringComparison.Ordinal),
            "The test-process boundary omitted the assertion message.");
        Assert(
            output.Contains(nameof(InvalidOperationException), StringComparison.Ordinal),
            "The test-process boundary omitted the exception type.");
        Assert(
            output.Contains(nameof(RunAsync), StringComparison.Ordinal),
            "The test-process boundary omitted useful stack information.");

        Console.WriteLine(
            "PASS: failed checks retain diagnostics and return a nonzero code through the process boundary.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
