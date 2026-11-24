using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;

namespace PulseReader.Speech;

internal sealed class KokoroPlaybackSession : IDisposable
{
    private readonly Lock _stateLock = new();
    private readonly Action<KokoroJob> _enqueueJob;
    private readonly KokoroVoice _voice;
    private readonly List<KokoroPlaybackSegment> _segments;
    private readonly KokoroTTSPipelineConfig _pipeline;
    private readonly PausableKokoroPlayback _playback;
    private bool _pauseOnStart;
    private readonly HashSet<KokoroJob> _pendingJobs = [];
    private SpeechPlaybackState _state = SpeechPlaybackState.Preparing;
    private int _nextSegmentIndex;
    private int _generation;
    private int _currentNavigationUnitIndex;
    private bool _segmentsComplete;
    private bool _disposed;

    public KokoroPlaybackSession(
        KokoroTTS engine,
        KokoroVoice voice,
        IReadOnlyList<KokoroPlaybackSegment> segments,
        KokoroTTSPipelineConfig pipeline,
        float volume,
        int startingNavigationUnitIndex = 0,
        bool pauseOnStart = false,
        bool segmentsComplete = true)
        : this(engine is null ? throw new ArgumentNullException(nameof(engine)) : new Action<KokoroJob>(job => engine.EnqueueJob(job)),
            voice, segments, pipeline, new PausableKokoroPlayback(volume),
            startingNavigationUnitIndex, pauseOnStart, segmentsComplete)
    {
    }

    internal KokoroPlaybackSession(
        Action<KokoroJob> enqueueJob, KokoroVoice voice,
        IReadOnlyList<KokoroPlaybackSegment> segments, KokoroTTSPipelineConfig pipeline,
        PausableKokoroPlayback playback, int startingNavigationUnitIndex = 0,
        bool pauseOnStart = false, bool segmentsComplete = true)
    {
        ArgumentNullException.ThrowIfNull(enqueueJob);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(pipeline);
        if (segments.Count == 0)
        {
            throw new ArgumentException("Kokoro playback requires at least one segment.", nameof(segments));
        }

        _enqueueJob = enqueueJob;
        _voice = voice;
        _segments = [.. segments];
        _pipeline = pipeline;
        _currentNavigationUnitIndex = startingNavigationUnitIndex;
        _pauseOnStart = pauseOnStart;
        _segmentsComplete = segmentsComplete;
        _playback = playback;
        _playback.SegmentStarted += OnSegmentStarted;
        _playback.SegmentCompleted += OnSegmentCompleted;
        _playback.NavigationUnitStarted += OnNavigationUnitStarted;
        _playback.PlaybackFailed += OnPlaybackFailed;
    }

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

    public event EventHandler? StateChanged;

    public event EventHandler? PlaybackFailed;

    public event EventHandler? NavigationUnitChanged;

    public int CurrentNavigationUnitIndex
    {
        get
        {
            lock (_stateLock)
            {
                return _currentNavigationUnitIndex;
            }
        }
    }

    public void Start()
    {
        _playback.Start();
        QueueNextSegment();
    }

    public bool TryPause()
    {
        lock (_stateLock)
        {
            if (_disposed || _state != SpeechPlaybackState.Speaking || !_playback.TryPause())
            {
                return false;
            }

            _state = SpeechPlaybackState.Paused;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryResume()
    {
        lock (_stateLock)
        {
            if (_disposed || _state != SpeechPlaybackState.Paused || !_playback.TryResume())
            {
                return false;
            }

            _state = SpeechPlaybackState.Speaking;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRestart(
        IReadOnlyList<KokoroPlaybackSegment> segments,
        int startingNavigationUnitIndex,
        bool pauseOnStart,
        bool segmentsComplete = true)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return false;
        }

        KokoroJob[] pendingJobs;
        int generation;
        lock (_stateLock)
        {
            if (_disposed || _state == SpeechPlaybackState.Idle)
            {
                return false;
            }

            generation = _generation + 1;
            if (!_playback.TryInterruptForNavigation(generation)) return false;
            _generation = generation;
            pendingJobs = [.. _pendingJobs];
            _pendingJobs.Clear();
            _segments.Clear();
            _segments.AddRange(segments);
            _segmentsComplete = segmentsComplete;
            _nextSegmentIndex = 0;
            _currentNavigationUnitIndex = startingNavigationUnitIndex;
            _pauseOnStart = pauseOnStart;
            _state = SpeechPlaybackState.Preparing;
        }

        foreach (var job in pendingJobs)
        {
            job.Cancel();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        NavigationUnitChanged?.Invoke(this, EventArgs.Empty);
        QueueNextSegment();
        return true;
    }

    public bool TryAppendSegments(
        IReadOnlyList<KokoroPlaybackSegment> segments,
        bool markComplete)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var shouldQueue = false;
        lock (_stateLock)
        {
            if (_disposed || _state == SpeechPlaybackState.Idle || _segmentsComplete)
            {
                return false;
            }

            _segments.AddRange(segments);
            _segmentsComplete = markComplete;
            shouldQueue = _pendingJobs.Count == 0 && _nextSegmentIndex < _segments.Count;
        }

        if (shouldQueue)
        {
            QueueNextSegment();
        }

        return true;
    }

    public void Stop() => StopCore();

    private void StopCore()
    {
        KokoroJob[] pendingJobs;
        var stateChanged = false;

        lock (_stateLock)
        {
            if (_state == SpeechPlaybackState.Idle)
            {
                return;
            }

            _state = SpeechPlaybackState.Idle;
            stateChanged = true;
            pendingJobs = [.. _pendingJobs];
            _pendingJobs.Clear();
        }

        foreach (var job in pendingJobs)
        {
            job.Cancel();
        }

        _playback.Stop();
        if (stateChanged)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void QueueNextSegment()
    {
        int index;
        int generation;
        KokoroPlaybackSegment segment;

        lock (_stateLock)
        {
            if (_disposed
                || _state == SpeechPlaybackState.Idle
                || _nextSegmentIndex >= _segments.Count)
            {
                return;
            }

            index = _nextSegmentIndex++;
            generation = _generation;
            segment = _segments[index];
        }

        KokoroJob? job = null;
        job = KokoroJob.Create(
            segment.Tokens,
            _voice,
            _pipeline.Speed,
            samples => OnInferenceCompleted(job!, generation, index, samples));

        lock (_stateLock)
        {
            if (_disposed || _state == SpeechPlaybackState.Idle || generation != _generation)
            {
                job.Cancel();
                return;
            }

            _pendingJobs.Add(job);
        }

        _enqueueJob(job);
    }

    private void OnInferenceCompleted(
        KokoroJob job,
        int generation,
        int index,
        float[] samples)
    {
        KokoroPlaybackSegment segment;
        bool isLast;
        lock (_stateLock)
        {
            _pendingJobs.Remove(job);
            if (_disposed
                || _state == SpeechPlaybackState.Idle
                || generation != _generation
                || index >= _segments.Count)
            {
                return;
            }

            segment = _segments[index];
            isLast = _segmentsComplete && index == _segments.Count - 1;
        }

        _playback.Enqueue(
            generation,
            index,
            samples,
            segment.Tokens.Length,
            segment.Cues,
            TrailingPauseSeconds(segment, isLast),
            isLast);
    }

    private void OnSegmentStarted(int generation, int index)
    {
        var stateChanged = false;

        lock (_stateLock)
        {
            if (_disposed || _state == SpeechPlaybackState.Idle || generation != _generation)
            {
                return;
            }

            if (_state == SpeechPlaybackState.Preparing)
            {
                if (_pauseOnStart && _playback.TryPause())
                {
                    _state = SpeechPlaybackState.Paused;
                }
                else
                {
                    _state = SpeechPlaybackState.Speaking;
                }
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        QueueNextSegment();
    }

    private void OnNavigationUnitStarted(int generation, int unitIndex)
    {
        lock (_stateLock)
        {
            if (_disposed
                || _state == SpeechPlaybackState.Idle
                || generation != _generation
                || _currentNavigationUnitIndex == unitIndex)
            {
                return;
            }

            _currentNavigationUnitIndex = unitIndex;
        }

        NavigationUnitChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSegmentCompleted(int generation, int index, bool isLast)
    {
        if (!isLast)
        {
            return;
        }

        var stateChanged = false;
        lock (_stateLock)
        {
            if (!_disposed && _state != SpeechPlaybackState.Idle && generation == _generation)
            {
                _state = SpeechPlaybackState.Idle;
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPlaybackFailed()
    {
        Stop();
        PlaybackFailed?.Invoke(this, EventArgs.Empty);
    }

    private float TrailingPauseSeconds(KokoroPlaybackSegment segment, bool isLast)
    {
        var naturalPause = 0f;
        if (!isLast
            && segment.Tokens.Length > 0
            && Tokenizer.PunctuationTokens.Contains(segment.Tokens[^1]))
        {
            naturalPause =
                _pipeline.SecondsOfPauseBetweenProperSegments[Tokenizer.TokenToChar[segment.Tokens[^1]]];
        }

        return Math.Max(naturalPause, segment.MinimumTrailingPauseSeconds);
    }

    public void Dispose()
    {
        DisposeCore();
    }

    private void DisposeCore()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        _playback.SegmentStarted -= OnSegmentStarted;
        _playback.SegmentCompleted -= OnSegmentCompleted;
        _playback.NavigationUnitStarted -= OnNavigationUnitStarted;
        _playback.PlaybackFailed -= OnPlaybackFailed;
        _playback.Dispose();
    }
}

internal sealed record KokoroPlaybackSegment(
    int[] Tokens,
    float MinimumTrailingPauseSeconds,
    IReadOnlyList<KokoroNavigationCue>? NavigationCues = null)
{
    public IReadOnlyList<KokoroNavigationCue> Cues =>
        NavigationCues ?? Array.Empty<KokoroNavigationCue>();
}

internal readonly record struct KokoroNavigationCue(int UnitIndex, int TokenOffset);

internal sealed record KokoroPlaybackPlan(
    IReadOnlyList<KokoroPlaybackSegment> Segments,
    int UnitCount)
{
    public IReadOnlyList<KokoroPlaybackSegment> CreateSegmentsBetweenUnits(
        int startUnitIndex,
        int endUnitIndex)
    {
        if (startUnitIndex < 0
            || endUnitIndex <= startUnitIndex
            || endUnitIndex > UnitCount)
        {
            return Array.Empty<KokoroPlaybackSegment>();
        }

        var prefix = CreateSegmentsBeforeUnit(endUnitIndex);
        return new KokoroPlaybackPlan(prefix, endUnitIndex)
            .CreateSegmentsFromUnit(startUnitIndex);
    }

    public IReadOnlyList<KokoroPlaybackSegment> CreateSegmentsBeforeUnit(int unitIndex)
    {
        if (unitIndex <= 0 || unitIndex >= UnitCount)
        {
            return unitIndex >= UnitCount ? Segments : Array.Empty<KokoroPlaybackSegment>();
        }

        var result = new List<KokoroPlaybackSegment>();
        foreach (var segment in Segments)
        {
            var cue = segment.Cues
                .Where(candidate => candidate.UnitIndex == unitIndex)
                .Select(candidate => (KokoroNavigationCue?)candidate)
                .FirstOrDefault();
            if (cue is null)
            {
                result.Add(segment);
                continue;
            }

            var tokenEnd = cue.Value.TokenOffset;
            var spaceToken = Tokenizer.Vocab[' '];
            while (tokenEnd > 0
                   && (segment.Tokens[tokenEnd - 1] == spaceToken
                       || segment.Tokens[tokenEnd - 1] < 0))
            {
                tokenEnd--;
            }

            if (tokenEnd > 0)
            {
                var prefixCues = segment.Cues
                    .Where(candidate => candidate.UnitIndex < unitIndex
                                        && candidate.TokenOffset < tokenEnd)
                    .ToArray();
                result.Add(
                    segment with
                    {
                        Tokens = segment.Tokens[..tokenEnd],
                        NavigationCues = prefixCues
                    });
            }

            return result;
        }

        return Array.Empty<KokoroPlaybackSegment>();
    }

    public IReadOnlyList<KokoroPlaybackSegment> CreateSegmentsFromUnit(int unitIndex)
    {
        if (unitIndex < 0 || unitIndex >= UnitCount)
        {
            return Array.Empty<KokoroPlaybackSegment>();
        }

        for (var segmentIndex = 0; segmentIndex < Segments.Count; segmentIndex++)
        {
            var segment = Segments[segmentIndex];
            var cue = segment.Cues
                .Where(candidate => candidate.UnitIndex == unitIndex)
                .Select(candidate => (KokoroNavigationCue?)candidate)
                .FirstOrDefault();
            if (cue is null)
            {
                continue;
            }

            var result = new List<KokoroPlaybackSegment>(Segments.Count - segmentIndex);
            var restartTokenOffset = cue.Value.TokenOffset;
            var spaceToken = Tokenizer.Vocab[' '];
            while (restartTokenOffset < segment.Tokens.Length
                   && (segment.Tokens[restartTokenOffset] == spaceToken
                       || segment.Tokens[restartTokenOffset] < 0))
            {
                restartTokenOffset++;
            }

            var firstTokens = segment.Tokens[restartTokenOffset..];
            if (firstTokens.Length > 0)
            {
                var firstCues = segment.Cues
                    .Where(candidate => candidate.UnitIndex >= unitIndex)
                    .Select(candidate => candidate with
                    {
                        TokenOffset = Math.Max(0, candidate.TokenOffset - restartTokenOffset)
                    })
                    .ToArray();
                result.Add(segment with { Tokens = firstTokens, NavigationCues = firstCues });
            }

            for (var index = segmentIndex + 1; index < Segments.Count; index++)
            {
                result.Add(Segments[index]);
            }

            if (result.Count > 0)
            {
                var first = result[0];
                var firstCues = first.Cues
                    .Where(candidate => candidate.UnitIndex != unitIndex)
                    .Prepend(new KokoroNavigationCue(unitIndex, 0))
                    .ToArray();
                result[0] = first with { NavigationCues = firstCues };
            }

            return result;
        }

        return Array.Empty<KokoroPlaybackSegment>();
    }
}
