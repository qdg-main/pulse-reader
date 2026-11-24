using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using PulseReader.TextPreparation;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace PulseReader.Speech;

internal sealed class KokoroSpeechService :
    IConfigurableKokoroSpeechService,
    IPlaybackNavigationSpeechService
{
    internal const string DefaultVoice = KokoroSpeechSettings.DefaultVoice;
    internal const int RapidBackWindowMilliseconds = 750;

    private readonly Lock _stateLock = new();
    private readonly Lock _playbackMutationLock = new();
    private readonly KokoroModelStore _modelStore;
    private readonly IContextualPronunciationProcessor? _contextualPronunciation;
    private KokoroSpeechSettings _settings;
    private VoiceSelection? _cachedVoiceSelection;
    private KokoroVoice? _cachedVoice;
    private CancellationTokenSource? _requestCancellation;
    private Task<KokoroTTS>? _engineLoadTask;
    private KokoroTTS? _engine;
    private KokoroPlaybackSession? _currentSession;
    private KokoroPlaybackPlan? _currentPlan;
    private int? _progressiveBoundaryUnitIndex;
    private int _lastBackTargetUnitIndex = -1;
    private long _lastBackTimestamp;
    private SpeechPlaybackState _state;
    private bool _disposed;

    public KokoroSpeechService(
        KokoroModelStore? modelStore = null,
        KokoroSpeechSettings? settings = null,
        IContextualPronunciationProcessor? contextualPronunciation = null)
    {
        _modelStore = modelStore ?? new KokoroModelStore();
        _settings = settings ?? KokoroSpeechSettings.Default;
        _contextualPronunciation = contextualPronunciation;
        _contextualPronunciation?.SetEnabled(_settings.ContextualPronunciationEnabled);
    }

    public ContextualPronunciationAvailability ContextualPronunciationAvailability =>
        _contextualPronunciation?.Availability
        ?? new ContextualPronunciationAvailability(
            false,
            string.Empty,
            "The optional contextual-pronunciation service is unavailable.");

    public SpeechPlaybackState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event EventHandler? SpeakingStateChanged;

    public void UpdateSettings(KokoroSpeechSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!KokoroSpeechSettings.IsValid(
                settings.VoiceName,
                settings.Speed,
                settings.Volume,
                settings.SecondaryVoiceName,
                settings.PrimaryVoiceWeight))
        {
            throw new ArgumentException("The Kokoro speech settings are invalid.", nameof(settings));
        }

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (VoiceSelection.From(_settings) != VoiceSelection.From(settings))
            {
                _cachedVoiceSelection = null;
                _cachedVoice = null;
            }

            _settings = settings;
        }

        _contextualPronunciation?.SetEnabled(settings.ContextualPronunciationEnabled);
    }

    public Task<SpeechStartResult> TrySpeakAsync(string text) => TrySpeakAsync(new SpeechRequest(text));

    public async Task<SpeechStartResult> TrySpeakAsync(SpeechRequest request)
    {
        CancellationTokenSource requestCancellation;
        KokoroSpeechSettings settings;
        KokoroPlaybackSession? session = null;

        lock (_stateLock)
        {
            if (_disposed)
            {
                return SpeechStartResult.Failure("Kokoro speech synthesis is unavailable.");
            }

            if (_state != SpeechPlaybackState.Idle)
            {
                return SpeechStartResult.Failure("Kokoro speech synthesis is already active.");
            }

            _state = SpeechPlaybackState.Preparing;
            _currentSession = null;
            ResetBackSequenceLocked();
            _requestCancellation = new CancellationTokenSource();
            requestCancellation = _requestCancellation;
            settings = _settings;
        }

        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var prepared = await Task.Run(
                request.Prepare,
                requestCancellation.Token);
            IReadOnlyList<ContextualPronunciationTarget> contextualTargets =
                Array.Empty<ContextualPronunciationTarget>();
            ChannelReader<ContextualPronunciationProgress>? contextualProgress = null;
            if (settings.ContextualPronunciationEnabled
                && _contextualPronunciation is not null)
            {
                contextualTargets = ContextualPronunciationRules.FindTargets(prepared);
                if (contextualTargets.Count > 0)
                {
                    contextualProgress = StartContextualProgress(
                        prepared,
                        requestCancellation.Token);
                }
            }

            var engineTask = GetEngineAsync(requestCancellation.Token);
            var voiceTask = Task.Run(
                () => ResolveVoice(settings),
                requestCancellation.Token);
            var engine = await engineTask;
            var voice = await voiceTask;
            var pipeline = CreatePipelineConfig(settings.Speed);
            var deterministicPlan = await Task.Run(
                () => CreatePlaybackPlan(prepared, voice, pipeline),
                requestCancellation.Token);

            if (contextualProgress is not null)
            {
                var firstContextualUnit = contextualTargets[0].UnitIndex;
                var initialPlan = deterministicPlan;
                var initialEndUnit = firstContextualUnit;
                var initialSegments = deterministicPlan.CreateSegmentsBeforeUnit(initialEndUnit);
                var initialSegmentsComplete = false;

                if (initialSegments.Count == 0)
                {
                    var firstProgress = await contextualProgress.ReadAsync(
                        requestCancellation.Token);
                    initialPlan = await Task.Run(
                        () => CreatePlaybackPlan(firstProgress.Document, voice, pipeline),
                        requestCancellation.Token);
                    initialEndUnit = GetResolvedRangeEnd(
                        contextualTargets,
                        firstProgress,
                        initialPlan.UnitCount);
                    initialSegments = initialPlan.CreateSegmentsBetweenUnits(0, initialEndUnit);
                    initialSegmentsComplete = firstProgress.IsFinal;
                }

                if (initialSegments.Count > 0)
                {
                    session = new KokoroPlaybackSession(
                        engine,
                        voice,
                        initialSegments,
                        pipeline,
                        settings.Volume / 100f,
                        segmentsComplete: initialSegmentsComplete);
                    if (!TryActivateSession(
                            session,
                            initialPlan,
                            requestCancellation,
                            initialSegmentsComplete ? null : initialEndUnit))
                    {
                        return SpeechStartResult.CanceledResult;
                    }

                    if (!initialSegmentsComplete)
                    {
                        _ = AppendContextualProgressAsync(
                            contextualProgress,
                            contextualTargets,
                            initialPlan,
                            initialEndUnit,
                            voice,
                            pipeline,
                            session,
                            requestCancellation);
                    }

                    return SpeechStartResult.Success();
                }
            }

            var plan = deterministicPlan;
            requestCancellation.Token.ThrowIfCancellationRequested();
            session = new KokoroPlaybackSession(
                engine,
                voice,
                plan.Segments,
                pipeline,
                settings.Volume / 100f);
            if (!TryActivateSession(session, plan, requestCancellation))
            {
                return SpeechStartResult.CanceledResult;
            }

            return SpeechStartResult.Success();
        }
        catch (OperationCanceledException)
        {
            session?.Dispose();
            CompleteRequest(requestCancellation);
            return SpeechStartResult.CanceledResult;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or TimeoutException
                                          or InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or TypeInitializationException
                                          or DllNotFoundException
                                          or BadImageFormatException
                                          or IndexOutOfRangeException
                                          or ArgumentException)
        {
            session?.Dispose();
            ResetFailedEngineLoad();
            CompleteRequest(requestCancellation);
            return SpeechStartResult.Failure(
                "Kokoro could not initialize or synthesize the selected text.");
        }
    }

    private bool TryActivateSession(
        KokoroPlaybackSession session,
        KokoroPlaybackPlan plan,
        CancellationTokenSource requestCancellation,
        int? progressiveBoundaryUnitIndex = null)
    {
        session.StateChanged += OnSessionStateChanged;
        session.PlaybackFailed += OnSessionPlaybackFailed;
        session.NavigationUnitChanged += OnSessionNavigationUnitChanged;

        lock (_stateLock)
        {
            if (!ReferenceEquals(_requestCancellation, requestCancellation)
                || requestCancellation.IsCancellationRequested)
            {
                session.StateChanged -= OnSessionStateChanged;
                session.PlaybackFailed -= OnSessionPlaybackFailed;
                session.NavigationUnitChanged -= OnSessionNavigationUnitChanged;
                session.Dispose();
                return false;
            }

            _currentSession = session;
            _currentPlan = plan;
            _progressiveBoundaryUnitIndex = progressiveBoundaryUnitIndex;
        }

        session.Start();
        return true;
    }

    private ChannelReader<ContextualPronunciationProgress> StartContextualProgress(
        PreparedDocument document,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<ContextualPronunciationProgress>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
        _ = ProduceContextualProgressAsync(document, channel.Writer, cancellationToken);
        return channel.Reader;
    }

    private async Task ProduceContextualProgressAsync(
        PreparedDocument document,
        ChannelWriter<ContextualPronunciationProgress> writer,
        CancellationToken cancellationToken)
    {
        var latestDocument = document;
        try
        {
            await _contextualPronunciation!.ApplyProgressivelyAsync(
                document,
                (progress, token) =>
                {
                    latestDocument = progress.Document;
                    return writer.WriteAsync(progress, token);
                },
                cancellationToken);
            writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            writer.TryWrite(new ContextualPronunciationProgress(latestDocument, -1, true));
            writer.TryComplete();
        }
    }

    private async Task AppendContextualProgressAsync(
        ChannelReader<ContextualPronunciationProgress> progressReader,
        IReadOnlyList<ContextualPronunciationTarget> contextualTargets,
        KokoroPlaybackPlan deterministicPlan,
        int nextUnitIndex,
        KokoroVoice voice,
        KokoroTTSPipelineConfig pipeline,
        KokoroPlaybackSession session,
        CancellationTokenSource requestCancellation)
    {
        var fallbackPlan = deterministicPlan;
        try
        {
            await foreach (var progress in progressReader.ReadAllAsync(requestCancellation.Token))
            {
                var completedPlan = await Task.Run(
                    () => CreatePlaybackPlan(progress.Document, voice, pipeline),
                    requestCancellation.Token);
                fallbackPlan = completedPlan;
                var rangeEnd = GetResolvedRangeEnd(
                    contextualTargets,
                    progress,
                    completedPlan.UnitCount);
                if (rangeEnd <= nextUnitIndex)
                {
                    continue;
                }

                var resolvedSegments = completedPlan.CreateSegmentsBetweenUnits(
                    nextUnitIndex,
                    rangeEnd);
                lock (_playbackMutationLock)
                {
                    lock (_stateLock)
                    {
                        if (!ReferenceEquals(_requestCancellation, requestCancellation)
                            || !ReferenceEquals(_currentSession, session)
                            || requestCancellation.IsCancellationRequested)
                        {
                            return;
                        }
                    }

                    if (resolvedSegments.Count == 0
                        || !session.TryAppendSegments(
                            resolvedSegments,
                            markComplete: progress.IsFinal))
                    {
                        session.Stop();
                        return;
                    }

                    lock (_stateLock)
                    {
                        if (!ReferenceEquals(_requestCancellation, requestCancellation)
                            || !ReferenceEquals(_currentSession, session))
                        {
                            return;
                        }

                        _currentPlan = completedPlan;
                        _progressiveBoundaryUnitIndex = progress.IsFinal ? null : rangeEnd;
                    }
                }

                nextUnitIndex = rangeEnd;
                if (progress.IsFinal)
                {
                    return;
                }
            }

            AppendDeterministicProgressFallback(
                fallbackPlan,
                nextUnitIndex,
                session,
                requestCancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            AppendDeterministicProgressFallback(
                fallbackPlan,
                nextUnitIndex,
                session,
                requestCancellation);
        }
    }

    private void AppendDeterministicProgressFallback(
        KokoroPlaybackPlan deterministicPlan,
        int nextUnitIndex,
        KokoroPlaybackSession session,
        CancellationTokenSource requestCancellation)
    {
        lock (_playbackMutationLock)
        {
            var remainingSegments = deterministicPlan.CreateSegmentsFromUnit(nextUnitIndex);
            lock (_stateLock)
            {
                if (!ReferenceEquals(_requestCancellation, requestCancellation)
                    || !ReferenceEquals(_currentSession, session)
                    || requestCancellation.IsCancellationRequested)
                {
                    return;
                }
            }

            if (remainingSegments.Count == 0
                || !session.TryAppendSegments(remainingSegments, markComplete: true))
            {
                session.Stop();
                return;
            }

            lock (_stateLock)
            {
                if (!ReferenceEquals(_requestCancellation, requestCancellation)
                    || !ReferenceEquals(_currentSession, session))
                {
                    return;
                }

                _currentPlan = deterministicPlan;
                _progressiveBoundaryUnitIndex = null;
            }
        }
    }

    private static int GetResolvedRangeEnd(
        IReadOnlyList<ContextualPronunciationTarget> targets,
        ContextualPronunciationProgress progress,
        int unitCount)
    {
        if (progress.IsFinal)
        {
            return unitCount;
        }

        return targets
            .Select(target => target.UnitIndex)
            .Distinct()
            .First(unitIndex => unitIndex > progress.ResolvedUnitIndex);
    }

    internal static KokoroVoice CreateVoice(KokoroSpeechSettings settings)
    {
        var primary = KokoroVoiceStore.GetVoice(settings.VoiceName);
        if (!settings.BlendEnabled || settings.PrimaryVoiceWeight == 100)
        {
            return primary;
        }

        var secondary = KokoroVoiceStore.GetVoice(settings.SecondaryVoiceName);
        if (settings.PrimaryVoiceWeight == 0)
        {
            return secondary;
        }

        if (string.Equals(primary.Name, secondary.Name, StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        return primary.MixWith(
            secondary,
            settings.PrimaryVoiceWeight,
            settings.SecondaryVoiceWeight);
    }

    internal static KokoroTTSPipelineConfig CreatePipelineConfig(float speed) =>
        new() { Speed = speed };

    internal static IReadOnlyList<KokoroPlaybackSegment> CreatePlaybackSegments(
        string text,
        KokoroVoice voice,
        KokoroTTSPipelineConfig pipeline) =>
        CreatePlaybackPlan(text, voice, pipeline).Segments;

    internal static KokoroPlaybackPlan CreatePlaybackPlan(
        string text,
        KokoroVoice voice,
        KokoroTTSPipelineConfig pipeline)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(pipeline);

        var prepared = SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(text));
        return CreatePlaybackPlan(prepared, voice, pipeline);
    }

    internal static KokoroPlaybackPlan CreatePlaybackPlan(
        PreparedDocument prepared,
        KokoroVoice voice,
        KokoroTTSPipelineConfig pipeline)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(pipeline);

        var segments = new List<KokoroPlaybackSegment>();
        var navigationUnitIndex = 0;
        foreach (var chunk in prepared.BuildNavigableSpeechChunks())
        {
            var chunkText = RemoveUnsupportedTokenizerSymbols(chunk.Text).Trim();
            if (chunkText.Length == 0)
            {
                continue;
            }

            var tokens = Tokenizer.Tokenize(
                chunkText,
                voice.GetLangCode(),
                pipeline.PreprocessText);
            var chunkSegments = pipeline.SegmentationFunc(tokens);
            var chunkCues = new List<(int UnitIndex, int TokenOffset)>();
            foreach (var unit in chunk.Units)
            {
                var prefix = RemoveUnsupportedTokenizerSymbols(
                    chunk.Text[..Math.Clamp(unit.SpokenStart, 0, chunk.Text.Length)]).Trim();
                var prefixTokens = prefix.Length == 0
                    ? Array.Empty<int>()
                    : Tokenizer.Tokenize(
                        prefix,
                        voice.GetLangCode(),
                        pipeline.PreprocessText);
                var tokenOffset = navigationUnitIndex == 0
                    ? 0
                    : CommonPrefixLength(prefixTokens, tokens);
                if (tokenOffset < tokens.Length)
                {
                    chunkCues.Add((navigationUnitIndex, tokenOffset));
                    navigationUnitIndex++;
                }
            }

            var chunkTokenOffset = 0;
            for (var index = 0; index < chunkSegments.Count; index++)
            {
                var pauseAfterSeconds = index == chunkSegments.Count - 1
                    ? chunk.PauseAfterMilliseconds / 1000f
                    : 0f;
                var segmentTokens = chunkSegments[index];
                var segmentEnd = chunkTokenOffset + segmentTokens.Length;
                var cues = chunkCues
                    .Where(cue => cue.TokenOffset >= chunkTokenOffset
                                  && (cue.TokenOffset < segmentEnd
                                      || index == chunkSegments.Count - 1))
                    .Select(cue => new KokoroNavigationCue(
                        cue.UnitIndex,
                        Math.Clamp(cue.TokenOffset - chunkTokenOffset, 0, segmentTokens.Length)))
                    .ToArray();
                segments.Add(
                    new KokoroPlaybackSegment(
                        segmentTokens,
                        pauseAfterSeconds,
                        cues));
                chunkTokenOffset = segmentEnd;
            }
        }

        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Kokoro preparation produced no speakable text.");
        }

        return new KokoroPlaybackPlan(segments, navigationUnitIndex);
    }

    private static int CommonPrefixLength(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        var length = Math.Min(left.Count, right.Count);
        var index = 0;
        while (index < length && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    internal static string RemoveUnsupportedTokenizerSymbols(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sanitized = new StringBuilder(text.Length);
        var removedSymbol = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value > char.MaxValue
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol
                || rune.Value is 0x200D or 0x20E3 or 0xFE0F)
            {
                removedSymbol = true;
                continue;
            }

            if (removedSymbol
                && sanitized.Length > 0
                && char.IsLetterOrDigit(sanitized[^1])
                && Rune.IsLetterOrDigit(rune))
            {
                sanitized.Append(' ');
            }

            removedSymbol = false;
            sanitized.Append(rune);
        }

        return sanitized.ToString();
    }

    private KokoroVoice ResolveVoice(KokoroSpeechSettings settings)
    {
        var selection = VoiceSelection.From(settings);
        lock (_stateLock)
        {
            if (_cachedVoiceSelection == selection && _cachedVoice is not null)
            {
                return _cachedVoice;
            }
        }

        var voice = CreateVoice(settings);
        lock (_stateLock)
        {
            if (!_disposed && VoiceSelection.From(_settings) == selection)
            {
                _cachedVoiceSelection = selection;
                _cachedVoice = voice;
            }
        }

        return voice;
    }

    public void Stop()
    {
        CancellationTokenSource? requestCancellation;
        KokoroPlaybackSession? session;
        var stateChanged = false;

        lock (_stateLock)
        {
            requestCancellation = _requestCancellation;
            _requestCancellation = null;
            session = _currentSession;
            _currentSession = null;
            ClearPlaybackPlanLocked();

            if (_state != SpeechPlaybackState.Idle)
            {
                _state = SpeechPlaybackState.Idle;
                stateChanged = true;
            }
        }

        requestCancellation?.Cancel();
        requestCancellation?.Dispose();

        if (session is not null)
        {
            session.StateChanged -= OnSessionStateChanged;
            session.PlaybackFailed -= OnSessionPlaybackFailed;
            session.NavigationUnitChanged -= OnSessionNavigationUnitChanged;
            session.Dispose();
        }

        if (stateChanged)
        {
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsSpeaking => State != SpeechPlaybackState.Idle;

    public bool TryPause()
    {
        KokoroPlaybackSession? session;
        lock (_stateLock)
        {
            session = _currentSession;
        }

        return session?.TryPause() == true;
    }

    public bool TryResume()
    {
        KokoroPlaybackSession? session;
        lock (_stateLock)
        {
            session = _currentSession;
        }

        return session?.TryResume() == true;
    }

    public PlaybackNavigationState NavigationState
    {
        get
        {
            lock (_stateLock)
            {
                if (_currentPlan is null || _currentSession is null || _state == SpeechPlaybackState.Idle)
                {
                    return PlaybackNavigationState.Unavailable;
                }

                return new PlaybackNavigationState(
                    (_progressiveBoundaryUnitIndex ?? _currentPlan.UnitCount) > 0,
                    _currentSession.CurrentNavigationUnitIndex,
                    _progressiveBoundaryUnitIndex ?? _currentPlan.UnitCount);
            }
        }
    }

    public bool TryRestartCurrentUnit()
    {
        var timestamp = Stopwatch.GetTimestamp();
        int targetUnitIndex;

        lock (_stateLock)
        {
            if (_currentPlan is null || _currentSession is null || _state == SpeechPlaybackState.Idle)
            {
                return false;
            }

            var withinRapidBackWindow = _lastBackTargetUnitIndex >= 0
                && Stopwatch.GetElapsedTime(_lastBackTimestamp, timestamp).TotalMilliseconds
                    <= RapidBackWindowMilliseconds;
            targetUnitIndex = withinRapidBackWindow
                ? Math.Max(0, _lastBackTargetUnitIndex - 1)
                : _currentSession.CurrentNavigationUnitIndex;
        }

        return TryRestartAtUnit(targetUnitIndex, timestamp);
    }

    public bool TryRestartFromBeginning()
    {
        lock (_stateLock)
        {
            ResetBackSequenceLocked();
        }

        return TryRestartAtUnit(0);
    }

    public bool TryMoveToNextUnit()
    {
        var state = NavigationState;
        lock (_stateLock)
        {
            ResetBackSequenceLocked();
        }

        return state.CanMoveNext && TryRestartAtUnit(state.CurrentUnitIndex + 1);
    }

    private bool TryRestartAtUnit(int unitIndex, long? backTimestamp = null)
    {
        lock (_playbackMutationLock)
        {
            KokoroPlaybackSession session;
            IReadOnlyList<KokoroPlaybackSegment> segments;
            var segmentsComplete = true;

            lock (_stateLock)
            {
                if (_disposed
                    || _currentPlan is null
                    || _currentSession is null
                    || _engine is null
                    || _state == SpeechPlaybackState.Idle)
                {
                    return false;
                }

                if (_progressiveBoundaryUnitIndex is int progressiveBoundary)
                {
                    if (unitIndex >= progressiveBoundary)
                    {
                        return false;
                    }

                    var prefixSegments = _currentPlan.CreateSegmentsBeforeUnit(progressiveBoundary);
                    var prefixPlan = new KokoroPlaybackPlan(prefixSegments, progressiveBoundary);
                    segments = prefixPlan.CreateSegmentsFromUnit(unitIndex);
                    segmentsComplete = false;
                }
                else
                {
                    segments = _currentPlan.CreateSegmentsFromUnit(unitIndex);
                }

                if (segments.Count == 0)
                {
                    return false;
                }

                session = _currentSession;
            }

            if (!session.TryRestart(
                    segments,
                    unitIndex,
                    pauseOnStart: false,
                    segmentsComplete))
            {
                return false;
            }

            lock (_stateLock)
            {
                if (!ReferenceEquals(_currentSession, session))
                {
                    return false;
                }

                if (backTimestamp.HasValue)
                {
                    _lastBackTargetUnitIndex = unitIndex;
                    _lastBackTimestamp = backTimestamp.Value;
                }
            }

            return true;
        }
    }

    private async Task<KokoroTTS> GetEngineAsync(CancellationToken cancellationToken)
    {
        Task<KokoroTTS> loadTask;

        lock (_stateLock)
        {
            if (_engine is not null)
            {
                return _engine;
            }

            _engineLoadTask ??= LoadEngineAsync(cancellationToken);
            loadTask = _engineLoadTask;
        }

        return await loadTask;
    }

    private async Task<KokoroTTS> LoadEngineAsync(CancellationToken cancellationToken)
    {
        var modelPath = await _modelStore.GetModelPathAsync(cancellationToken);
        var engine = await Task.Run(() => new KokoroTTS(modelPath), cancellationToken);

        lock (_stateLock)
        {
            if (_disposed)
            {
                engine.Dispose();
                throw new ObjectDisposedException(nameof(KokoroSpeechService));
            }

            _engine = engine;
        }

        return engine;
    }

    private void OnSessionStateChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is not KokoroPlaybackSession session)
        {
            return;
        }

        CancellationTokenSource? requestCancellation = null;
        var completed = false;

        lock (_stateLock)
        {
            if (!ReferenceEquals(_currentSession, session))
            {
                return;
            }

            _state = session.State;
            if (_state == SpeechPlaybackState.Idle)
            {
                completed = true;
                _currentSession = null;
                ClearPlaybackPlanLocked();
                requestCancellation = _requestCancellation;
                _requestCancellation = null;
            }
        }

        requestCancellation?.Dispose();
        if (completed)
        {
            session.StateChanged -= OnSessionStateChanged;
            session.PlaybackFailed -= OnSessionPlaybackFailed;
            session.NavigationUnitChanged -= OnSessionNavigationUnitChanged;
            session.Dispose();
        }

        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionPlaybackFailed(object? sender, EventArgs eventArgs)
    {
        if (sender is not KokoroPlaybackSession session)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!ReferenceEquals(_currentSession, session))
            {
                return;
            }
        }

        session.Stop();
    }

    private void OnSessionNavigationUnitChanged(object? sender, EventArgs eventArgs)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(_currentSession, sender))
            {
                return;
            }
        }

        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteRequest(CancellationTokenSource requestCancellation)
    {
        var stateChanged = false;

        lock (_stateLock)
        {
            if (!ReferenceEquals(_requestCancellation, requestCancellation))
            {
                return;
            }

            _requestCancellation = null;
            _currentSession = null;
            ClearPlaybackPlanLocked();

            if (_state != SpeechPlaybackState.Idle)
            {
                _state = SpeechPlaybackState.Idle;
                stateChanged = true;
            }
        }

        requestCancellation.Cancel();
        requestCancellation.Dispose();

        if (stateChanged)
        {
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetFailedEngineLoad()
    {
        lock (_stateLock)
        {
            if (_engine is null && _engineLoadTask is { IsCompleted: true })
            {
                _engineLoadTask = null;
            }
        }
    }

    private void ClearPlaybackPlanLocked()
    {
        _currentPlan = null;
        _progressiveBoundaryUnitIndex = null;
        ResetBackSequenceLocked();
    }

    private void ResetBackSequenceLocked()
    {
        _lastBackTargetUnitIndex = -1;
        _lastBackTimestamp = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _contextualPronunciation?.Dispose();

        // KokoroSharp cannot interrupt an ONNX inference step. Its worker thread is
        // background-only, so process teardown safely releases the CPU model without
        // disposing the ONNX session underneath a step that may still be finishing.
    }

    private readonly record struct VoiceSelection(
        string PrimaryVoiceName,
        bool BlendEnabled,
        string SecondaryVoiceName,
        int PrimaryVoiceWeight)
    {
        public static VoiceSelection From(KokoroSpeechSettings settings) =>
            new(
                settings.VoiceName,
                settings.BlendEnabled,
                settings.SecondaryVoiceName,
                settings.PrimaryVoiceWeight);
    }
}
