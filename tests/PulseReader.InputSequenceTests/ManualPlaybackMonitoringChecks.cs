using PulseReader.Hotkeys;
using PulseReader.Speech;
using PulseReader.Monitoring;
using PulseReader.Playback;
using System.Drawing;
using System.Reflection;

internal static class ManualPlaybackMonitoringChecks
{
    public static async Task RunAsync()
    {
        foreach (var state in new[] { SpeechPlaybackState.Speaking, SpeechPlaybackState.Paused, SpeechPlaybackState.Preparing })
        {
            if (!PlaybackHotkeyPolicy.ShouldStopImmediately(true, true, state))
                throw new InvalidOperationException($"Manual playback while monitoring must stop on the first hotkey press ({state}), before attempting another selection capture.");
        }
        if (!PlaybackHotkeyPolicy.ShouldStopImmediately(true, true, SpeechPlaybackState.Idle))
            throw new InvalidOperationException("A queued selection or pending manual capture must be cancelable before speech starts.");
        if (PlaybackHotkeyPolicy.ShouldStopImmediately(true, false, SpeechPlaybackState.Speaking))
            throw new InvalidOperationException("Monitoring speech must still allow a manual selection from another window.");
        if (PlaybackHotkeyPolicy.ShouldStopImmediately(false, false, SpeechPlaybackState.Idle))
            throw new InvalidOperationException("An idle hotkey must capture the new selection.");
        await CheckManualStopAsync();
        await CheckQueuedStopAsync();
        await CheckLatePreparationAsync();
        await CheckStopBeforeStartAsync();
        await CheckPausedMonitorAsync();
        await CheckPausedCurrentMonitorAsync();
        await CheckParkedContextChangesAsync();
        await CheckPositionHandoffAsync();
        Console.WriteLine("PASS: manual playback stops on one press, rejects late starts, preserves monitoring and hands off island placement.");
    }

    private static readonly PlaybackIslandPlacement ManualPlacement = new(
        new Rectangle(0, 0, 1920, 1080), new Rectangle(900, 100, 600, 600), new IntPtr(2));

    private static async Task CheckManualStopAsync()
    {
        using var speech = new WindowMonitoringChecks.ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.EnqueueManualSelection("First manual selection.", ManualPlacement);
        Equal(await speech.TakeStartedAsync(), "First manual selection.");
        if (queue.State.ManualPlacement != ManualPlacement) throw new InvalidOperationException("Manual controls lost their source placement.");
        queue.EnqueueCompletedResponse("Retained monitored update.");
        speech.TryPause();
        if (!queue.TryStopManualPlayback()) throw new InvalidOperationException("Paused manual speech was not stopped.");
        Equal(await speech.TakeStartedAsync(), "Retained monitored update.");
        if (queue.State.HasManualPlayback || queue.State.ManualPlacement is not null)
            throw new InvalidOperationException("Manual controls did not return to monitoring after cancellation.");
        queue.EnqueueManualSelection("Fresh selection on the next press.", ManualPlacement);
        speech.Complete();
        Equal(await speech.TakeStartedAsync(), "Fresh selection on the next press.");
        speech.Complete();
        await WaitUntilAsync(() => !queue.State.HasManualPlayback);
        if (queue.TryStopManualPlayback()) throw new InvalidOperationException("Completed manual reading consumed the next capture gesture.");
    }

    private static async Task CheckQueuedStopAsync()
    {
        using var speech = new WindowMonitoringChecks.ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.EnqueueCompletedResponse("Current monitoring sentence.");
        await speech.TakeStartedAsync();
        queue.EnqueueManualSelection("Canceled pending selection.", ManualPlacement);
        queue.EnqueueMonitoredText("Next monitoring sentence.");
        if (!queue.TryStopManualPlayback() || speech.State != SpeechPlaybackState.Speaking)
            throw new InvalidOperationException("Canceling pending manual work interrupted the monitor's current sentence.");
        speech.Complete();
        Equal(await speech.TakeStartedAsync(), "Next monitoring sentence.");
        if (queue.State.ManualPlacement is not null) throw new InvalidOperationException("A canceled pending selection moved the island.");
    }

    private static async Task CheckLatePreparationAsync()
    {
        using var speech = new WindowMonitoringChecks.ControlledSpeechService { DelayPreparation = true };
        using var queue = new MonitoringSpeechQueue(speech);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.SpeechResultProduced += (_, e) => { if (e.Result.Canceled) canceled.TrySetResult(); };
        queue.EnqueueManualSelection("Canceled preparation.", ManualPlacement);
        await speech.TakeStartedAsync();
        if (!queue.TryStopManualPlayback() || speech.State != SpeechPlaybackState.Idle)
            throw new InvalidOperationException("Manual preparation did not stop immediately.");
        speech.FinishPreparation();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (speech.State != SpeechPlaybackState.Idle || queue.State.ManualPlacement is not null)
            throw new InvalidOperationException("Late preparation revived canceled manual speech or controls.");
        speech.DelayPreparation = false;
        queue.EnqueueManualSelection("Replacement manual text.", ManualPlacement);
        Equal(await speech.TakeStartedAsync(), "Replacement manual text.");
    }

    private static async Task CheckStopBeforeStartAsync()
    {
        using var speech = new WindowMonitoringChecks.ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        var intercepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = 0;
        queue.StateChanged += (_, _) =>
        {
            if (queue.State.IsManualPlaybackActive && speech.State == SpeechPlaybackState.Idle
                && Interlocked.Exchange(ref once, 1) == 0)
            {
                queue.TryStopManualPlayback();
                intercepted.TrySetResult();
            }
        };
        queue.EnqueueManualSelection("Must never start.", ManualPlacement);
        await intercepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.EnqueueCompletedResponse("Only monitoring should start.");
        Equal(await speech.TakeStartedAsync(), "Only monitoring should start.");
        if (speech.StartedCount != 1) throw new InvalidOperationException("A canceled dequeued manual request still started.");
    }

    private static async Task CheckPausedMonitorAsync()
    {
        using var speech = new WindowMonitoringChecks.ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.Pause();
        queue.EnqueueCompletedResponse("Monitoring stays paused.");
        queue.EnqueueManualSelection("Independent manual reading.", ManualPlacement);
        Equal(await speech.TakeStartedAsync(), "Independent manual reading.");
        if (speech.State != SpeechPlaybackState.Speaking || !queue.State.IsPaused)
            throw new InvalidOperationException("Manual playback inherited or cleared the monitor's paused state.");
        queue.SetContextSuspended(true);
        if (speech.State != SpeechPlaybackState.Speaking) throw new InvalidOperationException("A monitored thread switch canceled unrelated manual speech.");
        queue.TryStopManualPlayback();
        await WaitUntilAsync(() => !queue.State.IsManualPlaybackActive);
        if (!queue.State.IsPaused || !queue.State.IsContextSuspended) throw new InvalidOperationException("Manual cancellation altered monitoring state.");
    }

    private static async Task CheckPausedCurrentMonitorAsync()
    {
        using var speech = new WindowMonitoringChecks.ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.EnqueueCompletedResponse("Paused monitored sentence resumes later.");
        await speech.TakeStartedAsync();
        queue.Pause();
        queue.EnqueueManualSelection("Manual selection while the feed is paused.", ManualPlacement);
        Equal(await speech.TakeStartedAsync(), "Manual selection while the feed is paused.");
        speech.Complete();
        await WaitUntilAsync(() => !queue.State.IsManualPlaybackActive);
        if (!queue.State.IsPaused) throw new InvalidOperationException("Manual playback unpaused the feed.");
        queue.Resume();
        Equal(await speech.TakeStartedAsync(), "Paused monitored sentence resumes later.");
    }

    private static async Task CheckParkedContextChangesAsync()
    {
        foreach (var changeContext in new[] { false, true })
        {
            using var speech = new WindowMonitoringChecks.ControlledSpeechService();
            using var queue = new MonitoringSpeechQueue(speech);
            queue.EnqueueCompletedResponse("Old paused thought must not return.");
            await speech.TakeStartedAsync();
            queue.Pause();
            queue.EnqueueManualSelection("Manual reading survives.", ManualPlacement);
            await speech.TakeStartedAsync();
            if (changeContext)
            {
                queue.SetContextSuspended(true);
                queue.SetContextSuspended(false);
            }
            queue.EnqueueCompletedResponse("Only the new final answer is eligible.");
            speech.Complete();
            await WaitUntilAsync(() => !queue.State.IsManualPlaybackActive);
            queue.Resume();
            Equal(await speech.TakeStartedAsync(), "Only the new final answer is eligible.");
        }
    }

    private static Task CheckPositionHandoffAsync()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new PlaybackIslandForm();
                var original = new Rectangle(120, 180, 182, 63);
                form.Bounds = original;
                var field = typeof(PlaybackIslandForm).GetField("_manualPositionActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
                field.SetValue(form, true);
                var saved = form.SuspendPresentation();
                if ((bool)field.GetValue(form)!) throw new InvalidOperationException("Manual controls inherited the monitor's dragged position.");
                form.Bounds = new Rectangle(900, 700, 182, 63);
                field.SetValue(form, true);
                form.RestorePresentation(saved);
                if (form.Bounds != original || !(bool)field.GetValue(form)!)
                    throw new InvalidOperationException("Returning controls lost the monitor's session position.");
                result.SetResult();
            }
            catch (Exception exception) { result.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static void Equal(string actual, string expected)
    {
        if (actual != expected) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new InvalidOperationException("Timed out waiting for playback state.");
            await Task.Delay(10);
        }
    }
}
