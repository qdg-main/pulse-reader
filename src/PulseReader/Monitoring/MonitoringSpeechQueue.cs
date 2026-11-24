using System.Diagnostics;
using PulseReader.Speech;
using PulseReader.Playback;

namespace PulseReader.Monitoring;

internal enum MonitoringSpeechItemKind
{
    Monitored,
    Manual,
    Thinking
}

internal sealed record MonitoringSpeechQueueState(
    bool IsPaused,
    bool IsSpeaking,
    int PendingItemCount,
    bool IsContextSuspended = false,
    bool HasManualPlayback = false,
    PlaybackIslandPlacement? ManualPlacement = null,
    bool IsManualPlaybackActive = false);

internal sealed class MonitoringSpeechQueue : IDisposable
{
    internal const int MaximumMonitoredQueueCharacters = 100_000;
    private static readonly TimeSpan InitialMonitoringDelay = TimeSpan.FromMilliseconds(1250);
    private static readonly TimeSpan PendingRedrawLifetime = TimeSpan.FromSeconds(2);
    private const int MinimumRedrawWords = 8;
    private const int MaximumOverlapComparisons = 100_000;

    private readonly Lock _stateLock = new();
    private readonly ISpeechService _speech;
    private readonly WindowTextFingerprinter _fingerprinter = new();
    private readonly Queue<MonitoringSpeechItem> _manualItems = new();
    private readonly Queue<MonitoringSpeechItem> _monitoredItems = new();
    private readonly SemaphoreSlim _itemAvailable = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private MonitoringSpeechItem? _currentItem;
    private bool _currentItemHasStartedPlayback;
    private MonitoringSpeechItem? _parkedMonitoringItem;
    private MonitoringSpeechItem? _yieldedForManual;
    private MonitoringSpeechItem? _supersededMonitoringItem;
    private int _monitoredQueueCharacters;
    private bool _paused;
    private bool _contextSuspended;
    private long _contextGeneration;
    private long _manualGeneration;
    private bool _disposed;
    private readonly MonitoringThinkingBuffer _thinking = new();
    private long _thinkingReadyAt;
    private MonitoringSpeechItem? _finishingThinking;

    public MonitoringSpeechQueue(ISpeechService speech)
    {
        ArgumentNullException.ThrowIfNull(speech);
        _speech = speech;
        _speech.SpeakingStateChanged += OnSpeechStateChanged;
        _worker = RunAsync();
    }

    public event EventHandler? StateChanged;

    public event EventHandler<MonitoringSpeechResultEventArgs>? SpeechResultProduced;

    public event EventHandler? QueueCapacityReached;

    public MonitoringSpeechQueueState State
    {
        get
        {
            lock (_stateLock)
            {
                return new MonitoringSpeechQueueState(
                    _paused,
                    _speech.State != SpeechPlaybackState.Idle,
                    _manualItems.Count + _monitoredItems.Count + _thinking.PendingCount
                        + (_parkedMonitoringItem is null ? 0 : 1),
                    _contextSuspended,
                    _manualItems.Count > 0 || IsCurrentManualLocked(),
                    IsCurrentManualLocked() ? _currentItem!.ManualPlacement : null,
                    IsCurrentManualLocked());
            }
        }
    }

    public void EnqueueInitialSelection(string text) =>
        Enqueue(text, MonitoringSpeechItemKind.Manual);

    public void EnqueueManualSelection(string text, PlaybackIslandPlacement? placement = null) =>
        Enqueue(text, MonitoringSpeechItemKind.Manual, manualPlacement: placement);

    private bool IsCurrentManualLocked() => _currentItem is { Kind: MonitoringSpeechItemKind.Manual } item
        && item.ManualGeneration == _manualGeneration;

    private bool HasCurrentPlaybackStartedLocked() => _currentItemHasStartedPlayback
        || _speech.State is SpeechPlaybackState.Speaking or SpeechPlaybackState.Paused;

    private bool CanPlayLocked(MonitoringSpeechItem item) => !ReferenceEquals(item, _yieldedForManual)
        && !ReferenceEquals(item, _supersededMonitoringItem)
        && (item.Kind == MonitoringSpeechItemKind.Manual
            ? item.ManualGeneration == _manualGeneration
            : !_contextSuspended && item.ContextGeneration == _contextGeneration && ThinkingAllowed(item));

    // Cancel this manual interaction, including pending requests and late preparation,
    // without changing the latch, monitoring backlog, or monitoring pause preference.
    public bool TryStopManualPlayback()
    {
        lock (_stateLock)
        {
            if (_disposed || (_manualItems.Count == 0 && !IsCurrentManualLocked())) return false;
            var stopCurrent = IsCurrentManualLocked();
            _manualGeneration++;
            _manualItems.Clear();
            if (stopCurrent) _speech.Stop();
        }
        SignalWorker();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void EnqueueMonitoredText(string text, bool isDiscreteMessage = false, SpeechRequest? speechRequest = null,
        Action? playbackFinished = null) =>
        Enqueue(text, MonitoringSpeechItemKind.Monitored, isDiscreteMessage: isDiscreteMessage, speechRequest: speechRequest,
            playbackFinished: playbackFinished);

    public void EnqueueCompletedResponse(string text) =>
        Enqueue(
            text,
            MonitoringSpeechItemKind.Monitored,
            discardPendingMonitoring: true);

    public void ReconcileCompletedResponse(IReadOnlyList<string> draftUnits, IReadOnlyList<string> finalUnits,
        IReadOnlyList<int>? publishedDraftIndices = null, int draftPrefixOffset = 0, int finalPrefixOffset = 0,
        (int DraftOffset, int Length)? earlierOpening = null)
    {
        ArgumentNullException.ThrowIfNull(draftUnits);
        ArgumentNullException.ThrowIfNull(finalUnits);
        if (draftPrefixOffset < 0 || draftPrefixOffset > draftUnits.Count)
            throw new ArgumentOutOfRangeException(nameof(draftPrefixOffset));
        if (finalPrefixOffset < 0 || finalPrefixOffset > finalUnits.Count)
            throw new ArgumentOutOfRangeException(nameof(finalPrefixOffset));
        var commonPrefix = 0;
        var rewrittenOpening = draftPrefixOffset == 0 && finalPrefixOffset == 0 && CompletedResponseAlignment.HasRewrittenOpening(draftUnits, finalUnits);
        if (rewrittenOpening) commonPrefix = 1;
        while (commonPrefix < Math.Min(draftUnits.Count - draftPrefixOffset, finalUnits.Count - finalPrefixOffset)
            && string.Equals(draftUnits[draftPrefixOffset + commonPrefix].Trim(), finalUnits[finalPrefixOffset + commonPrefix].Trim(), StringComparison.Ordinal))
            commonPrefix++;

        var capacityReached = false;
        lock (_stateLock)
        {
            if (_disposed || _contextSuspended) return;

            // Published units include spoken text. Match only outstanding items,
            // in reverse order, to keep each repeated sentence's draft position.
            var outstanding = new List<MonitoringSpeechItem>();
            if (_currentItem is { Kind: MonitoringSpeechItemKind.Monitored } current
                && CanPlayLocked(current)) outstanding.Add(current);
            if (_parkedMonitoringItem is { Kind: MonitoringSpeechItemKind.Monitored } parked)
                outstanding.Add(parked);
            outstanding.AddRange(_monitoredItems);
            var positions = new Dictionary<MonitoringSpeechItem, int>(ReferenceEqualityComparer.Instance);
            var draftPositions = new Dictionary<string, Stack<int>>(StringComparer.Ordinal);
            // A completed body can include occurrences suppressed by the
            // publication filter. Only actual published positions can own an
            // outstanding queue item, even when the text is repeated.
            var publishedPositions = publishedDraftIndices ?? Enumerable.Range(0, draftUnits.Count).ToArray();
            foreach (var index in publishedPositions)
            {
                var text = draftUnits[index].Trim();
                if (!draftPositions.TryGetValue(text, out var indices))
                    draftPositions.Add(text, indices = new Stack<int>());
                indices.Push(index);
            }
            var draftIndex = draftUnits.Count - 1;
            for (var index = outstanding.Count - 1; index >= 0; index--)
            {
                var item = outstanding[index];
                if (!draftPositions.TryGetValue(item.Text, out var indices)) continue;
                while (indices.TryPeek(out var position) && position > draftIndex) indices.Pop();
                if (!indices.TryPop(out var match)) continue;
                positions.Add(item, match);
                draftIndex = match - 1;
            }

            // An audible first sentence finishes in its original wording. An
            // opening still queued, parked or preparing has not finished its
            // playback, so replace it with the final wording instead of losing
            // it. A completed opening can still be current until the worker
            // processes its completion, so remember actual playback progress.
            var unreadOpening = rewrittenOpening && positions.Any(pair => pair.Value == 0
                && (!ReferenceEquals(pair.Key, _currentItem)
                    || !HasCurrentPlaybackStartedLocked()));

            var retained = _monitoredItems.Where(item => positions.TryGetValue(item, out var index)
                && index >= draftPrefixOffset && index < draftPrefixOffset + commonPrefix && !(unreadOpening && index == 0)).ToList();
            MonitoringSpeechItem? deferredCurrent = null;
            if (finalPrefixOffset > 0)
            {
                // New introduction must precede every matching unit that has
                // not become audible, including preparing or parked items.
                if (_parkedMonitoringItem is { } parkedPrefix && positions.TryGetValue(parkedPrefix, out var parkedPosition)
                    && parkedPosition >= draftPrefixOffset && parkedPosition < draftPrefixOffset + commonPrefix)
                {
                    retained.Insert(0, parkedPrefix);
                    _parkedMonitoringItem = null;
                }
                if (_currentItem is { Kind: MonitoringSpeechItemKind.Monitored } preparingPrefix && CanPlayLocked(preparingPrefix)
                    && positions.TryGetValue(preparingPrefix, out var preparingPosition)
                    && preparingPosition >= draftPrefixOffset && preparingPosition < draftPrefixOffset + commonPrefix
                    && !HasCurrentPlaybackStartedLocked())
                {
                    deferredCurrent = preparingPrefix;
                    retained.Insert(0, preparingPrefix with { });
                }
            }
            _monitoredItems.Clear();
            _monitoredQueueCharacters = 0;
            var now = Stopwatch.GetTimestamp();
            // A copied opening before the retained suffix can already be
            // audible. Unread units are queued once in their final order.
            var unreadDraftPositions = earlierOpening is null ? null : positions
                .Where(pair => !ReferenceEquals(pair.Key, _currentItem) || !HasCurrentPlaybackStartedLocked())
                .Select(pair => pair.Value).ToHashSet();
            for (var index = 0; index < finalPrefixOffset; index++)
            {
                if (earlierOpening is { } earlier && index < earlier.Length
                    && publishedPositions.Contains(earlier.DraftOffset + index)
                    && !unreadDraftPositions!.Contains(earlier.DraftOffset + index)) continue;
                var unit = finalUnits[index];
                if (string.IsNullOrWhiteSpace(unit)) continue;
                var text = unit.Trim();
                if (_monitoredQueueCharacters + text.Length > MaximumMonitoredQueueCharacters) { capacityReached = true; continue; }
                _monitoredItems.Enqueue(new MonitoringSpeechItem(text, MonitoringSpeechItemKind.Monitored, now, now, _contextGeneration));
                _monitoredQueueCharacters += text.Length;
            }
            if (unreadOpening)
            {
                var opening = finalUnits[0].Trim();
                if (opening.Length > MaximumMonitoredQueueCharacters) capacityReached = true;
                else
                {
                    _monitoredItems.Enqueue(new MonitoringSpeechItem(opening, MonitoringSpeechItemKind.Monitored,
                        now, now, _contextGeneration));
                    _monitoredQueueCharacters += opening.Length;
                }
            }
            foreach (var item in retained)
            {
                if (_monitoredQueueCharacters + item.Text.Length > MaximumMonitoredQueueCharacters)
                {
                    capacityReached = true;
                    continue;
                }
                _monitoredItems.Enqueue(item);
                _monitoredQueueCharacters += item.Text.Length;
            }

            if (_parkedMonitoringItem is { } parkedItem
                && (!positions.TryGetValue(parkedItem, out var parkedIndex) || parkedIndex < draftPrefixOffset || parkedIndex >= draftPrefixOffset + commonPrefix
                    || (unreadOpening && parkedIndex == 0)))
                _parkedMonitoringItem = null;

            if (_currentItem is { Kind: MonitoringSpeechItemKind.Monitored } currentItem
                && positions.TryGetValue(currentItem, out var currentIndex)
                && (currentIndex < draftPrefixOffset || currentIndex >= draftPrefixOffset + commonPrefix
                    || ReferenceEquals(currentItem, deferredCurrent) || (unreadOpening && currentIndex == 0))
                && !HasCurrentPlaybackStartedLocked())
            {
                // A dequeued or preparing suffix has not been heard. Prevent a
                // late preparation result from starting after its replacement.
                _supersededMonitoringItem = currentItem;
                _speech.Stop();
            }

            foreach (var unit in finalUnits.Skip(finalPrefixOffset + commonPrefix))
            {
                if (string.IsNullOrWhiteSpace(unit)) continue;
                var text = unit.Trim();
                if (_monitoredQueueCharacters + text.Length > MaximumMonitoredQueueCharacters)
                {
                    capacityReached = true;
                    continue;
                }
                _monitoredItems.Enqueue(new MonitoringSpeechItem(text, MonitoringSpeechItemKind.Monitored,
                    now, now, _contextGeneration));
                _monitoredQueueCharacters += text.Length;
            }
        }

        if (capacityReached) QueueCapacityReached?.Invoke(this, EventArgs.Empty);
        SignalWorker();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateThinking(MonitoringThinkingSnapshot? snapshot)
    {
        bool capacityReached;
        lock (_stateLock)
        {
            if (_disposed || _contextSuspended) return;
            var previousVersion = _thinking.Version;
            capacityReached = _thinking.Update(snapshot);
            if (_thinking.Enabled && previousVersion != _thinking.Version)
                _thinkingReadyAt = Stopwatch.GetTimestamp() + ToStopwatchTicks(InitialMonitoringDelay);
            if (_currentItem is { Kind: MonitoringSpeechItemKind.Thinking } item && !ThinkingAllowed(item))
            {
                if (_speech.State is SpeechPlaybackState.Speaking or SpeechPlaybackState.Paused)
                    _finishingThinking = item; // finish this sentence only
                else
                {
                    _thinking.ReturnUnstarted(item.ThinkingTurnKey, item.ThinkingIndex);
                    _speech.Stop();
                }
            }
        }
        if (capacityReached) QueueCapacityReached?.Invoke(this, EventArgs.Empty);
        SignalWorker();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool ThinkingAllowed(MonitoringSpeechItem item) => item.Kind != MonitoringSpeechItemKind.Thinking
        || ReferenceEquals(item, _finishingThinking)
        || (_thinking.Enabled && item.ThinkingVersion == _thinking.Version && item.ThinkingTurnKey == _thinking.TurnKey);

    public bool Pause()
    {
        lock (_stateLock)
        {
            if (_disposed || _paused)
            {
                return false;
            }

            _paused = true;
        }

        _speech.TryPause();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Resume()
    {
        lock (_stateLock)
        {
            if (_disposed || !_paused)
            {
                return false;
            }

            _paused = false;
        }

        _speech.TryResume();
        SignalWorker();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void StopCurrentSpeech()
    {
        if (TryStopManualPlayback()) return;
        _speech.Stop();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetContextSuspended(bool suspended)
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _contextSuspended = suspended;
            if (suspended)
            {
                _contextGeneration++;
                _monitoredItems.Clear();
                _parkedMonitoringItem = null;
                _thinking.Clear();
                _finishingThinking = null;
                _monitoredQueueCharacters = 0;
                // Serialize cancellation with the synchronous start of speech.
                // Manual selections belong to the user, not the replaced view.
                if (_currentItem is { Kind: not MonitoringSpeechItemKind.Manual }) _speech.Stop();
            }
        }
        SignalWorker();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Enqueue(
        string text,
        MonitoringSpeechItemKind kind,
        bool discardPendingMonitoring = false,
        bool isDiscreteMessage = false,
        SpeechRequest? speechRequest = null,
        PlaybackIslandPlacement? manualPlacement = null,
        Action? playbackFinished = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var capacityReached = false;
        lock (_stateLock)
        {
            if (_disposed || (_contextSuspended && kind == MonitoringSpeechItemKind.Monitored))
            {
                return;
            }

            if (discardPendingMonitoring)
            {
                _monitoredItems.Clear();
                _parkedMonitoringItem = null;
                _monitoredQueueCharacters = 0;
            }

            var enqueuedAt = Stopwatch.GetTimestamp();
            var startsIdleMonitoringBurst = kind == MonitoringSpeechItemKind.Monitored
                                            && !discardPendingMonitoring
                                            && _currentItem is null
                                            && _manualItems.Count == 0
                                            && _monitoredItems.Count == 0;
            var item = new MonitoringSpeechItem(
                text.Trim(),
                kind,
                enqueuedAt,
                startsIdleMonitoringBurst
                    ? enqueuedAt + ToStopwatchTicks(InitialMonitoringDelay)
                    : enqueuedAt,
                _contextGeneration, IsDiscreteMessage: isDiscreteMessage, SpeechRequest: speechRequest,
                PlaybackFinished: playbackFinished,
                ManualGeneration: _manualGeneration, ManualPlacement: manualPlacement);
            if (kind == MonitoringSpeechItemKind.Monitored)
            {
                if (!isDiscreteMessage) item = RemoveSupersededMonitoredItemsLocked(item);
                if (_monitoredQueueCharacters + item.Text.Length
                    > MaximumMonitoredQueueCharacters)
                {
                    capacityReached = true;
                }
                else
                {
                    _monitoredQueueCharacters += item.Text.Length;
                    _monitoredItems.Enqueue(item);
                }
            }
            else
            {
                _manualItems.Enqueue(item);
                if (_paused && _currentItem is { Kind: not MonitoringSpeechItemKind.Manual } current
                    && !ReferenceEquals(current, _yieldedForManual))
                {
                    // An already-paused sentence cannot reach the usual unit
                    // boundary. Park it for explicit monitor resume instead.
                    _parkedMonitoringItem = current with { };
                    _yieldedForManual = current;
                    _speech.Stop();
                }
            }
        }

        if (capacityReached)
        {
            QueueCapacityReached?.Invoke(this, EventArgs.Empty);
            return;
        }

        SignalWorker();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private MonitoringSpeechItem RemoveSupersededMonitoredItemsLocked(
        MonitoringSpeechItem replacement)
    {
        if (_monitoredItems.Count == 0)
        {
            return replacement;
        }

        var retained = new List<MonitoringSpeechItem>(_monitoredItems.Count);
        while (_monitoredItems.TryDequeue(out var pending))
        {
            if (!pending.IsDiscreteMessage && Stopwatch.GetElapsedTime(
                    pending.EnqueuedAtTimestamp,
                    replacement.EnqueuedAtTimestamp) <= PendingRedrawLifetime
                && IsSubstantialRedraw(pending.Text, replacement.Text))
            {
                _monitoredQueueCharacters -= pending.Text.Length;
                if (pending.ReadyAtTimestamp > replacement.ReadyAtTimestamp)
                {
                    replacement = replacement with
                    {
                        ReadyAtTimestamp = pending.ReadyAtTimestamp
                    };
                }
            }
            else
            {
                retained.Add(pending);
            }
        }

        foreach (var pending in retained)
        {
            _monitoredItems.Enqueue(pending);
        }

        return replacement;
    }

    private bool IsSubstantialRedraw(string pending, string replacement)
    {
        var pendingWords = _fingerprinter.Create(pending).WordHashes;
        var replacementWords = _fingerprinter.Create(replacement).WordHashes;
        if (pendingWords.Count < MinimumRedrawWords
            || replacementWords.Count <= pendingWords.Count)
        {
            return false;
        }

        var requiredRun = Math.Max(
            MinimumRedrawWords,
            (int)Math.Ceiling(pendingWords.Count * 0.75));
        if ((long)pendingWords.Count * replacementWords.Count
            > MaximumOverlapComparisons)
        {
            return WindowTextChangePolicy.FindSequence(
                replacementWords,
                pendingWords) >= 0;
        }

        for (var pendingStart = 0;
             pendingStart <= pendingWords.Count - requiredRun;
             pendingStart++)
        {
            for (var replacementStart = 0;
                 replacementStart <= replacementWords.Count - requiredRun;
                 replacementStart++)
            {
                var matches = true;
                for (var offset = 0; offset < requiredRun; offset++)
                {
                    if (!string.Equals(
                            pendingWords[pendingStart + offset],
                            replacementWords[replacementStart + offset],
                            StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _itemAvailable.WaitAsync(_cancellation.Token);

                var item = await TakeNextReadyItemAsync(_cancellation.Token);

                if (item is null)
                {
                    continue;
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
                Task<SpeechStartResult>? start = null;
                lock (_stateLock)
                {
                    if (!_disposed && CanPlayLocked(item))
                        start = _speech.TrySpeakAsync(item.SpeechRequest ?? new SpeechRequest(item.Text));
                }
                var result = start is null ? SpeechStartResult.CanceledResult : await start;
                lock (_stateLock)
                {
                    if (!CanPlayLocked(item))
                    {
                        if (result.Started) _speech.Stop();
                        result = SpeechStartResult.CanceledResult;
                    }
                }
                SpeechResultProduced?.Invoke(
                    this,
                    new MonitoringSpeechResultEventArgs(item.Kind, item.Text, result));
                if (result.Started)
                {
                    await WaitForSpeechToBecomeIdleAsync(_cancellation.Token);
                }

                lock (_stateLock)
                {
                    if (result.Started && CanPlayLocked(item)) item.PlaybackFinished?.Invoke();
                    _currentItem = null;
                    _currentItemHasStartedPlayback = false;
                    _yieldedForManual = null;
                    _supersededMonitoringItem = null;
                    _finishingThinking = null;
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
                SignalWorkerIfItemsRemain();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<MonitoringSpeechItem?> TakeNextReadyItemAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan monitoredDelay;
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return null;
                }

                if (_manualItems.TryDequeue(out var manual))
                {
                    _currentItem = manual;
                    _currentItemHasStartedPlayback = false;
                    return manual;
                }

                if (_paused) return null;

                if (_contextSuspended)
                {
                    return null;
                }

                if (_parkedMonitoringItem is { } parked)
                {
                    _parkedMonitoringItem = null;
                    if (CanPlayLocked(parked))
                    {
                        _currentItem = parked;
                        _currentItemHasStartedPlayback = false;
                        return parked;
                    }
                }

                if (!_monitoredItems.TryPeek(out var monitored))
                {
                    if (_thinking.PendingCount == 0) return null;
                    monitoredDelay = TimeUntil(_thinkingReadyAt);
                    if (monitoredDelay <= TimeSpan.Zero && _thinking.Take() is { } next)
                    {
                        var thought = new MonitoringSpeechItem(next.Text, MonitoringSpeechItemKind.Thinking,
                            Stopwatch.GetTimestamp(), _thinkingReadyAt, _contextGeneration,
                            _thinking.TurnKey, _thinking.Version, next.Index);
                        _currentItem = thought;
                        _currentItemHasStartedPlayback = false;
                        return thought;
                    }
                }
                else
                {
                    monitoredDelay = TimeUntil(monitored.ReadyAtTimestamp);
                    if (monitoredDelay <= TimeSpan.Zero)
                    {
                        _monitoredItems.Dequeue();
                        _monitoredQueueCharacters -= monitored.Text.Length;
                        _currentItem = monitored;
                        _currentItemHasStartedPlayback = false;
                        return monitored;
                    }
                }
            }

            await WaitForQueueChangeOrDelayAsync(monitoredDelay, cancellationToken);
        }
    }

    private async Task WaitForQueueChangeOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var queueChanged = _itemAvailable.WaitAsync(waitCancellation.Token);
        var delayElapsed = Task.Delay(delay, waitCancellation.Token);
        await Task.WhenAny(queueChanged, delayElapsed);
        waitCancellation.Cancel();
        try
        {
            await Task.WhenAll(queueChanged, delayElapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static long ToStopwatchTicks(TimeSpan duration) =>
        (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);

    private static TimeSpan TimeUntil(long timestamp)
    {
        var remainingTicks = timestamp - Stopwatch.GetTimestamp();
        return remainingTicks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
    }

    private async Task WaitForSpeechToBecomeIdleAsync(CancellationToken cancellationToken)
    {
        while (_speech.State != SpeechPlaybackState.Idle)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, EventArgs eventArgs)
            {
                if (_speech.State == SpeechPlaybackState.Idle)
                {
                    completion.TrySetResult();
                }
            }

            _speech.SpeakingStateChanged += Handler;
            try
            {
                if (_speech.State == SpeechPlaybackState.Idle)
                {
                    return;
                }

                await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                _speech.SpeakingStateChanged -= Handler;
            }
        }
    }

    private void OnSpeechStateChanged(object? sender, EventArgs eventArgs)
    {
        var shouldPause = false;
        var shouldStop = false;
        lock (_stateLock)
        {
            if (_currentItem is { } item && CanPlayLocked(item))
            {
                if (_speech.State is SpeechPlaybackState.Speaking or SpeechPlaybackState.Paused)
                    _currentItemHasStartedPlayback = true;
                else if (_currentItemHasStartedPlayback && _speech.State == SpeechPlaybackState.Idle)
                    item.PlaybackFinished?.Invoke();
            }
            shouldStop = !_disposed && _currentItem is { } current
                && !CanPlayLocked(current)
                && _speech.State is SpeechPlaybackState.Speaking or SpeechPlaybackState.Preparing or SpeechPlaybackState.Paused;
            shouldPause = !_disposed
                          && _paused
                          && _currentItem?.Kind != MonitoringSpeechItemKind.Manual
                          && _speech.State == SpeechPlaybackState.Speaking;
        }

        if (shouldStop) _speech.Stop();
        else if (shouldPause)
        {
            _speech.TryPause();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SignalWorkerIfItemsRemain()
    {
        lock (_stateLock)
        {
            if (_disposed || (_paused && _manualItems.Count == 0)
                || (_manualItems.Count + _monitoredItems.Count + _thinking.PendingCount == 0
                    && _parkedMonitoringItem is null))
            {
                return;
            }
        }

        SignalWorker();
    }

    private void SignalWorker()
    {
        try
        {
            _itemAvailable.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _manualItems.Clear();
            _monitoredItems.Clear();
            _thinking.Clear();
            _currentItem = null;
            _currentItemHasStartedPlayback = false;
            _parkedMonitoringItem = null;
            _yieldedForManual = null;
            _supersededMonitoringItem = null;
            _monitoredQueueCharacters = 0;
        }

        _speech.SpeakingStateChanged -= OnSpeechStateChanged;
        _fingerprinter.Dispose();
        _cancellation.Cancel();
        _speech.Stop();
        SignalWorker();
        _ = _worker.ContinueWith(
            _ =>
            {
                _cancellation.Dispose();
                _itemAvailable.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record MonitoringSpeechItem(
        string Text,
        MonitoringSpeechItemKind Kind,
        long EnqueuedAtTimestamp,
        long ReadyAtTimestamp,
        long ContextGeneration,
        string ThinkingTurnKey = "", long ThinkingVersion = 0, int ThinkingIndex = -1,
        bool IsDiscreteMessage = false, SpeechRequest? SpeechRequest = null,
        long ManualGeneration = 0, PlaybackIslandPlacement? ManualPlacement = null,
        Action? PlaybackFinished = null);
}

internal sealed class MonitoringSpeechResultEventArgs(
    MonitoringSpeechItemKind kind,
    string text,
    SpeechStartResult result) : EventArgs
{
    public MonitoringSpeechItemKind Kind { get; } = kind;

    public string Text { get; } = text;

    public SpeechStartResult Result { get; } = result;
}
