using System.Threading.Channels;
using PulseReader.Monitoring;
using PulseReader.Speech;

internal static class OpeningRevisionChecks
{
    public static async Task RunAsync()
    {
        CheckAlignmentGuards();
        await CheckCompletedOpeningSeamAsync(reviseAgain: false);
        await CheckCompletedOpeningSeamAsync(reviseAgain: true);
        await CheckPreparingOpeningAfterCompletedItemAsync();
        foreach (var mode in new[] { "speaking", "spoken", "paused", "pending", "preparing", "parked", "new-turn" })
            await CheckAsync(mode);
        Console.WriteLine("PASS: an anchored opening rewrite does not restart audible speech or lose unread/new text.");
    }

    private static async Task CheckCompletedOpeningSeamAsync(bool reviseAgain)
    {
        using var speech = new ControlledSpeech { HoldStartResult = true };
        using var queue = new MonitoringSpeechQueue(speech);
        string[] draft = ["Manual Save-24 is installed and verified.",
            "The next sentence stays exactly the same.", "The third sentence also stays the same."];
        string[] final = ["ManualSave-24 is installed and verified.", draft[1], draft[2], "A new ending follows."];
        queue.EnqueueCompletedResponse(draft[0]);
        foreach (var unit in draft.Skip(1)) queue.EnqueueMonitoredText(unit);
        Equal(await speech.TakeAsync(), draft[0]);

        var atCompletion = false;
        var reconciled = false;
        queue.StateChanged += (_, _) =>
        {
            if (!atCompletion || reconciled || speech.State != SpeechPlaybackState.Idle) return;
            reconciled = true;
            // Playback completed, but the held start result deterministically
            // keeps the real worker from clearing its current item yet.
            PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue,
                new MonitoredTextEventArgs("", completedRevision: new(draft, final)));
            if (reviseAgain)
            {
                string[] latest = [draft[0], final[1], final[2], "A revised ending follows."];
                PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue,
                    new MonitoredTextEventArgs("", completedRevision: new(final, latest)));
                final = latest;
            }
        };
        atCompletion = true;
        speech.Complete();
        atCompletion = false;
        speech.HoldStartResult = false;
        speech.FinishStartResult();
        if (!reconciled) throw new InvalidOperationException("The completion-boundary revision did not run.");
        foreach (var expected in final.Skip(1))
        {
            Equal(await speech.TakeAsync(), expected);
            speech.Complete();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (queue.State.PendingItemCount > 0 || queue.State.IsSpeaking)
            await Task.Delay(5, timeout.Token);
        if (speech.StartedCount != final.Length)
            throw new InvalidOperationException("A completed opening restarted at its completion boundary.");
    }

    private static async Task CheckPreparingOpeningAfterCompletedItemAsync()
    {
        using var speech = new ControlledSpeech();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.EnqueueCompletedResponse("An earlier item has already played.");
        Equal(await speech.TakeAsync(), "An earlier item has already played.");
        speech.DelayPreparation = true;
        speech.Complete();
        string[] draft = ["Manual Save-24 is installed and verified.",
            "The next sentence stays exactly the same.", "The third sentence also stays the same."];
        string[] final = ["ManualSave-24 is installed and verified.", draft[1], draft[2]];
        queue.EnqueueCompletedResponse(draft[0]);
        foreach (var unit in draft.Skip(1)) queue.EnqueueMonitoredText(unit);
        Equal(await speech.TakeAsync(), draft[0]);
        queue.ReconcileCompletedResponse(draft, final);
        if (speech.State != SpeechPlaybackState.Idle)
            throw new InvalidOperationException("An earlier item's playback progress leaked into an unread opening.");
        speech.DelayPreparation = false;
        speech.FinishPreparation(); // late successful preparation must remain canceled
        foreach (var expected in final)
        {
            Equal(await speech.TakeAsync(), expected);
            speech.Complete();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (queue.State.PendingItemCount > 0 || queue.State.IsSpeaking)
            await Task.Delay(5, timeout.Token);
        if (speech.StartedCount != final.Length + 2)
            throw new InvalidOperationException("Replacing the next unread opening left extra speech.");
    }

    private static void CheckAlignmentGuards()
    {
        const string next = "An unchanged second sentence remains.";
        const string last = "An unchanged third sentence remains.";
        foreach (var pair in new[] {
            (Before: "Manual Save-24 is installed and verified.", After: "manualsave-24 is installed and verified."),
            (Before: "Delete all files after completion.", After: "Keep all files after completion."),
            (Before: "No requests are ready to process.", After: "My requests are ready to process."),
            (Before: "12 requests are ready to process.", After: "13 requests are ready to process."),
            (Before: "The draft uses a completely different opening.", After: "The final answer introduces a new subject."),
            (Before: "My request is ready to process.", After: "This request is ready to process." ) })
        {
            var following = pair.Before.StartsWith("My", StringComparison.Ordinal) ? "A new second sentence follows." : next;
            if (CompletedResponseAlignment.HasRewrittenOpening([pair.Before, next, last], [pair.After, following, last]))
                throw new InvalidOperationException("Opening alignment swallowed a distinct correction or lost its anchors.");
        }
        if (CompletedResponseAlignment.HasRewrittenOpening(["My request is ready to process."],
                ["This request is ready to process."]))
            throw new InvalidOperationException("An unanchored one-line reply was treated as a duplicate opening.");
    }

    private static async Task CheckAsync(string mode)
    {
        using var speech = new ControlledSpeech { DelayPreparation = mode == "preparing" };
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        string[] draft = ["Manual Save-24 is installed and verified.", "The next sentence stays exactly the same.",
            "The third sentence also stays the same."];
        string[] final = ["ManualSave-24 is installed and verified.", draft[1], draft[2], "A genuinely new ending follows."];
        const string cycle = "codex-message:opening-rewrite";
        WindowTextSnapshot Snapshot(string[] units, bool completed, string responseCycle = cycle) => new([
            new(responseCycle + (completed ? ":answer" : ":working"), string.Join(' ', units),
                ContainerKey: responseCycle + (completed ? ":answer" : ":working"),
                GroupKey: responseCycle + (completed ? ":answer" : ":working"), IsAggregate: true,
                IsFinalResponse: completed)], ResponseCycleKey: responseCycle, Context: new("view"));
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(Snapshot([], false), now);
        if (mode == "pending") queue.Pause();
        session.ProcessSnapshot(Snapshot(draft, false), now.AddSeconds(1), ref changedAt);
        if (mode != "pending") Equal(await speech.TakeAsync(), draft[0]);
        if (mode == "spoken")
        {
            speech.Complete();
            Equal(await speech.TakeAsync(), draft[1]);
        }
        if (mode == "paused") queue.Pause();
        if (mode == "parked")
        {
            queue.Pause();
            queue.EnqueueManualSelection("An unrelated selection plays first.");
            Equal(await speech.TakeAsync(), "An unrelated selection plays first.");
        }
        session.ProcessSnapshot(Snapshot(final, true, mode == "new-turn" ? cycle + ":next" : cycle),
            now.AddSeconds(2), ref changedAt);
        session.ProcessSettled(now.AddSeconds(4), ref changedAt);
        if (mode is "pending" or "paused") queue.Resume();
        if (mode == "parked")
        {
            speech.Complete();
            using var manualTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (queue.State.IsManualPlaybackActive) await Task.Delay(5, manualTimeout.Token);
            queue.Resume();
            Equal(await speech.TakeAsync(), final[0]);
        }
        if (mode == "preparing")
        {
            speech.DelayPreparation = false;
            speech.FinishPreparation();
            Equal(await speech.TakeAsync(), final[0]);
        }
        else if (mode == "pending") Equal(await speech.TakeAsync(), final[0]);
        speech.Complete();
        if (mode == "new-turn")
        {
            Equal(await speech.TakeAsync(), final[0]);
            speech.Complete();
        }
        foreach (var expected in final.Skip(mode == "spoken" ? 2 : 1))
        {
            Equal(await speech.TakeAsync(), expected);
            speech.Complete();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (queue.State.PendingItemCount > 0 || queue.State.IsSpeaking)
            await Task.Delay(5, timeout.Token);
        if (speech.StartedCount != final.Length + (mode is "preparing" or "new-turn" ? 1 : mode == "parked" ? 2 : 0))
            throw new InvalidOperationException("Opening rewrite left extra speech: " + mode);
    }

    private static void Equal(string actual, string expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Opening rewrite replayed/lost text: expected '{expected}', got '{actual}'.");
    }

    private sealed class ControlledSpeech : ISpeechService, IDisposable
    {
        private readonly Channel<string> _starts = Channel.CreateUnbounded<string>();
        private TaskCompletionSource<SpeechStartResult>? _preparation;
        public bool DelayPreparation { get; set; }
        public bool HoldStartResult { get; set; }
        public int StartedCount { get; private set; }
        public SpeechPlaybackState State { get; private set; }
        public event EventHandler? SpeakingStateChanged;
        public Task<SpeechStartResult> TrySpeakAsync(string text)
        {
            var delay = DelayPreparation;
            _preparation = delay || HoldStartResult ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            State = delay ? SpeechPlaybackState.Preparing : SpeechPlaybackState.Speaking;
            StartedCount++;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            _starts.Writer.TryWrite(text);
            return _preparation?.Task ?? Task.FromResult(SpeechStartResult.Success());
        }
        public void FinishStartResult() => _preparation!.TrySetResult(SpeechStartResult.Success());
        public Task<string> TakeAsync() => _starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        public void FinishPreparation()
        {
            State = SpeechPlaybackState.Speaking;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            _preparation!.TrySetResult(SpeechStartResult.Success());
        }
        public void Complete() { State = SpeechPlaybackState.Idle; SpeakingStateChanged?.Invoke(this, EventArgs.Empty); }
        public bool TryPause()
        {
            if (State != SpeechPlaybackState.Speaking) return false;
            State = SpeechPlaybackState.Paused; SpeakingStateChanged?.Invoke(this, EventArgs.Empty); return true;
        }
        public bool TryResume()
        {
            if (State != SpeechPlaybackState.Paused) return false;
            State = SpeechPlaybackState.Speaking; SpeakingStateChanged?.Invoke(this, EventArgs.Empty); return true;
        }
        public void Stop() => Complete();
        public void Dispose() => _starts.Writer.TryComplete();
    }
}
