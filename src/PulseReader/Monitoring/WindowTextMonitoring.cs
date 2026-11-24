using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PulseReader.Selection;
using PulseReader.Speech;
using PulseReader.TextPreparation;
using UIA = Interop.UIAutomationClient;

namespace PulseReader.Monitoring;

internal static class ConversationMessageKey
{
    public static bool IsKnown(string key) => key.StartsWith("codex-message:", StringComparison.Ordinal)
        || key.StartsWith("lmstudio-message:", StringComparison.Ordinal)
        || key.StartsWith("t3-message:", StringComparison.Ordinal);
}

internal sealed record WindowTextBlock(
    string Key,
    string Text,
    string ContainerKey = "",
    int Order = 0,
    bool IsStatus = false,
    string GroupKey = "",
    bool IsAggregate = false,
    bool IsCompletedAggregate = false,
    bool IsFinalResponse = false,
    string ListItemHeading = "",
    WindowTextFingerprint? DiscreteBodyFingerprint = null,
    int DiscreteAuthorPrefixLength = 0,
    bool IsConfirmedFinalHandoff = false);

internal sealed record WindowTextFingerprint(
    string Hash,
    IReadOnlyList<string> WordHashes,
    int WordCount);

internal sealed record WindowTextSnapshot(
    IReadOnlyList<WindowTextBlock> Blocks,
    IReadOnlyList<WindowTextDiagnosticObservation>? Observations = null,
    IReadOnlyList<WindowTextFingerprint>? EditableFingerprints = null,
    string ResponseCycleKey = "",
    ConversationContext? Context = null,
    MonitoringThinkingSnapshot? Thinking = null,
    bool IsDiscreteFeed = false,
    string T3PromptHash = "",
    bool T3HasNewPrompt = false,
    bool T3StartsContinuation = false,
    bool T3CapturePaused = false)
{
    public IReadOnlyList<WindowTextDiagnosticObservation> DiagnosticObservations { get; } =
        Observations ?? Array.Empty<WindowTextDiagnosticObservation>();

    public IReadOnlyList<WindowTextFingerprint> EditableTextFingerprints { get; } =
        EditableFingerprints ?? Array.Empty<WindowTextFingerprint>();
}

internal readonly record struct WindowTextChange(
    string Text,
    bool IsNewBlock,
    bool StartsAtBoundary,
    string Key = "",
    string ContainerKey = "",
    int Order = 0,
    bool IsReplacement = false,
    bool IsStatus = false,
    bool IsProvisional = false,
    string GroupKey = "",
    bool IsCompletedAggregate = false,
    bool IsFinalResponse = false,
    bool IsConfirmedFinalHandoff = false);

internal sealed class WindowTextDeltaTracker
{
    private IReadOnlyList<WindowTextBlock> _previous = Array.Empty<WindowTextBlock>();
    private readonly Dictionary<string, WindowTextBlock> _recentByKey = new(StringComparer.Ordinal);
    private int _rememberedCharacters;

    public void EstablishBaseline(WindowTextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _previous = snapshot.Blocks.ToArray();
        _recentByKey.Clear();
        _rememberedCharacters = 0;
        Remember(snapshot.Blocks);
    }

    public IReadOnlyList<WindowTextChange> Update(WindowTextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // A streaming conversation can briefly expose only its opening nodes
        // during a redraw. Retain the captured high-water text for that same
        // aggregate so its restoration cannot be mistaken for a new suffix.
        // Divergent edits, final answers, and ordinary application text retain
        // their existing replacement semantics.
        snapshot = snapshot with
        {
            Blocks = snapshot.Blocks.Select(block =>
                ConversationMessageKey.IsKnown(block.Key) && block.IsAggregate && !block.IsFinalResponse
                && _recentByKey.TryGetValue(block.Key, out var remembered)
                && remembered.IsAggregate && !remembered.IsFinalResponse
                && remembered.Text.Length > block.Text.Length
                && remembered.Text.StartsWith(block.Text, StringComparison.Ordinal)
                    ? block with { Text = remembered.Text }
                    : block).ToArray()
        };

        var changes = new List<IndexedWindowTextChange>();
        var previousMatched = new bool[_previous.Count];
        var currentMatched = new bool[snapshot.Blocks.Count];
        var previousByKey = _previous
            .Select((block, index) => (block, index))
            .GroupBy(item => item.block.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);

        for (var currentIndex = 0; currentIndex < snapshot.Blocks.Count; currentIndex++)
        {
            var current = snapshot.Blocks[currentIndex];
            if (!previousByKey.TryGetValue(current.Key, out var previousIndex))
            {
                if (_recentByKey.TryGetValue(current.Key, out var hiddenPrevious))
                {
                    currentMatched[currentIndex] = true;
                    AddStableElementChange(hiddenPrevious, current, currentIndex, changes);
                }
                continue;
            }

            var previous = _previous[previousIndex];
            previousMatched[previousIndex] = true;
            currentMatched[currentIndex] = true;
            AddStableElementChange(previous, current, currentIndex, changes);
        }

        MatchRecreatedBlocks(snapshot, previousMatched, currentMatched, exactOnly: true, changes);
        MatchRecreatedBlocks(snapshot, previousMatched, currentMatched, exactOnly: false, changes);
        MatchExpandedAggregates(snapshot, previousMatched, currentMatched);

        for (var index = 0; index < snapshot.Blocks.Count; index++)
        {
            if (!currentMatched[index])
            {
                var text = snapshot.Blocks[index].Text.Trim();
                if (text.Length > 0)
                {
                    var block = snapshot.Blocks[index];
                    changes.Add(
                        new IndexedWindowTextChange(
                            index,
                            CreateChange(block, text, isNewBlock: true)));
                }
            }
        }

        _previous = snapshot.Blocks.ToArray();
        Remember(snapshot.Blocks);
        return changes
            .OrderBy(change => change.Index)
            .ThenBy(change => change.Change.Order)
            .Select(change => change.Change)
            .ToArray();
    }

    private void Remember(IReadOnlyList<WindowTextBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (_recentByKey.Remove(block.Key, out var previous))
            {
                _rememberedCharacters -= previous.Text.Length;
            }

            _recentByKey.Add(block.Key, block);
            _rememberedCharacters += block.Text.Length;
        }

        // Virtualized UI elements can disappear for several snapshots without
        // becoming new content. Bound retained text to the monitoring session.
        while (_recentByKey.Count > 2048 || _rememberedCharacters > 1_000_000)
        {
            var oldest = _recentByKey.First();
            _rememberedCharacters -= oldest.Value.Text.Length;
            _recentByKey.Remove(oldest.Key);
        }
    }

    private void MatchRecreatedBlocks(
        WindowTextSnapshot snapshot,
        bool[] previousMatched,
        bool[] currentMatched,
        bool exactOnly,
        List<IndexedWindowTextChange> changes)
    {
        for (var currentIndex = 0; currentIndex < snapshot.Blocks.Count; currentIndex++)
        {
            if (currentMatched[currentIndex])
            {
                continue;
            }

            var current = snapshot.Blocks[currentIndex];
            for (var previousIndex = 0; previousIndex < _previous.Count; previousIndex++)
            {
                if (previousMatched[previousIndex])
                {
                    continue;
                }

                var previous = _previous[previousIndex];
                if ((ConversationMessageKey.IsKnown(previous.Key)
                     || ConversationMessageKey.IsKnown(current.Key))
                    && previous.Key != current.Key) continue;
                var matches = exactOnly
                    ? string.Equals(previous.Text, current.Text, StringComparison.Ordinal)
                    : current.Text.StartsWith(previous.Text, StringComparison.Ordinal)
                      && previous.Text.Length >= 4;
                if (!matches)
                {
                    continue;
                }

                previousMatched[previousIndex] = true;
                currentMatched[currentIndex] = true;
                if ((current.IsCompletedAggregate && !previous.IsCompletedAggregate)
                    || (current.IsFinalResponse && !previous.IsFinalResponse)
                    || (current.IsConfirmedFinalHandoff && !previous.IsConfirmedFinalHandoff))
                {
                    changes.Add(new IndexedWindowTextChange(
                        currentIndex, CreateChange(current, current.Text, isNewBlock: true)));
                    break;
                }
                if (!exactOnly)
                {
                    // Short unfinished nodes are held by the settlement filter.
                    // A replacement node must carry their opening text too;
                    // emitting only the suffix loses words never published.
                    var prefix = previous.Text.Trim();
                    if (prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 3
                        && !prefix.EndsWith('.') && !prefix.EndsWith('!') && !prefix.EndsWith('?'))
                    {
                        changes.Add(new IndexedWindowTextChange(
                            currentIndex,
                            CreateChange(current, current.Text, isNewBlock: true)));
                    }
                    else
                    {
                        AddExtension(previous.Text, current, currentIndex, changes);
                    }
                }

                break;
            }
        }
    }

    private void MatchExpandedAggregates(
        WindowTextSnapshot snapshot,
        bool[] previousMatched,
        bool[] currentMatched)
    {
        for (var previousIndex = 0; previousIndex < _previous.Count; previousIndex++)
        {
            var previous = _previous[previousIndex];
            if (previousMatched[previousIndex] || !previous.IsAggregate)
            {
                continue;
            }

            var matchedAny = false;
            for (var currentIndex = 0; currentIndex < snapshot.Blocks.Count; currentIndex++)
            {
                if (currentMatched[currentIndex])
                {
                    continue;
                }

                var currentText = snapshot.Blocks[currentIndex].Text.Trim();
                if (ConversationMessageKey.IsKnown(previous.Key)
                    && previous.Key != snapshot.Blocks[currentIndex].Key) continue;
                if (currentText.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries).Length < 3
                    || !previous.Text.Contains(currentText, StringComparison.Ordinal))
                {
                    continue;
                }

                currentMatched[currentIndex] = true;
                matchedAny = true;
            }

            if (matchedAny)
            {
                previousMatched[previousIndex] = true;
            }
        }
    }

    private static void AddExtension(
        string previous,
        WindowTextBlock current,
        int currentIndex,
        List<IndexedWindowTextChange> changes)
    {
        if (string.Equals(previous, current.Text, StringComparison.Ordinal)
            || !current.Text.StartsWith(previous, StringComparison.Ordinal))
        {
            return;
        }

        var extension = current.Text[previous.Length..];
        if (extension.Length == 0)
        {
            return;
        }

        var startsAtBoundary = previous.Length == 0
                               || char.IsWhiteSpace(extension[0])
                               || char.IsWhiteSpace(previous[^1])
                               || previous[^1] is '.' or '!' or '?' or ':' or ';';
        changes.Add(
            new IndexedWindowTextChange(
                currentIndex,
                CreateChange(
                    current,
                    extension,
                    isNewBlock: false,
                    startsAtBoundary: startsAtBoundary)));
    }

    private static void AddStableElementChange(
        WindowTextBlock previousBlock,
        WindowTextBlock current,
        int currentIndex,
        List<IndexedWindowTextChange> changes)
    {
        if (ConversationMessageKey.IsKnown(current.Key) && current.IsFinalResponse)
        {
            if (current.Text != previousBlock.Text || (current.IsConfirmedFinalHandoff && !previousBlock.IsConfirmedFinalHandoff))
                changes.Add(new IndexedWindowTextChange(currentIndex, CreateChange(current, current.Text, isNewBlock: true)));
            return;
        }
        if ((current.IsCompletedAggregate && !previousBlock.IsCompletedAggregate)
            || (current.IsFinalResponse && !previousBlock.IsFinalResponse))
        {
            changes.Add(new IndexedWindowTextChange(
                currentIndex, CreateChange(current, current.Text, isNewBlock: true)));
            return;
        }

        var previous = previousBlock.Text;
        if (current.Text.StartsWith(previous, StringComparison.Ordinal))
        {
            AddExtension(previous, current, currentIndex, changes);
            return;
        }

        var replacement = current.Text.Trim();
        if (replacement.Length > 0
            && !previous.StartsWith(current.Text, StringComparison.Ordinal))
        {
            changes.Add(
                new IndexedWindowTextChange(
                    currentIndex,
                    CreateChange(
                        current,
                        replacement,
                        isNewBlock: true,
                        isReplacement: true)));
        }
    }

    private static WindowTextChange CreateChange(
        WindowTextBlock block,
        string text,
        bool isNewBlock,
        bool startsAtBoundary = true,
        bool isReplacement = false) =>
        new(
            text,
            isNewBlock,
            startsAtBoundary,
            block.Key,
            block.ContainerKey,
            block.Order,
            isReplacement,
            block.IsStatus,
            GroupKey: block.GroupKey,
            IsCompletedAggregate: block.IsCompletedAggregate,
            IsFinalResponse: block.IsFinalResponse,
            IsConfirmedFinalHandoff: block.IsConfirmedFinalHandoff);

    private readonly record struct IndexedWindowTextChange(
        int Index,
        WindowTextChange Change);
}

internal sealed class WindowTextAccumulator
{
    internal static readonly TimeSpan DefaultIdleFlushDelay = TimeSpan.FromMilliseconds(700);

    private string _pending = string.Empty;
    private string _pendingContainerKey = string.Empty;
    private bool _discardingPartialWord;
    private bool _pendingContainsOnlyConversationText;

    public bool HasPendingText => !string.IsNullOrWhiteSpace(_pending);

    public void Clear()
    {
        _pending = string.Empty;
        _pendingContainerKey = string.Empty;
        _discardingPartialWord = false;
        _pendingContainsOnlyConversationText = false;
    }

    public void Append(WindowTextChange change)
    {
        var value = change.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // A conversation rewrite supplies the complete replacement body. The
        // unfinished tail belongs to the old body, not a prefix of this one.
        // Keep completed units in the publication filter so its unchanged
        // opening is suppressed normally rather than joined to that old tail.
        if (change.IsReplacement && !change.IsFinalResponse
            && ConversationMessageKey.IsKnown(change.Key)
            && change.Key.EndsWith(":working", StringComparison.Ordinal)
            && change.ContainerKey.Length > 0 && change.ContainerKey == _pendingContainerKey)
        {
            Clear();
        }

        if (change.IsNewBlock)
        {
            _discardingPartialWord = false;
            if (_pending.Length > 0)
            {
                var sameContainer = !string.IsNullOrEmpty(change.ContainerKey)
                                    && string.Equals(
                                        _pendingContainerKey,
                                        change.ContainerKey,
                                        StringComparison.Ordinal);
                if (sameContainer)
                {
                    if (!char.IsWhiteSpace(_pending[^1]))
                    {
                        _pending += ' ';
                    }
                }
                else if (!_pending.EndsWith('\n'))
                {
                    _pending += Environment.NewLine;
                }
            }
        }
        else if (!change.StartsAtBoundary && _pending.Length == 0)
        {
            _discardingPartialWord = true;
        }

        if (_discardingPartialWord)
        {
            var boundary = value.IndexOfAny([' ', '\t', '\r', '\n']);
            if (boundary < 0)
            {
                return;
            }

            value = value[(boundary + 1)..];
            _discardingPartialWord = false;
        }

        var conversationText = ConversationMessageKey.IsKnown(change.Key);
        _pendingContainsOnlyConversationText = _pending.Length == 0
            ? conversationText : _pendingContainsOnlyConversationText && conversationText;
        _pending += value;
        if (_pendingContainsOnlyConversationText)
            _pending = StreamingTextFormatting.NormalizeInlineHyphenSpacing(_pending);
        if (!string.IsNullOrEmpty(change.ContainerKey))
        {
            _pendingContainerKey = change.ContainerKey;
        }
    }

    public IReadOnlyList<string> TakeCompletedUnits(bool flushIncomplete)
    {
        if (string.IsNullOrWhiteSpace(_pending))
        {
            _pending = string.Empty;
            _pendingContainerKey = string.Empty;
            return Array.Empty<string>();
        }

        var document = ReadingUnitAnalyzer.Prepare(_pending);
        if (document.Units.Count == 0)
        {
            return flushIncomplete
                ? DrainAllAsSingleUnit()
                : Array.Empty<string>();
        }

        var pendingEnd = _pending.Length;
        while (pendingEnd > 0 && char.IsWhiteSpace(_pending[pendingEnd - 1]))
        {
            pendingEnd--;
        }

        var ready = new List<string>();
        var consumedEnd = 0;
        foreach (var unit in document.Units)
        {
            var hasFollowingContent = unit.SourceRange.End < pendingEnd;
            var complete = flushIncomplete
                           || hasFollowingContent
                           || EndsWithSentencePunctuation(unit.OriginalText);
            if (!complete)
            {
                break;
            }

            var text = unit.OriginalText.Trim();
            if (text.Length > 0)
            {
                ready.Add(text);
            }

            consumedEnd = unit.SourceRange.End;
        }

        if (consumedEnd > 0)
        {
            _pending = _pending[consumedEnd..].TrimStart();
            if (_pending.Length == 0)
            {
                _pendingContainerKey = string.Empty;
            }
        }

        return ready;
    }

    private IReadOnlyList<string> DrainAllAsSingleUnit()
    {
        var text = _pending.Trim();
        _pending = string.Empty;
        _pendingContainerKey = string.Empty;
        return text.Length == 0 ? Array.Empty<string>() : [text];
    }

    private static bool EndsWithSentencePunctuation(string value)
    {
        var trimmed = value.TrimEnd('"', '\'', '\u2019', '\u201d', ')', ']', '}');
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?');
    }
}

internal sealed record WindowMonitoringStartResult(bool Started, string? Error)
{
    public static WindowMonitoringStartResult Success { get; } = new(true, null);

    public static WindowMonitoringStartResult Failure(string error) => new(false, error);
}

internal sealed class WindowTextMonitoringSession : IDisposable
{
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan EditableProbeInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);

    private readonly IntPtr _sourceWindow;
    private readonly CodexConversationAdapter _codexConversation = new();
    private readonly LmStudioConversationAdapter _lmStudioConversation = new();
    private readonly T3ConversationAdapter _t3Conversation = new();
    private readonly DiscordConversationAdapter _discordConversation = new();
    private readonly DiscordMessageTracker _discordMessages = new();
    private bool _isDiscreteFeed;
    private readonly AutoResetEvent _changeSignal = new(false);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<WindowMonitoringStartResult> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _worker;
    private WindowTextDeltaTracker _tracker = new();
    private readonly WindowTextAccumulator _accumulator = new();
    private readonly WindowTextFingerprinter _fingerprinter = new();
    private PublishedWindowTextUnitFilter _publishedUnitFilter;
    private readonly T3ResponseHistory _t3ResponseHistory = new();
    private WindowTextChangePolicy _changePolicy;
    private readonly bool _settleCodexThoughts;
    private readonly ConversationContextGuard _contextGuard = new();
    private long _contextVersion;
    private volatile bool _contextSuspended;
    private readonly WindowMonitoringDiagnosticLog? _diagnostics;
    private bool _completedResponsePublished;
    private string _completedResponseGroupKey = "";
    private bool _pendingRequiresBoundary;
    private readonly List<string> _publishedDraftUnits = [];
    private readonly List<int> _publishedDraftUnitIndices = [];
    private string _draftResponseKey = "";
    private const int MaximumDraftUnits = 512;
    private string _t3ViewKey = "";
    private string? _t3PausedResponse;
    private string _t3DraftPromptHash = "";
    private DraftPlayback? _t3DraftPlayback;
    private SuspendedT3Draft? _suspendedT3Draft;
    private bool _draftQueueInterrupted;
    private int _draftCharacters;
    private bool _draftPrefixEligible = true;
    private bool _disposed;
    private int _editableObservationVersion;
    private string _workerStage = "worker";
    private MonitoringPerformanceDiagnostics.WorkerLease? _performanceWorker;
    private readonly Action<MonitoringCaptureContext>? _workerBodyForChecks;
    private IDisposable? _captureLease;
    private int _startRequested;
    private volatile bool _startupAbandoned;
    private bool _sourceWindowClosed;

    public WindowTextMonitoringSession(
        IntPtr sourceWindow,
        WindowMonitoringDiagnosticLog? diagnostics = null,
        bool settleCodexThoughts = true,
        Action<MonitoringCaptureContext>? workerBodyForChecks = null)
    {
        if (sourceWindow == IntPtr.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWindow));
        }

        _sourceWindow = sourceWindow;
        _diagnostics = diagnostics;
        _settleCodexThoughts = settleCodexThoughts;
        _workerBodyForChecks = workerBodyForChecks;
        _changePolicy = new WindowTextChangePolicy(_fingerprinter, _settleCodexThoughts);
        _publishedUnitFilter = new PublishedWindowTextUnitFilter(_fingerprinter);
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "Pulse Reader window text monitor"
        };
        _worker.SetApartmentState(ApartmentState.MTA);
    }

    public IntPtr SourceWindow => _sourceWindow;
    internal int EditableObservationVersion => Volatile.Read(ref _editableObservationVersion);
    internal long ContextVersion => Interlocked.Read(ref _contextVersion);
    internal bool IsContextSuspended => _contextSuspended;
    internal bool HasCompletedResponse => Volatile.Read(ref _completedResponsePublished);

    public event EventHandler<MonitoringContextEventArgs>? ContextChanged;
    public event EventHandler<MonitoringThinkingEventArgs>? ThinkingChanged;

    public event EventHandler<MonitoredTextEventArgs>? TextAvailable;

    public event EventHandler<MonitoringWorkerStoppedEventArgs>? WorkerStopped;

    internal bool CompleteStartup(WindowMonitoringStartResult result) =>
        !(result.Started && (_startupAbandoned || _cancellation.IsCancellationRequested)) && _started.TrySetResult(result);

    public async Task<WindowMonitoringStartResult> StartAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _startRequested, 1) != 0 || (_worker.ThreadState & ThreadState.Unstarted) == 0)
        {
            throw new InvalidOperationException("The monitoring session has already started.");
        }

        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        _captureLease = await MonitoringCaptureGate.TryEnterAsync(startupCancellation.Token);
        if (_captureLease is null)
            return WindowMonitoringStartResult.Failure("The previous accessibility capture is still finishing. Wait a moment before trying again.");
        try { startupCancellation.Token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this); _worker.Start(); }
        catch { Interlocked.Exchange(ref _captureLease, null)?.Dispose(); throw; }
        try
        {
            return await _started.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
        }
        catch (TimeoutException)
        {
            _startupAbandoned = true;
            _cancellation.Cancel();
            _changeSignal.Set();
            var failure = WindowMonitoringStartResult.Failure("The application did not expose its text quickly enough to begin monitoring.");
            _started.TrySetResult(failure);
            return failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _startupAbandoned = true;
            _cancellation.Cancel();
            _changeSignal.Set();
            throw;
        }
    }

    private void Run()
    {
        using var captureLease = _captureLease;
        using var performanceWorker = _diagnostics?.TrackWorker(_sourceWindow);
        _performanceWorker = performanceWorker;
        var capture = new MonitoringCaptureContext(_cancellation.Token, SetWorkerStage);
        if (_workerBodyForChecks is not null)
        {
            ExecuteWorker(() => _workerBodyForChecks(capture), () => { });
            return;
        }
        UIA.IUIAutomation? automation = null;
        UIA.IUIAutomationElement? sourceElement = null;
        var subscriptions = new AutomationSubscriptions();
        ExecuteWorker(() =>
        {
            SetWorkerStage("create-automation");
            automation = CreateAutomation();
            capture.Check();
            _diagnostics?.RecordSessionStarted(_sourceWindow);
            SetWorkerStage("initial-window-element");
            sourceElement = automation.ElementFromHandle(_sourceWindow);
            capture.Check();
            if (sourceElement is null)
            {
                CompleteStartup(
                    WindowMonitoringStartResult.Failure(
                        "The selected window does not expose an accessibility surface."));
                return;
            }

            SetWorkerStage("initial-snapshot");
            var baseline = WindowTextSnapshotReader.Read(
                automation,
                sourceElement,
                _fingerprinter,
                _diagnostics?.IncludeStructure == true,
                _codexConversation, _lmStudioConversation, _discordConversation, capture, _t3Conversation);
            capture.Check();
            SetWorkerStage("establish-baseline");
            EstablishBaseline(baseline, DateTimeOffset.UtcNow);
            _diagnostics?.RecordSnapshot("baseline", baseline, Array.Empty<WindowTextChange>());
            capture.Check();
            SetWorkerStage("subscribe-events");
            TrySubscribe(automation, sourceElement, subscriptions);
            CompleteStartup(WindowMonitoringStartResult.Success);

            var lastChangeAt = DateTimeOffset.MinValue;
            var nextWatchdogAt = DateTimeOffset.UtcNow + WatchdogInterval;
            var nextEditableProbeAt = DateTimeOffset.UtcNow;
            var handles = new WaitHandle[]
            {
                _changeSignal,
                _cancellation.Token.WaitHandle
            };
            while (!_cancellation.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var sourceIsForeground = NativeInput.GetForegroundWindowHandle() == _sourceWindow;
                var waitTimeout = GetBaseWaitTimeout(now, nextWatchdogAt, nextEditableProbeAt,
                    sourceIsForeground, _codexConversation.IsRecognized || _t3Conversation.IsRecognized);
                if (!sourceIsForeground)
                {
                    nextEditableProbeAt = now;
                }

                if (_t3PausedResponse is null && !_pendingRequiresBoundary
                    && _accumulator.HasPendingText && lastChangeAt != DateTimeOffset.MinValue)
                {
                    var idleRemaining = WindowTextAccumulator.DefaultIdleFlushDelay
                                        - (now - lastChangeAt);
                    if (idleRemaining <= TimeSpan.Zero)
                    {
                        waitTimeout = TimeSpan.Zero;
                    }
                    else if (idleRemaining < waitTimeout)
                    {
                        waitTimeout = idleRemaining;
                    }
                }

                var policyRemaining = _changePolicy.TimeUntilNextSettlement(now);
                if (policyRemaining is { } remaining && remaining < waitTimeout)
                {
                    waitTimeout = remaining <= TimeSpan.Zero
                        ? TimeSpan.Zero
                        : remaining;
                }

                SetWorkerStage("wait-for-update");
                var signaled = WaitHandle.WaitAny(handles, waitTimeout);
                if (signaled == 1 || _cancellation.IsCancellationRequested)
                {
                    break;
                }

                SetWorkerStage("check-source-window");
                if (!NativeMethods.IsWindow(_sourceWindow))
                {
                    _sourceWindowClosed = true;
                    break;
                }

                if (signaled == 0)
                {
                    Thread.Sleep(ChangeDebounce);
                    while (_changeSignal.WaitOne(TimeSpan.Zero))
                    {
                    }
                }

                capture.Check();

                SetWorkerStage("refresh-window-element");
                sourceElement = automation.ElementFromHandle(_sourceWindow);
                capture.Check();
                if (sourceElement is null)
                {
                    if (_codexConversation.IsRecognized || _lmStudioConversation.IsRecognized || _discordConversation.IsRecognized
                        || _t3Conversation.IsRecognized)
                        AcceptContext(new WindowTextSnapshot([], Context: new("")), DateTimeOffset.UtcNow);
                    continue;
                }

                now = DateTimeOffset.UtcNow;
                sourceIsForeground = NativeInput.GetForegroundWindowHandle() == _sourceWindow;
                // Codex and T3 identify user bubbles structurally, so neither
                // needs a window-wide editor search, which can reach
                // an embedded browser's focused input instead of the composer.
                if (ShouldProbeEditable(sourceIsForeground, _codexConversation.IsRecognized || _t3Conversation.IsRecognized)
                    && now >= nextEditableProbeAt)
                {
                    SetWorkerStage("read-focused-editable");
                    var focusedEditable = WindowTextSnapshotReader.TryReadFocusedEditableFingerprints(
                        automation,
                        sourceElement,
                        _fingerprinter,
                        discordComposerOnly: _isDiscreteFeed, capture: capture);
                    if (focusedEditable is not null)
                    {
                        if (_isDiscreteFeed && !_contextSuspended) _discordMessages.ObserveComposer(focusedEditable, now);
                        _changePolicy.ObserveEditableFingerprints(focusedEditable, now);
                        if (focusedEditable.Count > 0) Interlocked.Increment(ref _editableObservationVersion);
                    }

                    nextEditableProbeAt = now + EditableProbeInterval;
                }

                var shouldReadFullSnapshot = signaled == 0 || now >= nextWatchdogAt;
                if (!shouldReadFullSnapshot)
                {
                    SetWorkerStage("settle-text");
                    ProcessSettled(now, ref lastChangeAt);
                    continue;
                }

                WindowTextSnapshot snapshot;
                try
                {
                    SetWorkerStage("read-snapshot");
                    snapshot = WindowTextSnapshotReader.Read(
                        automation, sourceElement, _fingerprinter, _diagnostics?.IncludeStructure == true,
                        _codexConversation, _lmStudioConversation, _discordConversation, capture, _t3Conversation);
                }
                catch (Exception exception) when ((_codexConversation.IsRecognized || _lmStudioConversation.IsRecognized
                    || _discordConversation.IsRecognized || _t3Conversation.IsRecognized) && IsAutomationException(exception))
                {
                    // A rebuilding provider is uncertain, not permission to
                    // continue publishing the old conversation's pending text.
                    snapshot = new WindowTextSnapshot([], Context: new(""));
                    _diagnostics?.RecordSignal($"snapshot-read-failed:{exception.GetType().Name}:{exception.HResult:X8}");
                }
                now = DateTimeOffset.UtcNow;
                SetWorkerStage("process-snapshot");
                ProcessSnapshot(snapshot, now, ref lastChangeAt, signaled == 0 ? "accessibility-event" : "watchdog");
                nextWatchdogAt = now + (_contextSuspended ? TimeSpan.FromMilliseconds(400) : WatchdogInterval);
            }
        }, () =>
        {
            if (automation is not null && sourceElement is not null)
                subscriptions.RemoveFrom(automation, sourceElement);
        });
    }

    internal void ExecuteWorker(Action run, Action cleanup)
    {
        Exception? failure = null;
        try { run(); }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // This is the thread boundary, not a successful retry. Any managed
            // failure terminates this session and must reach the owning UI.
            failure = exception;
            TryRecordWorkerDiagnostic(() => _diagnostics?.RecordWorkerFailure(_workerStage, exception));
            CompleteStartup(
                WindowMonitoringStartResult.Failure(
                    "Pulse Reader could not monitor this application's accessibility text."));
        }
        finally
        {
            var stopStage = _workerStage;
            var started = !_startupAbandoned && _started.Task.IsCompletedSuccessfully && _started.Task.Result.Started;
            var reason = _cancellation.IsCancellationRequested ? "cancelled"
                : failure is not null ? "failure" : _sourceWindowClosed ? "source-closed"
                : !started ? "startup-failed" : "unexpected-exit";
            _contextSuspended = true;
            Interlocked.Increment(ref _contextVersion);
            TryRecordWorkerDiagnostic(() => _diagnostics?.RecordWorkerStopped(reason, stopStage, started));

            CompleteStartup(
                WindowMonitoringStartResult.Failure("Window monitoring stopped."));
            // Provider cleanup can block. Invalidate and notify first so queued
            // text cannot revive the dead session while cleanup is pending.
            try
            {
                WorkerStopped?.Invoke(this, new MonitoringWorkerStoppedEventArgs(reason, started, stopStage));
            }
            catch (Exception exception)
            {
                TryRecordWorkerDiagnostic(() => _diagnostics?.RecordWorkerFailure("notify-owner", exception));
            }
            finally
            {
                try { SetWorkerStage("cleanup"); cleanup(); }
                catch (Exception exception)
                {
                    TryRecordWorkerDiagnostic(() => _diagnostics?.RecordWorkerFailure("cleanup", exception));
                }
                finally { _fingerprinter.Dispose(); }
            }
        }
    }

    private static void TryRecordWorkerDiagnostic(Action record)
    {
        try { record(); }
        catch (Exception exception)
        {
            // A broken/closed diagnostic destination must not prevent the owner
            // from clearing a failed latch. Never emit private exception text.
            System.Diagnostics.Debug.WriteLine("Monitor diagnostic failed: " + exception.GetType().FullName);
        }
    }

    private void SetWorkerStage(string stage)
    {
        _workerStage = stage;
        _performanceWorker?.SetStage(stage);
    }

    internal static TimeSpan GetBaseWaitTimeout(DateTimeOffset now, DateTimeOffset nextWatchdogAt,
        DateTimeOffset nextEditableProbeAt, bool sourceIsForeground, bool codexRecognized)
    {
        var wait = nextWatchdogAt - now;
        if (ShouldProbeEditable(sourceIsForeground, codexRecognized) && nextEditableProbeAt - now < wait)
            wait = nextEditableProbeAt - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    private static bool ShouldProbeEditable(bool sourceIsForeground, bool codexRecognized)
        => sourceIsForeground && !codexRecognized;

    internal void ProcessSettled(DateTimeOffset now, ref DateTimeOffset lastChangeAt)
    {
        if (_contextSuspended) return;
        if (_isDiscreteFeed)
        {
            PublishDiscordMessages(now);
            return;
        }
        var settledChanges = _changePolicy.TakeSettled(now);
        if (_t3PausedResponse is not null)
            settledChanges = settledChanges.Where(change => change.Key.EndsWith(":answer", StringComparison.Ordinal)).ToArray();
        _diagnostics?.RecordAcceptedChanges("settled", settledChanges);
        AppendChanges(settledChanges, ref lastChangeAt);
        if (_t3PausedResponse is null) FlushIfIdle(lastChangeAt);
    }

    internal void ProcessSnapshot(WindowTextSnapshot snapshot, DateTimeOffset now,
        ref DateTimeOffset lastChangeAt, string reason = "test")
    {
        if (snapshot.T3CapturePaused && (_contextSuspended || snapshot.Context?.ViewKey != _t3ViewKey))
            snapshot = new([], Context: new(""));
        if (!AcceptContext(snapshot, now))
        {
            lastChangeAt = DateTimeOffset.MinValue;
            _diagnostics?.RecordSnapshot("context-baseline-or-hold", snapshot, []);
            return;
        }
        if (snapshot.T3CapturePaused)
        {
            _t3PausedResponse = snapshot.ResponseCycleKey;
            ProcessSettled(now, ref lastChangeAt);
            _diagnostics?.RecordSnapshot("t3-capture-paused", snapshot, []);
            return;
        }
        if (_t3PausedResponse is { } previousResponse)
        {
            _t3PausedResponse = null;
            // Resuming capture can remount previously visible messages.
            // Baseline them without invalidating the active speech queue.
            // A final still settling on the same response can keep updating.
            if (snapshot.ResponseCycleKey != previousResponse || !_changePolicy.HasPendingFinal)
            {
                AppendChanges(_changePolicy.ConfirmPendingFinal(), ref lastChangeAt);
                lastChangeAt = DateTimeOffset.MinValue;
                _accumulator.Clear();
                _pendingRequiresBoundary = false;
                ResetDraftProgress();
                _completedResponsePublished = false;
                _completedResponseGroupKey = "";
                EstablishBaseline(snapshot, now);
                _diagnostics?.RecordSnapshot("t3-capture-resume-baseline", snapshot, []);
                return;
            }
        }
        if (snapshot.IsDiscreteFeed)
        {
            _isDiscreteFeed = true;
            _discordMessages.Observe(snapshot, now);
            _diagnostics?.RecordSnapshot(reason, snapshot, []);
            PublishDiscordMessages(now);
            return;
        }
        if (snapshot.T3StartsContinuation)
        {
            // A fresh working row after the old final is a confirmed boundary.
            // Hand off a pending final before the new cycle clears settlement.
            var priorFinal = _changePolicy.ConfirmPendingFinal();
            _diagnostics?.RecordAcceptedChanges("t3-continuation", priorFinal);
            AppendChanges(priorFinal, ref lastChangeAt);
        }
        _t3ResponseHistory.ObservePrompt(snapshot);
        var rawChanges = _tracker.Update(snapshot);
        ThinkingChanged?.Invoke(this, new(snapshot.Thinking, ContextVersion));
        var changes = _changePolicy.Process(snapshot, rawChanges, now);
        if (_changePolicy.ResponseCycleStarted)
        {
            if (!TryCarryT3DraftIntoFinal(snapshot, rawChanges)) ResetDraftProgress();
            _publishedUnitFilter.StartResponseCycle();
            _completedResponsePublished = false;
            _completedResponseGroupKey = "";
        }
        _diagnostics?.RecordSnapshot(reason, snapshot, rawChanges, changes);
        AppendChanges(changes, ref lastChangeAt);
        if (_t3DraftPlayback is not null && snapshot.ResponseCycleKey == _draftResponseKey
            && snapshot.T3PromptHash.Length > 0)
            _t3DraftPromptHash = snapshot.T3PromptHash;
        FlushIfIdle(lastChangeAt);
    }

    private bool TryCarryT3DraftIntoFinal(WindowTextSnapshot snapshot, IReadOnlyList<WindowTextChange> changes)
    {
        if (_t3ViewKey.Length == 0 || !_draftPrefixEligible || _completedResponsePublished
            || !_draftResponseKey.StartsWith("t3-message:", StringComparison.Ordinal)
            || !snapshot.ResponseCycleKey.StartsWith("t3-message:", StringComparison.Ordinal)
            || snapshot.T3HasNewPrompt || snapshot.T3StartsContinuation
            || (_t3DraftPromptHash.Length > 0 && snapshot.T3PromptHash.Length > 0 && _t3DraftPromptHash != snapshot.T3PromptHash)
            || changes.Count != 1 || !changes[0].IsFinalResponse
            || changes[0].Key != snapshot.ResponseCycleKey + ":answer")
            return false;

        var final = new WindowTextAccumulator();
        final.Append(changes[0]);
        var units = CompletedResponseAlignment.RestoreT3StreamedBoundaries(_publishedDraftUnits,
            final.TakeCompletedUnits(flushIncomplete: true));
        var offset = CompletedResponseAlignment.FindStreamedOpeningOffset(_publishedDraftUnits, units, out var finalOffset);
        // Require a substantial exact run at the start of the final. An ID
        // change alone never identifies two captures as the same response.
        if (finalOffset != 0 || units.Count < 2 || _publishedDraftUnits.Count - offset < 2
            || units[0] == units[1] || units[0] != _publishedDraftUnits[offset]
            || units[1] != _publishedDraftUnits[offset + 1]
            || _fingerprinter.Tokenize(units[0]).Count + _fingerprinter.Tokenize(units[1]).Count < 12)
            return false;

        _draftResponseKey = snapshot.ResponseCycleKey;
        _diagnostics?.RecordSignal("t3-draft-carried-to-final");
        return true;
    }

    internal void AppendChanges(
        IReadOnlyList<WindowTextChange> changes,
        ref DateTimeOffset lastChangeAt)
    {
        if (_contextSuspended) return;
        if (changes.Count == 0)
        {
            return;
        }

        var completedResponse = changes
            .LastOrDefault(change => change.IsFinalResponse);
        if (completedResponse.IsFinalResponse)
        {
            _accumulator.Clear();
            _pendingRequiresBoundary = false;
            var responseKey = ConversationMessageKey.IsKnown(completedResponse.Key)
                && completedResponse.Key.EndsWith(":answer", StringComparison.Ordinal) ? completedResponse.Key[..^7] : "";
            if (_t3ResponseHistory.IsRestoredReply(responseKey, completedResponse.Text, _fingerprinter))
            {
                _diagnostics?.RecordSignal("t3-restored-final-suppressed");
                return;
            }
            _t3ResponseHistory.ObserveLiveReply(responseKey);
            _accumulator.Append(completedResponse);
            lastChangeAt = DateTimeOffset.UtcNow;
            var finalUnits = _accumulator.TakeCompletedUnits(flushIncomplete: true);
            var sameTurn = responseKey.Length > 0 && responseKey == _draftResponseKey
                && _draftPrefixEligible && _publishedDraftUnits.Count > 0;
            if (sameTurn && responseKey.StartsWith("t3-message:", StringComparison.Ordinal))
            {
                var restored = CompletedResponseAlignment.RestoreT3StreamedBoundaries(_publishedDraftUnits, finalUnits);
                if (!ReferenceEquals(restored, finalUnits)) _diagnostics?.RecordSignal("t3-final-boundaries-restored");
                finalUnits = restored;
            }
            var commonPrefix = 0;
            var finalOffset = 0;
            var draftOffset = sameTurn ? CompletedResponseAlignment.FindStreamedOpeningOffset(_publishedDraftUnits, finalUnits, out finalOffset) : 0;
            (int DraftOffset, int Length)? earlierOpening = null;
            if (sameTurn && completedResponse.Key.StartsWith("t3-message:", StringComparison.Ordinal))
            {
                if (finalOffset > 0)
                    earlierOpening = CompletedResponseAlignment.FindEarlierFinalOpening(
                        _publishedDraftUnits, finalUnits, draftOffset, finalOffset, hasAlignedTail: true);
                else if (draftOffset == 0 && _publishedDraftUnits[0] != finalUnits.FirstOrDefault()
                    && !CompletedResponseAlignment.HasRewrittenOpening(_publishedDraftUnits, finalUnits)
                    && CompletedResponseAlignment.FindEarlierFinalOpening(_publishedDraftUnits, finalUnits,
                        _publishedDraftUnits.Count, finalUnits.Count) is { } opening)
                    draftOffset = opening.DraftOffset;
            }
            if (sameTurn)
                while (commonPrefix < Math.Min(finalUnits.Count - finalOffset, _publishedDraftUnits.Count - draftOffset)
                    && finalUnits[finalOffset + commonPrefix] == _publishedDraftUnits[draftOffset + commonPrefix]) commonPrefix++;
            var sameAnswer = sameTurn && draftOffset == 0 && finalOffset == 0 && commonPrefix == _publishedDraftUnits.Count;
            IReadOnlyList<int> publishedFinalIndices;
            if (sameTurn && _draftQueueInterrupted && _t3DraftPlayback is { } playback)
            {
                // Suspension canceled the old queue. Recover matching units
                // only when playback finished and queue the unread ones.
                var played = Enumerable.Range(0, commonPrefix)
                    .Where(index => playback.HasFinished(draftOffset + index))
                    .Select(index => finalOffset + index).ToHashSet();
                if (earlierOpening is { } earlier)
                    for (var index = 0; index < earlier.Length; index++)
                        if (playback.HasFinished(earlier.DraftOffset + index))
                            played.Add(index);

                var unread = Enumerable.Range(0, finalUnits.Count).Where(index => !played.Contains(index)).ToArray();
                _publishedUnitFilter.StartResponseCycle();
                publishedFinalIndices = Publish(unread.Select(index => finalUnits[index]).ToArray(), beginsCompletedResponse: true)
                    .Select(index => unread[index]).Concat(played).Order().ToArray();
                _publishedUnitFilter.Filter(finalUnits, DateTimeOffset.UtcNow);
                if (played.Count > 0) _diagnostics?.RecordSignal("t3-resumed-final-reconciled");
            }
            else if (sameAnswer)
            {
                // Keep the existing queue: this prefix includes both spoken
                // and still-queued units. Dropping the queue here would lose
                // unread sentences; publishing the whole answer would replay it.
                var prefixCount = _publishedDraftUnits.Count;
                var prefixIndices = _publishedDraftUnitIndices.ToArray();
                var appendedIndices = Publish(finalUnits.Skip(prefixCount).ToArray());
                publishedFinalIndices = prefixIndices.Concat(appendedIndices.Select(index => prefixCount + index)).ToArray();
            }
            else if (sameTurn && (commonPrefix > 0
                || CompletedResponseAlignment.HasRewrittenOpening(_publishedDraftUnits, finalUnits)))
            {
                // A later sentence was revised. The speech queue, not this
                // capture thread, knows which prefix units are still unread.
                // Reconcile atomically so neither spoken nor queued prefix
                // units are restarted or discarded with the superseded suffix.
                var revision = new CompletedResponseRevision(_publishedDraftUnits.ToArray(), finalUnits.ToArray(),
                    _publishedDraftUnitIndices.ToArray(), draftOffset, finalOffset, earlierOpening);
                var reconciledPrefix = commonPrefix;
                if (reconciledPrefix == 0)
                {
                    reconciledPrefix = 1; // the anchored identifier rewrite
                    while (reconciledPrefix < Math.Min(finalUnits.Count, _publishedDraftUnits.Count)
                        && finalUnits[reconciledPrefix] == _publishedDraftUnits[reconciledPrefix]) reconciledPrefix++;
                }
                publishedFinalIndices = Enumerable.Range(0, finalOffset)
                    .Concat(_publishedDraftUnitIndices.Where(index => index >= draftOffset && index < draftOffset + reconciledPrefix)
                        .Select(index => index - draftOffset + finalOffset))
                    .Concat(Enumerable.Range(finalOffset + reconciledPrefix, finalUnits.Count - finalOffset - reconciledPrefix)).ToArray();
                _publishedUnitFilter.StartResponseCycle();
                _publishedUnitFilter.Filter(finalUnits, DateTimeOffset.UtcNow);
                _diagnostics?.RecordPublishedUnits(finalUnits.Take(finalOffset).Skip(earlierOpening?.Length ?? 0)
                    .Concat(finalUnits.Skip(finalOffset + commonPrefix)).ToArray());
                TextAvailable?.Invoke(this, new MonitoredTextEventArgs("", beginsCompletedResponse: true,
                    contextVersion: ContextVersion, completedRevision: revision));
            }
            else
            {
                _publishedUnitFilter.StartResponseCycle();
                publishedFinalIndices = Publish(finalUnits, beginsCompletedResponse: true);
            }
            ResetDraftProgress();
            // Retain the latest completed body too: the quiet-period gate can
            // publish a later revision of this same answer. The cache remains
            // bounded exactly like the streamed draft and resets on new turns.
            if (responseKey.Length > 0)
            {
                _draftResponseKey = responseKey;
                RememberPublishedProgress(finalUnits, publishedFinalIndices);
                _t3ResponseHistory.Remember(responseKey, completedResponse.Text, finalUnits, _fingerprinter);
                _t3ResponseHistory.RememberPublishedUnits(responseKey, finalUnits, _fingerprinter);
            }
            _completedResponsePublished = true;
            _completedResponseGroupKey = completedResponse.GroupKey;
            return;
        }

        foreach (var change in changes)
        {
            if (_completedResponsePublished
                && (!ConversationMessageKey.IsKnown(change.Key)
                    || change.GroupKey != _completedResponseGroupKey)) continue;
            _pendingRequiresBoundary = ConversationMessageKey.IsKnown(change.Key)
                && change.Key.EndsWith(":working", StringComparison.Ordinal);
            if (_pendingRequiresBoundary)
            {
                var responseKey = change.Key[..^8];
                if (_draftResponseKey != responseKey)
                {
                    ResetDraftProgress();
                    _draftResponseKey = responseKey;
                }
            }
            _accumulator.Append(change);
        }

        lastChangeAt = DateTimeOffset.UtcNow;
        var units = _accumulator.TakeCompletedUnits(flushIncomplete: false);
        if (changes.Count == 1 && _pendingRequiresBoundary && (changes[0].IsNewBlock || changes[0].IsReplacement))
        {
            var repeated = _t3ResponseHistory.CountRemountedOpening(_draftResponseKey, units, _fingerprinter);
            if (repeated == 0 && changes[0].IsReplacement)
                repeated = _t3ResponseHistory.CountRepeatedOpening(_draftResponseKey, units, _fingerprinter);
            if (repeated > 0)
            {
                _diagnostics?.RecordSignal("t3-historical-opening-suppressed");
                units = units.Skip(repeated).ToArray();
            }
            if (changes[0].IsReplacement)
            {
                var retained = _t3ResponseHistory.FilterRepeatedSingleWords(_draftResponseKey, units, _fingerprinter);
                if (retained.Count < units.Count) _diagnostics?.RecordSignal("t3-working-short-replay-suppressed");
                units = retained;
            }
        }
        Publish(units);
    }

    private void FlushIfIdle(DateTimeOffset lastChangeAt)
    {
        // Streaming Codex drafts may pause between words. Only sentence or
        // paragraph boundaries (or the final-answer handoff) release that tail.
        // Generic feeds retain the existing idle-fragment behavior.
        if (!_pendingRequiresBoundary && _accumulator.HasPendingText
            && lastChangeAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow - lastChangeAt
            >= WindowTextAccumulator.DefaultIdleFlushDelay)
        {
            Publish(_accumulator.TakeCompletedUnits(flushIncomplete: true));
        }
    }

    private void PublishDiscordMessages(DateTimeOffset now)
    {
        var before = _discordMessages.SuppressedEchoCount;
        var ready = _discordMessages.TakeReady(now);
        if (_discordMessages.SuppressedEchoCount > before)
            _diagnostics?.RecordSignal("discord-submitted-echo-suppressed");
        _diagnostics?.RecordPublishedUnits(ready.Select(request => request.DisplayText).ToArray());
        foreach (var request in ready)
            TextAvailable?.Invoke(this, new(request.DisplayText, contextVersion: ContextVersion,
                isDiscreteMessage: true, speechRequest: request));
    }

    private IReadOnlyList<int> Publish(
        IReadOnlyList<string> units,
        bool beginsCompletedResponse = false,
        bool deduplicate = true)
    {
        var publishableUnits = deduplicate ? _publishedUnitFilter.Filter(units, DateTimeOffset.UtcNow) : units;
        var publishedIndices = new List<int>(publishableUnits.Count);
        var nextPublished = 0;
        for (var index = 0; index < units.Count && nextPublished < publishableUnits.Count; index++)
        {
            if (units[index] != publishableUnits[nextPublished]) continue;
            publishedIndices.Add(index);
            nextPublished++;
        }
        if (_pendingRequiresBoundary && !_completedResponsePublished && publishableUnits.Count > 0)
        {
            _t3ResponseHistory.ObserveLiveReply(_draftResponseKey);
            _t3ResponseHistory.RememberPublishedUnits(_draftResponseKey, publishableUnits, _fingerprinter);
        }
        var draftStart = _publishedDraftUnits.Count;
        if ((_pendingRequiresBoundary || _completedResponsePublished)
            && _draftResponseKey.Length > 0 && _draftPrefixEligible)
        {
            // Completed bodies must retain their structural positions, including
            // filtered occurrences, while the queue sees only published ones.
            if (_completedResponsePublished) RememberPublishedProgress(units, publishedIndices);
            else RememberPublishedProgress(publishableUnits);
        }
        var playback = _pendingRequiresBoundary && !_completedResponsePublished && _draftPrefixEligible
            && publishableUnits.Count > 0 && _t3ViewKey.Length > 0
            && _draftResponseKey.StartsWith("t3-message:", StringComparison.Ordinal)
                ? _t3DraftPlayback ??= new DraftPlayback()
                : null;
        _diagnostics?.RecordPublishedUnits(publishableUnits);
        for (var index = 0; index < publishableUnits.Count; index++)
        {
            var draftIndex = draftStart + index;
            TextAvailable?.Invoke(
                this,
                new MonitoredTextEventArgs(
                    publishableUnits[index],
                    beginsCompletedResponse && index == 0,
                    ContextVersion, isDiscreteMessage: !deduplicate,
                    playbackFinished: playback is null ? null : () => playback.MarkFinished(draftIndex)));
        }
        return publishedIndices;
    }

    private void RememberPublishedProgress(IReadOnlyList<string> units, IReadOnlyList<int>? publishedIndices = null)
    {
        var offset = _publishedDraftUnits.Count;
        foreach (var unit in units)
        {
            _draftCharacters += unit.Length;
            if (_draftCharacters > MonitoringSpeechQueue.MaximumMonitoredQueueCharacters
                || _publishedDraftUnits.Count >= MaximumDraftUnits)
            {
                _publishedDraftUnits.Clear();
                _publishedDraftUnitIndices.Clear();
                _draftPrefixEligible = false;
                break;
            }
            _publishedDraftUnits.Add(unit);
        }
        if (_draftPrefixEligible)
            _publishedDraftUnitIndices.AddRange((publishedIndices ?? Enumerable.Range(0, units.Count).ToArray())
                .Select(index => offset + index));
    }

    internal void EstablishBaseline(WindowTextSnapshot snapshot, DateTimeOffset now)
    {
        var view = snapshot.Context?.ViewKey;
        _t3ViewKey = view?.StartsWith("t3:", StringComparison.Ordinal) == true ? view : "";
        _t3ResponseHistory.EstablishBaseline(snapshot);
        foreach (var block in snapshot.Blocks.Where(block => block.IsFinalResponse && block.Key.EndsWith(":answer", StringComparison.Ordinal)))
            _t3ResponseHistory.Remember(block.Key[..^7], block.Text, [], _fingerprinter);
        _isDiscreteFeed = snapshot.IsDiscreteFeed;
        _discordMessages.EstablishBaseline(snapshot, now);
        _tracker.EstablishBaseline(snapshot);
        _changePolicy.EstablishBaseline(snapshot, now);
        // Attaching or returning mid-turn does not trigger ResponseCycleStarted.
        // Still retain published units for that recognized response, rather than
        // letting the generic two-second duplicate window expire after a redraw.
        if (ConversationMessageKey.IsKnown(snapshot.ResponseCycleKey))
        {
            _publishedUnitFilter.StartResponseCycle();
            // Baseline text is history, even when the active thought is later
            // rewritten wholesale. Context suspension resets publication memory;
            // seed it with completed baseline units so a replacement cannot
            // replay that unchanged opening. Do not consume an unfinished tail
            // or seed other turns/final answers into working-text deduplication.
            var baseline = new WindowTextAccumulator();
            foreach (var block in snapshot.Blocks.Where(block =>
                         block.Key == snapshot.ResponseCycleKey + ":working"
                         && !block.IsFinalResponse && !block.IsStatus))
                baseline.Append(new(block.Text, true, true, block.Key, block.ContainerKey));
            var baselineUnits = baseline.TakeCompletedUnits(flushIncomplete: false);
            _publishedUnitFilter.Filter(baselineUnits, now);
            _t3ResponseHistory.RememberBaselineUnits(snapshot.ResponseCycleKey, baselineUnits, _fingerprinter);
        }
        if (_suspendedT3Draft is { } draft && draft.View == _t3ViewKey && draft.Response == snapshot.ResponseCycleKey)
        {
            _draftResponseKey = draft.Response;
            _t3DraftPromptHash = draft.PromptHash;
            _t3DraftPlayback = draft.Playback;
            _draftQueueInterrupted = true;
            RememberPublishedProgress(draft.Units);
        }
        _suspendedT3Draft = null;
        _contextGuard.EstablishBaseline(snapshot.Context);
        _contextSuspended = _contextGuard.IsSuspended;
    }

    internal bool AcceptContext(WindowTextSnapshot snapshot, DateTimeOffset now)
    {
        var action = _contextGuard.Observe(snapshot.Context, now);
        if (action == ConversationContextAction.Continue) return true;
        _t3PausedResponse = null;
        if (!_contextSuspended)
        {
            if (_t3ViewKey.Length > 0 && _t3DraftPlayback is { } playback && _draftPrefixEligible && !_completedResponsePublished)
                _suspendedT3Draft = new(_t3ViewKey, _draftResponseKey, _t3DraftPromptHash, _publishedDraftUnits.ToArray(), playback);
            _contextSuspended = true;
            Interlocked.Increment(ref _contextVersion);
            _diagnostics?.RecordSignal("context-suspended");
            ContextChanged?.Invoke(this, new MonitoringContextEventArgs(true));
        }
        _accumulator.Clear();
        _pendingRequiresBoundary = false;
        ResetDraftProgress();
        _tracker = new WindowTextDeltaTracker();
        _changePolicy = new WindowTextChangePolicy(_fingerprinter, _settleCodexThoughts);
        _publishedUnitFilter = new PublishedWindowTextUnitFilter(_fingerprinter);
        _completedResponsePublished = false;
        _completedResponseGroupKey = "";
        if (action == ConversationContextAction.Baseline)
        {
            EstablishBaseline(snapshot, now);
            _diagnostics?.RecordSignal("context-rebaselined");
            ContextChanged?.Invoke(this, new MonitoringContextEventArgs(false));
        }
        return false;
    }

    private void ResetDraftProgress()
    {
        _publishedDraftUnits.Clear();
        _publishedDraftUnitIndices.Clear();
        _draftResponseKey = "";
        _t3DraftPromptHash = "";
        _t3DraftPlayback = null;
        _draftQueueInterrupted = false;
        _draftCharacters = 0;
        _draftPrefixEligible = true;
    }

    private sealed record SuspendedT3Draft(string View, string Response, string PromptHash, string[] Units, DraftPlayback Playback);

    private sealed class DraftPlayback
    {
        private readonly int[] _finished = new int[MaximumDraftUnits];
        public bool HasFinished(int index) => Volatile.Read(ref _finished[index]) != 0;
        public void MarkFinished(int index) => Volatile.Write(ref _finished[index], 1);
    }

    private void TrySubscribe(
        UIA.IUIAutomation automation,
        UIA.IUIAutomationElement sourceElement,
        AutomationSubscriptions subscriptions)
    {
        subscriptions.StructureChanged = new StructureChangedHandler(
            () => SignalChange("structure-changed"));
        subscriptions.PropertyChanged = new PropertyChangedHandler(
            () => SignalChange("name-changed"));
        subscriptions.AutomationEvent = new AutomationEventHandler(
            eventId => SignalChange($"automation-event-{eventId}"));

        try
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            automation.AddStructureChangedEventHandler(
                sourceElement,
                UIA.TreeScope.TreeScope_Descendants,
                null!,
                subscriptions.StructureChanged);
            subscriptions.StructureRegistered = true;
        }
        catch (Exception exception) when (IsAutomationException(exception))
        {
            subscriptions.StructureChanged = null;
        }

        try
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            automation.AddPropertyChangedEventHandler(
                sourceElement,
                UIA.TreeScope.TreeScope_Descendants,
                null!,
                subscriptions.PropertyChanged,
                [UIA.UIA_PropertyIds.UIA_NamePropertyId]);
            subscriptions.PropertyRegistered = true;
        }
        catch (Exception exception) when (IsAutomationException(exception))
        {
            subscriptions.PropertyChanged = null;
        }

        foreach (var eventId in new[]
                 {
                     UIA.UIA_EventIds.UIA_Text_TextChangedEventId,
                     UIA.UIA_EventIds.UIA_LiveRegionChangedEventId,
                     UIA.UIA_EventIds.UIA_AsyncContentLoadedEventId
                 })
        {
            try
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                automation.AddAutomationEventHandler(
                    eventId,
                    sourceElement,
                    UIA.TreeScope.TreeScope_Descendants,
                    null!,
                    subscriptions.AutomationEvent);
                subscriptions.AutomationEventIds.Add(eventId);
            }
            catch (Exception exception) when (IsAutomationException(exception))
            {
                // The two-second watchdog still detects providers without this event.
            }
        }
    }

    private void SignalChange(string signal)
    {
        _diagnostics?.RecordSignal(signal);
        _changeSignal.Set();
    }

    private static UIA.IUIAutomation CreateAutomation()
    {
        try
        {
            return new UIA.CUIAutomation8();
        }
        catch (COMException)
        {
            return new UIA.CUIAutomation();
        }
    }

    private static bool IsAutomationException(Exception exception) =>
        exception is ArgumentException
            or ExternalException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _changeSignal.Set();
        if (!_worker.IsAlive)
        {
            _fingerprinter.Dispose();
            _cancellation.Dispose();
            _changeSignal.Dispose();
            return;
        }

        var worker = _worker;
        var cancellation = _cancellation;
        var changeSignal = _changeSignal;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (worker.Join(TimeSpan.FromSeconds(2)))
            {
                cancellation.Dispose();
                changeSignal.Dispose();
            }
        });
    }

    private sealed class AutomationSubscriptions
    {
        public StructureChangedHandler? StructureChanged { get; set; }

        public PropertyChangedHandler? PropertyChanged { get; set; }

        public AutomationEventHandler? AutomationEvent { get; set; }

        public bool StructureRegistered { get; set; }

        public bool PropertyRegistered { get; set; }

        public List<int> AutomationEventIds { get; } = [];

        public void RemoveFrom(
            UIA.IUIAutomation automation,
            UIA.IUIAutomationElement sourceElement)
        {
            try
            {
                if (StructureRegistered && StructureChanged is not null)
                {
                    automation.RemoveStructureChangedEventHandler(sourceElement, StructureChanged);
                }

                if (PropertyRegistered && PropertyChanged is not null)
                {
                    automation.RemovePropertyChangedEventHandler(sourceElement, PropertyChanged);
                }

                if (AutomationEvent is not null)
                {
                    foreach (var eventId in AutomationEventIds)
                    {
                        automation.RemoveAutomationEventHandler(
                            eventId,
                            sourceElement,
                            AutomationEvent);
                    }
                }
            }
            catch (Exception exception) when (IsAutomationException(exception))
            {
                // The source provider may already have gone away.
            }
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class StructureChangedHandler(Action signal) :
        UIA.IUIAutomationStructureChangedEventHandler
    {
        public void HandleStructureChangedEvent(
            UIA.IUIAutomationElement sender,
            UIA.StructureChangeType changeType,
            int[] runtimeId) => signal();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class PropertyChangedHandler(Action signal) :
        UIA.IUIAutomationPropertyChangedEventHandler
    {
        public void HandlePropertyChangedEvent(
            UIA.IUIAutomationElement sender,
            int propertyId,
            object newValue) => signal();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class AutomationEventHandler(Action<int> signal) :
        UIA.IUIAutomationEventHandler
    {
        public void HandleAutomationEvent(
            UIA.IUIAutomationElement sender,
            int eventId) => signal(eventId);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr windowHandle);
    }
}

internal sealed record CompletedResponseRevision(
    IReadOnlyList<string> DraftUnits, IReadOnlyList<string> FinalUnits,
    IReadOnlyList<int>? PublishedDraftIndices = null, int DraftPrefixOffset = 0, int FinalPrefixOffset = 0,
    (int DraftOffset, int Length)? EarlierOpening = null);

internal sealed class MonitoredTextEventArgs(
    string text,
    bool beginsCompletedResponse = false,
    long contextVersion = 0,
    bool isDiscreteMessage = false,
    SpeechRequest? speechRequest = null,
    CompletedResponseRevision? completedRevision = null,
    Action? playbackFinished = null) : EventArgs
{
    public CompletedResponseRevision? CompletedRevision { get; } = completedRevision;
    public SpeechRequest? SpeechRequest { get; } = speechRequest;
    public Action? PlaybackFinished { get; } = playbackFinished;
    public bool IsDiscreteMessage { get; } = isDiscreteMessage;
    public long ContextVersion { get; } = contextVersion;
    public string Text { get; } = text;

    public bool BeginsCompletedResponse { get; } = beginsCompletedResponse;
}

internal sealed class MonitoringContextEventArgs(bool suspended) : EventArgs
{
    public bool Suspended { get; } = suspended;
}

internal sealed class MonitoringWorkerStoppedEventArgs(string reason, bool wasStarted, string stage) : EventArgs
{
    public string Reason { get; } = reason;
    public bool WasStarted { get; } = wasStarted;
    public string Stage { get; } = stage;
}

internal static partial class WindowTextSnapshotReader
{
    private const int MaximumEditableAncestorDepth = 32;
    private static readonly string[][] NonverbalEdgeLabels =
    [
        ["response", "complete"],
        ["context", "automatically", "compacted"],
        ["compacting", "context"],
        ["compacting", "the", "context"],
        ["compacted", "context"],
        ["compacted", "the", "context"],
        ["compressing", "context"],
        ["compressing", "the", "context"],
        ["compressed", "context"],
        ["compressed", "the", "context"]
    ];

    internal static IReadOnlyList<WindowTextBlock> PreferAccessibleGroupAggregates(
        IReadOnlyList<WindowTextBlock> visibleBlocks,
        IReadOnlyList<WindowTextBlock> aggregateBlocks,
        WindowTextFingerprinter fingerprinter)
    {
        if (visibleBlocks.Count == 0 || aggregateBlocks.Count == 0)
        {
            return visibleBlocks;
        }

        aggregateBlocks = CompleteListItemBodies(visibleBlocks, aggregateBlocks);

        var preferredByGroup = new Dictionary<string, WindowTextBlock>(StringComparer.Ordinal);
        foreach (var visibleGroup in visibleBlocks
                     .Where(block => !string.IsNullOrEmpty(block.GroupKey))
                     .GroupBy(block => block.GroupKey, StringComparer.Ordinal))
        {
            var meaningfulVisible = visibleGroup
                .Select(block => new
                {
                    Block = block,
                    Words = fingerprinter.Create(block.Text).WordHashes
                })
                .Where(item => item.Words.Count >= 3)
                .ToArray();
            if (meaningfulVisible.Length == 0)
            {
                continue;
            }
            if (visibleBlocks.Any(block => block.GroupKey != visibleGroup.Key
                    && block.Order > visibleGroup.Min(item => item.Order)
                    && block.Order < visibleGroup.Max(item => item.Order)))
            {
                // Nested lists belong to another UIA group. Assemble the
                // complete ordered range below rather than just its prose.
                continue;
            }

            var preferred = aggregateBlocks
                .Where(block => string.Equals(
                    block.GroupKey,
                    visibleGroup.Key,
                    StringComparison.Ordinal))
                .Select(block => new
                {
                    Block = block,
                    Words = fingerprinter.Create(block.Text).WordHashes
                })
                .Where(candidate =>
                    candidate.Words.Count >= meaningfulVisible.Max(item => item.Words.Count)
                    && meaningfulVisible.All(item =>
                        WindowTextChangePolicy.FindSequence(
                            candidate.Words,
                            item.Words) >= 0))
                .OrderByDescending(candidate => candidate.Words.Count)
                .Select(candidate => candidate.Block)
                .FirstOrDefault();
            if (preferred is null)
            {
                continue;
            }

            preferredByGroup[visibleGroup.Key] = preferred with
            {
                Text = RemoveNonverbalEdgeLabels(preferred.Text),
                ContainerKey = visibleGroup.Key,
                Order = visibleGroup.Min(block => block.Order),
                IsStatus = false,
                IsAggregate = true
            };
        }

        var meaningfulVisibleBlocks = visibleBlocks
            .Select(block => new
            {
                Block = block,
                Words = fingerprinter.Create(block.Text).WordHashes
            })
            .Where(item => item.Words.Count > 0)
            .ToArray();
        var preferredByBlock = new Dictionary<string, WindowTextBlock>(StringComparer.Ordinal);
        var suppressedEarlierBlocks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in aggregateBlocks
                     .Select(block => new
                     {
                         Block = block,
                         Words = fingerprinter.Create(block.Text).WordHashes
                     })
                     .OrderByDescending(item => item.Words.Count))
        {
            var matches = meaningfulVisibleBlocks
                .Where(item => !preferredByGroup.ContainsKey(item.Block.GroupKey))
                .Where(item => item.Block.Order >= candidate.Block.Order - 2)
                .Select(item => new
                {
                    item.Block,
                    item.Words,
                    Position = WindowTextChangePolicy.FindSequence(
                        candidate.Words,
                        item.Words)
                })
                .Where(item => item.Position >= 0)
                .OrderBy(item => item.Block.Order)
                .ToArray();
            var requiredMatches = candidate.Block.IsCompletedAggregate ? 1 : 2;
            if (matches.Count(item => item.Words.Count >= 3) < requiredMatches
                || candidate.Block.Order > matches[0].Block.Order
                || matches[0].Block.Order - candidate.Block.Order > 2
                || candidate.Words.Count < matches.Sum(item => item.Words.Count))
            {
                continue;
            }

            var previousEnd = -1;
            var preservesOrder = true;
            foreach (var match in matches)
            {
                if (match.Position < previousEnd)
                {
                    preservesOrder = false;
                    break;
                }

                previousEnd = match.Position + match.Words.Count;
            }

            if (!preservesOrder)
            {
                continue;
            }

            // A partial accessibility summary must not move the closing prose
            // ahead of list content that is only exposed on its parent item.
            if (meaningfulVisibleBlocks.Any(item =>
                    item.Block.Order > matches[0].Block.Order
                    && item.Block.Order < matches[^1].Block.Order
                    && !matches.Any(match => match.Block.Key == item.Block.Key)))
            {
                continue;
            }

            var preferred = candidate.Block with
            {
                Text = RemoveNonverbalEdgeLabels(candidate.Block.Text),
                ContainerKey = candidate.Block.GroupKey,
                Order = matches[0].Block.Order,
                IsStatus = false,
                IsAggregate = true
            };
            foreach (var match in matches)
            {
                preferredByBlock.TryAdd(match.Block.Key, preferred);
            }

            foreach (var earlier in meaningfulVisibleBlocks.Where(item =>
                         item.Words.Count >= 3
                         && item.Block.Order < candidate.Block.Order - 2
                         && matches.Any(match => match.Words.SequenceEqual(item.Words))))
            {
                suppressedEarlierBlocks.Add(earlier.Block.Key);
            }
        }

        if (preferredByGroup.Count == 0 && preferredByBlock.Count == 0)
        {
            return visibleBlocks;
        }

        var result = new List<WindowTextBlock>();
        var insertedAggregates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in visibleBlocks.OrderBy(block => block.Order))
        {
            if (suppressedEarlierBlocks.Contains(block.Key))
            {
                continue;
            }

            if (preferredByGroup.TryGetValue(block.GroupKey, out var preferred))
            {
                if (insertedAggregates.Add(preferred.Key))
                {
                    result.Add(preferred);
                }

                continue;
            }

            if (preferredByBlock.TryGetValue(block.Key, out preferred))
            {
                if (insertedAggregates.Add(preferred.Key))
                {
                    result.Add(preferred);
                }

                continue;
            }

            result.Add(block);
        }

        return result;
    }

    private static IReadOnlyList<WindowTextBlock> CompleteListItemBodies(
        IReadOnlyList<WindowTextBlock> visibleBlocks,
        IReadOnlyList<WindowTextBlock> aggregates)
    {
        // Some providers expose only a bold heading in the hidden summary,
        // while the list-item name carries the complete prose. Expand only
        // unambiguous standalone headings; never splice over partial prose.
        return aggregates.Select(aggregate =>
        {
            var text = aggregate.Text;
            var nextAggregateOrder = aggregates.Where(other => other.Order > aggregate.Order)
                .Select(other => other.Order).DefaultIfEmpty(int.MaxValue).Min();
            foreach (var item in visibleBlocks.Where(item => item.Order > aggregate.Order
                         && item.Order < nextAggregateOrder && item.ListItemHeading.Length > 0))
            {
                if (text.Contains(item.Text, StringComparison.Ordinal)) continue;
                var heading = item.ListItemHeading;
                var start = text.IndexOf(heading, StringComparison.Ordinal);
                if (start < 0 || text.IndexOf(heading, start + heading.Length, StringComparison.Ordinal) >= 0
                    || (start > 0 && !char.IsWhiteSpace(text[start - 1]))) continue;
                var end = start + heading.Length;
                if (end < text.Length && !char.IsWhiteSpace(text[end])) continue;
                var lineEnd = end;
                while (lineEnd < text.Length && text[lineEnd] is ' ' or '\t') lineEnd++;
                if (lineEnd < text.Length && text[lineEnd] is not '\r' and not '\n') continue;
                text = text[..start] + item.Text + text[end..];
            }
            return aggregate with { Text = text };
        }).ToArray();
    }

    internal static string RemoveNonverbalEdgeLabels(string text)
    {
        var result = text.Trim();
        var completedResponse = HasNonverbalEdgeLabel(
            result,
            NonverbalEdgeLabels[0]);
        var changed = true;
        while (result.Length > 0 && changed)
        {
            changed = false;
            var words = Regex.Matches(
                result,
                @"[\p{L}\p{Nd}]+",
                RegexOptions.CultureInvariant);
            foreach (var label in NonverbalEdgeLabels)
            {
                if (WordsMatch(words, 0, label))
                {
                    result = words.Count == label.Length
                        ? string.Empty
                        : result[words[label.Length].Index..].Trim();
                    changed = true;
                    break;
                }

                var trailingStart = words.Count - label.Length;
                if (trailingStart >= 0 && WordsMatch(words, trailingStart, label))
                {
                    result = Regex.Replace(
                            result[..words[trailingStart].Index],
                            @"[\s\p{Cf}\p{Pd}:;,|]+$",
                            string.Empty,
                            RegexOptions.CultureInvariant)
                        .Trim();
                    changed = true;
                    break;
                }
            }
        }

        return completedResponse
            ? RemoveRepeatedLeadingPartialLines(result)
            : result;
    }

    private static string RemoveRepeatedLeadingPartialLines(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
        while (TryFindLeadingContentLines(lines, out var first, out var second))
        {
            var partial = lines[first].Trim();
            var completed = lines[second].Trim();
            if (partial.Length >= completed.Length
                || !completed.StartsWith(partial, StringComparison.OrdinalIgnoreCase)
                || char.IsLetterOrDigit(completed[partial.Length]))
            {
                break;
            }

            lines.RemoveRange(0, second);
        }

        return string.Join(newline, lines);
    }

    private static bool TryFindLeadingContentLines(
        IReadOnlyList<string> lines,
        out int first,
        out int second)
    {
        first = -1;
        second = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            if (first < 0)
            {
                first = index;
            }
            else
            {
                second = index;
                return true;
            }
        }

        return false;
    }

    private static bool WordsMatch(
        MatchCollection words,
        int start,
        IReadOnlyList<string> expected)
    {
        if (start < 0 || start + expected.Count > words.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!words[start + index].Value.Equals(
                    expected[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasNonverbalEdgeLabel(
        string text,
        IReadOnlyList<string> label)
    {
        var words = Regex.Matches(
            text,
            @"[\p{L}\p{Nd}]+",
            RegexOptions.CultureInvariant);
        return WordsMatch(words, 0, label)
               || WordsMatch(words, words.Count - label.Count, label);
    }

    public static WindowTextSnapshot Read(
        UIA.IUIAutomation automation,
        UIA.IUIAutomationElement sourceElement,
        WindowTextFingerprinter fingerprinter,
        bool includeDiagnostics = false,
        CodexConversationAdapter? codexConversation = null,
        LmStudioConversationAdapter? lmStudioConversation = null,
        DiscordConversationAdapter? discordConversation = null,
        MonitoringCaptureContext? capture = null,
        T3ConversationAdapter? t3Conversation = null)
    {
        capture ??= new MonitoringCaptureContext();
        discordConversation ??= new DiscordConversationAdapter();
        var discordSnapshot = capture.Read("capture-discord-detection", () => discordConversation.TryRead(automation, sourceElement, fingerprinter));
        if (discordSnapshot is not null) return discordSnapshot;
        lmStudioConversation ??= new LmStudioConversationAdapter();
        var lmStudioSnapshot = capture.Read("capture-lmstudio-detection", () => lmStudioConversation.TryRead(automation, sourceElement, fingerprinter));
        if (lmStudioSnapshot is not null) return lmStudioSnapshot;
        t3Conversation ??= new T3ConversationAdapter();
        var t3Snapshot = ReadScopedT3Snapshot(automation, sourceElement, fingerprinter, t3Conversation, capture);
        if (t3Snapshot is not null) return t3Snapshot;
        codexConversation ??= new CodexConversationAdapter();
        var codexSnapshot = ReadScopedCodexSnapshot(automation, sourceElement, fingerprinter,
            includeDiagnostics, codexConversation, capture);
        if (codexSnapshot is not null) return codexSnapshot;
        capture.Stage("capture-create-conditions");
        var textCondition = automation.CreatePropertyCondition(
            UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
            UIA.UIA_ControlTypeIds.UIA_TextControlTypeId);
        var listCondition = automation.CreatePropertyCondition(
            UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
            UIA.UIA_ControlTypeIds.UIA_ListItemControlTypeId);
        var visibleCondition = automation.CreatePropertyCondition(
            UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId,
            false);
        var elements = capture.Read("capture-enumerate-visible", () => sourceElement.FindAll(
            UIA.TreeScope.TreeScope_Descendants,
            automation.CreateAndCondition(automation.CreateOrCondition(textCondition, listCondition), visibleCondition)));
        var diagnostics = includeDiagnostics
            ? new List<WindowTextDiagnosticObservation>()
            : null;
        var editableFingerprints = new List<WindowTextFingerprint>();
        var blocks = ReadTextElements(
            automation,
            sourceElement,
            elements,
            diagnostics,
            editableFingerprints,
            fingerprinter,
            codexConversation, capture: capture);
        if (blocks.Count > 0 || editableFingerprints.Count > 0 || codexConversation.IsRecognized)
        {
            return new WindowTextSnapshot(blocks, diagnostics, editableFingerprints, codexConversation.ResponseCycleKey,
                codexConversation.IsRecognized ? new ConversationContext(codexConversation.ViewKey) : null);
        }

        return new WindowTextSnapshot(
            capture.Read("capture-document-fallback", () => ReadDocumentFallback(automation, sourceElement, diagnostics, capture)),
            diagnostics,
            editableFingerprints);
    }

    private static WindowTextSnapshot? ReadScopedCodexSnapshot(UIA.IUIAutomation automation,
        UIA.IUIAutomationElement source, WindowTextFingerprinter fingerprinter,
        bool includeDiagnostics, CodexConversationAdapter adapter, MonitoringCaptureContext capture)
    {
        var classes = automation.CreateOrCondition(
            automation.CreateOrCondition(
                automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
                    CodexConversationView.LegacyTranscriptPrefix, UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring),
                automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
                    CodexConversationView.TranscriptPrefix, UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring)),
            automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
                "thread-scroll-container", UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring));
        var overlays = automation.CreateOrCondition(
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_AriaRolePropertyId, "dialog"),
            automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_AriaPropertiesPropertyId,
                "modal=true", UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring));
        var visible = automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId, false);
        var request = automation.CreateCacheRequest();
        request.TreeScope = UIA.TreeScope.TreeScope_Element;
        request.TreeFilter = automation.RawViewCondition;
        foreach (var property in new[] { UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
            UIA.UIA_PropertyIds.UIA_AriaRolePropertyId, UIA.UIA_PropertyIds.UIA_AriaPropertiesPropertyId,
            UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId }) request.AddProperty(property);
        // Discover only structural landmarks. In particular, do not read Name,
        // TextPattern, or editor values anywhere in a browser/sidebar panel.
        var elements = capture.Read("capture-codex-landmarks", () => source.FindAllBuildCache(
            UIA.TreeScope.TreeScope_Descendants, automation.CreateAndCondition(visible,
                automation.CreateOrCondition(classes, overlays)), request));
        var landmarks = new List<CodexCaptureLandmark<UIA.IUIAutomationElement>>();
        for (var index = 0; index < elements.Length; index++)
        {
            capture.Check();
            var element = elements.GetElement(index);
            // Match ancestor-cache identity even if this provider has no runtime ID.
            landmarks.Add(new(element, CreateKey(element, 0), element.CachedClassName ?? "",
                element.CachedAriaRole ?? "", element.CachedAriaProperties ?? "", element.CachedIsOffscreen == 0));
        }
        return CodexCaptureScope.Read(landmarks, adapter, (transcript, key) =>
        {
            capture.Stage(CodexConversationView.IsTranscript(transcript.CachedClassName ?? "")
                ? "capture-codex-selected-transcript" : "capture-codex-selected-scroll");
            var tree = CacheCodexTranscript(automation, transcript, key, capture);
            adapter.IsResponseActive = capture.Read("capture-response-activity",
                () => ReadCodexResponseActivity(automation, source, capture));
            return ReadCachedCodexFrame(tree, key, fingerprinter, adapter, includeDiagnostics, capture);
        });
    }

    public static IReadOnlyList<WindowTextFingerprint>? TryReadFocusedEditableFingerprints(
        UIA.IUIAutomation automation,
        UIA.IUIAutomationElement sourceElement,
        WindowTextFingerprinter fingerprinter,
        bool discordComposerOnly = false,
        MonitoringCaptureContext? capture = null)
    {
        capture ??= new MonitoringCaptureContext();
        try
        {
            capture.Check();
            var focused = automation.GetFocusedElement();
            if (focused is null)
            {
                return null;
            }

            UIA.IUIAutomationElement? editable = null;
            var current = focused;
            var withinSource = false;
            for (var depth = 0;
                 depth < MaximumEditableAncestorDepth && current is not null;
                 depth++)
            {
                capture.Check();
                if (editable is null && IsEditableElement(current, capture, automation: automation))
                {
                    editable = current;
                }

                if (automation.CompareElements(current, sourceElement) != 0)
                {
                    withinSource = true;
                    break;
                }

                current = automation.RawViewWalker.GetParentElement(current);
            }

            if (withinSource && editable is not null && editable.CurrentIsPassword == 0)
            {
                if (discordComposerOnly && !(editable.CurrentClassName ?? "").Split(' ')
                    .Any(c => c.StartsWith("slateTextArea_", StringComparison.Ordinal))) return null;
                var focusedText = TryReadEditableText(automation, editable);
                if (discordComposerOnly)
                {
                    if (focusedText is null) return null;
                    var draft = fingerprinter.Create(focusedText);
                    return draft.WordCount > 0 ? [draft] : [];
                }
                if (!string.IsNullOrWhiteSpace(focusedText))
                {
                    return [fingerprinter.Create(focusedText)];
                }
            }

            if (discordComposerOnly) return null;
            return ReadEditableDescendantFingerprints(
                automation,
                sourceElement,
                fingerprinter, capture);
        }
        catch (Exception exception) when (IsElementException(exception))
        {
            return null;
        }
    }

    private static IReadOnlyList<WindowTextFingerprint> ReadEditableDescendantFingerprints(
        UIA.IUIAutomation automation,
        UIA.IUIAutomationElement sourceElement,
        WindowTextFingerprinter fingerprinter,
        MonitoringCaptureContext capture)
    {
        capture.Check();
        var condition = automation.CreatePropertyCondition(
            UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
            UIA.UIA_ControlTypeIds.UIA_EditControlTypeId);
        var editableElements = sourceElement.FindAll(
            UIA.TreeScope.TreeScope_Descendants,
            condition);
        if (editableElements is null)
        {
            return Array.Empty<WindowTextFingerprint>();
        }

        var fingerprints = new List<WindowTextFingerprint>();
        for (var index = 0; index < editableElements.Length; index++)
        {
            capture.Check();
            var editableElement = editableElements.GetElement(index);
            if (editableElement is null
                || editableElement.CurrentIsPassword != 0
                || !IsEditableElement(editableElement, capture))
            {
                continue;
            }

            var text = TryReadEditableText(automation, editableElement);
            if (!string.IsNullOrWhiteSpace(text))
            {
                fingerprints.Add(fingerprinter.Create(text));
            }
        }

        return fingerprints;
    }

    private static string? TryReadEditableText(
        UIA.IUIAutomation automation,
        UIA.IUIAutomationElement editable)
    {
        if (editable.GetCurrentPattern(UIA.UIA_PatternIds.UIA_ValuePatternId)
            is UIA.IUIAutomationValuePattern valuePattern
            && !string.IsNullOrWhiteSpace(valuePattern.CurrentValue))
        {
            return valuePattern.CurrentValue.Trim();
        }

        if (editable.GetCurrentPattern(UIA.UIA_PatternIds.UIA_TextPatternId)
            is UIA.IUIAutomationTextPattern textPattern)
        {
            var text = textPattern.DocumentRange.GetText(-1)?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        var condition = automation.CreatePropertyCondition(
            UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
            UIA.UIA_ControlTypeIds.UIA_TextControlTypeId);
        var descendants = editable.FindAll(UIA.TreeScope.TreeScope_Descendants, condition);
        if (descendants is null)
        {
            return null;
        }

        var parts = new List<string>();
        for (var index = 0; index < descendants.Length; index++)
        {
            var text = descendants.GetElement(index)?.CurrentName?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static IReadOnlyList<WindowTextBlock> ReadTextElements(
        UIA.IUIAutomation automation,
        UIA.IUIAutomationElement sourceElement,
        UIA.IUIAutomationElementArray? elements,
        List<WindowTextDiagnosticObservation>? diagnostics,
        List<WindowTextFingerprint> editableFingerprints,
        WindowTextFingerprinter fingerprinter,
        CodexConversationAdapter codexConversation,
        string expandedViewKey = "",
        MonitoringCaptureContext? capture = null,
        UIA.IUIAutomationElement? responseActivitySource = null)
    {
        capture ??= new MonitoringCaptureContext();
        capture.Stage(expandedViewKey.Length == 0 ? "capture-inspect-visible" : "capture-inspect-conversation");
        if (elements is null)
        {
            return Array.Empty<WindowTextBlock>();
        }

        var blocks = new List<WindowTextBlock>();
        var accessibleGroupAggregates = new List<WindowTextBlock>();
        var codexParts = new List<CodexTextPart>();
        var capturedListItems = new Dictionary<string, string>(StringComparer.Ordinal);
        var inspectionCache = new ElementInspectionCache(automation, sourceElement, capture);
        var editableTextByContainer = new Dictionary<string, List<string>>(
            StringComparer.Ordinal);
        for (var index = 0; index < elements.Length; index++)
        {
            capture.Check();
            try
            {
                var element = elements.GetElement(index);
                if (element is null
                    || element.CurrentIsPassword != 0)
                {
                    continue;
                }

                var text = element.CurrentName?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var completedAggregate = HasNonverbalEdgeLabel(
                    text,
                    NonverbalEdgeLabels[0]);
                text = RemoveNonverbalEdgeLabels(text);
                if (text.Length == 0)
                {
                    continue;
                }

                var context = capture.Read("capture-element-context", () => InspectElementContext(
                    automation,
                    sourceElement,
                    element,
                    diagnostics is not null,
                    inspectionCache, capture));
                var coveredByList = context.AncestorKeys.Skip(1).Any(key =>
                    capturedListItems.TryGetValue(key, out var itemText)
                    && itemText.Contains(text, StringComparison.Ordinal));
                if (coveredByList) continue;
                var included = ShouldIncludeObservedText(
                    text,
                    context.Editable,
                    context.InteractiveChrome,
                    context.AccessibilityOnly,
                    context.AuxiliaryChrome,
                    context.WithinCodexThread,
                    element.CurrentAriaRole ?? "",
                    context.ParagraphKey.Length > 0);
                codexConversation.IsRecognized |= context.WithinCodexThread;
                if (context.WithinCodexThread && !context.Editable && !context.InteractiveChrome)
                {
                    var role = element.CurrentAriaRole ?? "";
                    var isBoundary = role == "heading" && ContainsCssClassToken(element.CurrentClassName ?? "", "sr-only");
                    if (isBoundary && text is "You said:" or "ChatGPT said:")
                        codexParts.Add(new(text == "You said:" ? CodexTextPartKind.UserBoundary : CodexTextPartKind.AssistantBoundary, text, index));
                    else if (context.WithinUserMessage)
                        codexParts.Add(new(CodexTextPartKind.UserText, text, index, UserMessageKey: context.UserMessageKey));
                    else if (context.IsStatus && context.IsFinalResponseContext && !context.AuxiliaryChrome)
                        codexParts.Add(new(CodexTextPartKind.FinalAnnouncement, text, index));
                    else if (included)
                        codexParts.Add(new(context.WithinWorkingColumn ? CodexTextPartKind.WorkingContent : CodexTextPartKind.Content,
                            text, index, context.ParagraphKey));
                }
                if (diagnostics is not null)
                {
                    var bounds = element.CurrentBoundingRectangle;
                    diagnostics.Add(
                        new WindowTextDiagnosticObservation(
                            CreateKey(element, index),
                            text,
                            context.Editable,
                            included,
                            element.CurrentControlType,
                            element.CurrentClassName ?? string.Empty,
                            element.CurrentAriaRole ?? string.Empty,
                            element.CurrentAutomationId ?? string.Empty,
                            bounds.left,
                            bounds.top,
                            bounds.right,
                            bounds.bottom,
                            context.Ancestors!));
                }

                if (context.Editable)
                {
                    var editableContainerKey = string.IsNullOrEmpty(context.EditableContainerKey)
                        ? CreateKey(element, index)
                        : context.EditableContainerKey;
                    if (!editableTextByContainer.TryGetValue(
                            editableContainerKey,
                            out var editableText))
                    {
                        editableText = [];
                        editableTextByContainer[editableContainerKey] = editableText;
                    }

                    editableText.Add(text);
                }

                if (!included)
                {
                    if (context.AccessibilityOnly
                        && !context.Editable
                        && !context.InteractiveChrome
                        && !context.AuxiliaryChrome
                        && !string.IsNullOrEmpty(context.GroupKey))
                    {
                        accessibleGroupAggregates.Add(
                            new WindowTextBlock(
                                CreateKey(element, index),
                                text,
                                context.ContainerKey,
                                index,
                                IsStatus: false,
                                GroupKey: context.GroupKey,
                                IsCompletedAggregate: completedAggregate,
                                IsFinalResponse: completedAggregate && context.IsStatus
                                    && context.IsFinalResponseContext));
                    }

                    continue;
                }

                var listHeading = string.Empty;
                if (element.CurrentControlType == UIA.UIA_ControlTypeIds.UIA_ListItemControlTypeId)
                {
                    var firstText = element.FindFirst(UIA.TreeScope.TreeScope_Descendants,
                        automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
                            UIA.UIA_ControlTypeIds.UIA_TextControlTypeId))?.CurrentName?.Trim();
                    if (!string.IsNullOrEmpty(firstText) && firstText.Length >= 8
                        && firstText.Length < text.Length && text.StartsWith(firstText, StringComparison.Ordinal)
                        && firstText[^1] is '.' or '?' or '!' or ':')
                        listHeading = firstText;
                }
                blocks.Add(
                    new WindowTextBlock(
                        CreateKey(element, index),
                        text,
                        context.ContainerKey,
                        index,
                        context.IsStatus,
                        context.GroupKey,
                        ListItemHeading: listHeading));
                if (element.CurrentControlType == UIA.UIA_ControlTypeIds.UIA_ListItemControlTypeId)
                {
                    capturedListItems[CreateKey(element, index)] = text;
                }
            }
            catch (Exception exception) when (IsElementException(exception))
            {
                // A virtualized child can disappear between FindAll and inspection.
            }
        }

        editableFingerprints.AddRange(
            editableTextByContainer.Values
                .Select(values => fingerprinter.Create(string.Join(' ', values))));

        if (codexConversation.IsRecognized)
        {
            var resolvedView = inspectionCache.ConversationView.Resolve(inspectionCache.HasModal);
            capture.Stage(inspectionCache.HasModal ? "capture-codex-view-modal"
                : resolvedView.Length == 0 ? "capture-codex-view-unavailable"
                : expandedViewKey.Length > 0 && resolvedView != expandedViewKey ? "capture-codex-view-mismatch"
                : "capture-codex-view-resolved");
            codexConversation.SetView(resolvedView);
            if (codexConversation.ViewKey.Length == 0) return [];
            if (expandedViewKey.Length > 0 && expandedViewKey != codexConversation.ViewKey)
            {
                // The view changed while its subtree was being captured.
                codexConversation.SetView("");
                return [];
            }
            if (expandedViewKey.Length == 0
                && inspectionCache.ConversationElements.TryGetValue(codexConversation.ViewKey, out var conversation))
            {
                // The visible pass identifies the active conversation and
                // collects composer echoes. Read mounted text throughout that
                // conversation, including paragraphs below the viewport, without
                // expanding capture to hidden views or unrelated application UI.
                var textKinds = automation.CreateOrCondition(
                    automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId, UIA.UIA_ControlTypeIds.UIA_TextControlTypeId),
                    automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId, UIA.UIA_ControlTypeIds.UIA_ListItemControlTypeId));
                var conversationElements = capture.Read("capture-enumerate-conversation",
                    () => conversation.FindAll(UIA.TreeScope.TreeScope_Descendants, textKinds));
                diagnostics?.Clear();
                return ReadTextElements(automation, sourceElement, conversationElements, diagnostics,
                    editableFingerprints, fingerprinter, codexConversation, codexConversation.ViewKey, capture);
            }
            codexConversation.IsResponseActive = capture.Read("capture-response-activity", () => ReadCodexResponseActivity(automation, responseActivitySource ?? sourceElement, capture));
            capture.Stage("capture-assemble-conversation");
            return codexConversation.Assemble(codexParts, fingerprinter);
        }
        return PreferAccessibleGroupAggregates(
            blocks,
            accessibleGroupAggregates,
            fingerprinter);
    }

    private static ElementContext InspectElementContext(
        UIA.IUIAutomation automation,
        UIA.IUIAutomationElement sourceElement,
        UIA.IUIAutomationElement element,
        bool includeDiagnostics,
        ElementInspectionCache inspectionCache,
        MonitoringCaptureContext capture)
    {
        var path = new List<InspectedElement>();
        var current = element;
        for (var depth = 0; depth < MaximumEditableAncestorDepth && current is not null; depth++)
        {
            capture.Check();
            var inspected = inspectionCache.Get(current);
            path.Add(inspected);
            if (inspected.IsSource) break;
            current = inspected.Parent;
        }
        var bounds = element.CurrentBoundingRectangle;
        return CreateElementContext(path, bounds.right - bounds.left <= 2 || bounds.bottom - bounds.top <= 2,
            includeDiagnostics, capture);
    }

    private static ElementContext CreateElementContext(IReadOnlyList<InspectedElement> path,
        bool smallBounds, bool includeDiagnostics, MonitoringCaptureContext capture)
    {
        List<WindowTextDiagnosticAncestor>? ancestorList = includeDiagnostics ? [] : null;
        var ancestorKeys = new List<string>();
        var editable = false;
        var isStatus = false;
        var interactiveChrome = false;
        var accessibilityOnly = false;
        var auxiliaryChrome = false;
        var containerKey = string.Empty;
        var groupKey = string.Empty;
        var editableContainerKey = string.Empty;
        var responseContainerClass = string.Empty;
        var withinCodexThread = false;
        var withinUserMessage = false;
        var userMessageKey = string.Empty;
        var withinWorkingColumn = false;
        var paragraphKey = string.Empty;
        for (var depth = 0; depth < path.Count; depth++)
        {
            capture.Check();
            var inspected = path[depth];
            ancestorKeys.Add(inspected.Key);
            ancestorList?.Add(inspected.Diagnostic);
            var currentClass = inspected.Diagnostic.ClassName;
            if (paragraphKey.Length == 0 && (inspected.Diagnostic.AriaRole == "paragraph"
                || currentClass.Split(' ').Any(token => token.StartsWith("_Paragraph_", StringComparison.Ordinal)
                    || token.StartsWith("Paragraph-", StringComparison.Ordinal))))
                paragraphKey = inspected.Key;
            if (depth == 1) containerKey = inspected.Key;
            if (depth == 2) groupKey = inspected.Key;
            if (depth == 2) responseContainerClass = currentClass;
            withinCodexThread |= CodexConversationView.IsContainer(currentClass);
            withinUserMessage |= ContainsCssClassToken(currentClass, "bg-user-message");
            if (userMessageKey.Length == 0 && ContainsCssClassToken(currentClass, "bg-user-message"))
                userMessageKey = inspected.Key;
            withinWorkingColumn |= ContainsCssClassToken(currentClass, "group")
                && ContainsCssClassToken(currentClass, "flex-col")
                && ContainsCssClassToken(currentClass, "min-w-0");
            isStatus |= inspected.IsStatus;
            interactiveChrome |= inspected.Interactive;
            accessibilityOnly |= inspected.AccessibilityOnly;
            if (depth == 0)
            {
                accessibilityOnly |= smallBounds;
            }
            auxiliaryChrome |= inspected.Auxiliary;
            if (inspected.Editable)
            {
                editable = true;
                if (string.IsNullOrEmpty(editableContainerKey))
                {
                    editableContainerKey = inspected.Key;
                }
            }

            if (inspected.IsSource)
            {
                break;
            }
        }

        return new ElementContext(
            editable,
            isStatus,
            interactiveChrome,
            accessibilityOnly,
            auxiliaryChrome,
            containerKey,
            groupKey,
            editableContainerKey,
            IsFinalResponseContext(responseContainerClass, withinCodexThread),
            withinCodexThread,
            withinUserMessage,
            userMessageKey,
            withinWorkingColumn,
            paragraphKey,
            ancestorKeys,
            ancestorList);
    }

    // In the observed Codex accessibility tree, working updates are nested in
    // a flex-column wrapper. Only the final summary belongs directly to the
    // top-level response container. Unknown layouts retain ordinary playback.
    internal static bool IsFinalResponseContext(string containerClass, bool withinCodexThread) =>
        withinCodexThread
        && ContainsCssClassToken(containerClass, "relative")
        && ContainsCssClassToken(containerClass, "shrink-0")
        && !ContainsCssClassToken(containerClass, "flex-col");

    private static bool IsStatusElement(int type, string role) =>
        type == 50017 || string.Equals(role, "status", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldIncludeObservedText(
        string text,
        bool editable,
        bool interactiveChrome,
        bool accessibilityOnly,
        bool auxiliaryChrome,
        bool withinCodexThread = false,
        string ariaRole = "",
        bool withinParagraph = false) =>
        !editable
        && !interactiveChrome
        && !accessibilityOnly
        && !auxiliaryChrome
        // Codex exposes its changing progress metadata as a standalone
        // description. Including it in the working aggregate makes the entire
        // response look volatile. Descriptions within real paragraphs, and
        // descriptions in other applications, remain eligible.
        && !(withinCodexThread && !withinParagraph
             && ariaRole.Equals("description", StringComparison.OrdinalIgnoreCase))
        && !IsStandaloneTimestamp(text)
        && !IsElapsedProgressLabel(text)
        && !IsNonverbalProgressLabel(text);

    internal static bool IsStandaloneTimestamp(string text) =>
        DateTime.TryParseExact(
            text.Trim(),
            ["H:mm", "HH:mm", "h:mm tt", "hh:mm tt"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out _);

    internal static bool IsElapsedProgressLabel(string text) =>
        Regex.IsMatch(
            text.Trim(),
            @"^(?:thinking|thought|worked)\s+for\s+\d+(?:\.\d+)?\s*(?:ms|s|m|h|milliseconds?|seconds?|minutes?|hours?)(?:\s+\d+(?:\.\d+)?\s*(?:ms|s|m|h|milliseconds?|seconds?|minutes?|hours?))*[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsNonverbalProgressLabel(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && RemoveNonverbalEdgeLabels(text).Length == 0;

    private static bool IsInteractiveChromeElement(int type) =>
        type is
            UIA.UIA_ControlTypeIds.UIA_ButtonControlTypeId
            or UIA.UIA_ControlTypeIds.UIA_CheckBoxControlTypeId
            or UIA.UIA_ControlTypeIds.UIA_ComboBoxControlTypeId
            or UIA.UIA_ControlTypeIds.UIA_MenuItemControlTypeId
            or UIA.UIA_ControlTypeIds.UIA_RadioButtonControlTypeId
            or UIA.UIA_ControlTypeIds.UIA_SliderControlTypeId
            or UIA.UIA_ControlTypeIds.UIA_SpinnerControlTypeId
            or UIA.UIA_ControlTypeIds.UIA_SplitButtonControlTypeId
            or UIA.UIA_ControlTypeIds.UIA_TabItemControlTypeId;

    private static bool IsAuxiliaryChromeElement(int type, string className, string role)
    {
        return IsAuxiliaryClassName(className)
               || IsNavigationRole(role)
               || type is UIA.UIA_ControlTypeIds.UIA_ToolBarControlTypeId
                   or UIA.UIA_ControlTypeIds.UIA_MenuBarControlTypeId
               || string.Equals(
                   role,
                   "progressbar",
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAuxiliaryClassName(string className) =>
        className.Contains("shimmer", StringComparison.OrdinalIgnoreCase)
        || className.Contains("skeleton", StringComparison.OrdinalIgnoreCase)
        || className.Contains("spinner", StringComparison.OrdinalIgnoreCase)
        || className.Contains("activity-header", StringComparison.OrdinalIgnoreCase)
        || className.Contains("turn-diff", StringComparison.OrdinalIgnoreCase)
        || className.Contains("tool-call", StringComparison.OrdinalIgnoreCase)
        || className.Contains("tool-output", StringComparison.OrdinalIgnoreCase)
        || className.Contains("user-message", StringComparison.OrdinalIgnoreCase)
        || className.Contains("outgoing-message", StringComparison.OrdinalIgnoreCase)
        || className.Contains("message-out", StringComparison.OrdinalIgnoreCase)
        || className.Contains("message-sent", StringComparison.OrdinalIgnoreCase);

    internal static bool IsNavigationRole(string role) =>
        role.Equals("navigation", StringComparison.OrdinalIgnoreCase)
        || role.Equals("toolbar", StringComparison.OrdinalIgnoreCase)
        || role.Equals("menubar", StringComparison.OrdinalIgnoreCase)
        || role.Equals("tablist", StringComparison.OrdinalIgnoreCase);

    private static bool? ReadCodexResponseActivity(UIA.IUIAutomation automation, UIA.IUIAutomationElement source,
        MonitoringCaptureContext capture)
    {
        var buttons = source.FindAll(UIA.TreeScope.TreeScope_Descendants,
            automation.CreateAndCondition(
                automation.CreateAndCondition(
                    automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId, UIA.UIA_ControlTypeIds.UIA_ButtonControlTypeId),
                    automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId, false)),
                automation.CreateAndCondition(
                    automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
                        "size-token-button-composer", UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring),
                    automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
                        "bg-composer-primary", UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring))));
        bool? active = null;
        for (var index = 0; index < buttons.Length; index++)
        {
            capture.Check();
            try
            {
                var button = buttons.GetElement(index);
                var classes = button.CurrentClassName ?? "";
                if (!ContainsCssClassToken(classes, "size-token-button-composer")
                    || !ContainsCssClassToken(classes, "bg-composer-primary")) continue;
                var name = button.CurrentName;
                if (name == "Stop") return true;
                if (name is "Start voice chat" or "Send" or "Send message" or "Submit") active = false;
            }
            catch (Exception exception) when (IsElementException(exception)) { }
        }
        return active;
    }

    private static bool IsAccessibilityOnlyClass(string className) =>
        ContainsCssClassToken(className, "sr-only")
            || ContainsCssClassToken(className, "visually-hidden")
            || ContainsCssClassToken(className, "screen-reader-only");

    internal static bool ContainsCssClassToken(string className, string token) =>
        className.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(token, StringComparer.OrdinalIgnoreCase);

    private sealed record ElementContext(
        bool Editable,
        bool IsStatus,
        bool InteractiveChrome,
        bool AccessibilityOnly,
        bool AuxiliaryChrome,
        string ContainerKey,
        string GroupKey,
        string EditableContainerKey,
        bool IsFinalResponseContext,
        bool WithinCodexThread,
        bool WithinUserMessage,
        string UserMessageKey,
        bool WithinWorkingColumn,
        string ParagraphKey,
        IReadOnlyList<string> AncestorKeys,
        IReadOnlyList<WindowTextDiagnosticAncestor>? Ancestors);

    // UIA properties and patterns cross the process boundary. Inspect shared
    // ancestors once per snapshot, not once for every text node beneath them.
    // This cache is deliberately discarded between snapshots so focus,
    // editability, and navigation changes cannot leave stale filtering rules.
    private sealed class ElementInspectionCache(UIA.IUIAutomation automation, UIA.IUIAutomationElement source,
        MonitoringCaptureContext capture)
    {
        private readonly Dictionary<string, InspectedElement> _elements = new(StringComparer.Ordinal);
        private readonly UIA.IUIAutomationCacheRequest _request = CreateRequest(automation);
        public CodexConversationView ConversationView { get; } = new();
        public Dictionary<string, UIA.IUIAutomationElement> ConversationElements { get; } = new(StringComparer.Ordinal);
        public bool HasModal { get; private set; }

        public InspectedElement Get(UIA.IUIAutomationElement element)
        {
            capture.Check();
            var key = CreateKey(element, 0);
            if (_elements.TryGetValue(key, out var existing)) return existing;
            var isSource = automation.CompareElements(element, source) != 0;
            var cached = capture.Read("capture-cache-ancestor", () => element.BuildUpdatedCache(_request));
            var metadata = new WindowTextDiagnosticAncestor(cached.CachedControlType,
                cached.CachedClassName ?? "", cached.CachedAriaRole ?? "", cached.CachedAutomationId ?? "");
            var result = capture.Read("capture-ancestor-properties", () =>
            {
                var status = IsStatusElement(metadata.ControlType, metadata.AriaRole);
                var interactive = IsInteractiveChromeElement(metadata.ControlType);
                var accessibilityOnly = IsAccessibilityOnlyClass(metadata.ClassName);
                var auxiliary = IsAuxiliaryChromeElement(metadata.ControlType, metadata.ClassName, metadata.AriaRole);
                var editable = IsEditableElement(element, capture, "-" + metadata.ControlType, automation: automation,
                    knownType: metadata.ControlType, knownPassword: cached.CachedIsPassword != 0,
                    patterns: CachedMonitoringPatterns.FromProperties(
                        cached.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId),
                        cached.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId),
                        cached.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId)));
                var parent = isSource ? null : capture.Read("ancestor-parent", () => automation.RawViewWalker.GetParentElement(element));
                return new InspectedElement(key, metadata, status, interactive, accessibilityOnly, auxiliary, editable, isSource, parent);
            });
            if (CodexConversationView.IsContainer(result.Diagnostic.ClassName))
            {
                ConversationView.Observe(key, result.Diagnostic.ClassName, cached.CachedIsOffscreen == 0);
                ConversationElements[key] = element;
            }
            if (cached.CachedIsOffscreen == 0 && CodexConversationView.IsBlockingOverlay(
                result.Diagnostic.AriaRole, result.Diagnostic.ClassName,
                cached.CachedAriaProperties ?? "")) HasModal = true;
            _elements.Add(key, result);
            return result;
        }

        private static UIA.IUIAutomationCacheRequest CreateRequest(UIA.IUIAutomation automation)
        {
            var request = automation.CreateCacheRequest();
            request.TreeScope = UIA.TreeScope.TreeScope_Element;
            request.TreeFilter = automation.RawViewCondition;
            request.AutomationElementMode = UIA.AutomationElementMode.AutomationElementMode_None;
            foreach (var property in new[] { UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
                UIA.UIA_PropertyIds.UIA_ClassNamePropertyId, UIA.UIA_PropertyIds.UIA_AriaRolePropertyId,
                UIA.UIA_PropertyIds.UIA_AutomationIdPropertyId, UIA.UIA_PropertyIds.UIA_IsPasswordPropertyId,
                UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId, UIA.UIA_PropertyIds.UIA_AriaPropertiesPropertyId,
                UIA.UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId, UIA.UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId,
                UIA.UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId })
                request.AddProperty(property);
            return request;
        }
    }

    private sealed record InspectedElement(string Key, WindowTextDiagnosticAncestor Diagnostic,
        bool IsStatus, bool Interactive, bool AccessibilityOnly, bool Auxiliary, bool Editable,
        bool IsSource, UIA.IUIAutomationElement? Parent);

    private static bool IsEditableElement(UIA.IUIAutomationElement element, MonitoringCaptureContext? capture = null,
        string category = "", bool confirmedChatDocument = false, UIA.IUIAutomation? automation = null,
        int? knownType = null, bool? knownPassword = null, CachedMonitoringPatterns? patterns = null)
    {
        capture ??= new MonitoringCaptureContext();
        var type = knownType ?? capture.Read("editable-control-type" + category, () => element.CurrentControlType);
        var password = type != UIA.UIA_ControlTypeIds.UIA_EditControlTypeId
            && (knownPassword ?? capture.Read("editable-password" + category, () => element.CurrentIsPassword) != 0);
        if (!password && type == UIA.UIA_ControlTypeIds.UIA_DocumentControlTypeId && automation is not null)
            confirmedChatDocument |= IsConfirmedCodexDocument(automation, element, capture);
        return MonitoringEditabilityPolicy.IsEditable(type, password, confirmedChatDocument,
            () => patterns?.WritableValue(WritableValue) ?? WritableValue(),
            () => patterns?.WritableText(WritableTextRange) ?? WritableTextRange());

        bool WritableValue() => capture.Read("editable-value-pattern" + category, () => element.GetCurrentPattern(UIA.UIA_PatternIds.UIA_ValuePatternId))
            is UIA.IUIAutomationValuePattern valuePattern
            && capture.Read("editable-value-readonly" + category, () => valuePattern.CurrentIsReadOnly) == 0;

        bool WritableTextRange()
        {
            if (capture.Read("editable-text-pattern" + category, () => element.GetCurrentPattern(UIA.UIA_PatternIds.UIA_TextPatternId))
                is not UIA.IUIAutomationTextPattern textPattern) return false;
            var range = capture.Read("editable-document-range" + category, () => textPattern.DocumentRange);
            var isReadOnly = capture.Read("editable-range-readonly" + category, () => range.GetAttributeValue(
                UIA.UIA_TextAttributeIds.UIA_IsReadOnlyAttributeId));
            return isReadOnly is false || isReadOnly is int and 0;
        }
    }

    private static bool IsConfirmedCodexDocument(UIA.IUIAutomation automation,
        UIA.IUIAutomationElement document, MonitoringCaptureContext capture)
    {
        try
        {
            return capture.Read("capture-identify-chat-document", () =>
            {
                // Use existing Codex landmarks and visibility rules. Do not
                // infer read-only content from a process name or window title.
                if (document.CurrentAriaRole is "textbox" or "searchbox") return false;
                var classes = automation.CreateOrCondition(
                    automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
                        "_transcriptContent_", UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring),
                    automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
                        "thread-scroll-container", UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring));
                var visible = automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId, false);
                var landmark = document.FindFirst(UIA.TreeScope.TreeScope_Descendants, automation.CreateAndCondition(classes, visible));
                return landmark is not null && CodexConversationView.IsContainer(landmark.CurrentClassName ?? "");
            });
        }
        catch (Exception error) when (IsElementException(error)) { return false; }
    }

    private static IReadOnlyList<WindowTextBlock> ReadDocumentFallback(
        UIA.IUIAutomation automation,
        UIA.IUIAutomationElement sourceElement,
        List<WindowTextDiagnosticObservation>? diagnostics,
        MonitoringCaptureContext capture)
    {
        var condition = automation.CreatePropertyCondition(
            UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
            UIA.UIA_ControlTypeIds.UIA_DocumentControlTypeId);
        var documents = sourceElement.FindAll(UIA.TreeScope.TreeScope_Descendants, condition);
        if (documents is null)
        {
            return Array.Empty<WindowTextBlock>();
        }

        var blocks = new List<WindowTextBlock>();
        for (var index = 0; index < documents.Length; index++)
        {
            capture.Check();
            try
            {
                var document = documents.GetElement(index);
                if (document is null
                    || !IsSafeFallbackDocument(automation, sourceElement, document)
                    || document.GetCurrentPattern(UIA.UIA_PatternIds.UIA_TextPatternId)
                    is not UIA.IUIAutomationTextPattern textPattern)
                {
                    continue;
                }

                var text = textPattern.DocumentRange.GetText(-1)?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var key = CreateKey(document, index);
                    blocks.Add(new WindowTextBlock(key, text));
                    if (diagnostics is not null)
                    {
                        var bounds = document.CurrentBoundingRectangle;
                        diagnostics.Add(
                            new WindowTextDiagnosticObservation(
                                key,
                                text,
                                Editable: false,
                                Included: true,
                                document.CurrentControlType,
                                document.CurrentClassName ?? string.Empty,
                                document.CurrentAriaRole ?? string.Empty,
                                document.CurrentAutomationId ?? string.Empty,
                                bounds.left,
                                bounds.top,
                                bounds.right,
                                bounds.bottom,
                                Array.Empty<WindowTextDiagnosticAncestor>()));
                    }
                }
            }
            catch (Exception exception) when (IsElementException(exception))
            {
                // Ignore an inaccessible or stale document provider.
            }
        }

        return blocks;
    }

    private static bool IsSafeFallbackDocument(UIA.IUIAutomation automation,
        UIA.IUIAutomationElement source, UIA.IUIAutomationElement document)
    {
        // A whole DocumentRange cannot separate editor content from readable
        // content. Fail closed, including when a read-only wrapper contains an editor.
        var current = document;
        var reachedSource = false;
        for (var depth = 0; current is not null && depth < MaximumEditableAncestorDepth; depth++)
        {
            if (current.CurrentIsOffscreen != 0 || IsEditableElement(current)) return false;
            if (automation.CompareElements(current, source) != 0) { reachedSource = true; break; }
            current = automation.RawViewWalker.GetParentElement(current);
        }
        if (!reachedSource) return false;
        var descendants = document.FindAll(UIA.TreeScope.TreeScope_Descendants, automation.CreateTrueCondition());
        if (descendants is not null)
        {
            if (descendants.Length > 2048) return false;
            for (var index = 0; index < descendants.Length; index++)
                if (IsEditableElement(descendants.GetElement(index))) return false;
        }
        return true;
    }

    private static string CreateKey(UIA.IUIAutomationElement element, int fallbackIndex)
    {
        try
        {
            var runtimeId = element.GetRuntimeId();
            if (runtimeId is { Length: > 0 })
            {
                return string.Join('.', runtimeId);
            }
        }
        catch (Exception exception) when (IsElementException(exception))
        {
        }

        var bounds = element.CurrentBoundingRectangle;
        return string.Join(
            ':',
            element.CurrentControlType,
            element.CurrentAutomationId,
            bounds.left,
            bounds.top,
            bounds.right,
            bounds.bottom,
            fallbackIndex);
    }

    private static bool IsElementException(Exception exception) =>
        exception is ArgumentException
            or ExternalException
            or InvalidOperationException
            or NotSupportedException;
}
