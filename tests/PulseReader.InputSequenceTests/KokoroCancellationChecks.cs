using System.Diagnostics;
using KokoroSharp;
using KokoroSharp.Core;
using PulseReader.Speech;

internal static class KokoroCancellationChecks
{
    // KokoroSharp cannot interrupt an active ONNX segment. Its original policy
    // can therefore delay a restart while a segment of up to 510 tokens finishes.
    private static readonly TimeSpan MaximumRestartLatency = TimeSpan.FromSeconds(10);

    public static async Task RunSmokeAsync()
    {
        var modelStore = new KokoroModelStore();
        var modelPath = await modelStore.GetModelPathAsync(CancellationToken.None);

        var loadTimer = Stopwatch.StartNew();
        using var engine = KokoroTTS.LoadModel(modelPath);
        loadTimer.Stop();

        engine.SetVolume(0f);
        var voice = KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice);
        var pipeline = KokoroSpeechService.CreatePipelineConfig(
            KokoroSpeechSettings.DefaultSpeed);

        var warmLatencies = new List<double>();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var timer = Stopwatch.StartNew();
            await SpeakUntilPlaybackAsync(
                engine,
                voice,
                pipeline,
                "Pulse Reader is ready to speak selected text.");
            timer.Stop();
            warmLatencies.Add(timer.Elapsed.TotalMilliseconds);
            await Task.Delay(150);
        }

        CollectManagedGarbage();
        var warmPrivateBytes = ReadPrivateBytes();

        var longText = string.Join(
            ' ',
            Enumerable.Repeat(
                "Pulse Reader continues speaking this deliberately long passage so cancellation occurs while a later segment is being synthesized.",
                70));
        var longHandle = engine.SpeakFast(longText, voice, pipeline);
        var longPlaybackStarted = CompletionSource();
        longHandle.OnSpeechStarted += _ => longPlaybackStarted.TrySetResult();
        await longPlaybackStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // KokoroSharp begins the next inference while the first segment plays.
        // Stopping here exercises the non-interruptible inference tail.
        await Task.Delay(150);
        var stopTimer = Stopwatch.StartNew();
        engine.StopPlayback();
        stopTimer.Stop();

        var restartTimer = Stopwatch.StartNew();
        await SpeakUntilPlaybackAsync(
            engine,
            voice,
            pipeline,
            "Pulse Reader restarted after cancellation.");
        restartTimer.Stop();

        await Task.Delay(TimeSpan.FromSeconds(10));
        CollectManagedGarbage();
        var settledPrivateBytes = ReadPrivateBytes();
        var retainedIncreaseBytes = settledPrivateBytes - warmPrivateBytes;

        Assert(
            stopTimer.Elapsed <= TimeSpan.FromMilliseconds(50),
            $"Kokoro stop took {stopTimer.Elapsed.TotalMilliseconds:F1} ms.");
        Assert(
            restartTimer.Elapsed <= MaximumRestartLatency,
            $"Kokoro restart took {restartTimer.Elapsed.TotalMilliseconds:F1} ms.");
        warmLatencies.Sort();
        Console.WriteLine(
            "PASS: warm Kokoro cancellation stopped immediately and speech restarted successfully.");
        Console.WriteLine(
            $"LOAD_MS={loadTimer.Elapsed.TotalMilliseconds:F1} "
            + $"WARM_FIRST_AUDIO_MEDIAN_MS={warmLatencies[1]:F1} "
            + $"STOP_MS={stopTimer.Elapsed.TotalMilliseconds:F3} "
            + $"RESTART_FIRST_AUDIO_MS={restartTimer.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine(
            $"WARM_PRIVATE_MB={ToMegabytes(warmPrivateBytes):F1} "
            + $"SETTLED_PRIVATE_MB={ToMegabytes(settledPrivateBytes):F1} "
            + $"RETAINED_INCREASE_MB={ToMegabytes(retainedIncreaseBytes):F1}");
    }

    private static async Task SpeakUntilPlaybackAsync(
        KokoroTTS engine,
        KokoroVoice voice,
        KokoroSharp.Processing.KokoroTTSPipelineConfig pipeline,
        string text)
    {
        var playbackStarted = CompletionSource();
        var handle = engine.SpeakFast(text, voice, pipeline);
        handle.OnSpeechStarted += _ => playbackStarted.TrySetResult();
        await playbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        engine.StopPlayback();
    }

    private static TaskCompletionSource CompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static long ReadPrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static void CollectManagedGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double ToMegabytes(long bytes) => bytes / 1024d / 1024d;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
