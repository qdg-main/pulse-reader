using System.Reflection;
using KokoroSharp;
using KokoroSharp.Processing;
using PulseReader.Speech;

internal static class KokoroPlaybackLifecycleChecks
{
    public static async Task RunAsync()
    {
        await CheckNavigationAtCompletionAsync();
        await CheckNavigationBeforeCompletionAsync();
        using var playback = new PausableKokoroPlayback(0f);
        var waveOutField = typeof(PausableKokoroPlayback).GetField(
            "_waveOut",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The Kokoro playback output field could not be inspected.");
        var lastSegmentCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        playback.SegmentCompleted += (_, _, isLast) =>
        {
            if (isLast)
            {
                lastSegmentCompleted.TrySetResult(waveOutField.GetValue(playback) is null);
            }
        };

        playback.Start();
        playback.Enqueue(
            generation: 0,
            index: 0,
            samples: new float[2400],
            tokenCount: 1,
            navigationCues: Array.Empty<KokoroNavigationCue>(),
            trailingPauseSeconds: 0,
            isLast: true);

        var outputWasReleased = await lastSegmentCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        if (!outputWasReleased)
        {
            throw new InvalidOperationException(
                "Kokoro reported idle before releasing the completed audio output.");
        }

        Console.WriteLine(
            "PASS: Kokoro releases completed output, rejects late navigation without changing session state, and accepts active restarts.");
    }

    private static async Task CheckNavigationAtCompletionAsync()
    {
        using var playback = new PausableKokoroPlayback(0f);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Register before the session so it still reports Speaking while the
        // audio thread has finished and its final notification is in transit.
        playback.SegmentCompleted += (_, _, _) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var queued = 0;
        var segments = new[] { new KokoroPlaybackSegment([1], 0) };
        using var session = new KokoroPlaybackSession(_ => queued++,
            KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice), segments,
            new KokoroTTSPipelineConfig(), playback);
        playback.SegmentCompleted += (_, _, _) => delivered.TrySetResult();
        session.Start();
        playback.Enqueue(0, 0, new float[2400], 1, [], 0, true);
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Completion boundary was not reached.");
            var before = queued;
            var accepted = session.TryRestart(segments, 0, false);
            if (accepted || queued != before || session.State != SpeechPlaybackState.Speaking)
                throw new InvalidOperationException("A finished player accepted navigation and changed session state before completion was delivered.");
        }
        finally { release.Set(); }
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (session.State != SpeechPlaybackState.Idle)
            throw new InvalidOperationException("Rejected late navigation prevented the original completion from setting idle.");
        if (playback.TryPause() || playback.TryResume() || playback.TryInterruptForNavigation(1))
            throw new InvalidOperationException("A completed output accepted another playback operation.");
        playback.Enqueue(0, 1, new float[24], 1, [], 0, true);
    }

    private static async Task CheckNavigationBeforeCompletionAsync()
    {
        using var playback = new PausableKokoroPlayback(0f);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = 0;
        var segments = new[] { new KokoroPlaybackSegment([1], 0) };
        using var session = new KokoroPlaybackSession(_ => Interlocked.Increment(ref queued),
            KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice), segments,
            new KokoroTTSPipelineConfig(), playback);
        playback.SegmentStarted += (generation, _) =>
        {
            if (generation == 0) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
        };
        playback.SegmentCompleted += (generation, _, _) => { if (generation == 1) delivered.TrySetResult(); };
        session.Start();
        playback.Enqueue(0, 0, new float[2400], 1, [], 0, true);
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Active segment was not reached.");
            if (!session.TryRestart(segments, 0, false) || Volatile.Read(ref queued) != 2)
                throw new InvalidOperationException("Navigation was rejected before the player completed.");
            playback.Enqueue(1, 0, new float[2400], 1, [], 0, true);
        }
        finally { release.Set(); }
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (session.State != SpeechPlaybackState.Idle)
            throw new InvalidOperationException("The restarted generation failed to play and complete.");
    }
}
