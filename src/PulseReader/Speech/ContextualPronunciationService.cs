using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using PulseReader.TextPreparation;

namespace PulseReader.Speech;

internal interface IContextualPronunciationProcessor : IDisposable
{
    ContextualPronunciationAvailability Availability { get; }

    void SetEnabled(bool enabled);

    Task<PreparedDocument> ApplyAsync(
        PreparedDocument document,
        CancellationToken cancellationToken);

    Task ApplyProgressivelyAsync(
        PreparedDocument document,
        Func<ContextualPronunciationProgress, CancellationToken, ValueTask> progressSink,
        CancellationToken cancellationToken);
}

internal sealed record ContextualPronunciationProgress(
    PreparedDocument Document,
    int ResolvedUnitIndex,
    bool IsFinal);

internal sealed class ContextualPronunciationService : IContextualPronunciationProcessor
{
    internal static readonly TimeSpan DefaultMemoryCheckInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DefaultColdRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly ContextualPronunciationModelStore _modelStore;
    private readonly ContextualPronunciationAvailability? _availabilityOverride;
    private readonly Func<CancellationToken, Task<ContextualPronunciationInstallation>>
        _resolveInstallation;
    private readonly Func<ContextualPronunciationInstallation, IContextualSenseClassifier>
        _classifierFactory;
    private readonly Func<ContextualPronunciationMemorySnapshot?> _captureMemory;
    private readonly System.Threading.Timer _memoryTimer;
    private readonly TimeSpan _memoryCheckInterval;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _coldRequestTimeout;
    private CancellationTokenSource _enabledCancellation = new();
    private ContextualPronunciationInstallation? _verifiedInstallation;
    private IContextualSenseClassifier? _classifier;
    private bool _enabled;
    private bool _disposed;

    public ContextualPronunciationService(
        ContextualPronunciationModelStore? modelStore = null,
        Func<ContextualPronunciationInstallation, IContextualSenseClassifier>?
            classifierFactory = null,
        ContextualPronunciationAvailability? availability = null,
        Func<CancellationToken, Task<ContextualPronunciationInstallation>>?
            resolveInstallation = null,
        Func<ContextualPronunciationMemorySnapshot?>? captureMemory = null,
        TimeSpan? memoryCheckInterval = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? coldRequestTimeout = null)
    {
        _modelStore = modelStore ?? new ContextualPronunciationModelStore();
        _classifierFactory = classifierFactory ?? (installation =>
            new ContextualPronunciationServer(
                installation.ServerPath,
                installation.ModelPath,
                installation.LogDirectory));
        _availabilityOverride = availability;
        _resolveInstallation = resolveInstallation ?? _modelStore.ResolveVerifiedAsync;
        var memoryMonitor = new WindowsContextualPronunciationMemoryMonitor();
        _captureMemory = captureMemory ?? memoryMonitor.Capture;
        _memoryCheckInterval = memoryCheckInterval ?? DefaultMemoryCheckInterval;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _coldRequestTimeout = coldRequestTimeout ?? DefaultColdRequestTimeout;
        if (_memoryCheckInterval <= TimeSpan.Zero
            || _requestTimeout <= TimeSpan.Zero
            || _coldRequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(memoryCheckInterval),
                "Contextual-pronunciation timeouts must be positive.");
        }

        _memoryTimer = new System.Threading.Timer(OnMemoryTimer);
    }

    public ContextualPronunciationAvailability Availability =>
        _availabilityOverride ?? _modelStore.GetAvailability();

    public void SetEnabled(bool enabled)
    {
        CancellationTokenSource? cancellationToSignal = null;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (!enabled)
            {
                cancellationToSignal = _enabledCancellation;
                _enabledCancellation = new CancellationTokenSource();
            }
        }

        if (cancellationToSignal is not null)
        {
            cancellationToSignal.Cancel();
            cancellationToSignal.Dispose();
            TryScheduleMemoryCheck(TimeSpan.Zero);
        }
    }

    public async Task<PreparedDocument> ApplyAsync(
        PreparedDocument document,
        CancellationToken cancellationToken) =>
        await ApplyCoreAsync(document, null, cancellationToken);

    public async Task ApplyProgressivelyAsync(
        PreparedDocument document,
        Func<ContextualPronunciationProgress, CancellationToken, ValueTask> progressSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progressSink);
        await ApplyCoreAsync(document, progressSink, cancellationToken);
    }

    private async Task<PreparedDocument> ApplyCoreAsync(
        PreparedDocument document,
        Func<ContextualPronunciationProgress, CancellationToken, ValueTask>? progressSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        CancellationToken enabledToken = default;
        var unavailable = false;
        lock (_stateLock)
        {
            if (_disposed || !_enabled || !Availability.IsInstalled)
            {
                unavailable = true;
            }
            else
            {
                enabledToken = _enabledCancellation.Token;
            }
        }

        if (unavailable)
        {
            return await ReportFinalProgressAsync(
                document,
                -1,
                progressSink,
                cancellationToken);
        }

        var targets = ContextualPronunciationRules.FindTargets(document);
        if (targets.Count == 0)
        {
            return await ReportFinalProgressAsync(
                document,
                -1,
                progressSink,
                cancellationToken);
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            enabledToken);
        var token = operationCancellation.Token;
        var gateHeld = false;
        var currentDocument = document;
        var resolvedUnitIndex = -1;
        try
        {
            await _requestGate.WaitAsync(token);
            gateHeld = true;
            token.ThrowIfCancellationRequested();

            var classifierStarted = false;
            if (_classifier is null)
            {
                if (!ContextualPronunciationMemoryPolicy.CanStart(_captureMemory()))
                {
                    return await ReportFinalProgressAsync(
                        currentDocument,
                        resolvedUnitIndex,
                        progressSink,
                        cancellationToken);
                }

                _verifiedInstallation ??= await _resolveInstallation(token);
                _classifier = _classifierFactory(_verifiedInstallation);
                await _classifier.StartAsync(token);
                classifierStarted = true;
            }

            var processedTargets = new List<ContextualPronunciationTarget>(targets.Count);
            var labels = new List<string>(targets.Count);
            var targetGroups = targets.GroupBy(target => target.UnitIndex).ToArray();
            for (var groupIndex = 0; groupIndex < targetGroups.Length; groupIndex++)
            {
                var targetGroup = targetGroups[groupIndex];
                foreach (var target in targetGroup)
                {
                    using var requestCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestCancellation.CancelAfter(
                        classifierStarted && labels.Count == 0
                            ? _coldRequestTimeout
                            : _requestTimeout);
                    var label = await _classifier.ClassifyAsync(
                        target,
                        requestCancellation.Token);
                    if (label is null)
                    {
                        ScheduleMemoryCheck();
                        return await ReportFinalProgressAsync(
                            currentDocument,
                            resolvedUnitIndex,
                            progressSink,
                            cancellationToken);
                    }

                    processedTargets.Add(target);
                    labels.Add(label);
                }

                resolvedUnitIndex = targetGroup.Key;
                if (progressSink is not null)
                {
                    currentDocument = ContextualPronunciationRules.ApplyLabels(
                        document,
                        processedTargets,
                        labels);
                    await progressSink(
                        new ContextualPronunciationProgress(
                            currentDocument,
                            resolvedUnitIndex,
                            groupIndex == targetGroups.Length - 1),
                        token);
                }
            }

            ScheduleMemoryCheck();
            return progressSink is null
                ? ContextualPronunciationRules.ApplyLabels(document, targets, labels)
                : currentDocument;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ResetClassifier();
            return await ReportFinalProgressAsync(
                currentDocument,
                resolvedUnitIndex,
                progressSink,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ResetClassifier();
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or HttpRequestException
                                          or JsonException
                                          or ObjectDisposedException
                                          or TimeoutException
                                          or Win32Exception
                                          or CryptographicException
                                          or ArgumentException)
        {
            ResetClassifier();
            return await ReportFinalProgressAsync(
                currentDocument,
                resolvedUnitIndex,
                progressSink,
                cancellationToken);
        }
        finally
        {
            if (gateHeld)
            {
                _requestGate.Release();
            }
        }
    }

    private static async Task<PreparedDocument> ReportFinalProgressAsync(
        PreparedDocument document,
        int resolvedUnitIndex,
        Func<ContextualPronunciationProgress, CancellationToken, ValueTask>? progressSink,
        CancellationToken cancellationToken)
    {
        if (progressSink is not null)
        {
            await progressSink(
                new ContextualPronunciationProgress(document, resolvedUnitIndex, true),
                cancellationToken);
        }

        return document;
    }

    private void ScheduleMemoryCheck() =>
        TryScheduleMemoryCheck(_memoryCheckInterval);

    private void OnMemoryTimer(object? state)
    {
        if (!_requestGate.Wait(0))
        {
            TryScheduleMemoryCheck(TimeSpan.FromSeconds(1));
            return;
        }

        try
        {
            bool shouldStop;
            bool shouldReschedule;
            lock (_stateLock)
            {
                shouldStop = _disposed
                    || !_enabled
                    || ContextualPronunciationMemoryPolicy.ShouldRelease(_captureMemory());
                shouldReschedule = !shouldStop && _classifier is not null;
            }

            if (shouldStop)
            {
                ResetClassifier();
            }
            else if (shouldReschedule)
            {
                ScheduleMemoryCheck();
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private void ResetClassifier()
    {
        var classifier = Interlocked.Exchange(ref _classifier, null);
        _verifiedInstallation = null;
        try
        {
            classifier?.Dispose();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or Win32Exception
                                          or IOException
                                          or ObjectDisposedException)
        {
            // Optional-model cleanup must not crash the tray process.
        }
    }

    private void TryScheduleMemoryCheck(TimeSpan dueTime)
    {
        try
        {
            _memoryTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the race with the idle callback.
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            cancellation = _enabledCancellation;
        }

        cancellation.Cancel();
        _memoryTimer.Dispose();
        if (_requestGate.Wait(TimeSpan.FromSeconds(6)))
        {
            try
            {
                ResetClassifier();
            }
            finally
            {
                _requestGate.Release();
            }
        }

        cancellation.Dispose();
    }
}
