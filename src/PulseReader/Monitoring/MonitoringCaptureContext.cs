namespace PulseReader.Monitoring;

// Provider calls are synchronous and cannot be killed by cancelling a Task.
// Check on both sides of each phase and between inspected nodes instead.
internal sealed class MonitoringCaptureContext(CancellationToken cancellationToken = default,
    Action<string>? reportStage = null)
{
    private string? _stage;
    internal void Check() => cancellationToken.ThrowIfCancellationRequested();
    internal void Stage(string stage)
    {
        Check();
        _stage = stage;
        reportStage?.Invoke(stage);
    }
    internal T Read<T>(string stage, Func<T> read)
    {
        var previous = _stage;
        Stage(stage);
        try
        {
            var result = read();
            Check();
            return result;
        }
        finally
        {
            _stage = previous;
            if (previous is not null) reportStage?.Invoke(previous);
        }
    }
}

internal static class MonitoringCaptureGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<IDisposable?> TryEnterAsync(CancellationToken token)
        => await Gate.WaitAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false) ? new Lease() : null;

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Gate.Release(); }
    }
}
