internal static class TestProcessBoundary
{
    internal const int FailureExitCode = 1;

    public static async Task<int> RunAsync(Func<Task> run, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            await run();
            return Environment.ExitCode;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync("FAIL: PulseReader.InputSequenceTests encountered a failed check.");
            await error.WriteLineAsync(exception.ToString());
            return FailureExitCode;
        }
    }
}
