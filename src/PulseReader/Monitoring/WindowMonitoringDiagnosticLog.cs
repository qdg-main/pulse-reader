using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReader.Speech;

namespace PulseReader.Monitoring;

internal sealed record WindowTextDiagnosticAncestor(
    int ControlType,
    string ClassName,
    string AriaRole,
    string AutomationId);

internal sealed record WindowTextDiagnosticObservation(
    string Key,
    string Text,
    bool Editable,
    bool Included,
    int ControlType,
    string ClassName,
    string AriaRole,
    string AutomationId,
    int Left,
    int Top,
    int Right,
    int Bottom,
    IReadOnlyList<WindowTextDiagnosticAncestor> Ancestors);

internal sealed class WindowMonitoringDiagnosticLog : IDisposable
{
    internal const string PerformanceOnlyArgumentName = "--monitor-performance-only";
#if DEBUG
    internal const string ArgumentName = "--monitor-diagnostics";

    private readonly Lock _writeLock = new();
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private readonly BoundedDiagnosticWriter _writer;
    private readonly MonitoringPerformanceDiagnostics _performance;
    private readonly Dictionary<string, int> _pendingSignals = new(StringComparer.Ordinal);
    private Dictionary<string, string> _previousObservationSignatures =
        new(StringComparer.Ordinal);
    private bool _disposed;

    private WindowMonitoringDiagnosticLog(string path, bool includeStructure = true)
    {
        IncludeStructure = includeStructure;
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _writer = new BoundedDiagnosticWriter(Path);
        _performance = new MonitoringPerformanceDiagnostics(Path + ".performance.jsonl");
        Write(
            new
            {
                kind = "header",
                version = 3,
                startedUtc = DateTimeOffset.UtcNow,
                fileLimitBytes = BoundedDiagnosticWriter.DefaultMaximumBytes,
                archives = 2,
                includeStructure,
                privacy = "No text is stored. Text and identifiers use a per-process salt that is not written to disk."
            });
    }

    public string Path { get; }
    internal bool IncludeStructure { get; }

    internal MonitoringPerformanceDiagnostics.WorkerLease TrackWorker(IntPtr window) => _performance.TrackWorker(window);
    internal void RecordAttached(IntPtr window) => _performance.Attached(window);
    internal void RecordDetached() => _performance.Detached();
    internal void RecordQueueState(MonitoringSpeechQueueState state) => _performance.Queue(state);

    public static WindowMonitoringDiagnosticLog? CreateFromArguments(
        IReadOnlyList<string> arguments)
    {
        var argumentIndex = -1;
        var performanceOnly = arguments.Contains(PerformanceOnlyArgumentName, StringComparer.OrdinalIgnoreCase);
        var selectedArgument = performanceOnly ? PerformanceOnlyArgumentName : ArgumentName;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals(selectedArgument, StringComparison.OrdinalIgnoreCase))
            {
                argumentIndex = index;
                break;
            }
        }

        if (argumentIndex < 0)
        {
            return null;
        }

        var path = argumentIndex + 1 < arguments.Count
            && !arguments[argumentIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? arguments[argumentIndex + 1]
                : System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PulseReader",
                    "Diagnostics",
                    $"window-monitoring-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jsonl");
        return new WindowMonitoringDiagnosticLog(path, includeStructure: !performanceOnly);
    }

    public void RecordSessionStarted(IntPtr sourceWindow) =>
        Write(
            new
            {
                kind = "session-started",
                timestampUtc = DateTimeOffset.UtcNow,
                sourceWindow = sourceWindow.ToInt64()
            });

    public void RecordWorkerFailure(string stage, Exception exception)
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            const int maximumInnerExceptions = 4;
            var innerExceptions = new List<object>();
            var inner = exception.InnerException;
            while (inner is not null && innerExceptions.Count < maximumInnerExceptions)
            {
                innerExceptions.Add(DescribeException(inner));
                inner = inner.InnerException;
            }

            WriteUnlocked(new
            {
                kind = "worker-failure",
                timestampUtc = DateTimeOffset.UtcNow,
                stage,
                exception = DescribeException(exception),
                innerExceptions,
                innerExceptionsTruncated = inner is not null
            });
        }
    }

    public void RecordWorkerStopped(string reason, string stage, bool started) =>
        Write(new
        {
            kind = "worker-stopped",
            timestampUtc = DateTimeOffset.UtcNow,
            reason,
            stage,
            started
        });

    private object DescribeException(Exception exception)
    {
        const int maximumFrames = 32;
        // Never serialize the exception, its message, or a formatted stack trace.
        // Source-file information is deliberately excluded at capture time.
        var stack = new StackTrace(exception, fNeedFileInfo: false);
        var frames = Enumerable.Range(0, Math.Min(stack.FrameCount, maximumFrames))
            .Select(index =>
            {
                var frame = stack.GetFrame(index)!;
                var method = frame.GetMethod();
                return new
                {
                    declaringType = method?.DeclaringType?.FullName,
                    method = method?.Name,
                    ilOffset = frame.GetILOffset()
                };
            }).ToArray();
        var message = exception.Message;
        return new
        {
            type = exception.GetType().FullName,
            hResult = $"0x{exception.HResult:X8}",
            messageHash = Hash(message),
            messageLength = message.Length,
            frames,
            framesTruncated = stack.FrameCount > maximumFrames
        };
    }

    public void RecordSignal(string signal)
    {
        _performance.Signal();
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _pendingSignals.TryGetValue(signal, out var count);
            _pendingSignals[signal] = count + 1;
        }
    }

    public void RecordSnapshot(
        string reason,
        WindowTextSnapshot snapshot,
        IReadOnlyList<WindowTextChange> changes,
        IReadOnlyList<WindowTextChange>? acceptedChanges = null)
    {
        using var measurement = _performance.Measure("diagnostic-snapshot");
        _performance.Snapshot(snapshot);
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            var currentSignatures = new Dictionary<string, string>(StringComparer.Ordinal);
            var observations = new List<object>();
            for (var index = 0; index < snapshot.DiagnosticObservations.Count; index++)
            {
                var observation = snapshot.DiagnosticObservations[index];
                var keyHash = Hash(observation.Key);
                var signature = CreateObservationSignature(observation);
                currentSignatures[keyHash] = signature;
                if (_previousObservationSignatures.TryGetValue(keyHash, out var previous)
                    && string.Equals(previous, signature, StringComparison.Ordinal))
                {
                    continue;
                }

                observations.Add(
                    new
                    {
                        index,
                        keyHash,
                        text = Fingerprint(observation.Text),
                        observation.Editable,
                        observation.Included,
                        observation.ControlType,
                        observation.ClassName,
                        observation.AriaRole,
                        automationIdHash = Hash(observation.AutomationId),
                        bounds = new
                        {
                            observation.Left,
                            observation.Top,
                            observation.Right,
                            observation.Bottom
                        },
                        ancestors = observation.Ancestors.Select(
                            ancestor => new
                            {
                                ancestor.ControlType,
                                ancestor.ClassName,
                                ancestor.AriaRole,
                                automationIdHash = Hash(ancestor.AutomationId)
                            })
                    });
            }

            var removedKeyHashes = _previousObservationSignatures.Keys
                .Except(currentSignatures.Keys, StringComparer.Ordinal)
                .ToArray();
            _previousObservationSignatures = currentSignatures;
            var recordedChanges = changes.Select(
                    change => new
                    {
                        text = Fingerprint(change.Text),
                        change.IsNewBlock,
                        change.StartsAtBoundary,
                        keyHash = Hash(change.Key),
                        containerKeyHash = Hash(change.ContainerKey),
                        groupKeyHash = Hash(change.GroupKey),
                        change.Order,
                        change.IsCompletedAggregate,
                        change.IsFinalResponse,
                        change.IsReplacement,
                        change.IsStatus,
                        change.IsProvisional
                    })
                .ToArray();
            var recordedAcceptedChanges = (acceptedChanges ?? changes).Select(
                    change => new
                    {
                        text = Fingerprint(change.Text),
                        keyHash = Hash(change.Key),
                        containerKeyHash = Hash(change.ContainerKey),
                        groupKeyHash = Hash(change.GroupKey),
                        change.IsNewBlock,
                        change.IsCompletedAggregate,
                        change.IsFinalResponse,
                        change.IsReplacement,
                        change.IsStatus,
                        change.IsProvisional
                    })
                .ToArray();
            var signals = _pendingSignals.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            _pendingSignals.Clear();
            WriteUnlocked(
                new
                {
                    kind = "snapshot",
                    timestampUtc = DateTimeOffset.UtcNow,
                    reason,
                    responseCycleHash = Hash(snapshot.ResponseCycleKey),
                    thinking = snapshot.Thinking is null ? null : new
                    {
                        turnKeyHash = Hash(snapshot.Thinking.TurnKey),
                        snapshot.Thinking.Expanded,
                        snapshot.Thinking.Retired,
                        textHash = Hash(snapshot.Thinking.Text),
                        length = snapshot.Thinking.Text.Length
                    },
                    context = snapshot.Context is null ? null : new
                    {
                        available = snapshot.Context.ViewKey.Length > 0,
                        viewKeyHash = Hash(snapshot.Context.ViewKey)
                    },
                    discreteMessages = snapshot.IsDiscreteFeed ? snapshot.Blocks.Select(block => new
                    {
                        keyHash = Hash(block.Key),
                        text = Fingerprint(block.Text),
                        block.Order
                    }).ToArray() : null,
                    signals,
                    observationCount = snapshot.DiagnosticObservations.Count,
                    observations,
                    removedKeyHashes,
                    changes = recordedChanges,
                    acceptedChanges = recordedAcceptedChanges
                });
        }
    }

    public void RecordAcceptedChanges(
        string reason,
        IReadOnlyList<WindowTextChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        Write(
            new
            {
                kind = "accepted-changes",
                timestampUtc = DateTimeOffset.UtcNow,
                reason,
                changes = changes.Select(
                    change => new
                    {
                        text = Fingerprint(change.Text),
                        keyHash = Hash(change.Key),
                        containerKeyHash = Hash(change.ContainerKey),
                        groupKeyHash = Hash(change.GroupKey),
                        change.IsNewBlock,
                        change.IsCompletedAggregate,
                        change.IsFinalResponse,
                        change.IsReplacement,
                        change.IsStatus,
                        change.IsProvisional
                    })
            });
    }

    public void RecordPublishedUnits(IReadOnlyList<string> units)
    {
        if (units.Count == 0)
        {
            return;
        }

        Write(
            new
            {
                kind = "published-units",
                timestampUtc = DateTimeOffset.UtcNow,
                units = units.Select(DescribeSpeechText)
            });
    }

    public void RecordSpeechResult(
        MonitoringSpeechItemKind itemKind,
        string text,
        SpeechStartResult result) =>
        Write(
            new
            {
                kind = "speech-result",
                timestampUtc = DateTimeOffset.UtcNow,
                itemKind,
                text = DescribeSpeechText(text),
                result.Started,
                result.Canceled,
                warningHash = result.Warning is null ? null : Hash(result.Warning),
                errorHash = result.Error is null ? null : Hash(result.Error)
            });

    private object DescribeSpeechText(string value)
    {
        var normalizedLines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        return new
        {
            fingerprint = Fingerprint(value),
            lineCount = normalizedLines.Length,
            lines = normalizedLines.Select(line => new
            {
                length = line.Length,
                wordCount = Normalize(line).Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries).Length,
                firstCodePoint = line.EnumerateRunes().FirstOrDefault().Value,
                startsWithListMarker = line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                                       || line.TrimStart().StartsWith("* ", StringComparison.Ordinal)
                                       || line.TrimStart().StartsWith("+ ", StringComparison.Ordinal)
                                       || line.TrimStart().StartsWith("• ", StringComparison.Ordinal)
            })
        };
    }

    private string CreateObservationSignature(WindowTextDiagnosticObservation observation)
    {
        var ancestorSignature = string.Join(
            '|',
            observation.Ancestors.Select(
                ancestor => string.Join(
                    ':',
                    ancestor.ControlType,
                    ancestor.ClassName,
                    ancestor.AriaRole,
                    ancestor.AutomationId)));
        return Hash(
            string.Join(
                '\u001f',
                Normalize(observation.Text),
                observation.Editable,
                observation.Included,
                observation.ControlType,
                observation.ClassName,
                observation.AriaRole,
                observation.AutomationId,
                observation.Left,
                observation.Top,
                observation.Right,
                observation.Bottom,
                ancestorSignature));
    }

    private object Fingerprint(string value)
    {
        var normalized = Normalize(value);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new
        {
            hash = Hash(normalized),
            length = value.Length,
            normalizedLength = normalized.Length,
            wordCount = words.Length,
            wordHashes = words.Select(Hash).ToArray(),
            endsWithSentencePunctuation = normalized.EndsWith('.')
                                          || normalized.EndsWith('!')
                                          || normalized.EndsWith('?')
        };
    }

    private string Hash(string value)
    {
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var input = new byte[_salt.Length + valueBytes.Length];
        _salt.CopyTo(input, 0);
        valueBytes.CopyTo(input, _salt.Length);
        return Convert.ToHexString(SHA256.HashData(input))[..20];
    }

    private static string Normalize(string value) =>
        string.Join(
                ' ',
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private void Write(object entry)
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            WriteUnlocked(entry);
        }
    }

    private void WriteUnlocked(object entry) =>
        _writer.Write(entry);

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _performance.Dispose();
            _writer.Dispose();
            CryptographicOperations.ZeroMemory(_salt);
        }
    }
#else
    internal const string ArgumentName = "--monitor-diagnostics";
    internal bool IncludeStructure => false;

    public static WindowMonitoringDiagnosticLog? CreateFromArguments(
        IReadOnlyList<string> arguments) => null;

    internal MonitoringPerformanceDiagnostics.WorkerLease? TrackWorker(IntPtr window) => null;
    internal void RecordAttached(IntPtr window) { }
    internal void RecordDetached() { }
    internal void RecordQueueState(MonitoringSpeechQueueState state) { }

    public void RecordSessionStarted(IntPtr sourceWindow)
    {
    }

    public void RecordWorkerFailure(string stage, Exception exception)
    {
    }

    public void RecordWorkerStopped(string reason, string stage, bool started)
    {
    }

    public void RecordSignal(string signal)
    {
    }

    public void RecordSnapshot(
        string reason,
        WindowTextSnapshot snapshot,
        IReadOnlyList<WindowTextChange> changes,
        IReadOnlyList<WindowTextChange>? acceptedChanges = null)
    {
    }

    public void RecordAcceptedChanges(
        string reason,
        IReadOnlyList<WindowTextChange> changes)
    {
    }

    public void RecordPublishedUnits(IReadOnlyList<string> units)
    {
    }

    public void RecordSpeechResult(
        MonitoringSpeechItemKind itemKind,
        string text,
        SpeechStartResult result)
    {
    }

    public void Dispose()
    {
    }
#endif
}
