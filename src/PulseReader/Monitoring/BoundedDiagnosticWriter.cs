using System.Text.Json;

namespace PulseReader.Monitoring;

// Debug-only destinations are supplied by the developer. Keep only this
// destination and two numbered archives; never enumerate or prune other files.
internal sealed class BoundedDiagnosticWriter : IDisposable
{
    internal const int DefaultMaximumBytes = 16 * 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly int _maximumBytes;
    private FileStream? _stream;
    private long _bytes;
    private bool _disposed;
    internal int FailureCount { get; private set; }

    internal BoundedDiagnosticWriter(string path, int maximumBytes = DefaultMaximumBytes)
    {
        if (maximumBytes < 512) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _path = System.IO.Path.GetFullPath(path);
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        _stream = Open();
    }

    internal void Write(object entry)
    {
        lock (_gate)
        {
            if (_disposed || _stream is null) return;
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
                // Oversized snapshots must not defeat the cap. Preserve an
                // explicit omission record instead of a partial JSON document.
                if (bytes.Length + 1 > Math.Min(_maximumBytes, 1024 * 1024))
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        kind = "diagnostic-record-omitted", timestampUtc = DateTimeOffset.UtcNow,
                        serializedBytes = bytes.Length, reason = "record-size-limit"
                    });
                if (_bytes + bytes.Length + 1 > _maximumBytes)
                {
                    _stream.Dispose();
                    _stream = null;
                    if (File.Exists(_path + ".1")) File.Move(_path + ".1", _path + ".2", overwrite: true);
                    // The oldest archive was replaced above. On the first
                    // rotation no archive exists yet.
                    File.Move(_path, _path + ".1", overwrite: true);
                    _stream = Open();
                    _bytes = 0;
                }
                _stream.Write(bytes);
                _stream.WriteByte((byte)'\n');
                _stream.Flush();
                _bytes += bytes.Length + 1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                // Diagnostic I/O must not kill monitoring or a timer callback.
                FailureCount++;
                try { _stream?.Dispose(); }
                catch (Exception closeError) when (closeError is IOException or UnauthorizedAccessException) { }
                _stream = null;
            }
        }
    }

    private FileStream Open() => new(_path, FileMode.Create, FileAccess.Write,
        FileShare.ReadWrite | FileShare.Delete);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try { _stream?.Dispose(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { FailureCount++; }
            _stream = null;
        }
    }
}
