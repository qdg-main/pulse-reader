using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace PulseReader.Setup;

internal sealed class VerifiedAssetDownloader
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _readTimeout;

    internal VerifiedAssetDownloader(HttpClient? client = null,
        TimeSpan? headerTimeout = null, TimeSpan? readTimeout = null)
    {
        _client = client ?? SharedClient;
        _headerTimeout = headerTimeout ?? TimeSpan.FromSeconds(90);
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
        if (_headerTimeout <= TimeSpan.Zero || _readTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(headerTimeout));
    }

    internal static async Task<IDisposable> LockAsync(string path, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetupPaths.AssertSafe(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // FileShare.None also serializes installers running in other processes.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetupPaths.AssertSafe(path);
                try
                {
                    return new InstallationLock(gate, new FileStream(path, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch { gate.Release(); throw; }
    }

    internal async Task EnsureAsync(SetupAsset asset, string destination,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        using var lease = await LockAsync(destination + ".install-lock", cancellationToken).ConfigureAwait(false);
        progress?.Report(new(asset.Name, 0, asset.Length, "Verifying"));
        if (await MatchesAsync(destination, asset.Sha256, cancellationToken, asset.Length).ConfigureAwait(false))
        {
            progress?.Report(new(asset.Name, new FileInfo(destination).Length,
                new FileInfo(destination).Length, "Installed"));
            return;
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        SetupPaths.AssertSafe(destination);
        SetupPaths.AssertSafe(temporary);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_headerTimeout);
            using var response = await _client.GetAsync(asset.Uri,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total > asset.MaximumBytes || asset.Length is not null && total is not null && total != asset.Length)
                throw new InvalidDataException($"Unexpected download size for {asset.Name}.");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                var lastProgress = Stopwatch.GetTimestamp();
                progress?.Report(new(asset.Name, 0, total, "Downloading"));
                while (true)
                {
                    timeout.CancelAfter(_readTimeout);
                    count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (count == 0) break;
                    received += count;
                    if (received > asset.MaximumBytes || asset.Length is not null && received > asset.Length)
                        throw new InvalidDataException($"Download exceeded its size limit: {asset.Name}.");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (Stopwatch.GetElapsedTime(lastProgress) >= TimeSpan.FromMilliseconds(100))
                    {
                        progress?.Report(new(asset.Name, received, total, "Downloading"));
                        lastProgress = Stopwatch.GetTimestamp();
                    }
                }
                if (total is not null && received != total)
                    throw new InvalidDataException($"Incomplete download: {asset.Name}.");
                progress?.Report(new(asset.Name, received, total, "Downloading"));
            }
            progress?.Report(new(asset.Name, new FileInfo(temporary).Length, total, "Verifying"));
            if (!await MatchesAsync(temporary, asset.Sha256, cancellationToken, asset.Length).ConfigureAwait(false))
                throw new InvalidDataException($"Checksum verification failed: {asset.Name}.");
            cancellationToken.ThrowIfCancellationRequested();
            SetupPaths.AssertSafe(destination);
            SetupPaths.AssertSafe(temporary);
            File.Move(temporary, destination, true);
            progress?.Report(new(asset.Name, new FileInfo(destination).Length, total, "Installed"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The download stalled: {asset.Name}. Please retry.");
        }
        finally
        {
            SetupPaths.AssertSafe(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static async Task<bool> MatchesAsync(string path, string sha256,
        CancellationToken cancellationToken, long? length = null)
    {
        SetupPaths.AssertSafe(path);
        if (!File.Exists(path) || length is not null && new FileInfo(path).Length != length) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)
            .ConfigureAwait(false)), sha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InstallationLock(SemaphoreSlim gate, FileStream stream) : IDisposable
    {
        public void Dispose() { stream.Dispose(); gate.Release(); }
    }
}
