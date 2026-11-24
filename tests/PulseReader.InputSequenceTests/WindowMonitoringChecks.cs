using System.Threading.Channels;
using PulseReader.Hotkeys;
using PulseReader.Monitoring;
using PulseReader.Speech;

internal static class WindowMonitoringChecks
{
    // Legacy delta/queue checks opt out of sentence settlement so their timing
    // stays isolated. Settlement integration below uses production defaults.
    public static async Task RunAsync()
    {
        CheckHotkeyGestureClassification();
        MonitoringLatchSoundChecks.Run();
        await CodexLatchChecks.RunAsync();
        MonitorWorkerLifecycleChecks.Run();
        MonitorExitDiagnosticChecks.Run();
        MonitoringPerformanceDiagnosticChecks.Run();
        MonitoringWaitSchedulingChecks.Run();
        await MonitorStartupCancellationChecks.RunAsync();
        InlineHyphenChecks.Run();
        await OpeningRevisionChecks.RunAsync();
        EditableDuplicateChecks.Run();
        CodexThoughtSettlementChecks.Run();
        CodexThoughtSettlementIntegrationChecks.Run();
        DocumentMonitoringFallbackChecks.Run();
        MonitoringDocumentEditabilityChecks.Run();
        CodexCaptureScopeChecks.Run();
        CachedCodexTranscriptChecks.Run();
        DiscordMonitoringChecks.Run();
        await LmStudioMonitoringChecks.RunAsync();
        T3MonitoringChecks.Run();
        await CheckThinkingPanelPlaybackAsync();
        CheckConversationContextTransitions();
        CheckConversationContextBaseline();
        CheckFinalResponseAfterContextReturn();
        CheckLateSubmissionPreservesPendingFinal();
        CheckCodexStreamingParagraphFragments();
        await CheckStreamedAnswerFinalHandoffAsync();
        CheckStreamedAnswerHandoffBoundaries();
        await CheckPartialFinalRevisionRoutingAsync();
        await CheckLateFinalRevisionRoutingAsync();
        await CompletedResponseRevisionChecks.RunAsync();
        await CheckContextQueueCancellationAsync();
        CheckCodexConversationIdentity();
        CheckCodexWorkingPhaseAndHistory();
        CheckCodexFinalResponseSettlement();
        CheckAppendOnlyTextTracking();
        CheckRecreatedElementDeduplication();
        CheckTemporarilyHiddenContent();
        CheckOpeningWordSurvivesRedraw();
        CheckStreamingUnitAssembly();
        CheckPublishedUnitDeduplication();
        CheckSubmittedTextEchoSuppression();
        CheckVolatileReplacementSuppression();
        CheckFragmentCoalescingAndConsolidation();
        CheckObservedContentBoundaries();
        CheckInlineOpeningAggregateRecovery();
        CheckNumberedListBodyRecovery();
        CheckCompletedWorkingUpdateDoesNotEndTurn();
        CheckDiagnosticPrivacyAndCorrelation();
        await CheckPriorityPlaybackAsync();
        await CheckInitialMonitoringDelayAsync();
        await CheckPendingRedrawReplacementAsync();
        await CheckDiscreteMessageQueueAsync();
        await CheckDiscordInterjectionAsync();
        await CheckPreparedDiscordQueueAsync();
        await CheckCompletedResponseTakeoverAsync();
        await CheckPausedQueueAsync();

        Console.WriteLine(
            "PASS: hold gestures, redraw-safe text deltas, submitted-echo and volatile-text filters, streaming units, priority interjections, and monitoring pause state validate.");
    }

    private static async Task CheckThinkingPanelPlaybackAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        const string text = "First thought is ready. Second thought is ready. Third thought is ready.";
        queue.UpdateThinking(new("turn", text, false, false));
        await Task.Delay(30);
        if (queue.State.PendingItemCount != 0 || speech.StartedCount != 0)
            throw new InvalidOperationException("Collapsed thoughts were queued for speech.");
        queue.UpdateThinking(new("turn", text, true, false));
        if (await speech.TakeStartedAsync() != "First thought is ready.") throw new InvalidOperationException("Expanded thinking did not start from the beginning.");
        queue.UpdateThinking(new("turn", text, false, false));
        if (speech.State != SpeechPlaybackState.Speaking || queue.State.PendingItemCount != 0)
            throw new InvalidOperationException("Collapsing thinking cut the current sentence or left queued thoughts eligible.");
        speech.Complete();
        await Task.Delay(30);
        queue.UpdateThinking(new("turn", text, true, false));
        if (await speech.TakeStartedAsync() != "Second thought is ready.") throw new InvalidOperationException("Reopening thinking replayed a spoken sentence or skipped an unread one.");
        queue.UpdateThinking(new("turn", text, false, true));
        queue.EnqueueCompletedResponse("Final answer is ready.");
        speech.Complete();
        if (await speech.TakeStartedAsync() != "Final answer is ready.") throw new InvalidOperationException("The final answer did not supersede queued thoughts.");
        speech.Complete();
        queue.UpdateThinking(new("turn", text, true, false));
        await Task.Delay(30);
        if (queue.State.PendingItemCount != 0 || speech.StartedCount != 3)
            throw new InvalidOperationException("Opening historical thinking restarted a completed response.");

        using var delayed = new ControlledSpeechService { DelayPreparation = true };
        using var delayedQueue = new MonitoringSpeechQueue(delayed);
        delayedQueue.UpdateThinking(new("delayed", text, true, false));
        await delayed.TakeStartedAsync();
        delayedQueue.UpdateThinking(new("delayed", text, false, false));
        delayed.FinishPreparation();
        await Task.Delay(30);
        if (delayed.State != SpeechPlaybackState.Idle)
            throw new InvalidOperationException("A thought that was still preparing became audible after collapse.");
        delayed.DelayPreparation = false;
        delayedQueue.UpdateThinking(new("delayed", text, true, false));
        if (await delayed.TakeStartedAsync() != "First thought is ready.")
            throw new InvalidOperationException("Reopening skipped a thought which never began speaking.");
        delayed.Complete();
    }

    private static void CheckConversationContextTransitions()
    {
        var guard = new ConversationContextGuard();
        var now = DateTimeOffset.UtcNow;
        guard.EstablishBaseline(new("luna-view-1"));
        if (guard.Observe(new("luna-view-1"), now) != ConversationContextAction.Continue)
            throw new InvalidOperationException("Scrolling or hiding the sidebar interrupted the same conversation view.");
        if (guard.Observe(new("other-view-1"), now) != ConversationContextAction.Hold || !guard.IsSuspended)
            throw new InvalidOperationException("Switching conversations did not suspend old-context processing.");
        if (guard.Observe(new("other-view-1"), now.AddMilliseconds(500)) != ConversationContextAction.Hold
            || guard.Observe(new("other-view-1"), now.AddSeconds(2)) != ConversationContextAction.Baseline)
            throw new InvalidOperationException("A replacement view did not settle before its history was baselined.");
        if (guard.IsSuspended || guard.Observe(new("other-view-1"), now.AddSeconds(3)) != ConversationContextAction.Continue)
            throw new InvalidOperationException("A settled conversation did not resume monitoring.");
        if (guard.Observe(new(""), now.AddSeconds(4)) != ConversationContextAction.Hold)
            throw new InvalidOperationException("Settings or an unknown view remained eligible for speech.");
        if (guard.Observe(new("other-view-1"), now.AddSeconds(5)) != ConversationContextAction.Hold
            || guard.Observe(new("other-view-1"), now.AddSeconds(7)) != ConversationContextAction.Baseline)
            throw new InvalidOperationException("Returning from settings replayed unbaselined history.");
        guard.Observe(new("luna-redrawn"), now.AddSeconds(8));
        guard.Observe(new("other-view-2"), now.AddSeconds(9));
        if (guard.Observe(new("other-view-2"), now.AddMilliseconds(9500)) != ConversationContextAction.Hold)
            throw new InvalidOperationException("Rapid navigation reused another view's settlement deadline.");
        var generic = new ConversationContextGuard();
        generic.EstablishBaseline(null);
        if (generic.Observe(null, now) != ConversationContextAction.Continue)
            throw new InvalidOperationException("The Codex context guard changed generic stream monitoring.");
    }

    private static void CheckConversationContextBaseline()
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        var states = new List<bool>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        session.ContextChanged += (_, e) => states.Add(e.Suspended);
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        WindowTextSnapshot Snapshot(string view, string text) => new(
            [new("body", text)], Context: new(view));
        session.ProcessSnapshot(Snapshot("first", "Existing history."), now, ref lastChange);
        session.ProcessSnapshot(Snapshot("first", "Existing history."), now.AddSeconds(2), ref lastChange);
        session.ProcessSnapshot(Snapshot("first", "Existing history. New content."), now.AddSeconds(3), ref lastChange);
        session.ProcessSnapshot(Snapshot("second", "Other conversation history."), now.AddSeconds(4), ref lastChange);
        session.ProcessSnapshot(Snapshot("second", "Other conversation history."), now.AddSeconds(6), ref lastChange);
        session.ProcessSnapshot(Snapshot("second", "Other conversation history. Fresh update."), now.AddSeconds(7), ref lastChange);
        session.ProcessSnapshot(Snapshot("", "Settings content."), now.AddSeconds(8), ref lastChange);
        session.ProcessSnapshot(Snapshot("second", "Other conversation history. Fresh update. Added while away."), now.AddSeconds(9), ref lastChange);
        session.ProcessSnapshot(Snapshot("second", "Other conversation history. Fresh update. Added while away."), now.AddSeconds(11), ref lastChange);
        if (!spoken.SequenceEqual(new[] { "New content.", "Fresh update." })
            || !states.SequenceEqual(new[] { true, false, true, false, true, false }))
            throw new InvalidOperationException("Context transitions leaked history/settings or lost fresh content.");
    }

    private static void CheckFinalResponseAfterContextReturn()
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        WindowTextSnapshot Snapshot(string view, string cycle, string text) => new(
            [new($"codex-message:{cycle}:answer", text, GroupKey: $"codex-message:{cycle}:answer", IsAggregate: true, IsFinalResponse: true)],
            ResponseCycleKey: cycle, Context: new(view));
        session.ProcessSnapshot(Snapshot("old", "old-turn", "Old garden story."), now, ref lastChange);
        session.ProcessSnapshot(Snapshot("old", "old-turn", "Old garden story."), now.AddSeconds(2), ref lastChange);
        session.ProcessSnapshot(Snapshot("away", "other-turn", "Other history."), now.AddSeconds(3), ref lastChange);
        session.ProcessSnapshot(Snapshot("away", "other-turn", "Other history."), now.AddSeconds(5), ref lastChange);
        session.ProcessSnapshot(Snapshot("returned", "old-turn", "Old garden story."), now.AddSeconds(6), ref lastChange);
        session.ProcessSnapshot(Snapshot("returned", "old-turn", "Old garden story."), now.AddSeconds(8), ref lastChange);
        var shortAnswer = Snapshot("returned", "new-turn", "Fresh copper bells mark the");
        var complete = Snapshot("returned", "new-turn", "Fresh copper bells mark the return to this conversation. The reader should speak this new message without repeating the earlier garden story. Quiet violet lanterns mark the end of the test.");
        session.ProcessSnapshot(shortAnswer, now.AddSeconds(9), ref lastChange);
        session.ProcessSnapshot(complete, now.AddSeconds(10), ref lastChange);
        session.ProcessSnapshot(complete, now.AddSeconds(11), ref lastChange);
        session.ProcessSettled(now.AddSeconds(12), ref lastChange);
        if (spoken.Count != 3 || spoken[0] != "Fresh copper bells mark the return to this conversation.")
            throw new InvalidOperationException("A complete fresh final answer was not spoken after returning to a conversation.");
    }

    private static void CheckLateSubmissionPreservesPendingFinal()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprinter);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new WindowTextSnapshot([], ResponseCycleKey: "known-codex-turn");
        policy.EstablishBaseline(snapshot, now);
        var final = new WindowTextChange("Fresh copper bells mark the return to this conversation.", true, true,
            "codex-message:known-codex-turn:answer", GroupKey: "answer", IsFinalResponse: true);
        policy.Process(snapshot, [final], now);
        // Editable observations can arrive late, or disappear during a redraw.
        // The authoritative conversation turn identity has not changed.
        policy.ObserveEditableFingerprints([fingerprinter.Create("Composer content briefly exposed.")], now.AddMilliseconds(300));
        policy.ObserveEditableFingerprints([], now.AddMilliseconds(400));
        policy.Process(snapshot, [], now.AddMilliseconds(500));
        var ready = policy.TakeSettled(now.AddSeconds(2));
        if (ready.Count != 1 || ready[0].Text != final.Text)
            throw new InvalidOperationException("A late same-turn submission signal erased the pending final response.");
        policy.Process(snapshot, [final with { Text = "Pending old answer." }], now.AddSeconds(3));
        policy.Process(snapshot with { ResponseCycleKey = "genuinely-new-turn" }, [], now.AddSeconds(3.5));
        if (policy.TakeSettled(now.AddSeconds(6)).Count != 0)
            throw new InvalidOperationException("A genuinely new turn retained the previous answer.");
    }

    private static void CheckCodexStreamingParagraphFragments()
    {
        using var fingerprint = new WindowTextFingerprinter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var adapter = new CodexConversationAdapter { IsResponseActive = true };
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        CodexTextPart[] prompt = [new(CodexTextPartKind.UserBoundary, "You said:"),
            new(CodexTextPartKind.UserText, "Read the settings recovery fixture."),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:")];
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        void Observe(IEnumerable<CodexTextPart> parts, double seconds)
        {
            var blocks = adapter.Assemble(parts.ToArray(), fingerprint);
            session.ProcessSnapshot(new WindowTextSnapshot(blocks, ResponseCycleKey: adapter.ResponseCycleKey,
                Context: new("same-view")), now.AddSeconds(seconds), ref lastChange);
        }
        Observe(prompt, 0);
        Observe(prompt, 2);
        Observe(prompt.Concat(new[] { "The", "reader", "has", "returned" }.Select((word, i) =>
            new CodexTextPart(CodexTextPartKind.Content, word, i + 3, "paragraph"))), 3);
        if (spoken.Count != 0)
            throw new InvalidOperationException("Streaming inline words escaped as separate spoken items before the paragraph settled.");
        session.ProcessSettled(now.AddSeconds(6), ref lastChange);
        lastChange = DateTimeOffset.UtcNow.AddSeconds(-2);
        session.ProcessSettled(now.AddSeconds(7), ref lastChange);
        if (spoken.Count != 0)
            throw new InvalidOperationException("The idle timer released an unfinished Codex paragraph.");
        adapter.IsResponseActive = false;
        const string full = "The reader has returned from settings. This fresh response should play without repeating the earlier river passage. The settings recovery test is complete.";
        Observe(prompt.Append(new(CodexTextPartKind.Content, full, 3, "paragraph")), 8);
        session.ProcessSettled(now.AddSeconds(10), ref lastChange);
        if (!spoken.SequenceEqual(new[] { "The reader has returned from settings.",
                "This fresh response should play without repeating the earlier river passage.",
                "The settings recovery test is complete." }))
            throw new InvalidOperationException("The final response repeated or lost its fragmented opening.");

        using var progressSession = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var progressAdapter = new CodexConversationAdapter { IsResponseActive = true };
        var progressSpoken = new List<string>();
        progressSession.TextAvailable += (_, e) => progressSpoken.Add(e.Text);
        WindowTextSnapshot Progress(IEnumerable<CodexTextPart> content) => new(
            progressAdapter.Assemble(prompt.Concat(content).ToArray(), fingerprint),
            ResponseCycleKey: progressAdapter.ResponseCycleKey, Context: new("progress-view"));
        progressSession.ProcessSnapshot(Progress([]), now, ref lastChange);
        progressSession.ProcessSnapshot(Progress([]), now.AddSeconds(2), ref lastChange);
        var firstProgress = new[] { "The", "first", "progress", "update", "is", "ready." }.Select((word, i) =>
            new CodexTextPart(CodexTextPartKind.WorkingContent, word, 3 + i, "progress-paragraph")).ToArray();
        progressSession.ProcessSnapshot(Progress(firstProgress), now.AddSeconds(3), ref lastChange);
        if (!progressSpoken.SequenceEqual(new[] { "The first progress update is ready." }))
            throw new InvalidOperationException("Complete progress sentences were delayed or split by inline-fragment handling.");
        var secondProgress = firstProgress.Append(new CodexTextPart(CodexTextPartKind.WorkingContent,
            "Another paragraph is ready.", 20, "second-paragraph"));
        progressSession.ProcessSnapshot(Progress(secondProgress), now.AddSeconds(4), ref lastChange);
        if (!progressSpoken.SequenceEqual(new[] { "The first progress update is ready.", "Another paragraph is ready." }))
            throw new InvalidOperationException("Paragraph assembly merged or repeated genuine progress updates.");
    }

    private static async Task CheckStreamedAnswerFinalHandoffAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) =>
        {
            if (e.BeginsCompletedResponse) queue.EnqueueCompletedResponse(e.Text);
            else queue.EnqueueMonitoredText(e.Text);
        };
        string[] sentences = ["The sidebar test begins now.", "The second sentence is already playing.",
            "The third sentence is still queued.", "The fourth sentence is also queued.",
            "The final sentence has just arrived."];
        var lastChange = DateTimeOffset.MinValue;
        session.AppendChanges([new WindowTextChange(string.Join(' ', sentences[..4]), true, true,
            "codex-message:handoff:working", GroupKey: "codex-message:handoff:working")], ref lastChange);
        if (await speech.TakeStartedAsync() != sentences[0]) throw new InvalidOperationException("Handoff fixture did not begin.");
        speech.Complete();
        if (await speech.TakeStartedAsync() != sentences[1]) throw new InvalidOperationException("Handoff fixture did not advance.");
        session.AppendChanges([new WindowTextChange(string.Join(' ', sentences), true, true,
            "codex-message:handoff:answer", GroupKey: "codex-message:handoff:answer", IsFinalResponse: true)], ref lastChange);
        speech.Complete();
        foreach (var expected in sentences[2..])
        {
            var actual = await speech.TakeStartedAsync();
            if (actual != expected)
                throw new InvalidOperationException($"Streamed-to-final handoff repeated or dropped speech: expected '{expected}', got '{actual}'.");
            speech.Complete();
        }
        await Task.Delay(50);
        if (speech.StartedCount != sentences.Length)
            throw new InvalidOperationException("Streamed-to-final handoff added trailing repeated speech.");
    }

    private static void CheckStreamedAnswerHandoffBoundaries()
    {
        CheckPartialFinalRevisionSignal();
        Check("The first sentence is ready.", "The first sentence is ready.", "one", []);
        Check("The first sentence is ready.", "The first sentence is ready. The next sentence follows.", "one", ["The next sentence follows."]);
        Check("The first sentence is ready.", "The first sentence was corrected.", "one", ["The first sentence was corrected."], takeover: true);
        Check("The first sentence is ready. More detail follows.", "The first sentence is ready.", "one", [""], takeover: true);
        Check("The first sentence is ready.", "The first sentence is ready.", "two", ["The first sentence is ready."], takeover: true);
        Check("A genuine working update is ready.", "The different final answer is ready.", "one", ["The different final answer is ready."], takeover: true);
        Check("The first sentence is ready. An unfinished", "The first sentence is ready. An unfinished draft is now complete.", "one", ["An unfinished draft is now complete."]);

        static void Check(string draft, string final, string finalTurn, string[] expected, bool takeover = false)
        {
            using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
            var emitted = new List<MonitoredTextEventArgs>();
            session.TextAvailable += (_, e) => emitted.Add(e);
            var lastChange = DateTimeOffset.MinValue;
            session.AppendChanges([new WindowTextChange(draft, true, true, "codex-message:one:working",
                GroupKey: "codex-message:one:working")], ref lastChange);
            emitted.Clear();
            session.AppendChanges([new WindowTextChange(final, true, true, $"codex-message:{finalTurn}:answer",
                GroupKey: $"codex-message:{finalTurn}:answer", IsFinalResponse: true)], ref lastChange);
            if (!emitted.Select(e => e.Text).SequenceEqual(expected)
                || emitted.Count(e => e.BeginsCompletedResponse) != (takeover ? 1 : 0))
                throw new InvalidOperationException("Answer handoff crossed a turn/revision boundary or dropped an unfinished sentence.");
        }
    }

    private static void CheckPartialFinalRevisionSignal()
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var emitted = new List<MonitoredTextEventArgs>();
        session.TextAvailable += (_, e) => emitted.Add(e);
        var changedAt = DateTimeOffset.MinValue;
        const string first = "The opening sentence must only be read once.";
        const string second = "The unchanged second sentence may still be queued.";
        session.AppendChanges([new WindowTextChange(first + " " + second + " The next detail is provisional.",
            true, true, "codex-message:revision:working", GroupKey: "codex-message:revision:working")], ref changedAt);
        emitted.Clear();
        session.AppendChanges([new WindowTextChange(first + " " + second + " The next detail was corrected. A new closing sentence follows.",
            true, true, "codex-message:revision:answer", GroupKey: "codex-message:revision:answer", IsFinalResponse: true)], ref changedAt);
        if (emitted.Any(e => e.Text == first || e.Text == second))
            throw new InvalidOperationException("A final response with a later revised sentence republished its unchanged opening.");
        if (emitted is not [{ Text: "", BeginsCompletedResponse: true, CompletedRevision: { } revision }]
            || revision.DraftUnits.Count != 3 || revision.FinalUnits.Count != 4
            || revision.FinalUnits[0] != first || revision.FinalUnits[1] != second)
            throw new InvalidOperationException("A partial final revision lost the queue reconciliation payload.");
    }

    private static async Task CheckPartialFinalRevisionRoutingAsync()
    {
        foreach (var provider in new[] { "codex-message", "lmstudio-message" })
        {
            using var speech = new ControlledSpeechService();
            using var queue = new MonitoringSpeechQueue(speech);
            using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
            session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
            string[] draft = ["The opening sentence has begun playing.", "The second sentence is waiting in the queue.",
                "The third sentence still uses preliminary wording.", "The fourth sentence remains unchanged."];
            string[] final = [draft[0], draft[1], "The third sentence now uses corrected wording.", draft[3],
                "The completed answer has a new closing sentence."];
            var cycle = provider + ":partial-routing";
            WindowTextSnapshot Snapshot(string text, bool completed) => new([
                new(cycle + (completed ? ":answer" : ":working"), text,
                    ContainerKey: cycle + (completed ? ":answer" : ":working"),
                    GroupKey: cycle + (completed ? ":answer" : ":working"),
                    IsAggregate: true, IsFinalResponse: completed)], ResponseCycleKey: cycle, Context: new("view"));
            var now = DateTimeOffset.UtcNow;
            var changedAt = DateTimeOffset.MinValue;
            session.EstablishBaseline(Snapshot("", false), now);
            session.ProcessSnapshot(Snapshot(string.Join(' ', draft), false), now.AddSeconds(1), ref changedAt);
            if (await speech.TakeStartedAsync() != draft[0])
                throw new InvalidOperationException("The partial-revision routing fixture did not start its opening.");
            session.ProcessSnapshot(Snapshot(string.Join(' ', final), true), now.AddSeconds(2), ref changedAt);
            session.ProcessSettled(now.AddSeconds(4), ref changedAt);
            speech.Complete();
            foreach (var expected in final[1..])
            {
                if (await speech.TakeStartedAsync() != expected)
                    throw new InvalidOperationException(provider + ": session-to-queue revision repeated or dropped a sentence.");
                speech.Complete();
            }
            await Task.Delay(50);
            if (speech.StartedCount != final.Length || queue.State.PendingItemCount != 0)
                throw new InvalidOperationException(provider + ": session-to-queue revision left repeated trailing speech.");
        }
    }

    private static async Task CheckLateFinalRevisionRoutingAsync()
    {
        foreach (var withAppend in new[] { false, true })
        {
            using var speech = new ControlledSpeechService();
            using var queue = new MonitoringSpeechQueue(speech);
            using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
            session.TextAvailable += (_, e) => PulseReader.PulseReaderApplicationContext.EnqueueMonitoredText(queue, e);
            string[] draft = ["The opening sentence must not restart.", "The second sentence is still waiting.", "An early draft ending."];
            string[] firstFinal = [draft[0], draft[1], "The first completed ending."];
            string[] latest = withAppend
                ? [..firstFinal, "An unchanged late addition.", "The revised late ending."]
                : [draft[0], draft[1], "The revised completed ending."];
            const string cycle = "codex-message:late-revision";
            WindowTextSnapshot Snapshot(string[] units, bool final) => new([
                new(cycle + (final ? ":answer" : ":working"), string.Join(' ', units),
                    ContainerKey: cycle + (final ? ":answer" : ":working"),
                    GroupKey: cycle + (final ? ":answer" : ":working"), IsAggregate: true, IsFinalResponse: final)],
                ResponseCycleKey: cycle, Context: new("view"));
            var now = DateTimeOffset.UtcNow;
            var changedAt = DateTimeOffset.MinValue;
            session.EstablishBaseline(Snapshot([], false), now);
            session.ProcessSnapshot(Snapshot(draft, false), now.AddSeconds(1), ref changedAt);
            if (await speech.TakeStartedAsync() != draft[0])
                throw new InvalidOperationException("The late final revision fixture did not start its opening.");
            session.ProcessSnapshot(Snapshot(firstFinal, true), now.AddSeconds(2), ref changedAt);
            session.ProcessSettled(now.AddSeconds(4), ref changedAt);
            if (withAppend)
            {
                session.ProcessSnapshot(Snapshot([..firstFinal, latest[3], "The provisional late ending."], true),
                    now.AddSeconds(5), ref changedAt);
                session.ProcessSettled(now.AddSeconds(7), ref changedAt);
            }
            session.ProcessSnapshot(Snapshot(latest, true), now.AddSeconds(8), ref changedAt);
            session.ProcessSettled(now.AddSeconds(10), ref changedAt);
            speech.Complete();
            foreach (var expected in latest[1..])
            {
                var actual = await speech.TakeStartedAsync();
                if (actual != expected)
                    throw new InvalidOperationException($"A later final revision replayed or retained superseded text (append={withAppend}): expected '{expected}', got '{actual}'.");
                speech.Complete();
            }
            await Task.Delay(50);
            if (speech.StartedCount != latest.Length || queue.State.PendingItemCount != 0)
                throw new InvalidOperationException("A later final revision left repeated trailing text.");
        }
    }

    private static async Task CheckContextQueueCancellationAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.EnqueueCompletedResponse("Old context is speaking.");
        await speech.TakeStartedAsync();
        queue.EnqueueMonitoredText("Old queued text must never speak.");
        queue.SetContextSuspended(true);
        queue.EnqueueMonitoredText("Hidden settings must never speak.");
        if (speech.State != SpeechPlaybackState.Idle || queue.State.PendingItemCount != 0)
            throw new InvalidOperationException("Context suspension left old audio or queued monitoring active.");
        queue.EnqueueManualSelection("Manual reading remains available.");
        if (await speech.TakeStartedAsync() != "Manual reading remains available.")
            throw new InvalidOperationException("Context suspension blocked manual selection.");
        queue.SetContextSuspended(true);
        if (speech.State != SpeechPlaybackState.Speaking)
            throw new InvalidOperationException("Navigation interrupted unrelated manual speech.");
        speech.Complete();
        queue.Pause();
        queue.SetContextSuspended(false);
        queue.EnqueueCompletedResponse("New context is ready.");
        await Task.Delay(50);
        if (!queue.State.IsPaused || speech.StartedCount != 2)
            throw new InvalidOperationException("Returning to a conversation overrode the user's pause.");
        queue.Resume();
        if (await speech.TakeStartedAsync() != "New context is ready.")
            throw new InvalidOperationException("The new context did not resume cleanly.");
        speech.Complete();

        using var delayed = new ControlledSpeechService { DelayPreparation = true };
        using var delayedQueue = new MonitoringSpeechQueue(delayed);
        delayedQueue.EnqueueCompletedResponse("Old preparation must be canceled.");
        await delayed.TakeStartedAsync();
        delayedQueue.SetContextSuspended(true);
        delayedQueue.SetContextSuspended(false);
        delayed.FinishPreparation();
        delayed.DelayPreparation = false;
        delayedQueue.EnqueueCompletedResponse("Only the new preparation may speak.");
        if (await delayed.TakeStartedAsync() != "Only the new preparation may speak.")
            throw new InvalidOperationException("Stale preparation blocked the new context.");
        delayed.Complete();
    }

    private static void CheckCodexConversationIdentity()
    {
        if (!WindowTextSnapshotReader.IsNavigationRole("navigation")
            || !WindowTextSnapshotReader.IsNavigationRole("toolbar")
            || WindowTextSnapshotReader.IsNavigationRole("listitem")
            || WindowTextSnapshotReader.IsNavigationRole("list"))
            throw new InvalidOperationException("Navigation exclusion also suppressed conversational lists or missed sidebar ancestry.");
        using var fingerprint = new WindowTextFingerprinter();
        var adapter = new CodexConversationAdapter();
        CodexTextPart[] first = [
            new(CodexTextPartKind.UserBoundary, "You said:", 1),
            new(CodexTextPartKind.UserText, "Read the first fixture.", 2),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:", 3),
            new(CodexTextPartKind.Content, "Amber lanterns mark the beginning.", 4),
            new(CodexTextPartKind.Content, "Birch trees. The explanation must remain attached.", 5),
            new(CodexTextPartKind.FinalAnnouncement, "Amber lanterns mark the beginning. Birch trees.", 6)
        ];
        var baseline = adapter.Assemble(first, fingerprint);
        if (baseline is not [{ IsFinalResponse: true }] || baseline[0].Text != first[3].Text + "\n" + first[4].Text)
            throw new InvalidOperationException("Codex message boundaries lost the list explanation or final-response signal.");
        var tracker = new WindowTextDeltaTracker();
        tracker.EstablishBaseline(new WindowTextSnapshot(baseline));
        tracker.Update(new WindowTextSnapshot([]));
        var redrawn = adapter.Assemble(first.Where(part => part.Kind != CodexTextPartKind.FinalAnnouncement)
            .Select(part => part with { Order = part.Order + 100 }).ToArray(), fingerprint);
        if (tracker.Update(new WindowTextSnapshot(redrawn)).Count != 0)
            throw new InvalidOperationException("Recreated Codex messages replayed baseline content.");
        var second = first.Concat(first.Select(part => part with { Order = part.Order + 20 })).ToArray();
        var both = adapter.Assemble(second, fingerprint);
        var changes = tracker.Update(new WindowTextSnapshot(both));
        if (both.Count != 1 || both[0].Key == baseline[0].Key || changes.Count != 1
            || changes[0].Text != baseline[0].Text)
            throw new InvalidOperationException("A genuinely new identical prompt and reply was suppressed as old history.");
        var policy = new WindowTextChangePolicy(fingerprint);
        policy.EstablishBaseline(new WindowTextSnapshot(baseline, ResponseCycleKey: "old-turn"), DateTimeOffset.UtcNow);
        policy.Process(new WindowTextSnapshot(both, ResponseCycleKey: adapter.ResponseCycleKey), changes, DateTimeOffset.UtcNow);
        if (!policy.ResponseCycleStarted)
            throw new InvalidOperationException("A new programmatic prompt failed to start a response cycle without composer typing.");
    }

    private static void CheckCodexWorkingPhaseAndHistory()
    {
        using var fingerprint = new WindowTextFingerprinter();
        var adapter = new CodexConversationAdapter { IsResponseActive = true };
        CodexTextPart[] first = [new(CodexTextPartKind.UserBoundary, "You said:"),
            new(CodexTextPartKind.UserText, "First controlled prompt."),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new(CodexTextPartKind.Content, "The first working sentence is ready.")];
        var firstBlocks = adapter.Assemble(first, fingerprint);
        if (firstBlocks is not [{ IsFinalResponse: false }] || !firstBlocks[0].Key.EndsWith(":working"))
            throw new InvalidOperationException("An active commentary heading was mistaken for a final answer.");
        var tracker = new WindowTextDeltaTracker();
        tracker.EstablishBaseline(new WindowTextSnapshot(firstBlocks));
        var moved = first[..2].Concat(new CodexTextPart[] {
            new(CodexTextPartKind.WorkingContent, first[3].Text),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new(CodexTextPartKind.Content, "The second working sentence is ready.") }).ToArray();
        var movedBlocks = adapter.Assemble(moved, fingerprint);
        var extension = tracker.Update(new WindowTextSnapshot(movedBlocks));
        if (extension.Count != 1 || extension[0].Text.Trim() != "The second working sentence is ready.")
            throw new InvalidOperationException("Moving commentary into the working section queued the first sentence twice.");
        adapter.IsResponseActive = false;
        var final = moved[..^2].Concat(new CodexTextPart[] {
            new(CodexTextPartKind.WorkingContent, moved[^1].Text),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new(CodexTextPartKind.Content, "The genuine final answer is ready.") }).ToArray();
        var finalBlocks = adapter.Assemble(final, fingerprint);
        if (finalBlocks.Count != 2 || !finalBlocks[1].IsFinalResponse || finalBlocks[0].Text != movedBlocks[0].Text)
            throw new InvalidOperationException("Final takeover changed already captured working text.");
        adapter.IsResponseActive = true;
        var stillFinal = adapter.Assemble(final, fingerprint);
        if (!stillFinal[^1].IsFinalResponse)
            throw new InvalidOperationException("A new run starting in the composer reclassified an older finished answer as working text.");
        adapter.IsResponseActive = null;
        var previousKey = adapter.ResponseCycleKey;
        var secondTurn = final.Concat(new CodexTextPart[] {
            new(CodexTextPartKind.UserBoundary, "You said:"),
            new(CodexTextPartKind.UserText, "Second controlled prompt."),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new(CodexTextPartKind.Content, "A different final answer.") }).ToArray();
        adapter.Assemble(secondTurn, fingerprint);
        if (adapter.Assemble(secondTurn, fingerprint).Any(block => block.IsFinalResponse))
            throw new InvalidOperationException("An unknown composer state was treated as confirmed completion.");
        if (adapter.ResponseCycleKey == previousKey || adapter.Assemble(final, fingerprint).Count != 0
            || adapter.ResponseCycleKey == previousKey)
            throw new InvalidOperationException("A transient snapshot moved monitoring backwards to an older turn.");
    }

    private static void CheckCodexFinalResponseSettlement()
    {
        var gate = new CodexFinalResponseGate();
        var now = DateTimeOffset.UtcNow;
        var opening = new WindowTextChange("The final answer begins.", true, true,
            "codex-message:fixture:answer", "answer", GroupKey: "answer", IsFinalResponse: true);
        var full = opening with { Text = opening.Text + " The complete list follows." };
        if (gate.Observe([opening], now).Count != 0 || gate.TakeReady(now.AddSeconds(1)).Count != 0)
            throw new InvalidOperationException("A streaming final answer escaped its initial settlement interval.");
        gate.Observe([full], now.AddSeconds(1));
        if (gate.TakeReady(now.AddSeconds(2)).Count != 0)
            throw new InvalidOperationException("A changed final answer was not given a fresh settlement interval.");
        var ready = gate.TakeReady(now.AddSeconds(3));
        if (ready is not [{ IsFinalResponse: true }] || ready[0].Text != full.Text)
            throw new InvalidOperationException("The complete settled final answer did not trigger takeover once.");
        gate.Observe([full with { IsCompletedAggregate = true }], now.AddSeconds(4));
        if (gate.TakeReady(now.AddSeconds(6)).Count != 0)
            throw new InvalidOperationException("A late completion announcement repeated a published answer.");
        gate.Observe([full with { Text = full.Text + " A late final paragraph." }], now.AddSeconds(7));
        var tail = gate.TakeReady(now.AddSeconds(9));
        if (tail is not [{ IsFinalResponse: false }] || tail[0].Text != " A late final paragraph.")
            throw new InvalidOperationException("A final-answer extension repeated its previously published body.");
        gate.Reset();
        gate.Observe([full], now.AddSeconds(10));
        if (gate.TakeReady(now.AddSeconds(12)).Count != 1)
            throw new InvalidOperationException("A new response cycle suppressed an intentional repeated answer.");

        var confirmed = full with { IsConfirmedFinalHandoff = true };
        if (gate.Observe([confirmed], now.AddSeconds(13)).Count != 0)
            throw new InvalidOperationException("A confirmed handoff replayed a final that had already settled.");
        gate.Reset();
        gate.Observe([full], now.AddSeconds(14));
        if (gate.Observe([confirmed], now.AddSeconds(14.1)) is not [{ IsFinalResponse: true }]
            || gate.TakeReady(now.AddSeconds(16)).Count != 0)
            throw new InvalidOperationException("The next live continuation did not release a pending final exactly once.");

        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var lastChange = DateTimeOffset.MinValue;
        session.AppendChanges(ready, ref lastChange);
        session.AppendChanges(tail, ref lastChange);
        session.AppendChanges([new WindowTextChange("Stale working update.", true, true,
            "codex-message:fixture:working", "working", GroupKey: "working")], ref lastChange);
        if (spoken.Count != 3 || spoken[^1] != "A late final paragraph.")
            throw new InvalidOperationException("Final takeover lost a same-answer extension or admitted stale working content.");
    }

    private static void CheckObservedContentBoundaries()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var visibleBlocks = new[]
        {
            new WindowTextBlock(
                "older-closing",
                "The test ends with this closing sentence.",
                "older-container",
                0,
                GroupKey: "response"),
            new WindowTextBlock(
                "opening",
                "The monitoring test begins with this opening sentence.",
                "opening-container",
                17,
                GroupKey: "response"),
            new WindowTextBlock(
                "closing",
                "The test ends with this closing sentence.",
                "closing-container",
                18,
                GroupKey: "response")
        };
        var aggregateBlocks = new[]
        {
            new WindowTextBlock(
                "heading-label",
                "Assistant said",
                "hidden-heading",
                16,
                GroupKey: "accessibility-response"),
            new WindowTextBlock(
                "complete-response",
                "Response complete\nThe monitoring test begins with this opening sentence.\nAlpha is the first list item.\nBravo is the second list item.\nThe test ends with this closing sentence.",
                "hidden-response",
                16,
                GroupKey: "accessibility-response")
        };
        var preferredBlocks = WindowTextSnapshotReader.PreferAccessibleGroupAggregates(
            visibleBlocks,
            aggregateBlocks,
            fingerprinter);
        var completedAggregateAfterVirtualization =
            WindowTextSnapshotReader.PreferAccessibleGroupAggregates(
                [
                    new WindowTextBlock(
                        "closing-only",
                        "Read the bottom part.",
                        "closing-container",
                        18,
                        GroupKey: "visible-response")
                ],
                [
                    new WindowTextBlock(
                        "completed-after-virtualization",
                        "Response complete\nRead the top part.\nFirst bullet item.\nSecond bullet item.\nRead the bottom part.",
                        "hidden-response",
                        17,
                        GroupKey: "accessibility-response",
                        IsCompletedAggregate: true)
                ],
                fingerprinter);
        var completedResponseWithoutPartialPrefix =
            WindowTextSnapshotReader.RemoveNonverbalEdgeLabels(
                "Response complete\nCommitted as\nCommitted as 41a690f Release audio output before monitored handoff.");
        if (completedResponseWithoutPartialPrefix !=
            "Committed as 41a690f Release audio output before monitored handoff.")
        {
            throw new InvalidOperationException(
                $"A completed response retained its provisional opening: '{completedResponseWithoutPartialPrefix}'.");
        }

        var completedResponseWithParagraphBreak =
            WindowTextSnapshotReader.RemoveNonverbalEdgeLabels(
                "Response complete\nSummary\n\nThe completed response keeps its paragraph break.");
        if (completedResponseWithParagraphBreak !=
            "Summary\n\nThe completed response keeps its paragraph break.")
        {
            throw new InvalidOperationException(
                "Completed-response cleanup changed intentional paragraph spacing.");
        }

        if (WindowTextSnapshotReader.ShouldIncludeObservedText(
                "14:14",
                editable: false,
                interactiveChrome: false,
                accessibilityOnly: false,
                auxiliaryChrome: false)
            || WindowTextSnapshotReader.ShouldIncludeObservedText(
                "Edited files",
                editable: false,
                interactiveChrome: true,
                accessibilityOnly: false,
                auxiliaryChrome: false)
            || WindowTextSnapshotReader.ShouldIncludeObservedText(
                "Assistant said",
                editable: false,
                interactiveChrome: false,
                accessibilityOnly: true,
                auxiliaryChrome: false)
            || WindowTextSnapshotReader.ShouldIncludeObservedText(
                "Running a diagnostic command",
                editable: false,
                interactiveChrome: false,
                accessibilityOnly: false,
                auxiliaryChrome: true)
            || WindowTextSnapshotReader.ShouldIncludeObservedText(
                "Thought for 5s",
                editable: false,
                interactiveChrome: false,
                accessibilityOnly: false,
                auxiliaryChrome: false)
            || WindowTextSnapshotReader.ShouldIncludeObservedText(
                "Context automatically compacted",
                editable: false,
                interactiveChrome: false,
                accessibilityOnly: false,
                auxiliaryChrome: false)
            || WindowTextSnapshotReader.ShouldIncludeObservedText(
                "Response complete",
                editable: false,
                interactiveChrome: false,
                accessibilityOnly: false,
                auxiliaryChrome: false)
            || !WindowTextSnapshotReader.IsAuxiliaryClassName(
                "loading-shimmer-pure-text")
            || !WindowTextSnapshotReader.IsAuxiliaryClassName(
                "group/activity-header")
            || !WindowTextSnapshotReader.IsAuxiliaryClassName(
                "group/turn-diff-header relative")
            || !WindowTextSnapshotReader.IsAuxiliaryClassName(
                "bg-user-message text-user-message")
            || WindowTextSnapshotReader.IsAuxiliaryClassName(
                "ordinary response paragraph")
            || WindowTextSnapshotReader.ShouldIncludeObservedText(
                "Draft message",
                editable: true,
                interactiveChrome: false,
                accessibilityOnly: false,
                auxiliaryChrome: false)
            || !WindowTextSnapshotReader.ShouldIncludeObservedText(
                "The response arrived at 14:14.",
                editable: false,
                interactiveChrome: false,
                accessibilityOnly: false,
                auxiliaryChrome: false)
            || !WindowTextSnapshotReader.ShouldIncludeObservedText(
                "The response arrived.",
                editable: false,
                interactiveChrome: false,
                accessibilityOnly: false,
                auxiliaryChrome: false)
            || preferredBlocks is not
                [{ Key: "complete-response", Order: 17, IsStatus: false }]
            || preferredBlocks[0].Text !=
                "The monitoring test begins with this opening sentence.\nAlpha is the first list item.\nBravo is the second list item.\nThe test ends with this closing sentence."
            || completedAggregateAfterVirtualization is not
                [{ Key: "completed-after-virtualization", IsAggregate: true, IsCompletedAggregate: true }]
            || completedAggregateAfterVirtualization[0].Text !=
                "Read the top part.\nFirst bullet item.\nSecond bullet item.\nRead the bottom part."
            || WindowTextSnapshotReader.RemoveNonverbalEdgeLabels(
                "The response is visible. Response complete") !=
                "The response is visible."
            || WindowTextSnapshotReader.RemoveNonverbalEdgeLabels(
                "Context automatically compacted The response is visible.") !=
                "The response is visible."
            || WindowTextSnapshotReader.RemoveNonverbalEdgeLabels(
                "Response complete The ordinary visible response follows.") !=
                "The ordinary visible response follows."
            || WindowTextSnapshotReader.RemoveNonverbalEdgeLabels(
                "\u200BResponse\u00A0complete:\u200BThe ordinary visible response follows.") !=
                "The ordinary visible response follows."
            || WindowTextSnapshotReader.RemoveNonverbalEdgeLabels(
                "The ordinary visible response comes first — response complete:") !=
                "The ordinary visible response comes first")
        {
            throw new InvalidOperationException(
                "Window monitoring did not separate conversation content from timestamps, interactive chrome, hidden labels, or editable text.");
        }
    }

    private static void CheckInlineOpeningAggregateRecovery()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        const string full = "Committed as e58d0dc Preserve monitored response openings and final handoffs.\nThe worktree is clean.\nThe full Debug suite passed.\nPulse Reader stayed running throughout this commit.\nThis final response should be read once, in order, then stop without adding older text.";
        var recovered = WindowTextSnapshotReader.PreferAccessibleGroupAggregates([
            new WindowTextBlock("prefix", "Committed as", "opening", 16, GroupKey: "visible"),
            new WindowTextBlock("hash", "e58d0dc", "opening", 17, GroupKey: "visible"),
            new WindowTextBlock("title", "Preserve monitored response openings and final handoffs.", "opening", 18, GroupKey: "visible"),
            new WindowTextBlock("closing", "This final response should be read once, in order, then stop without adding older text.", "closing", 19, GroupKey: "visible")
        ], [new WindowTextBlock("summary", full, "hidden", 15, GroupKey: "accessible", IsCompletedAggregate: true, IsFinalResponse: true)], fingerprinter);
        if (recovered is not [{ IsAggregate: true, IsCompletedAggregate: true, IsFinalResponse: true }] || recovered[0].Text != full)
        {
            throw new InvalidOperationException("Inline opening fragments prevented recovery of the final response and its three bullets.");
        }

        var tracker = new WindowTextDeltaTracker();
        var draft = new WindowTextBlock("response", full, "container", GroupKey: "answer", IsAggregate: true);
        tracker.EstablishBaseline(new WindowTextSnapshot([draft]));
        var completed = tracker.Update(new WindowTextSnapshot([draft with { IsCompletedAggregate = true, IsFinalResponse = true }]));
        if (completed is not [{ IsCompletedAggregate: true, IsFinalResponse: true }] || completed[0].Text != full)
        {
            throw new InvalidOperationException("A completion signal without text changes failed to publish the full final response.");
        }
        if (tracker.Update(new WindowTextSnapshot([draft with { IsCompletedAggregate = true, IsFinalResponse = true }])).Count != 0)
        {
            throw new InvalidOperationException("An unchanged final response triggered takeover twice.");
        }

        var policy = new WindowTextChangePolicy(fingerprinter);
        var now = DateTimeOffset.UtcNow;
        var draftSnapshot = new WindowTextSnapshot([draft]);
        policy.EstablishBaseline(new WindowTextSnapshot([]), now);
        policy.Process(draftSnapshot, [new WindowTextChange(full, true, true, "response", "container")], now);
        policy.Process(draftSnapshot, [new WindowTextChange("Unfinished working update", true, true, "pending", "working")], now.AddMilliseconds(100));
        var finalChanges = policy.Process(new WindowTextSnapshot([draft with { IsCompletedAggregate = true, IsFinalResponse = true }]), completed, now.AddSeconds(1));
        if (finalChanges is not [{ IsCompletedAggregate: true }] || finalChanges[0].Text != full)
        {
            throw new InvalidOperationException("Text deduplication swallowed the completion event before queue takeover.");
        }
        if (policy.TakeSettled(now.AddSeconds(5)).Count != 0)
        {
            throw new InvalidOperationException("A pending working update escaped after completion.");
        }
    }

    private static void CheckNumberedListBodyRecovery()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        const string opening = "I’d put a branch-wide audit next. We’ve fixed several interacting behaviours, and the happy path now sounds right. It’s worth checking the whole design before adding more rules.";
        string[] items = [
            "Response tracking. Make sure each update belongs to the correct response, including quick submissions, steering midway through playback, and opening older working sections. That should reduce our reliance on text-matching heuristics.",
            "Queue management. Decide what happens when incoming text outpaces speech for several minutes. Final-answer priority helps, but general feeds also need a sensible backlog policy.",
            "Monitoring cost. Measure CPU and memory while idle and while text streams in. Check that retained text and diagnostics stay bounded during long sessions.",
            "Other applications. Test another chat app and a general text feed. Final-answer detection currently depends on the Codex layout we observed; other apps should still work as ordinary streams."
        ];
        const string closing = "I’d keep the rare compaction announcement and isolated audio noise lower priority unless they recur.\nBefore making changes, though, I’d hear your ideas. They may affect how we approach response tracking or the queue.";
        var headings = items.Select(text => text[..(text.IndexOf('.') + 1)]).ToArray();
        var visible = new List<WindowTextBlock> {
            new("opening", opening, "opening", 16, GroupKey: "prose"),
            new("intro", "My priorities would be:", "intro", 17, GroupKey: "prose")
        };
        visible.AddRange(items.Select((text, i) => new WindowTextBlock($"item-{i}", text,
            "list", 18 + i, GroupKey: "list-group", ListItemHeading: headings[i])));
        visible.Add(new("closing", closing, "closing", 22, GroupKey: "prose"));
        var partial = opening + "\nMy priorities would be:\n" + string.Join('\n', headings) + "\n" + closing;
        var expected = opening + "\nMy priorities would be:\n" + string.Join('\n', items) + "\n" + closing;
        var recovered = WindowTextSnapshotReader.PreferAccessibleGroupAggregates(visible,
            [new("summary", partial, "hidden", 15, GroupKey: "accessible", IsCompletedAggregate: true, IsFinalResponse: true)], fingerprinter);
        if (recovered is not [{ IsFinalResponse: true }] || recovered[0].Text != expected)
            throw new InvalidOperationException("Numbered-list recovery omitted explanatory bodies or changed response order.");
        var sameGroup = WindowTextSnapshotReader.PreferAccessibleGroupAggregates(visible,
            [new("summary", partial, "hidden", 15, GroupKey: "prose", IsCompletedAggregate: true, IsFinalResponse: true)], fingerprinter);
        if (sameGroup is not [{ IsFinalResponse: true }] || sameGroup[0].Text != expected)
            throw new InvalidOperationException("A shared prose group duplicated or displaced nested numbered items.");

        var streamingPartial = partial.Replace(headings[0], headings[0] + " Make sure each update", StringComparison.Ordinal);
        var preservedPartial = WindowTextSnapshotReader.PreferAccessibleGroupAggregates(visible,
            [new("summary", streamingPartial, "hidden", 15, GroupKey: "accessible", IsCompletedAggregate: true)], fingerprinter);
        if (!preservedPartial.Select(b => b.Text).SequenceEqual(visible.Select(b => b.Text)))
            throw new InvalidOperationException("List-body recovery spliced complete prose over a partially populated summary.");

        var tracker = new WindowTextDeltaTracker();
        tracker.EstablishBaseline(new WindowTextSnapshot([]));
        var policy = new WindowTextChangePolicy(fingerprinter);
        var now = DateTimeOffset.UtcNow;
        policy.EstablishBaseline(new WindowTextSnapshot([]), now);
        var snapshot = new WindowTextSnapshot(recovered);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var lastChange = DateTimeOffset.MinValue;
        session.AppendChanges(policy.Process(snapshot, tracker.Update(snapshot), now), ref lastChange);
        var accumulator = new WindowTextAccumulator();
        accumulator.Append(new WindowTextChange(expected, true, true));
        var expectedUnits = accumulator.TakeCompletedUnits(true);
        if (!spoken.SequenceEqual(expectedUnits))
            throw new InvalidOperationException("Recovered numbered reply lost its opening, list bodies, or closing in the speech pipeline.");

        var unknownHeading = visible.Select(block => block with { ListItemHeading = "" }).ToArray();
        var retained = WindowTextSnapshotReader.PreferAccessibleGroupAggregates(unknownHeading,
            [new("summary", partial, "hidden", 15, GroupKey: "accessible", IsCompletedAggregate: true)], fingerprinter);
        if (!retained.Select(b => b.Text).SequenceEqual(unknownHeading.Select(b => b.Text)))
            throw new InvalidOperationException("An incomplete summary reordered list bodies it could not recover.");
    }

    private static void CheckCompletedWorkingUpdateDoesNotEndTurn()
    {
        if (WindowTextSnapshotReader.IsFinalResponseContext("group flex min-w-0 flex-col", true)
            || WindowTextSnapshotReader.IsFinalResponseContext("relative shrink-0", false)
            || WindowTextSnapshotReader.IsFinalResponseContext("unknown", true)
            || !WindowTextSnapshotReader.IsFinalResponseContext("relative shrink-0", true))
        {
            throw new InvalidOperationException("Working updates and unknown providers were not distinguished from the captured final-answer layout.");
        }
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<MonitoredTextEventArgs>();
        session.TextAvailable += (_, e) => spoken.Add(e);
        var lastChange = DateTimeOffset.MinValue;
        session.AppendChanges([new WindowTextChange("The first working update is complete.", true, true,
            "working-1", "working-1", IsCompletedAggregate: true)], ref lastChange);
        session.AppendChanges([new WindowTextChange("The second working update should still be spoken.", true, true,
            "working-2", "working-2")], ref lastChange);
        session.AppendChanges([new WindowTextChange("The third working update should also be spoken.", true, true,
            "working-3", "working-3")], ref lastChange);
        if (spoken.Count != 3 || spoken.Any(e => e.BeginsCompletedResponse))
        {
            throw new InvalidOperationException("A completed working block suppressed later updates or triggered final takeover.");
        }
        session.AppendChanges([new WindowTextChange("The final answer begins. First bullet. Second bullet. Closing sentence.", true, true,
            "final", "final", IsCompletedAggregate: true, IsFinalResponse: true)], ref lastChange);
        if (spoken.Count != 7 || !spoken[3].BeginsCompletedResponse
            || spoken.Skip(4).Any(e => e.BeginsCompletedResponse))
        {
            throw new InvalidOperationException("The actual final answer failed to take over exactly once with all its units.");
        }
        session.AppendChanges([new WindowTextChange("A stale working fragment.", true, true)], ref lastChange);
        if (spoken.Count != 7)
        {
            throw new InvalidOperationException("Stale working text returned after the final answer.");
        }
    }

    private static void CheckDiagnosticPrivacyAndCorrelation()
    {
#if DEBUG
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"pulse-reader-monitor-diagnostics-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "diagnostic.jsonl");
        const string privateText = "A private diagnostic sentence must never reach the log.";
        const string privateError = "Private backend failure.";
        try
        {
            using (var diagnostics = WindowMonitoringDiagnosticLog.CreateFromArguments(
                       [WindowMonitoringDiagnosticLog.ArgumentName, path])
                   ?? throw new InvalidOperationException("Diagnostic arguments were not recognised."))
            {
                var observation = new WindowTextDiagnosticObservation(
                    "private-key",
                    privateText,
                    Editable: true,
                    Included: false,
                    ControlType: 50020,
                    ClassName: "TestEditor",
                    AriaRole: "textbox",
                    AutomationId: "private-editor",
                    Left: 10,
                    Top: 20,
                    Right: 210,
                    Bottom: 80,
                    Ancestors: Array.Empty<WindowTextDiagnosticAncestor>());
                diagnostics.RecordSnapshot(
                    "test",
                    new WindowTextSnapshot(Array.Empty<WindowTextBlock>(), [observation]),
                    [new WindowTextChange(privateText, true, true)]);
                diagnostics.RecordSnapshot(
                    "unchanged",
                    new WindowTextSnapshot(Array.Empty<WindowTextBlock>(), [observation]),
                    Array.Empty<WindowTextChange>());
                diagnostics.RecordPublishedUnits([privateText]);
                diagnostics.RecordSpeechResult(
                    MonitoringSpeechItemKind.Monitored,
                    privateText,
                    SpeechStartResult.Failure(privateError));
            }

            var lines = File.ReadAllLines(path);
            var content = string.Join(Environment.NewLine, lines);
            if (content.Contains(privateText, StringComparison.Ordinal)
                || content.Contains(privateError, StringComparison.Ordinal)
                || content.Contains("private-key", StringComparison.Ordinal)
                || content.Contains("private-editor", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Window-monitoring diagnostics wrote private text or identifiers to disk.");
            }

            var snapshotLine = lines.First(line =>
                line.Contains("\"kind\":\"snapshot\"", StringComparison.Ordinal)
                && line.Contains("\"reason\":\"test\"", StringComparison.Ordinal));
            using var snapshot = System.Text.Json.JsonDocument.Parse(snapshotLine);
            var root = snapshot.RootElement;
            var observationHash = root.GetProperty("observations")[0]
                .GetProperty("text")
                .GetProperty("hash")
                .GetString();
            var changeHash = root.GetProperty("changes")[0]
                .GetProperty("text")
                .GetProperty("hash")
                .GetString();
            if (string.IsNullOrWhiteSpace(observationHash)
                || observationHash != changeHash)
            {
                throw new InvalidOperationException(
                    "Diagnostic fingerprints could not correlate identical text within one run.");
            }

            var unchangedSnapshotLine = lines.First(line =>
                line.Contains("\"kind\":\"snapshot\"", StringComparison.Ordinal)
                && line.Contains("\"reason\":\"unchanged\"", StringComparison.Ordinal));
            using var unchangedSnapshot = System.Text.Json.JsonDocument.Parse(
                unchangedSnapshotLine);
            if (unchangedSnapshot.RootElement.GetProperty("observations").GetArrayLength() != 0)
            {
                throw new InvalidOperationException(
                    "Window-monitoring diagnostics repeated an unchanged accessibility tree.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
#endif
    }

    private static void CheckSubmittedTextEchoSuppression()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprinter);
        var now = DateTimeOffset.UtcNow;
        const string submitted = "Please explain why the monitor repeated this prompt.";
        policy.EstablishBaseline(
            new WindowTextSnapshot(
                Array.Empty<WindowTextBlock>(),
                EditableFingerprints: [fingerprinter.Create(submitted)]),
            now);

        var echoed = policy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [
                new WindowTextChange(
                    submitted,
                    IsNewBlock: true,
                    StartsAtBoundary: true,
                    Key: "submitted-message",
                    ContainerKey: "conversation",
                    Order: 1)
            ],
            now + TimeSpan.FromMilliseconds(100));
        var responseCycleStarted = policy.ResponseCycleStarted;
        var response = policy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [
                new WindowTextChange(
                    "The assistant response remains readable.",
                    IsNewBlock: true,
                    StartsAtBoundary: true,
                    Key: "assistant-message",
                    ContainerKey: "response",
                    Order: 2)
            ],
            now + TimeSpan.FromMilliseconds(200));

        var splitPolicy = new WindowTextChangePolicy(fingerprinter);
        splitPolicy.EstablishBaseline(
            new WindowTextSnapshot(
                Array.Empty<WindowTextBlock>(),
                EditableFingerprints: [fingerprinter.Create(submitted)]),
            now);
        var splitEcho = splitPolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [
                new WindowTextChange(
                    "Please explain why the monitor",
                    true,
                    true,
                    "submitted-part-1",
                    "submitted-message",
                    1),
                new WindowTextChange(
                    "repeated this prompt.",
                    true,
                    true,
                    "submitted-part-2",
                    "submitted-message",
                    2)
            ],
            now + TimeSpan.FromMilliseconds(100));

        var excludedEchoPolicy = new WindowTextChangePolicy(fingerprinter);
        excludedEchoPolicy.EstablishBaseline(
            new WindowTextSnapshot(
                Array.Empty<WindowTextBlock>(),
                EditableFingerprints: [fingerprinter.Create("OK, commit that.")]),
            now);
        var responseWithoutVisibleEcho = excludedEchoPolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [
                new WindowTextChange(
                    "The immediate assistant response remains readable.",
                    true,
                    true,
                    "immediate-response",
                    "response",
                    1)
            ],
            now + TimeSpan.FromMilliseconds(100));

        var typingPolicy = new WindowTextChangePolicy(fingerprinter);
        typingPolicy.EstablishBaseline(new WindowTextSnapshot([], EditableFingerprints:
            [fingerprinter.Create("Still typing")]), now);
        typingPolicy.Process(new WindowTextSnapshot([], EditableFingerprints:
            [fingerprinter.Create("Still typing this message")]), [], now.AddMilliseconds(100));
        if (typingPolicy.ResponseCycleStarted)
        {
            throw new InvalidOperationException("Editing a draft reset the completed response history before submission.");
        }

        if (echoed.Count != 0
            || splitEcho.Count != 0
            || response is not [{ Text: "The assistant response remains readable." }]
            || !responseCycleStarted
            || !splitPolicy.ResponseCycleStarted
            || responseWithoutVisibleEcho is not
                [{ Text: "The immediate assistant response remains readable." }]
            || !excludedEchoPolicy.ResponseCycleStarted)
        {
            throw new InvalidOperationException(
                "Submitted editable text was not suppressed independently of assistant output.");
        }
    }

    private static void CheckVolatileReplacementSuppression()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var now = DateTimeOffset.UtcNow;
        var volatilePolicy = new WindowTextChangePolicy(fingerprinter);
        volatilePolicy.EstablishBaseline(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            now);
        var first = volatilePolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [Replacement("Thinking for 1 second", "timer")],
            now);
        var second = volatilePolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [Replacement("Thinking for 2 seconds", "timer")],
            now + TimeSpan.FromSeconds(1));
        var later = volatilePolicy.TakeSettled(now + TimeSpan.FromSeconds(4));

        var stablePolicy = new WindowTextChangePolicy(fingerprinter);
        stablePolicy.EstablishBaseline(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            now);
        var held = stablePolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [Replacement("Connection restored.", "connection")],
            now);
        var settled = stablePolicy.TakeSettled(now + TimeSpan.FromSeconds(3));
        var status = stablePolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [
                new WindowTextChange(
                    "The final response is ready.",
                    IsNewBlock: true,
                    StartsAtBoundary: true,
                    Key: "status",
                    ContainerKey: "status-container",
                    IsStatus: true)
            ],
            now + TimeSpan.FromSeconds(4));

        if (first.Count != 0
            || second.Count != 0
            || later.Count != 0
            || held.Count != 0
            || settled is not [{ Text: "Connection restored." }]
            || status is not [{ Text: "The final response is ready." }])
        {
            throw new InvalidOperationException(
                "Volatile text was spoken or stable replacement and final status text was lost.");
        }

        static WindowTextChange Replacement(string text, string key) =>
            new(
                text,
                IsNewBlock: true,
                StartsAtBoundary: true,
                Key: key,
                ContainerKey: "status-container",
                IsReplacement: true);
    }

    private static void CheckFragmentCoalescingAndConsolidation()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprinter);
        var now = DateTimeOffset.UtcNow;
        policy.EstablishBaseline(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            now);
        var fragments = new[]
        {
            Fragment("The", "word-1", 1),
            Fragment("answer", "word-2", 2),
            Fragment("arrived.", "word-3", 3)
        };
        var first = policy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            fragments,
            now);
        var consolidated = policy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [Fragment("The answer arrived.", "paragraph", 1, "consolidated-paragraph")],
            now + TimeSpan.FromMilliseconds(100));

        var timestampFragmentFilter = new ProvisionalTextChangeFilter(fingerprinter);
        var heldTimestampFragment = timestampFragmentFilter.Filter(
            [
                new WindowTextChange(
                    "Leave monitoring active until it finishes 14:21",
                    IsNewBlock: true,
                    StartsAtBoundary: true,
                    Key: "timestamp-fragment",
                    ContainerKey: "provisional",
                    IsProvisional: true)
            ],
            now);
        var completedParagraph = timestampFragmentFilter.Filter(
            [
                new WindowTextChange(
                    "This response should be spoken once. Leave monitoring active until it finishes.",
                    IsNewBlock: true,
                    StartsAtBoundary: true,
                    Key: "completed-paragraph",
                    ContainerKey: "completed")
            ],
            now + TimeSpan.FromMilliseconds(100));
        var lateTimestampFragment = timestampFragmentFilter.TakeSettled(
            now + TimeSpan.FromSeconds(2));

        var orderedFilter = new ProvisionalTextChangeFilter(fingerprinter);
        var heldFirstLine = orderedFilter.Filter(
            [
                new WindowTextChange(
                    "Committed as fba444c.",
                    IsNewBlock: true,
                    StartsAtBoundary: true,
                    Key: "inline-code-fragment",
                    ContainerKey: "first-line",
                    IsProvisional: true)
            ],
            now);
        var heldFollowingParagraph = orderedFilter.Filter(
            [
                new WindowTextChange(
                    "The worktree is clean.",
                    IsNewBlock: true,
                    StartsAtBoundary: true,
                    Key: "following-paragraph",
                    ContainerKey: "second-line")
            ],
            now + TimeSpan.FromMilliseconds(100));
        var orderedRelease = orderedFilter.TakeSettled(now + TimeSpan.FromSeconds(2));

        var groupedPolicy = new WindowTextChangePolicy(fingerprinter);
        groupedPolicy.EstablishBaseline(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            now);
        var groupedResponse = groupedPolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [
                new WindowTextChange(
                    "The final response begins.",
                    true,
                    true,
                    "paragraph-1",
                    "paragraph-container-1",
                    10,
                    GroupKey: "response-group"),
                new WindowTextChange(
                    "The first list item follows.",
                    true,
                    true,
                    "paragraph-2",
                    "paragraph-container-2",
                    11,
                    GroupKey: "response-group")
            ],
            now);

        var redrawFilter = new ConsolidatedTextFilter(fingerprinter);
        var originalRedraw = redrawFilter.Filter(
            [Fragment(
                "The monitor reads this progress update while work continues.",
                "progress-original",
                1,
                "progress-container-original")],
            now);
        var revisedRedraw = redrawFilter.Filter(
            [Fragment(
                    "The monitor reads the progress update while work continues.",
                    "progress-revised",
                    1,
                    "progress-container-revised")
                with
                {
                    IsProvisional = true
                }],
            now + TimeSpan.FromMilliseconds(100));

        var insertedContentFilter = new ConsolidatedTextFilter(fingerprinter);
        var provisionalPrefix = insertedContentFilter.Filter(
            [
                new WindowTextChange(
                    "marker The response begins. The first item is ready.",
                    true,
                    true,
                    "provisional-prefix",
                    "provisional-prefix-container",
                    IsProvisional: true)
            ],
            now);
        var completedAfterInsertion = insertedContentFilter.Filter(
            [
                new WindowTextChange(
                    "The response begins. The first item is ready. The second item is ready. The closing paragraph follows.",
                    true,
                    true,
                    "completed-after-insertion",
                    "completed-after-insertion-container")
            ],
            now + TimeSpan.FromMilliseconds(100));

        var tracePolicy = new WindowTextChangePolicy(fingerprinter);
        tracePolicy.EstablishBaseline(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            now);
        var spokenProgress = tracePolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [
                new WindowTextChange(
                    "The monitor reads this progress update while work continues.",
                    true,
                    true,
                    "progress-original",
                    "progress-container-original",
                    1,
                    GroupKey: "progress-group-original")
            ],
            now);
        var heldRedrawAndFinal = tracePolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            [
                new WindowTextChange(
                    "The monitor reads",
                    true,
                    true,
                    "progress-word-1",
                    "progress-container-revised",
                    1,
                    GroupKey: "progress-group-revised"),
                new WindowTextChange(
                    "the progress update while work continues.",
                    true,
                    true,
                    "progress-word-2",
                    "progress-container-revised",
                    2,
                    GroupKey: "progress-group-revised"),
                new WindowTextChange(
                    "The final response begins.",
                    true,
                    true,
                    "final-word-1",
                    "final-paragraph",
                    10,
                    GroupKey: "final-response-group"),
                new WindowTextChange(
                    "It continues cleanly.",
                    true,
                    true,
                    "final-word-2",
                    "final-paragraph",
                    11,
                    GroupKey: "final-response-group"),
                new WindowTextChange(
                    "Playback policy:",
                    true,
                    true,
                    "final-heading",
                    "final-heading-container",
                    12,
                    GroupKey: "final-response-group"),
                new WindowTextChange(
                    "Read each list item in order.",
                    true,
                    true,
                    "final-list",
                    "final-list-container",
                    13,
                    GroupKey: "final-response-group")
            ],
            now + TimeSpan.FromMilliseconds(100));
        var settledTrace = tracePolicy.TakeSettled(now + TimeSpan.FromSeconds(2));

        var settledPolicy = new WindowTextChangePolicy(fingerprinter);
        settledPolicy.EstablishBaseline(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            now);
        var heldFragments = settledPolicy.Process(
            new WindowTextSnapshot(Array.Empty<WindowTextBlock>()),
            fragments,
            now);
        var settledFragments = settledPolicy.TakeSettled(now + TimeSpan.FromSeconds(2));

        var aggregateChanges = WindowTextChangePolicy.RemoveAggregateStatusDuplicates(
            [
                new WindowTextChange(
                    "Assistant response. The first paragraph arrived. The second paragraph followed.",
                    true,
                    true,
                    IsStatus: true),
                new WindowTextChange("The first paragraph arrived.", true, true),
                new WindowTextChange("The second paragraph followed.", true, true)
            ],
            fingerprinter);

        var accumulator = new WindowTextAccumulator();
        accumulator.Append(Fragment("The answer", "partial-1", 1));
        accumulator.Append(Fragment("arrived.", "partial-2", 2));
        var assembled = accumulator.TakeCompletedUnits(flushIncomplete: false);

        if (first.Count != 0
            || consolidated is not [{ Text: "The answer arrived." }]
            || heldTimestampFragment.Count != 0
            || completedParagraph.Count != 1
            || lateTimestampFragment.Count != 0
            || heldFirstLine.Count != 0
            || heldFollowingParagraph.Count != 0
            || orderedRelease.Select(change => change.Text).ToArray()
                is not ["Committed as fba444c.", "The worktree is clean."]
            || groupedResponse is not
                [{ Text: "The final response begins.\r\nThe first list item follows." }]
            || originalRedraw.Count != 1
            || revisedRedraw.Count != 0
            || provisionalPrefix.Count != 1
            || completedAfterInsertion is not
                [{ Text: "The second item is ready. The closing paragraph follows." }]
            || spokenProgress is not
                [{ Text: "The monitor reads this progress update while work continues." }]
            || heldRedrawAndFinal.Count != 0
            || settledTrace is not
                [{ Text: "The final response begins. It continues cleanly.\r\nPlayback policy:\r\nRead each list item in order." }]
            || heldFragments.Count != 0
            || settledFragments is not [{ Text: "The answer arrived." }]
            || aggregateChanges.Count != 2
            || aggregateChanges.Any(change => change.IsStatus)
            || assembled is not ["The answer arrived."])
        {
            throw new InvalidOperationException(
                "Sibling stream fragments were not grouped or their consolidated redraw replayed.");
        }

        static WindowTextChange Fragment(
            string text,
            string key,
            int order,
            string container = "paragraph") =>
            new(
                text,
                IsNewBlock: true,
                StartsAtBoundary: true,
                Key: key,
                ContainerKey: container,
                Order: order);
    }

    private static void CheckHotkeyGestureClassification()
    {
        var threshold = TimeSpan.FromMilliseconds(650);
        if (HotkeyHoldGestureDetector.Classify(
                TimeSpan.FromMilliseconds(100),
                chordPressed: true,
                threshold)
            != HotkeyGestureProgress.Pending
            || HotkeyHoldGestureDetector.Classify(
                TimeSpan.FromMilliseconds(100),
                chordPressed: false,
                threshold)
            != HotkeyGestureProgress.Tap
            || HotkeyHoldGestureDetector.Classify(
                threshold,
                chordPressed: true,
                threshold)
            != HotkeyGestureProgress.Hold)
        {
            throw new InvalidOperationException(
                "The hotkey hold classifier did not preserve tap and hold boundaries.");
        }
    }

    private static void CheckAppendOnlyTextTracking()
    {
        var tracker = new WindowTextDeltaTracker();
        tracker.EstablishBaseline(
            new WindowTextSnapshot(
                [new WindowTextBlock("existing", "Existing conversation")]));

        var changes = tracker.Update(
            new WindowTextSnapshot(
                [
                    new WindowTextBlock("existing", "Existing conversation"),
                    new WindowTextBlock("response", "The response arrived.")
                ]));
        var extension = tracker.Update(
            new WindowTextSnapshot(
                [
                    new WindowTextBlock("existing", "Existing conversation"),
                    new WindowTextBlock("response", "The response arrived. Another sentence.")
                ]));
        var replacement = tracker.Update(
            new WindowTextSnapshot(
                [
                    new WindowTextBlock("existing", "Existing conversation"),
                    new WindowTextBlock("response", "A completely replaced status")
                ]));
        var shortening = tracker.Update(
            new WindowTextSnapshot(
                [
                    new WindowTextBlock("existing", "Existing conversation"),
                    new WindowTextBlock("response", "A completely")
                ]));
        var completedResponse = tracker.Update(
            new WindowTextSnapshot(
                [
                    new WindowTextBlock("existing", "Existing conversation"),
                    new WindowTextBlock("response", "A completely"),
                    new WindowTextBlock(
                        "completed",
                        "The final response is complete.",
                        IsCompletedAggregate: true)
                ]));

        if (changes is not [{ Text: "The response arrived.", IsNewBlock: true }]
            || extension.Count != 1
            || extension[0].Text != " Another sentence."
            || replacement is not
                [{ Text: "A completely replaced status", IsNewBlock: true }]
            || shortening.Count != 0
            || completedResponse is not
                [{ Text: "The final response is complete.", IsCompletedAggregate: true }])
        {
            throw new InvalidOperationException(
                "Window text tracking did not isolate append-only and stable-element replacement changes.");
        }
    }

    private static void CheckRecreatedElementDeduplication()
    {
        var tracker = new WindowTextDeltaTracker();
        tracker.EstablishBaseline(
            new WindowTextSnapshot(
                [
                    new WindowTextBlock("old-a", "First stable block"),
                    new WindowTextBlock("old-b", "Second stable block")
                ]));

        var redraw = tracker.Update(
            new WindowTextSnapshot(
                [
                    new WindowTextBlock("new-a", "First stable block"),
                    new WindowTextBlock("new-b", "Second stable block")
                ]));
        if (redraw.Count != 0)
        {
            throw new InvalidOperationException(
                "A provider redraw was incorrectly treated as new readable text.");
        }

        var aggregateTracker = new WindowTextDeltaTracker();
        aggregateTracker.EstablishBaseline(
            new WindowTextSnapshot(
                [
                    new WindowTextBlock(
                        "aggregate",
                        "The monitoring test begins now.\nAmber is the first list item.\nThe monitoring test is complete.",
                        IsAggregate: true)
                ]));
        var expandedAggregate = aggregateTracker.Update(
            new WindowTextSnapshot(
                [
                    new WindowTextBlock("old-opening", "The monitoring test begins now."),
                    new WindowTextBlock("old-closing", "The monitoring test is complete."),
                    new WindowTextBlock("new-opening", "The monitoring test begins now."),
                    new WindowTextBlock("new-closing", "The monitoring test is complete.")
                ]));
        if (expandedAggregate.Count != 0)
        {
            throw new InvalidOperationException(
                "Removing an accessibility aggregate replayed visible paragraphs it already contained.");
        }
    }

    private static void CheckTemporarilyHiddenContent()
    {
        var tracker = new WindowTextDeltaTracker();
        var shortcut = new WindowTextBlock("shortcut", "Ctrl+Shift+Space");
        tracker.EstablishBaseline(new WindowTextSnapshot([shortcut]));
        tracker.Update(new WindowTextSnapshot([]));
        var returned = tracker.Update(new WindowTextSnapshot([
            shortcut,
            new WindowTextBlock("new-answer", "The new answer remains readable.")
        ]));
        if (returned is not [{ Text: "The new answer remains readable." }])
        {
            throw new InvalidOperationException("A temporarily hidden baseline shortcut was replayed.");
        }

        tracker.Update(new WindowTextSnapshot([]));
        var extended = tracker.Update(new WindowTextSnapshot([
            new WindowTextBlock("new-answer", "The new answer remains readable. More details follow."),
            new WindowTextBlock("intentional-repeat", "Ctrl+Shift+Space")
        ]));
        if (extended is not [{ Text: " More details follow." }, { Text: "Ctrl+Shift+Space" }])
        {
            throw new InvalidOperationException("Hidden-element tracking lost an extension or a distinct new occurrence.");
        }

        using var fingerprinter = new WindowTextFingerprinter();
        var filter = new PublishedWindowTextUnitFilter(fingerprinter);
        filter.StartResponseCycle();
        var now = DateTimeOffset.UtcNow;
        const string full = "I will check whether those earlier words came from the initial selection when monitoring attached.";
        filter.Filter([full], now);
        var suffix = filter.Filter([full[2..]], now + TimeSpan.FromSeconds(6));
        if (suffix.Count != 0)
        {
            throw new InvalidOperationException("A paragraph redrawn without its opening word was replayed.");
        }

        var policy = new WindowTextChangePolicy(fingerprinter);
        var covered = policy.RemoveCoveredFragments([
            new WindowTextChange(full, true, true, "full", GroupKey: "answer", IsProvisional: true),
            new WindowTextChange(full[2..], false, true, "suffix", GroupKey: "answer"),
            new WindowTextChange("The closing paragraph must remain.", true, true, "closing", GroupKey: "answer")
        ]);
        if (covered.Count != 2 || covered[0].Text != full || covered[1].Key != "closing")
        {
            throw new InvalidOperationException("A settled batch interleaved a covered redraw with the final paragraphs.");
        }
        filter.StartResponseCycle();
        if (filter.Filter([full], now.AddSeconds(10)) is not [full])
        {
            throw new InvalidOperationException("A new response could not repeat a previously spoken sentence.");
        }
    }

    private static void CheckOpeningWordSurvivesRedraw()
    {
        CheckOpeningWordSurvivesRedraw("This", "is", "the", "short",
            "This is the short response test. It should read once and stop here.");
        CheckOpeningWordSurvivesRedraw("I’d", "put", "a", "branch-wide",
            "I’d put a branch-wide audit next. We’ve fixed several interacting behaviours.");
    }

    private static void CheckOpeningWordSurvivesRedraw(
        string firstWord, string secondWord, string thirdWord, string fourthWord, string response)
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var tracker = new WindowTextDeltaTracker();
        var policy = new WindowTextChangePolicy(fingerprinter);
        var accumulator = new WindowTextAccumulator();
        var now = DateTimeOffset.UtcNow;
        var baseline = new WindowTextSnapshot([]);
        tracker.EstablishBaseline(baseline);
        policy.EstablishBaseline(baseline, now);
        var spoken = new List<string>();
        var snapshots = new[]
        {
            new WindowTextSnapshot([new WindowTextBlock("word-1", firstWord, "words", GroupKey: "paragraph")]),
            new WindowTextSnapshot([
                new WindowTextBlock("word-1", firstWord, "words", GroupKey: "paragraph"),
                new WindowTextBlock("word-2", secondWord, "words", Order: 1, GroupKey: "paragraph"),
                new WindowTextBlock("word-3", thirdWord, "words", Order: 2, GroupKey: "paragraph"),
                new WindowTextBlock("word-4", fourthWord, "words", Order: 3, GroupKey: "paragraph")]),
            new WindowTextSnapshot([new WindowTextBlock("full", response, "full-container", GroupKey: "paragraph")])
        };
        for (var index = 0; index < snapshots.Length; index++)
        {
            var changes = policy.Process(snapshots[index], tracker.Update(snapshots[index]), now.AddMilliseconds(index * 800));
            foreach (var change in changes) accumulator.Append(change);
            spoken.AddRange(accumulator.TakeCompletedUnits(false));
        }
        foreach (var change in policy.TakeSettled(now.AddSeconds(5))) accumulator.Append(change);
        spoken.AddRange(accumulator.TakeCompletedUnits(true));
        if (string.Join(" ", spoken) != response)
        {
            throw new InvalidOperationException($"Redrawing an unfinished opening changed the spoken response: '{string.Join(" ", spoken)}'.");
        }
    }

    private static void CheckStreamingUnitAssembly()
    {
        var accumulator = new WindowTextAccumulator();
        accumulator.Append(new WindowTextChange("The model is", true, true));
        if (accumulator.TakeCompletedUnits(flushIncomplete: false).Count != 0)
        {
            throw new InvalidOperationException("Incomplete streamed prose was released too early.");
        }

        accumulator.Append(new WindowTextChange(" working. The next", false, true));
        var completed = accumulator.TakeCompletedUnits(flushIncomplete: false);
        if (completed is not ["The model is working."])
        {
            throw new InvalidOperationException(
                "A complete streamed sentence was not released independently.");
        }

        accumulator.Append(new WindowTextChange(" thought", false, true));
        var idleFlush = accumulator.TakeCompletedUnits(flushIncomplete: true);
        if (idleFlush is not ["The next thought"])
        {
            throw new InvalidOperationException(
                "The final incomplete streamed unit was not retained for an idle flush.");
        }
    }

    private static void CheckPublishedUnitDeduplication()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var filter = new PublishedWindowTextUnitFilter(fingerprinter);
        var now = DateTimeOffset.UtcNow;
        const string repeated =
            "This middle reading unit appears in both streamed revisions.";

        var provisional = filter.Filter(
            ["The provisional response begins here.", repeated],
            now);
        var completed = filter.Filter(
            ["The completed response has a new opening.", repeated, "It also has a new ending."],
            now + TimeSpan.FromSeconds(1));
        var intentionalSameBatch = filter.Filter(
            ["Intentional repetition remains audible.", "Intentional repetition remains audible."],
            now + TimeSpan.FromSeconds(4));
        var laterRepetition = filter.Filter(
            [repeated],
            now + TimeSpan.FromSeconds(4));
        filter.StartResponseCycle();
        var responseOpening = filter.Filter(
            ["Read the top part.", "Validation passed:", "Read the bottom part."],
            now + TimeSpan.FromSeconds(5));
        var completedResponse = filter.Filter(
            [
                "Read the top part.",
                "Validation passed:",
                "First bullet item.",
                "Second bullet item.",
                "Read the bottom part.",
                "."
            ],
            now + TimeSpan.FromSeconds(9));
        var orphanedFragment = filter.Filter(
            ["as"],
            now + TimeSpan.FromSeconds(9.5));
        var shortStandaloneResponse = filter.Filter(
            ["OK"],
            now + TimeSpan.FromSeconds(9.6));
        filter.StartResponseCycle();
        var nextResponse = filter.Filter(
            ["Read the top part."],
            now + TimeSpan.FromSeconds(10));

        if (provisional.Count != 2
            || completed is not
                ["The completed response has a new opening.", "It also has a new ending."]
            || intentionalSameBatch.Count != 2
            || laterRepetition is not [repeated]
            || responseOpening.Count != 3
            || completedResponse is not ["First bullet item.", "Second bullet item."]
            || orphanedFragment.Count != 0
            || shortStandaloneResponse is not ["OK"]
            || nextResponse is not ["Read the top part."])
        {
            throw new InvalidOperationException(
                "Published reading units did not suppress only a recent cross-revision duplicate.");
        }
    }

    private static async Task CheckPriorityPlaybackAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.EnqueueMonitoredText("First monitored sentence.");
        var first = await speech.TakeStartedAsync();
        queue.EnqueueMonitoredText("Second monitored sentence.");
        queue.EnqueueManualSelection("Manual selection.");
        speech.Complete();
        var manual = await speech.TakeStartedAsync();
        speech.Complete();
        var second = await speech.TakeStartedAsync();
        speech.Complete();

        if (first != "First monitored sentence."
            || manual != "Manual selection."
            || second != "Second monitored sentence.")
        {
            throw new InvalidOperationException(
                "A manual selection did not take priority at the next monitored unit boundary.");
        }
    }

    private static async Task CheckPendingRedrawReplacementAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.Pause();
        queue.EnqueueMonitoredText(
            "I will use this as the isolated live test while examining the next queue policy step.");
        queue.EnqueueMonitoredText(
            "I am tracing the settled response now. I will use this as the isolated live test while examining the next queue policy. The final words remain.");
        queue.Resume();

        var started = await speech.TakeStartedAsync();
        speech.Complete();
        if (started !=
            "I am tracing the settled response now. I will use this as the isolated live test while examining the next queue policy. The final words remain.")
        {
            throw new InvalidOperationException(
                "A consolidated redraw did not replace its substantially overlapping pending unit.");
        }

        using var repeatedSpeech = new ControlledSpeechService();
        using var repeatedQueue = new MonitoringSpeechQueue(repeatedSpeech);
        repeatedQueue.Pause();
        const string repeated =
            "This intentionally repeated sentence contains enough words to exercise redraw detection.";
        repeatedQueue.EnqueueMonitoredText(repeated);
        repeatedQueue.EnqueueMonitoredText(repeated);
        repeatedQueue.Resume();
        var firstRepetition = await repeatedSpeech.TakeStartedAsync();
        repeatedSpeech.Complete();
        var secondRepetition = await repeatedSpeech.TakeStartedAsync();
        repeatedSpeech.Complete();
        if (firstRepetition != repeated || secondRepetition != repeated)
        {
            throw new InvalidOperationException(
                "Pending redraw replacement suppressed intentional repeated speech.");
        }
    }

    private static async Task CheckDiscreteMessageQueueAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.Pause();
        const string first = "Alice. This separate message contains enough words to trigger the streaming redraw heuristic.";
        var second = first + " Another sentence follows in this separate post.";
        queue.EnqueueMonitoredText(first, isDiscreteMessage: true);
        queue.EnqueueMonitoredText(second, isDiscreteMessage: true);
        queue.Resume();
        var actualFirst = await speech.TakeStartedAsync();
        speech.Complete();
        var actualSecond = await speech.TakeStartedAsync();
        speech.Complete();
        if (actualFirst != first || actualSecond != second)
            throw new InvalidOperationException("Distinct Discord posts were treated as a streaming redraw in the speech queue.");
    }

    private static async Task CheckDiscordInterjectionAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => queue.EnqueueMonitoredText(e.Text, isDiscreteMessage: e.IsDiscreteMessage, speechRequest: e.SpeechRequest);
        var now = DateTimeOffset.UtcNow;
        var last = DateTimeOffset.MinValue;
        var empty = DiscordMonitoringChecks.QueueFixture(now, false);
        session.ProcessSnapshot(empty, now.AddSeconds(-2), ref last);
        session.ProcessSnapshot(empty, now, ref last);
        var post = DiscordMonitoringChecks.QueueFixture(now.AddSeconds(1), true);
        session.ProcessSnapshot(post, now.AddSeconds(1), ref last);
        session.ProcessSettled(now.AddSeconds(2), ref last);
        if (await speech.TakeStartedAsync() != "Alice. First sentence.")
            throw new InvalidOperationException("Discord did not start with just the first reading unit and its author.");
        if (speech.LastRequest?.Attribution != "Alice. " || speech.LastRequest.Text != "First sentence.")
            throw new InvalidOperationException("The session or queue flattened Discord attribution before speech preparation.");
        queue.EnqueueManualSelection("Manual selection.");
        speech.Complete();
        foreach (var expected in new[] { "Manual selection.", "Second sentence.", "Third sentence." })
        {
            if (await speech.TakeStartedAsync() != expected)
                throw new InvalidOperationException("A manual selection did not interject at the Discord sentence boundary and resume in order.");
            speech.Complete();
        }
    }

    private static async Task CheckPreparedDiscordQueueAsync()
    {
        using var speech = new ControlledSpeechService { PrepareRequests = true };
        using var queue = new MonitoringSpeechQueue(speech);
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        session.TextAvailable += (_, e) => queue.EnqueueMonitoredText(e.Text, e.IsDiscreteMessage, e.SpeechRequest);
        var now = DateTimeOffset.UtcNow;
        var last = DateTimeOffset.MinValue;
        var empty = DiscordMonitoringChecks.QueueFixture(now, false);
        session.ProcessSnapshot(empty, now.AddSeconds(-2), ref last);
        session.ProcessSnapshot(empty, now, ref last);
        const string body = "| Name | Status |\n| --- | --- |\n| Bob | Ready |\n\n> First quotation.\n> Second quotation.";
        var post = DiscordMonitoringChecks.QueueFixture(now.AddSeconds(1), true, body);
        session.ProcessSnapshot(post, now.AddSeconds(1), ref last);
        session.ProcessSettled(now.AddSeconds(2), ref last);
        if (await speech.TakeStartedAsync() != "Alice. Name; Status.")
            throw new InvalidOperationException("Prepared table heading was lost between capture and the speech backend.");
        queue.EnqueueManualSelection("Manual selection.");
        speech.Complete();
        foreach (var expected in new[] { "Manual selection.", "Bob; Ready.", "Quote. First quotation.", "Second quotation." })
        {
            if (await speech.TakeStartedAsync() != expected)
                throw new InvalidOperationException("The speech queue lost prepared table/quote formatting or the interjection boundary.");
            speech.Complete();
        }
        session.ProcessSnapshot(post, now.AddSeconds(3), ref last);
        session.ProcessSettled(now.AddSeconds(4), ref last);
        if (queue.State.PendingItemCount != 0 || speech.StartedCount != 5)
            throw new InvalidOperationException("A prepared post replayed after redraw or queued its silent separator row.");
    }

    private static async Task CheckCompletedResponseTakeoverAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.EnqueueMonitoredText("The current thought is already being spoken.");
        var current = await speech.TakeStartedAsync();
        queue.EnqueueMonitoredText("This queued thought should be discarded.");
        queue.EnqueueCompletedResponse("The completed response starts here.");
        var bullets = new[] { "The worktree is clean.", "The full Debug suite passed.", "Pulse Reader stayed running throughout this commit." };
        foreach (var bullet in bullets) queue.EnqueueMonitoredText(bullet);
        queue.EnqueueMonitoredText("The completed response ends here.");

        speech.Complete();
        var finalOpening = await speech.TakeStartedAsync();
        speech.Complete();
        foreach (var bullet in bullets)
        {
            if (await speech.TakeStartedAsync() != bullet)
            {
                throw new InvalidOperationException("Final-response takeover skipped or reordered a bullet.");
            }
            speech.Complete();
        }
        var finalClosing = await speech.TakeStartedAsync();
        speech.Complete();

        if (current != "The current thought is already being spoken."
            || finalOpening != "The completed response starts here."
            || finalClosing != "The completed response ends here.")
        {
            throw new InvalidOperationException(
                "A completed response did not replace queued rolling thoughts at the next speech boundary.");
        }
    }

    private static async Task CheckInitialMonitoringDelayAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        queue.EnqueueMonitoredText("The first monitored unit waits briefly for a redraw.");
        await Task.Delay(100);
        if (speech.StartedCount != 0)
        {
            throw new InvalidOperationException(
                "Initial monitored speech started before its redraw stabilization window.");
        }

        queue.EnqueueManualSelection("Manual selection remains immediate.");
        var manual = await speech.TakeStartedAsync();
        speech.Complete();
        var monitored = await speech.TakeStartedAsync();
        speech.Complete();
        if (manual != "Manual selection remains immediate."
            || monitored != "The first monitored unit waits briefly for a redraw.")
        {
            throw new InvalidOperationException(
                "Initial monitoring stabilization delayed a manual selection or lost monitored text.");
        }
    }

    private static async Task CheckPausedQueueAsync()
    {
        using var speech = new ControlledSpeechService();
        using var queue = new MonitoringSpeechQueue(speech);
        if (!queue.Pause())
        {
            throw new InvalidOperationException("An idle monitoring queue could not be paused.");
        }

        queue.EnqueueMonitoredText("Queued while paused.");
        await Task.Delay(50);
        if (speech.StartedCount != 0 || !queue.Resume())
        {
            throw new InvalidOperationException(
                "A paused monitoring queue dispatched speech or could not resume.");
        }

        var resumed = await speech.TakeStartedAsync();
        speech.Complete();
        if (resumed != "Queued while paused.")
        {
            throw new InvalidOperationException(
                "Queued monitoring text did not resume from the paused state.");
        }
    }

    internal sealed class ControlledSpeechService : ISpeechService
    {
        private readonly Channel<string> _started = Channel.CreateUnbounded<string>();
        private SpeechPlaybackState _state;
        private readonly TaskCompletionSource<SpeechStartResult> _preparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayPreparation { get; set; }
        public bool PrepareRequests { get; set; }
        public void FinishPreparation()
        {
            _state = SpeechPlaybackState.Speaking;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            _preparation.TrySetResult(SpeechStartResult.Success());
        }

        public SpeechPlaybackState State => _state;

        public int StartedCount { get; private set; }
        public SpeechRequest? LastRequest { get; private set; }

        public Task<SpeechStartResult> TrySpeakAsync(SpeechRequest request)
        {
            LastRequest = request;
            return TrySpeakAsync(PrepareRequests ? request.Prepare().BuildSpokenText() : request.DisplayText);
        }

        public event EventHandler? SpeakingStateChanged;

        public Task<SpeechStartResult> TrySpeakAsync(string text)
        {
            if (_state != SpeechPlaybackState.Idle)
            {
                return Task.FromResult(SpeechStartResult.Failure("Already speaking."));
            }

            _state = DelayPreparation ? SpeechPlaybackState.Preparing : SpeechPlaybackState.Speaking;
            StartedCount++;
            _started.Writer.TryWrite(text);
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            return DelayPreparation ? _preparation.Task : Task.FromResult(SpeechStartResult.Success());
        }

        public Task<string> TakeStartedAsync() =>
            _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        public void Complete()
        {
            _state = SpeechPlaybackState.Idle;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool TryPause()
        {
            if (_state != SpeechPlaybackState.Speaking)
            {
                return false;
            }

            _state = SpeechPlaybackState.Paused;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public bool TryResume()
        {
            if (_state != SpeechPlaybackState.Paused)
            {
                return false;
            }

            _state = SpeechPlaybackState.Speaking;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void Stop() => Complete();

        public void Dispose()
        {
            _started.Writer.TryComplete();
        }
    }
}
