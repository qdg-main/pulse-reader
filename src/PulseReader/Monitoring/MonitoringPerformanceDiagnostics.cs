using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseReader.Monitoring;

internal sealed class MonitoringPerformanceDiagnostics : IDisposable
{
#if DEBUG
    private readonly Lock _gate = new();
    private readonly BoundedDiagnosticWriter _writer;
    private readonly System.Threading.Timer? _timer;
    private readonly Dictionary<long, WorkerLease> _workers = [];
    private readonly Dictionary<string, StageTotals> _stages = new(StringComparer.Ordinal);
    private long _nextWorker;
    private long _signals;
    private long _snapshotCount;
    private int _blockCount, _characterCount, _observationCount;
    private int _sourcePid;
    private long _sourceStartTicks;
    private bool _attached, _disposed;
    private MonitoringSpeechQueueState? _queue;
    private int _sampling;
    private readonly long _started = Stopwatch.GetTimestamp();

    internal MonitoringPerformanceDiagnostics(string path, bool startTimer = true)
    {
        _writer = new BoundedDiagnosticWriter(path, 4 * 1024 * 1024);
        _writer.Write(new { kind = "performance-header", version = 1, timestampUtc = DateTimeOffset.UtcNow,
            intervalSeconds = 5, fileLimitBytes = 4 * 1024 * 1024, archives = 2,
            processScope = "Source and matching GUI executable descendants only; at most 12 source processes plus the reader.",
            privacy = "Numeric metrics only. No conversation text, titles, paths or command lines.",
            cpu = "Cumulative CPU milliseconds; differences between samples measure CPU use. No forced GC or extra accessibility reads." });
        if (startTimer) _timer = new System.Threading.Timer(_ => Sample(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal WorkerLease TrackWorker(IntPtr window)
    {
        var worker = new WorkerLease(this, Interlocked.Increment(ref _nextWorker), window);
        lock (_gate) { if (!_disposed) _workers.Add(worker.Id, worker); }
        _writer.Write(new { kind = "performance-worker-started", timestampUtc = DateTimeOffset.UtcNow,
            workerId = worker.Id, sourceWindow = window.ToInt64() });
        return worker;
    }

    internal void Attached(IntPtr window)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _attached = true;
            GetWindowThreadProcessId(window, out var pid);
            _sourcePid = (int)pid;
            _sourceStartTicks = 0;
            if (_sourcePid > 0)
            {
                try { using var process = Process.GetProcessById(_sourcePid); _sourceStartTicks = process.StartTime.ToUniversalTime().Ticks; }
                catch (Exception error) when (IsProcessError(error)) { }
            }
        }
        _writer.Write(new { kind = "performance-attached", timestampUtc = DateTimeOffset.UtcNow,
            sourceWindow = window.ToInt64(), sourcePid = _sourcePid });
    }

    internal void Detached()
    {
        lock (_gate) { _attached = false; _queue = null; }
        _writer.Write(new { kind = "performance-detached", timestampUtc = DateTimeOffset.UtcNow });
        // Keep sampling the last source after detachment to observe recovery.
    }

    internal void Signal() => Interlocked.Increment(ref _signals);
    internal void Queue(MonitoringSpeechQueueState state) { lock (_gate) { if (_attached) _queue = state; } }
    internal void Snapshot(WindowTextSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshotCount++;
            _blockCount = snapshot.Blocks.Count;
            _characterCount = (int)Math.Min(int.MaxValue, snapshot.Blocks.Sum(block => (long)block.Text.Length));
            _observationCount = snapshot.DiagnosticObservations.Count;
        }
    }

    internal WorkMeasurement Measure(string stage) => new(this, stage, Stopwatch.GetTimestamp());
    private void RecordDuration(string stage, double milliseconds)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_stages.TryGetValue(stage, out var totals)) _stages.Add(stage, totals = new());
            totals.Count++; totals.TotalMs += milliseconds; totals.MaximumMs = Math.Max(totals.MaximumMs, milliseconds);
        }
    }

    private void Transition(WorkerLease worker, string stage, bool complete)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = Stopwatch.GetTimestamp();
            if (!_stages.TryGetValue(worker.Stage, out var totals)) _stages.Add(worker.Stage, totals = new());
            var elapsed = Stopwatch.GetElapsedTime(worker.StageStarted, now).TotalMilliseconds;
            totals.Count++; totals.TotalMs += elapsed; totals.MaximumMs = Math.Max(totals.MaximumMs, elapsed);
            worker.Stage = stage; worker.StageStarted = now;
            if (complete) _workers.Remove(worker.Id);
        }
    }

    internal void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0) return;
        var sampleStart = Stopwatch.GetTimestamp();
        try
        {
            object state;
            int sourcePid;
            long sourceStart;
            lock (_gate)
            {
                if (_disposed) return;
                sourcePid = _sourcePid; sourceStart = _sourceStartTicks;
                var now = Stopwatch.GetTimestamp();
                state = new
                {
                    attached = _attached, activeWorkers = _workers.Count,
                    workers = _workers.Values.Select(worker => new { workerId = worker.Id,
                        sourceWindow = worker.Window.ToInt64(), stage = worker.Stage,
                        stageElapsedMs = Stopwatch.GetElapsedTime(worker.StageStarted, now).TotalMilliseconds }).ToArray(),
                    stages = _stages.Select(pair => new { stage = pair.Key, count = pair.Value.Count,
                        totalMs = pair.Value.TotalMs, maximumMs = pair.Value.MaximumMs }).ToArray(),
                    signalsSinceSample = Interlocked.Exchange(ref _signals, 0),
                    snapshotsSinceSample = _snapshotCount, blocks = _blockCount,
                    characters = _characterCount, observations = _observationCount,
                    // Native handles in placement state are not JSON values.
                    // Performance telemetry needs only queue flags/counts, not
                    // the playback bar's coordinates or source handle.
                    queue = _queue is { } queue ? new
                    {
                        queue.IsPaused, queue.IsSpeaking, queue.PendingItemCount,
                        queue.IsContextSuspended, queue.HasManualPlayback,
                        HasManualPlacement = queue.ManualPlacement.HasValue,
                        queue.IsManualPlaybackActive
                    } : null
                };
                _stages.Clear(); _snapshotCount = 0;
            }
            var processes = ReadProcesses(sourcePid, sourceStart);
            _writer.Write(new { kind = "performance-sample", timestampUtc = DateTimeOffset.UtcNow,
                elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds, sourcePid, state, processes,
                managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                gcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
                samplerDurationMs = Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds,
                diagnosticWriteFailures = _writer.FailureCount });
        }
        catch (Exception error)
        {
            // No exception from observational telemetry may escape the timer.
            _writer.Write(new { kind = "performance-sample-failed", timestampUtc = DateTimeOffset.UtcNow,
                exceptionType = error.GetType().FullName });
        }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    private static List<object> ReadProcesses(int sourcePid, long sourceStart)
    {
        var output = new List<object>();
        var selected = new HashSet<int> { Environment.ProcessId };
        if (sourcePid > 0 && sourceStart != 0)
        {
            try
            {
                using var source = Process.GetProcessById(sourcePid);
                if (source.StartTime.ToUniversalTime().Ticks == sourceStart)
                {
                    var sourceProcesses = new HashSet<int> { sourcePid };
                    // Process metadata only, not window or accessibility queries.
                    using var snapshot = CreateToolhelp32Snapshot(2, 0);
                    if (!snapshot.IsInvalid)
                    {
                        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                        var parents = new Dictionary<int, (int Parent, string Executable)>();
                        if (Process32First(snapshot, ref entry))
                            do { parents[(int)entry.Id] = ((int)entry.ParentId, entry.Executable); } while (Process32Next(snapshot, ref entry));
                        var sourceExecutable = source.ProcessName + ".exe";
                        for (var pass = 0; pass < 8 && sourceProcesses.Count < 12; pass++)
                        {
                            var before = sourceProcesses.Count;
                            foreach (var child in parents)
                                if (sourceProcesses.Count < 12 && sourceProcesses.Contains(child.Value.Parent)
                                    && child.Value.Executable.Equals(sourceExecutable, StringComparison.OrdinalIgnoreCase)) sourceProcesses.Add(child.Key);
                            if (before == sourceProcesses.Count) break;
                        }
                    }
                    selected.UnionWith(sourceProcesses);
                }
            }
            catch (Exception error) when (IsProcessError(error)) { }
        }
        foreach (var pid in selected)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                output.Add(new { pid, role = pid == Environment.ProcessId ? "reader" : pid == sourcePid ? "source" : "source-child",
                    startUtc = process.StartTime.ToUniversalTime(), cpuMs = process.TotalProcessorTime.TotalMilliseconds,
                    privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64,
                    handles = process.HandleCount, threads = process.Threads.Count });
            }
            catch (Exception error) when (IsProcessError(error))
            { output.Add(new { pid, unavailable = true, exceptionType = error.GetType().Name }); }
        }
        return output;
    }

    private static bool IsProcessError(Exception error) => error is ArgumentException or InvalidOperationException
        or System.ComponentModel.Win32Exception or UnauthorizedAccessException or NotSupportedException;

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        _timer?.Dispose();
        _writer.Dispose();
    }

    private sealed class StageTotals { internal int Count; internal double TotalMs, MaximumMs; }
    internal readonly record struct WorkMeasurement(MonitoringPerformanceDiagnostics Owner, string Stage, long Started) : IDisposable
    {
        public void Dispose() => Owner.RecordDuration(Stage, Stopwatch.GetElapsedTime(Started).TotalMilliseconds);
    }
    internal sealed class WorkerLease(MonitoringPerformanceDiagnostics owner, long id, IntPtr window) : IDisposable
    {
        internal long Id { get; } = id;
        internal IntPtr Window { get; } = window;
        internal string Stage { get; set; } = "worker-start";
        internal long StageStarted { get; set; } = Stopwatch.GetTimestamp();
        private int _disposed;
        internal void SetStage(string stage) => owner.Transition(this, stage, complete: false);
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Transition(this, "finished", complete: true); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, Id;
        internal UIntPtr Heap;
        internal uint Module, Threads, ParentId;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
#else
    internal MonitoringPerformanceDiagnostics(string path, bool startTimer = true) { }
    internal void Attached(IntPtr window) { }
    internal WorkerLease TrackWorker(IntPtr window) => new();
    internal void Detached() { }
    internal void Signal() { }
    internal void Queue(MonitoringSpeechQueueState state) { }
    internal void Snapshot(WindowTextSnapshot snapshot) { }
    internal void Sample() { }
    public void Dispose() { }
    internal sealed class WorkerLease : IDisposable { internal void SetStage(string stage) { } public void Dispose() { } }
#endif
}
