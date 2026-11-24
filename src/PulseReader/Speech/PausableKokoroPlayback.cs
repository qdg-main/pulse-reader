using System.Collections.Concurrent;
using System.Diagnostics;
using KokoroSharp;
using NAudio.Wave;

namespace PulseReader.Speech;

internal sealed class PausableKokoroPlayback : IDisposable
{
    private readonly Lock _stateLock = new();
    private readonly ConcurrentQueue<AudioSegment> _segments = new();
    private readonly AutoResetEvent _segmentAvailable = new(false);
    private readonly ManualResetEventSlim _resumeSignal = new(true);
    private readonly Thread _playbackThread;
    private WaveOutEvent? _waveOut;
    private int _currentGeneration;
    private bool _paused;
    private bool _started;
    private bool _stopping;
    private bool _playbackFinished;
    private bool _disposed;

    public PausableKokoroPlayback(float volume)
    {
        Volume = Math.Clamp(volume, 0f, 1f);
        _playbackThread = new Thread(PlaybackLoop)
        {
            IsBackground = true,
            Name = "Pulse Reader Kokoro playback"
        };
    }

    public float Volume { get; }

    public event Action<int, int>? SegmentStarted;

    public event Action<int, int, bool>? SegmentCompleted;

    public event Action<int, int>? NavigationUnitStarted;

    public event Action? PlaybackFailed;

    public void Start()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _playbackThread.Start();
        }
    }

    public void Enqueue(
        int generation,
        int index,
        float[] samples,
        int tokenCount,
        IReadOnlyList<KokoroNavigationCue> navigationCues,
        float trailingPauseSeconds,
        bool isLast)
    {
        ArgumentNullException.ThrowIfNull(samples);

        lock (_stateLock)
        {
            if (_disposed || !_started || _stopping || _playbackFinished || generation != _currentGeneration)
            {
                return;
            }

            _segments.Enqueue(
                new AudioSegment(
                    generation,
                    index,
                    samples,
                    tokenCount,
                    navigationCues,
                    trailingPauseSeconds,
                    isLast));
            _segmentAvailable.Set();
        }
    }

    public bool TryPause()
    {
        lock (_stateLock)
        {
            if (_disposed || _stopping || _playbackFinished || _paused)
            {
                return false;
            }

            _paused = true;
            _resumeSignal.Reset();
            if (_waveOut?.PlaybackState == PlaybackState.Playing)
            {
                _waveOut.Pause();
            }

            return true;
        }
    }

    public bool TryResume()
    {
        lock (_stateLock)
        {
            if (_disposed || _stopping || _playbackFinished || !_paused)
            {
                return false;
            }

            _paused = false;
            _resumeSignal.Set();
            if (_waveOut?.PlaybackState == PlaybackState.Paused)
            {
                _waveOut.Play();
            }

            return true;
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _paused = false;
            if (!_playbackFinished)
            {
                _resumeSignal.Set();
                _waveOut?.Stop();
                _segmentAvailable.Set();
            }
            while (_segments.TryDequeue(out _))
            {
            }
        }
    }

    public bool TryInterruptForNavigation(int generation)
    {
        lock (_stateLock)
        {
            if (_disposed || !_started || _stopping || generation <= _currentGeneration)
            {
                return false;
            }

            if (_playbackFinished)
            {
                return false;
            }

            _currentGeneration = generation;
            _paused = false;
            _resumeSignal.Set();
            _waveOut?.Stop();
            while (_segments.TryDequeue(out _))
            {
            }

            _segmentAvailable.Set();
            return true;
        }
    }

    private void PlaybackLoop()
    {
        AudioSegment? completedLastSegment = null;
        try
        {
            using (var waveOut = new WaveOutEvent())
            {
                waveOut.Volume = Volume;
                lock (_stateLock)
                {
                    _waveOut = waveOut;
                }

                try
                {
                    while (WaitForNextSegment(out var segment))
                    {
                        var completed = PlaySegment(waveOut, segment);
                        if (IsStopping()) break;

                        if (completed && segment.IsLast)
                        {
                            // Completion and navigation compete under the same
                            // lock. An accepted newer generation keeps this
                            // playback thread alive; otherwise further restarts
                            // are rejected before the session changes state.
                            lock (_stateLock)
                            {
                                if (_stopping) break;
                                if (segment.Generation != _currentGeneration) continue;
                                _playbackFinished = true;
                                _waveOut = null;
                                completedLastSegment = segment;
                            }
                            break;
                        }
                    }
                }
                finally
                {
                    // Detach the output before Dispose, so pause/stop/restart
                    // cannot touch an output being disposed by this thread.
                    lock (_stateLock)
                    {
                        _waveOut = null;
                        _playbackFinished = true;
                    }
                }
            }

            if (completedLastSegment is { } lastSegment)
            {
                SegmentCompleted?.Invoke(
                    lastSegment.Generation,
                    lastSegment.Index,
                    true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ArgumentException
                                          or NAudio.MmException
                                          or ObjectDisposedException)
        {
            if (!IsStopping())
            {
                PlaybackFailed?.Invoke();
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _waveOut = null;
                _playbackFinished = true;
            }

            _segmentAvailable.Dispose();
            _resumeSignal.Dispose();
        }
    }

    private bool WaitForNextSegment(out AudioSegment segment)
    {
        while (true)
        {
            if (IsStopping())
            {
                segment = default;
                return false;
            }

            if (_segments.TryDequeue(out segment))
            {
                return true;
            }

            _segmentAvailable.WaitOne(50);
        }
    }

    private bool PlaySegment(WaveOutEvent waveOut, AudioSegment segment)
    {
        var samples = KokoroPlayback.PostProcessSamples(segment.Samples);
        if (segment.Generation > 0 && segment.Index == 0)
        {
            ApplyFadeIn(samples, 20);
        }
        if (segment.TrailingPauseSeconds > 0)
        {
            var sampleCount = (int)Math.Round(
                segment.TrailingPauseSeconds * KokoroPlayback.waveFormat.SampleRate);
            Array.Resize(ref samples, samples.Length + sampleCount);
        }

        var bytes = KokoroPlayback.GetBytes(samples);
        using var stream = new RawSourceWaveStream(bytes, 0, bytes.Length, KokoroPlayback.waveFormat);
        waveOut.Init(stream);
        if (!IsCurrentGeneration(segment.Generation))
        {
            return false;
        }

        SegmentStarted?.Invoke(segment.Generation, segment.Index);

        WaitUntilResumed(segment.Generation);
        if (IsStopping() || !IsCurrentGeneration(segment.Generation))
        {
            return false;
        }

        var audibleDurationSeconds = (double)samples.Length / KokoroPlayback.waveFormat.SampleRate;
        var cuePoints = segment.NavigationCues
            .Select(cue => new CuePlaybackPoint(
                cue.UnitIndex,
                audibleDurationSeconds * cue.TokenOffset / Math.Max(1, segment.TokenCount)))
            .OrderBy(cue => cue.Second)
            .ToArray();
        var nextCueIndex = 0;
        EmitReachedCues(segment.Generation, cuePoints, ref nextCueIndex, 0);

        waveOut.Play();
        var elapsed = Stopwatch.StartNew();
        while (!IsStopping() && IsCurrentGeneration(segment.Generation))
        {
            if (IsPaused())
            {
                elapsed.Stop();
                WaitUntilResumed(segment.Generation);
                elapsed.Start();
                continue;
            }

            EmitReachedCues(
                segment.Generation,
                cuePoints,
                ref nextCueIndex,
                elapsed.Elapsed.TotalSeconds);

            if (waveOut.PlaybackState == PlaybackState.Stopped)
            {
                break;
            }

            Thread.Sleep(10);
        }

        if (IsStopping() || !IsCurrentGeneration(segment.Generation))
        {
            waveOut.Stop();
            return false;
        }

        if (!segment.IsLast)
        {
            SegmentCompleted?.Invoke(segment.Generation, segment.Index, false);
        }

        return true;
    }

    private void EmitReachedCues(
        int generation,
        IReadOnlyList<CuePlaybackPoint> cuePoints,
        ref int nextCueIndex,
        double elapsedSeconds)
    {
        while (nextCueIndex < cuePoints.Count
               && cuePoints[nextCueIndex].Second <= elapsedSeconds)
        {
            NavigationUnitStarted?.Invoke(generation, cuePoints[nextCueIndex].UnitIndex);
            nextCueIndex++;
        }
    }

    internal static void ApplyFadeIn(float[] samples, int milliseconds)
    {
        var fadeSampleCount = Math.Min(
            samples.Length,
            KokoroPlayback.waveFormat.SampleRate * Math.Max(0, milliseconds) / 1000);
        for (var index = 0; index < fadeSampleCount; index++)
        {
            samples[index] *= index / (float)Math.Max(1, fadeSampleCount - 1);
        }
    }

    private void WaitUntilResumed(int generation)
    {
        while (IsPaused() && !IsStopping() && IsCurrentGeneration(generation))
        {
            _resumeSignal.Wait(50);
        }
    }

    private bool IsPaused()
    {
        lock (_stateLock)
        {
            return _paused;
        }
    }

    private bool IsStopping()
    {
        lock (_stateLock)
        {
            return _stopping;
        }
    }

    private bool IsCurrentGeneration(int generation)
    {
        lock (_stateLock)
        {
            return generation == _currentGeneration;
        }
    }

    public void Dispose()
    {
        var started = false;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            started = _started;
        }

        Stop();
        if (!started)
        {
            _segmentAvailable.Dispose();
            _resumeSignal.Dispose();
        }
    }

    private readonly record struct AudioSegment(
        int Generation,
        int Index,
        float[] Samples,
        int TokenCount,
        IReadOnlyList<KokoroNavigationCue> NavigationCues,
        float TrailingPauseSeconds,
        bool IsLast);

    private readonly record struct CuePlaybackPoint(int UnitIndex, double Second);
}
