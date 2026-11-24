using System.Threading.Channels;
using PulseReader.Monitoring;
using PulseReader.Speech;

internal static class CompletedResponseRevisionChecks
{
    public static async Task RunAsync()
    {
        await CheckT3MergedFinalAsync(reidentified: false);
        await CheckT3MergedFinalAsync(reidentified: true);
        await CheckT3MergedFinalAsync(reidentified: false, revisedEnding: true);
        await CheckT3HandoffAcrossCaptureBoundaryAsync(suspended: false);
        await CheckT3HandoffAcrossCaptureBoundaryAsync(suspended: true);
        await CheckT3HandoffAcrossCaptureBoundaryAsync(suspended: false, newPrompt: true);
        await CheckT3HandoffAcrossCaptureBoundaryAsync(suspended: true, newView: true);
        await CheckT3HandoffAcrossCaptureBoundaryAsync(suspended: true, interruptOpening: true);
        CheckAlignmentSearch();
        foreach (var mode in new[] { "speaking", "spoken", "pending", "preparing" })
            await CheckSingleStreamedOpeningAsync(mode);
        await CheckSingleStreamedOpeningAsync("spoken", newTurn: true);
        await CheckFinalOpeningAfterEarlierThoughtsAsync();
        await CheckFinalOpeningAfterEarlierThoughtsAsync(prependIntroduction: true);
        await CheckT3OpeningBeforeReformattedTextAsync(false);
        await CheckT3OpeningBeforeReformattedTextAsync(true);
        await CheckT3OpeningWithoutMatchingTailAsync();
        await CheckT3SingleOpeningBeforeMatchingTailAsync(heardOpening: true);
        await CheckT3SingleOpeningBeforeMatchingTailAsync(heardOpening: false);
        foreach (var mode in new[] { "pending", "preparing", "parked" })
            await CheckNewIntroductionOrderingAsync(mode);
        await CheckTrailingOpeningQueueAsync(preparing: false);
        await CheckTrailingOpeningQueueAsync(preparing: true);
        Equal(CompletedResponseAlignment.FindStreamedOpeningOffset(["Earlier thought.", "Shared sentence."],
            ["Shared sentence.", "New detail."]), 0);
        Equal(CompletedResponseAlignment.FindStreamedOpeningOffset(["Earlier thought.", "Shared first.", "Shared second.", "Other tail."],
            ["Shared first.", "Shared second.", "New detail."]), 0);
        foreach (var mode in new[] { "pending", "preparing", "parked" })
            await CheckFilteredOccurrenceHistoryAsync(mode);
        await CheckPublishedRepeatedOccurrencesAsync();
        await CheckSpokenAndQueuedPrefixAsync();
        await CheckPausedPrefixAsync();
        await CheckPausedCurrentPrefixAsync();
        await CheckPreparingPrefixAsync();
        await CheckParkedPrefixAsync();
        await CheckParkedSuffixAsync();
        await CheckPreparingSuffixAsync();
        await CheckBeforeStartAsync();
        await CheckSpeakingSuffixAsync();
        await CheckShortenedFinalAsync();
        await CheckRepeatedUnitsAsync();
        await CheckContextAndCapacityAsync();
        Console.WriteLine("PASS: completed-response revisions preserve unread prefixes, replace draft suffixes, and respect playback/context state.");
    }

    private static async Task CheckT3MergedFinalAsync(bool reidentified, bool revisedEnding = false)
    {
        const string thought = "I will check how the working answer is presented.";
        const string opening = "Here is the first part of the completed result";
        const string middle = "Followed by a separate explanation of the second stage";
        const string ending = "And this is the closing part of the reply.";
        const string revised = "And the final report records an additional result.";
        const string cycle = "t3-message:merged-final";
        const string view = "t3:merged-final";
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        var now = DateTimeOffset.UtcNow;
        var changed = DateTimeOffset.MinValue;
        session.EstablishBaseline(new([], ResponseCycleKey: cycle, Context: new(view)), now);
        Feed(cycle, string.Join("\n\n", thought, opening, middle, ending), final: false);
        Equal(await speech.TakeStartedAsync(), thought);
        speech.Complete();
        Equal(await speech.TakeStartedAsync(), opening);

        var finalCycle = reidentified ? "t3-message:merged-final-rekeyed" : cycle;
        var finalEnding = revisedEnding ? revised : ending;
        var finalBody = string.Join(' ', opening, middle, finalEnding);
        Feed(finalCycle, finalBody, final: true);
        Feed(finalCycle, finalBody, final: true);
        speech.Complete();
        await DrainAsync(speech, queue, [middle, finalEnding]);

        void Feed(string response, string text, bool final)
        {
            var key = response + (final ? ":answer" : ":working");
            var snapshot = new WindowTextSnapshot([new(key, text, ContainerKey: key, GroupKey: key,
                IsAggregate: true, IsFinalResponse: final)], ResponseCycleKey: response, Context: new(view));
            now += TimeSpan.FromSeconds(3);
            session.ProcessSnapshot(snapshot, now, ref changed);
            session.ProcessSettled(now, ref changed);
        }
    }

    private static async Task CheckT3HandoffAcrossCaptureBoundaryAsync(bool suspended, bool newPrompt = false,
        bool newView = false, bool interruptOpening = false)
    {
        const string opening = "The first stage completed with the expected result.";
        const string second = "The second test confirmed the reported configuration.";
        const string ending = "The final report also records a new detail.";
        const string draft = "t3-message:original";
        const string view = "t3:handoff";
        var activeView = view;
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        session.ContextChanged += (_, e) => queue.SetContextSuspended(e.Suspended);
        var now = DateTimeOffset.UtcNow;
        var changed = DateTimeOffset.MinValue;
        session.EstablishBaseline(new([], ResponseCycleKey: draft, Context: new(view)), now);
        var working = Snapshot(draft, opening + " " + second, final: false);
        Feed(working);
        Equal(await speech.TakeStartedAsync(), opening);
        queue.Pause();
        if (!interruptOpening)
        {
            speech.Complete();
            await WaitUntilAsync(() => speech.State == SpeechPlaybackState.Idle);
        }
        if (suspended)
        {
            Feed(new([], Context: new("")));
            if (newView) activeView = "t3:other";
            var baseline = working with { Context = new(activeView) };
            Feed(baseline);
            Feed(baseline);
        }

        var completed = Snapshot(suspended ? draft : "t3-message:reidentified", opening + " " + second + " " + ending, final: true);
        Feed(completed);
        Feed(completed);
        queue.Resume();
        await DrainAsync(speech, queue, newPrompt || newView || interruptOpening ? [opening, second, ending] : [second, ending]);

        WindowTextSnapshot Snapshot(string cycle, string text, bool final)
        {
            var key = cycle + (final ? ":answer" : ":working");
            return new([new(key, text, ContainerKey: key, GroupKey: key, IsAggregate: true, IsFinalResponse: final)],
                ResponseCycleKey: cycle, Context: new(activeView), T3HasNewPrompt: final && newPrompt);
        }

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(3);
            session.ProcessSnapshot(snapshot, now, ref changed);
            session.ProcessSettled(now, ref changed);
        }
    }

    private static void CheckAlignmentSearch()
    {
        const string opening = "This completed response is now ready.";
        Equal(CompletedResponseAlignment.FindStreamedOpeningOffset(["Earlier thought.", opening],
            [opening, "New detail."]), 1);
        Equal(CompletedResponseAlignment.FindStreamedOpeningOffset(["Earlier thought.", opening],
            ["New introduction.", opening, "New detail."]), 0);
        Equal(CompletedResponseAlignment.FindStreamedOpeningOffset(["Earlier thought.", opening, "Other tail."],
            [opening, "New detail."]), 0);
        Equal(CompletedResponseAlignment.FindStreamedOpeningOffset([opening, "Earlier thought.", opening],
            [opening, "New detail."]), 0);
        Equal(CompletedResponseAlignment.FindStreamedOpeningOffset(["Earlier thought.", opening],
            [opening, opening, "New detail."]), 0);
        Equal(CompletedResponseAlignment.FindStreamedOpeningOffset(["Earlier thought.", opening.TrimEnd('.')],
            [opening.TrimEnd('.'), "New detail."]), 0);
        var random = new Random(314159);
        string[] vocabulary = ["One sentence.", "Another sentence.", "A third sentence.", "Final sentence."];
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var draft = Enumerable.Range(0, random.Next(0, 8)).Select(_ => vocabulary[random.Next(vocabulary.Length)]).ToArray();
            var final = Enumerable.Range(0, random.Next(0, 10)).Select(_ => vocabulary[random.Next(vocabulary.Length)]).ToArray();
            var expectedDraft = 0; var expectedFinal = 0; var found = false;
            if (draft.Length >= 2 && final.Length >= 2 && draft[0] != final[0])
                for (var f = 0; f <= final.Length - 2 && !found; f++)
                    for (var d = 0; d <= draft.Length - 2 && !found; d++)
                        if (draft.Length - d <= final.Length - f && draft.Skip(d).SequenceEqual(final.Skip(f).Take(draft.Length - d)))
                        { expectedDraft = d; expectedFinal = f; found = true; }
            var actual = CompletedResponseAlignment.FindStreamedOpeningOffset(draft, final, out var finalOffset);
            Equal(actual, expectedDraft); Equal(finalOffset, expectedFinal);
        }
    }

    private static async Task CheckSingleStreamedOpeningAsync(string mode, bool newTurn = false)
    {
        const string thought = "I am checking the earlier synthetic evidence.";
        const string opening = "This completed response is now ready.";
        const string ending = "The completed answer adds this final detail.";
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        const string cycle = "codex-message:single-final-opening";
        session.EstablishBaseline(new([], ResponseCycleKey: cycle, Context: new("view")), DateTimeOffset.UtcNow);
        var changedAt = DateTimeOffset.MinValue;
        void Working(string body) => session.AppendChanges([new(body, true, true, cycle + ":working",
            ContainerKey: cycle + ":working", GroupKey: cycle + ":working")], ref changedAt);
        Working(thought);
        Equal(await speech.TakeStartedAsync(), thought);
        speech.Complete();
        await WaitUntilAsync(() => !queue.State.IsSpeaking);
        if (mode == "pending") queue.Pause();
        speech.DelayPreparation = mode == "preparing";
        Working(opening);
        if (mode != "pending") Equal(await speech.TakeStartedAsync(), opening);
        if (mode == "spoken")
        {
            speech.Complete();
            await WaitUntilAsync(() => !queue.State.IsSpeaking);
        }
        var finalKey = (newTurn ? "codex-message:different-turn" : cycle) + ":answer";
        session.AppendChanges([new(opening + " " + ending, true, true, finalKey,
            ContainerKey: finalKey, GroupKey: finalKey, IsFinalResponse: true)], ref changedAt);
        if (mode == "preparing")
        {
            speech.DelayPreparation = false;
            speech.FinishPreparation();
        }
        if (mode is "speaking" or "preparing") speech.Complete();
        if (mode == "pending") queue.Resume();
        await DrainAsync(speech, queue, mode == "pending" || newTurn ? [opening, ending] : [ending]);
        Equal(speech.StartedCount, newTurn ? 4 : 3);
    }

    private static async Task CheckNewIntroductionOrderingAsync(string mode)
    {
        string[] draft = ["Earlier thought.", "Existing opening sentence.", "Existing second sentence."];
        string[] final = ["A newly inserted introduction.", draft[1], draft[2], "A new final detail."];
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        if (mode == "pending") queue.Pause();
        Publish(queue, draft);
        if (mode != "pending")
        {
            Equal(await speech.TakeStartedAsync(), draft[0]);
            speech.DelayPreparation = mode == "preparing";
            speech.Complete();
            Equal(await speech.TakeStartedAsync(), draft[1]);
            if (mode == "parked")
            {
                queue.Pause();
                queue.EnqueueManualSelection("Manual interjection.");
                Equal(await speech.TakeStartedAsync(), "Manual interjection.");
            }
        }
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.SpeechResultProduced += (_, e) => { if (e.Text == draft[1] && e.Result.Canceled) canceled.TrySetResult(); };
        var draftOffset = CompletedResponseAlignment.FindStreamedOpeningOffset(draft, final, out var finalOffset);
        Equal(draftOffset, 1); Equal(finalOffset, 1);
        queue.ReconcileCompletedResponse(draft, final, draftPrefixOffset: draftOffset, finalPrefixOffset: finalOffset);
        if (mode == "preparing")
        {
            if (speech.State != SpeechPlaybackState.Idle) throw new InvalidOperationException("A preparing prefix bypassed the new introduction.");
            speech.DelayPreparation = false;
            speech.FinishPreparation();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        else
        {
            if (mode == "parked")
            {
                speech.Complete();
                await WaitUntilAsync(() => !queue.State.IsManualPlaybackActive);
            }
            queue.Resume();
        }
        await DrainAsync(speech, queue, final);
    }

    private static async Task CheckTrailingOpeningQueueAsync(bool preparing)
    {
        string[] draft = ["Earlier thought.", "Final opening sentence.", "Final second sentence."];
        string[] final = [draft[1], draft[2], "Completed final addition."];
        using var speech = new ControlledSpeechService { DelayPreparation = preparing };
        using var queue = new MonitoringSpeechQueue(speech);
        if (!preparing) queue.Pause();
        Publish(queue, draft);
        if (preparing) Equal(await speech.TakeStartedAsync(), draft[0]);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.SpeechResultProduced += (_, e) => { if (e.Text == draft[0] && e.Result.Canceled) canceled.TrySetResult(); };
        var offset = CompletedResponseAlignment.FindStreamedOpeningOffset(draft, final);
        Equal(offset, 1);
        queue.ReconcileCompletedResponse(draft, final, draftPrefixOffset: offset);
        if (preparing)
        {
            if (speech.State != SpeechPlaybackState.Idle) throw new InvalidOperationException("Unstarted earlier thought survived the final handoff.");
            speech.DelayPreparation = false;
            speech.FinishPreparation();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        else
        {
            Equal(queue.State.PendingItemCount, 3);
            queue.Resume();
        }
        await DrainAsync(speech, queue, final);
    }

    private static async Task CheckFinalOpeningAfterEarlierThoughtsAsync(bool prependIntroduction = false)
    {
        const string thought = "I am checking the earlier synthetic evidence.";
        const string opening = "Mixed, but there is useful progress since the last check.";
        const string second = "The next sentence is already available.";
        const string ending = "The completed answer adds this final detail.";
        const string introduction = "The final answer adds a new introduction.";
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        const string cycle = "codex-message:late-final-opening";
        session.EstablishBaseline(new([], ResponseCycleKey: cycle, Context: new("view")), DateTimeOffset.UtcNow);
        var changedAt = DateTimeOffset.MinValue;
        session.AppendChanges([new(string.Join(' ', thought, opening, second), true, true,
            cycle + ":working", ContainerKey: cycle + ":working", GroupKey: cycle + ":working")], ref changedAt);
        Equal(await speech.TakeStartedAsync(), thought);
        speech.Complete();
        Equal(await speech.TakeStartedAsync(), opening);
        speech.Complete();
        Equal(await speech.TakeStartedAsync(), second);
        session.AppendChanges([new(string.Join(' ', prependIntroduction ? introduction + " " + opening : opening, second, ending), true, true,
            cycle + ":answer", ContainerKey: cycle + ":answer", GroupKey: cycle + ":answer", IsFinalResponse: true)], ref changedAt);
        speech.Complete();
        await DrainAsync(speech, queue, prependIntroduction ? [introduction, ending] : [ending]);
        Equal(speech.StartedCount, prependIntroduction ? 5 : 4);
    }

    private static async Task CheckT3OpeningBeforeReformattedTextAsync(bool heardOpening)
    {
        const string cycle = "t3-message:reformatted-final";
        const string thought = "I am checking the earlier evidence.";
        const string opening = "The first result matched the expected output.";
        const string second = "The second result also matched our test fixture.";
        const string draftMiddle = "The earlier draft presents the next part as a separate sentence.";
        const string finalMiddle = "The final wording adds an important detail in the middle.";
        const string tail = "The remaining cases were checked with the same setup.";
        const string last = "The last check finished without any reported errors.";
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        session.EstablishBaseline(new([], ResponseCycleKey: cycle, Context: new("view")), DateTimeOffset.UtcNow);
        var changedAt = DateTimeOffset.MinValue;
        var working = cycle + ":working";
        if (!heardOpening) queue.Pause();
        session.AppendChanges([new(string.Join(' ', thought, opening, second, draftMiddle, tail, last),
            true, true, working, ContainerKey: working, GroupKey: working)], ref changedAt);
        if (heardOpening)
        {
            foreach (var unit in new[] { thought, opening, second })
            {
                Equal(await speech.TakeStartedAsync(), unit);
                if (unit != second) speech.Complete();
            }
        }
        var completed = cycle + ":answer";
        session.AppendChanges([new(string.Join(' ', opening, second, finalMiddle, tail, last),
            true, true, completed, ContainerKey: completed, GroupKey: completed, IsFinalResponse: true)], ref changedAt);
        if (heardOpening) speech.Complete();
        else queue.Resume();
        await DrainAsync(speech, queue, heardOpening ? [finalMiddle, tail, last] : [opening, second, finalMiddle, tail, last]);
    }

    private static async Task CheckT3OpeningWithoutMatchingTailAsync()
    {
        const string cycle = "t3-message:nonmatching-tail";
        const string progress = "Earlier progress describes the planned investigation.";
        const string opening = "The first result matched exactly what we expected.";
        const string second = "The test also passed.";
        const string third = "The final answer adds a detail that was missing in the draft.";
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        session.EstablishBaseline(new([], ResponseCycleKey: cycle, Context: new("view")), DateTimeOffset.UtcNow);
        var changedAt = DateTimeOffset.MinValue;
        var working = cycle + ":working";
        session.AppendChanges([new(string.Join(' ', progress, opening, second, "Here is the previous ending."),
            true, true, working, ContainerKey: working, GroupKey: working)], ref changedAt);
        foreach (var sentence in new[] { progress, opening, second })
        {
            Equal(await speech.TakeStartedAsync(), sentence);
            await WaitUntilAsync(() => speech.State == SpeechPlaybackState.Speaking);
            if (sentence != second) speech.Complete();
        }
        var final = cycle + ":answer";
        session.AppendChanges([new(string.Join(' ', opening, second, third), true, true,
            final, ContainerKey: final, GroupKey: final, IsFinalResponse: true)], ref changedAt);
        speech.Complete();
        await DrainAsync(speech, queue, [third]);
    }

    private static async Task CheckT3SingleOpeningBeforeMatchingTailAsync(bool heardOpening)
    {
        const string cycle = "t3-message:single-opening-matching-tail";
        const string progress = "Earlier progress describes the planned investigation.";
        const string opening = "The first result matched what we expected from the revised test environment.";
        const string draftMiddle = "The draft presents the next detail using a separate sentence.";
        const string finalMiddle = "The final answer groups the next detail differently.";
        const string tail = "The remaining cases ran with the same inputs.";
        const string last = "The last check finished without errors.";
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        session.EstablishBaseline(new([], ResponseCycleKey: cycle, Context: new("t3:view")), DateTimeOffset.UtcNow);
        var changedAt = DateTimeOffset.MinValue;
        var working = cycle + ":working";
        if (!heardOpening) queue.Pause();
        session.AppendChanges([new(string.Join(' ', progress, opening, draftMiddle, tail, last),
            true, true, working, ContainerKey: working, GroupKey: working)], ref changedAt);
        if (heardOpening)
        {
            Equal(await speech.TakeStartedAsync(), progress);
            speech.Complete();
            Equal(await speech.TakeStartedAsync(), opening);
            await WaitUntilAsync(() => speech.State == SpeechPlaybackState.Speaking);
        }

        var completed = cycle + ":answer";
        session.AppendChanges([new(string.Join(' ', opening, finalMiddle, tail, last), true, true,
            completed, ContainerKey: completed, GroupKey: completed, IsFinalResponse: true)], ref changedAt);
        if (heardOpening) speech.Complete();
        else queue.Resume();
        await DrainAsync(speech, queue, heardOpening ? [finalMiddle, tail, last] : [opening, finalMiddle, tail, last]);
    }

    private static readonly string[] Draft = ["Opening sentence.", "Retained second sentence.", "Draft ending."];
    private static readonly string[] Final = ["Opening sentence.", "Retained second sentence.", "Corrected ending.", "Final addition."];

    private static async Task CheckFilteredOccurrenceHistoryAsync(string mode)
    {
        using var speech = new ControlledSpeechService { DelayPreparation = mode == "preparing" };
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        const string cycle = "codex-message:filtered-occurrences";
        WindowTextSnapshot Snapshot(string body, bool final) => new([
            new(cycle + (final ? ":answer" : ":working"), body,
                ContainerKey: cycle + (final ? ":answer" : ":working"),
                GroupKey: cycle + (final ? ":answer" : ":working"), IsAggregate: true,
                IsFinalResponse: final)], ResponseCycleKey: cycle, Context: new("view"));
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(Snapshot("", false), now);
        if (mode == "pending") queue.Pause();
        session.ProcessSnapshot(Snapshot("Repeated", false), now.AddMilliseconds(500), ref changedAt);
        Equal(queue.State.PendingItemCount, 0);
        session.ProcessSnapshot(Snapshot("Repeated sentence.", false), now.AddSeconds(1), ref changedAt);
        if (mode != "pending")
        {
            Equal(await speech.TakeStartedAsync(), "Repeated sentence.");
            if (mode == "parked")
            {
                queue.Pause();
                queue.EnqueueManualSelection("Manual interjection.");
                Equal(await speech.TakeStartedAsync(), "Manual interjection.");
            }
        }
        session.ProcessSnapshot(Snapshot("Repeated sentence. Repeated sentence. Final ending.", true),
            now.AddSeconds(2), ref changedAt);
        session.ProcessSettled(now.AddSeconds(4), ref changedAt);
        var currentOffset = mode == "preparing" ? 1 : 0;
        Equal(queue.State.PendingItemCount, 2 - currentOffset); // the already-published repeat is filtered
        session.ProcessSnapshot(Snapshot("Repeated sentence. Corrected sentence. Final ending.", true),
            now.AddSeconds(5), ref changedAt);
        session.ProcessSettled(now.AddSeconds(7), ref changedAt);
        Equal(queue.State.PendingItemCount, 3 - currentOffset);
        // Appended finals pass through ordinary publication. Another repeated
        // occurrence is filtered there, but must keep its structural position
        // for both the next correction and the revision after that.
        session.ProcessSnapshot(Snapshot("Repeated sentence. Corrected sentence. Final ending. Repeated sentence. Appended ending.", true),
            now.AddSeconds(8), ref changedAt);
        session.ProcessSettled(now.AddSeconds(10), ref changedAt);
        Equal(queue.State.PendingItemCount, 4 - currentOffset);
        session.ProcessSnapshot(Snapshot("Repeated sentence. Corrected sentence. Final ending. New sentence. Appended ending.", true),
            now.AddSeconds(11), ref changedAt);
        session.ProcessSettled(now.AddSeconds(13), ref changedAt);
        Equal(queue.State.PendingItemCount, 5 - currentOffset);
        session.ProcessSnapshot(Snapshot("Repeated sentence. Corrected sentence. Final ending. New sentence. Latest ending.", true),
            now.AddSeconds(14), ref changedAt);
        session.ProcessSettled(now.AddSeconds(16), ref changedAt);
        Equal(queue.State.PendingItemCount, 5 - currentOffset);
        if (mode == "preparing")
        {
            speech.DelayPreparation = false;
            speech.FinishPreparation();
            speech.Complete();
        }
        else
        {
            if (mode == "parked")
            {
                speech.Complete();
                await WaitUntilAsync(() => !queue.State.IsManualPlaybackActive);
            }
            queue.Resume();
        }
        await DrainAsync(speech, queue,
            new[] { "Repeated sentence.", "Corrected sentence.", "Final ending.", "New sentence.", "Latest ending." }
                .Skip(currentOffset));
    }

    private static async Task CheckPublishedRepeatedOccurrencesAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
        const string cycle = "codex-message:published-occurrences";
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(new([], ResponseCycleKey: cycle, Context: new("view")), DateTimeOffset.UtcNow);
        queue.Pause();
        void Complete(string body) => session.AppendChanges([new(body, true, true, cycle + ":answer",
            ContainerKey: cycle + ":answer", GroupKey: cycle + ":answer", IsFinalResponse: true)], ref changedAt);
        Complete("Repeated sentence. Repeated sentence. Final ending.");
        Equal(queue.State.PendingItemCount, 3); // repeats in one batch both publish
        Complete("Repeated sentence. Repeated sentence. Corrected ending.");
        Equal(queue.State.PendingItemCount, 3);
        Complete("Repeated sentence. Repeated sentence.");
        Equal(queue.State.PendingItemCount, 2);
        Complete("Repeated sentence. Repeated sentence. Appended ending.");
        Equal(queue.State.PendingItemCount, 3);
        Complete("Repeated sentence. Repeated sentence. Latest ending.");
        Equal(queue.State.PendingItemCount, 3);
        queue.Resume();
        await DrainAsync(speech, queue, ["Repeated sentence.", "Repeated sentence.", "Latest ending."]);
    }

    private static void Publish(MonitoringSpeechQueue queue, IReadOnlyList<string> units)
    {
        // The first completed unit avoids the initial burst delay in these checks.
        queue.EnqueueCompletedResponse(units[0]);
        foreach (var unit in units.Skip(1)) queue.EnqueueMonitoredText(unit);
    }

    private static async Task CheckSpokenAndQueuedPrefixAsync()
    {
        string[] draft = [Draft[0], Draft[1], "Retained third sentence.", Draft[2]];
        string[] final = [Final[0], Final[1], draft[2], Final[2], Final[3]];
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        Publish(queue, draft);
        Equal(await speech.TakeStartedAsync(), draft[0]);
        speech.Complete();
        Equal(await speech.TakeStartedAsync(), draft[1]);
        queue.ReconcileCompletedResponse(draft, final);
        speech.Complete();
        await DrainAsync(speech, queue, final.Skip(2));
        Equal(speech.StartedCount, 5);
    }

    private static async Task CheckPausedPrefixAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.Pause();
        Publish(queue, Draft);
        queue.ReconcileCompletedResponse(Draft, Final);
        Equal(queue.State.PendingItemCount, 4);
        Equal(speech.StartedCount, 0);
        if (!queue.State.IsPaused) throw new InvalidOperationException("Final revision cleared the pause preference.");
        queue.Resume();
        await DrainAsync(speech, queue, Final);
    }

    private static async Task CheckPausedCurrentPrefixAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        Publish(queue, Draft);
        Equal(await speech.TakeStartedAsync(), Draft[0]);
        queue.Pause();
        queue.ReconcileCompletedResponse(Draft, Final);
        if (speech.State != SpeechPlaybackState.Paused || !queue.State.IsPaused)
            throw new InvalidOperationException("Final revision discarded a paused unchanged current unit.");
        queue.Resume();
        speech.Complete();
        await DrainAsync(speech, queue, Final.Skip(1));
        Equal(speech.StartedCount, 4);
    }

    private static async Task CheckPreparingPrefixAsync()
    {
        using var speech = new ControlledSpeechService { DelayPreparation = true };
        using var queue = new MonitoringSpeechQueue(speech);
        Publish(queue, Draft);
        Equal(await speech.TakeStartedAsync(), Draft[0]);
        queue.ReconcileCompletedResponse(Draft, Final);
        if (speech.State != SpeechPlaybackState.Preparing)
            throw new InvalidOperationException("Final revision canceled unchanged prefix preparation.");
        speech.DelayPreparation = false;
        speech.FinishPreparation();
        speech.Complete();
        await DrainAsync(speech, queue, Final.Skip(1));
        Equal(speech.StartedCount, 4);
    }

    private static async Task CheckParkedPrefixAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        Publish(queue, Draft);
        await speech.TakeStartedAsync();
        queue.Pause();
        queue.EnqueueManualSelection("Manual interjection.");
        Equal(await speech.TakeStartedAsync(), "Manual interjection.");
        queue.ReconcileCompletedResponse(Draft, Final);
        if (speech.State != SpeechPlaybackState.Speaking || !queue.State.IsPaused)
            throw new InvalidOperationException("Final revision interrupted manual playback or resumed monitoring.");
        speech.Complete();
        await WaitUntilAsync(() => !queue.State.IsManualPlaybackActive);
        queue.Resume();
        await DrainAsync(speech, queue, Final);
        Equal(speech.StartedCount, 6); // original parked start, manual, resumed final units
    }

    private static async Task CheckParkedSuffixAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        Publish(queue, Draft);
        for (var index = 0; index < 2; index++)
        {
            Equal(await speech.TakeStartedAsync(), Draft[index]);
            speech.Complete();
        }
        Equal(await speech.TakeStartedAsync(), Draft[2]);
        queue.Pause();
        queue.EnqueueManualSelection("Manual interjection.");
        await speech.TakeStartedAsync();
        queue.ReconcileCompletedResponse(Draft, Final);
        speech.Complete();
        await WaitUntilAsync(() => !queue.State.IsManualPlaybackActive);
        queue.Resume();
        await DrainAsync(speech, queue, Final.Skip(2));
        Equal(speech.StartedCount, 6);
    }

    private static async Task CheckPreparingSuffixAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        Publish(queue, Draft);
        await speech.TakeStartedAsync();
        speech.Complete();
        await speech.TakeStartedAsync();
        speech.DelayPreparation = true;
        speech.Complete();
        Equal(await speech.TakeStartedAsync(), Draft[2]);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.SpeechResultProduced += (_, e) => { if (e.Text == Draft[2] && e.Result.Canceled) canceled.TrySetResult(); };
        queue.ReconcileCompletedResponse(Draft, Final);
        if (speech.State != SpeechPlaybackState.Idle)
            throw new InvalidOperationException("Superseded draft preparation was not stopped.");
        speech.DelayPreparation = false;
        speech.FinishPreparation(); // deliberately simulates a backend's late successful return
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await DrainAsync(speech, queue, Final.Skip(2));
    }

    private static async Task CheckBeforeStartAsync()
    {
        // The worker captures this context before the caller flag is set.
        // Its dequeue notification is synchronous and precedes TrySpeakAsync.
        var callerNotification = new AsyncLocal<bool>();
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        var reconciled = 0;
        queue.StateChanged += (_, _) =>
        {
            if (!callerNotification.Value && speech.State == SpeechPlaybackState.Idle
                && queue.State.PendingItemCount == 0
                && Interlocked.CompareExchange(ref reconciled, 1, 0) == 0)
            {
                queue.ReconcileCompletedResponse(Draft, Final);
            }
        };
        // Consume the unchanged prefix before exercising the dequeue/start seam.
        callerNotification.Value = true;
        try { queue.EnqueueCompletedResponse(Draft[2]); }
        finally { callerNotification.Value = false; }
        await DrainAsync(speech, queue, Final.Skip(2));
        Equal(reconciled, 1);
        Equal(speech.StartedCount, 2);
    }

    private static async Task CheckSpeakingSuffixAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        Publish(queue, Draft);
        for (var index = 0; index < Draft.Length; index++)
        {
            Equal(await speech.TakeStartedAsync(), Draft[index]);
            if (index < Draft.Length - 1) speech.Complete();
        }
        queue.ReconcileCompletedResponse(Draft, Final);
        if (speech.State != SpeechPlaybackState.Speaking)
            throw new InvalidOperationException("A revision cut off an already speaking draft unit.");
        speech.Complete();
        await DrainAsync(speech, queue, Final.Skip(2));
    }

    private static async Task CheckShortenedFinalAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.Pause();
        Publish(queue, Draft);
        queue.ReconcileCompletedResponse(Draft, Draft.Take(2).ToArray());
        queue.Resume();
        await DrainAsync(speech, queue, Draft.Take(2));
        Equal(speech.StartedCount, 2);
    }

    private static async Task CheckRepeatedUnitsAsync()
    {
        string[] draft = ["Repeated sentence.", "Middle sentence.", "Repeated sentence.", "Draft ending."];
        string[] final = ["Repeated sentence.", "Middle sentence.", "Repeated sentence.", "Corrected ending."];
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        Publish(queue, draft);
        Equal(await speech.TakeStartedAsync(), draft[0]);
        queue.ReconcileCompletedResponse(draft, final);
        speech.Complete();
        await DrainAsync(speech, queue, final.Skip(1));
        Equal(speech.StartedCount, 4);
    }

    private static async Task CheckContextAndCapacityAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.Pause();
        Publish(queue, Draft);
        queue.SetContextSuspended(true);
        queue.ReconcileCompletedResponse(Draft, Final);
        Equal(queue.State.PendingItemCount, 0);
        queue.SetContextSuspended(false);
        var capacityEvents = 0;
        queue.QueueCapacityReached += (_, _) => capacityEvents++;
        Publish(queue, Draft);
        queue.ReconcileCompletedResponse(Draft, [Draft[0], Draft[1], new string('x', MonitoringSpeechQueue.MaximumMonitoredQueueCharacters)]);
        Equal(capacityEvents, 1);
        Equal(queue.State.PendingItemCount, 2);
        queue.Resume();
        await DrainAsync(speech, queue, Draft.Take(2));
    }

    private static async Task DrainAsync(ControlledSpeechService speech, MonitoringSpeechQueue queue, IEnumerable<string> expected)
    {
        foreach (var unit in expected)
        {
            Equal(await speech.TakeStartedAsync(), unit);
            speech.Complete();
        }
        await WaitUntilAsync(() => !queue.State.IsSpeaking && queue.State.PendingItemCount == 0);
    }

    private static void Equal<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class ControlledSpeechService : ISpeechService
    {
        private readonly Channel<string> _started = Channel.CreateUnbounded<string>();
        private TaskCompletionSource<SpeechStartResult>? _preparation;
        public bool DelayPreparation { get; set; }
        public int StartedCount { get; private set; }
        public SpeechPlaybackState State { get; private set; }
        public event EventHandler? SpeakingStateChanged;

        public Task<SpeechStartResult> TrySpeakAsync(string text)
        {
            State = DelayPreparation ? SpeechPlaybackState.Preparing : SpeechPlaybackState.Speaking;
            StartedCount++;
            _started.Writer.TryWrite(text);
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            if (!DelayPreparation) return Task.FromResult(SpeechStartResult.Success());
            _preparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _preparation.Task;
        }

        public Task<string> TakeStartedAsync() => _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        public void FinishPreparation()
        {
            State = SpeechPlaybackState.Speaking;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            _preparation!.TrySetResult(SpeechStartResult.Success());
        }
        public void Complete()
        {
            State = SpeechPlaybackState.Idle;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        }
        public bool TryPause()
        {
            if (State != SpeechPlaybackState.Speaking) return false;
            State = SpeechPlaybackState.Paused;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        public bool TryResume()
        {
            if (State != SpeechPlaybackState.Paused) return false;
            State = SpeechPlaybackState.Speaking;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        public void Stop() => Complete();
        public void Dispose() => _started.Writer.TryComplete();
    }
}
