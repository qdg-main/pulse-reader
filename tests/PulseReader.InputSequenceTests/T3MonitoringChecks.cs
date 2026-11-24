using PulseReader.Monitoring;

internal static class T3MonitoringChecks
{
    internal static void Run()
    {
        const string opening = "This assistant update is ready.";
        const string finalFirst = "The final reply starts here.";
        const string finalSecond = "The reply can still explain which files changed.";
        var adapter = new T3ConversationAdapter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var fingerprint = new WindowTextFingerprinter();
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var start = DateTimeOffset.UtcNow;
        var time = start;
        var lastChange = DateTimeOffset.MinValue;
        var old = Assistant("old", Text("Existing reply is history."));
        var user = User("prompt", "Do not read back my message.");
        var embedded = Node("external", "", "document", 50030, [Assistant("preview", Text("Browser content."))]);
        var tool = Node("tool", "group/timeline-row", "button", 50000, [Text("Running a command.")]);
        var update = Assistant("update", Text(opening));
        var final = Assistant("final", Text(finalFirst), Text(finalSecond),
            Node("announcement", "sr-only", "group", 50026, [Text(finalSecond)]) with { AriaProperties = "live=polite" },
            Node("flattened-files-header",
                "sticky top-2 z-10 flex items-center justify-between gap-2 rounded-t-lg bg-secondary px-3 py-2",
                "group", 50026, [Text("Five changed files.")]),
            Text("file-name.cs"),
            Node("diff", "@container/changed-files", "group", 50026, [Text("Five changed files.")]),
            Node("meta", "text-xs tabular-nums", "group", 50026, [Text("12:30.")]));
        var baseline = Read("first", false, User("old-user", "Old prompt."), old, embedded);
        session.EstablishBaseline(baseline, start);

        void Feed(WindowTextSnapshot snapshot)
        {
            time += TimeSpan.FromSeconds(2);
            session.ProcessSnapshot(snapshot, time, ref lastChange, "fixture");
            session.ProcessSettled(time, ref lastChange);
        }

        Feed(Read("first", true, User("old-user", "Old prompt."), old, user, tool, update, embedded));
        Feed(Read("first", true, user, tool, update, embedded));
        Feed(Read("first", false, update, tool, final, embedded)); // The user bubble unmounted.
        Feed(Read("first", false, update, tool, final, embedded));
        Feed(Read("first", false, update, tool, final, embedded));
        if (!spoken.SequenceEqual(new[] { opening, finalFirst, finalSecond }))
            throw new InvalidOperationException("T3 monitored speech differed from the three expected assistant sentences: "
                + string.Join(" | ", spoken));

        Feed(Read("second", false, User("history-user", "Earlier question."),
            Assistant("earlier", Text("Older reply in another chat."))));
        Feed(Read("second", false, User("history-user", "Earlier question."),
            Assistant("earlier", Text("Older reply in another chat."))));
        Feed(adapter.ReadFrame(Root("second", User("older-user", "Hidden older question."), old),
            "second", atLiveEdge: false, generating: false, fingerprint));
        Feed(Read("second", false, User("history-user", "Earlier question."),
            Assistant("earlier", Text("Older reply in another chat."))));
        Feed(Read("second", false, User("history-user", "Earlier question."),
            Assistant("earlier", Text("Older reply in another chat."))));
        if (spoken.Count != 3)
            throw new InvalidOperationException("T3 replayed history when changing chat or scrolling.");

        var empty = Read("empty", false);
        if (empty.Context?.ViewKey != "t3:empty" || empty.Blocks.Count != 0)
            throw new InvalidOperationException("An empty T3 chat did not provide a silent baseline.");
        var midReply = Read("long", true, update);
        var remountedPrompt = Read("long", true, user, update);
        if (midReply.Blocks.Count != 1 || remountedPrompt.ResponseCycleKey != midReply.ResponseCycleKey)
            throw new InvalidOperationException("A long active T3 reply lost its identity when its prompt remounted.");
        CheckVirtualizedFollowUp();
        CheckRemountedOlderPrompt();
        CheckCompactionDoesNotReplayWorkingReply();
        CheckSlowThreadLoad();
        CheckTabSwitchReusesTimeline();
        CheckHistoricalOpeningOnWorkingRedraw();
        CheckBulkReplayAcrossResponseCycles();
        CheckOneWordOnWorkingRedraw();
        CheckRestoredThreadDoesNotReplayCompletedReply(sawWorkingUpdate: true);
        CheckRestoredThreadDoesNotReplayCompletedReply(sawWorkingUpdate: false);
        CheckIdenticalFollowUpAfterLatching();
        CheckIdenticalFollowUpWithVirtualizedPrompt();
        CheckShortSentenceAlreadyVisibleAtLatch();
        CheckGenerationContinuesAfterCompletedReply();
        CheckContinuationPreservesPendingFinal();
        CheckContinuationAfterPreviousFinalVirtualizes(promptVisible: true);
        CheckContinuationAfterPreviousFinalVirtualizes(promptVisible: false);
        CheckFastContinuation(sawBusyFrame: true, previousVisible: true);
        CheckFastContinuation(sawBusyFrame: true, previousVisible: false);
        CheckFastContinuation(sawBusyFrame: false, previousVisible: true);
        CheckContinuationFinalCanGrowAfterBusySignal();
        CheckScrollingPreservesFinalPlayback();
        CheckImagePreviewPreservesFinalPlayback();
        CheckFreshVerbatimRecap();
        Console.WriteLine("PASS: T3 scoped monitoring, working/final handoff, virtualized prompt continuity, and silent history.");

        WindowTextSnapshot Read(string key, bool generating, params CachedCodexTranscriptNode[] nodes)
            => adapter.ReadFrame(Root(key, nodes), key, atLiveEdge: true, generating, fingerprint);
    }

    private static void CheckTabSwitchReusesTimeline()
    {
        const string first = "t3code://app/#/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/11111111-1111-1111-1111-111111111111";
        const string second = "t3code://app/#/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/22222222-2222-2222-2222-222222222222";
        const string fresh = "This is the only reply created after switching tabs.";
        var adapter = new T3ConversationAdapter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        using var fingerprint = new WindowTextFingerprinter();
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        session.EstablishBaseline(Read(first, User("first-prompt", "Earlier question in the first tab."),
            Assistant("first-answer", Text("The first tab already has this reply."))), now);

        // T3 reused the same timeline runtime ID and did not expose a loading
        // frame before replacing the mounted messages with another thread.
        var destination = Read(second, User("second-prompt", "Earlier question in the second tab."),
            Assistant("second-answer", Text("This reply was already waiting in the second tab.")));
        Feed(destination);
        Feed(destination);
        Feed(destination);
        if (spoken.Count != 0)
            throw new InvalidOperationException("Switching T3 tabs replayed the destination's last reply: " + string.Join(" | ", spoken));

        var remounted = adapter.ReadDocumentFrame(Root("replacement-timeline",
                User("second-prompt", "Earlier question in the second tab."),
                Assistant("second-answer", Text("This reply was already waiting in the second tab."))),
            second + "?panel=details", atLiveEdge: true, generating: false, fingerprint);
        Feed(remounted);
        if (destination.Context != remounted.Context || spoken.Count != 0)
            throw new InvalidOperationException("Changing T3 panel state or replacing the timeline lost the thread identity.");

        var newReply = Read(second, User("new-prompt", "A question sent after switching tabs."), Assistant("new-answer", Text(fresh)));
        Feed(newReply);
        Feed(newReply);
        if (!spoken.SequenceEqual(new[] { fresh }))
            throw new InvalidOperationException("The destination tab lost a genuinely new reply: " + string.Join(" | ", spoken));
        if (Read("t3code://app/#/settings/chat").Context?.ViewKey.Length != 0)
            throw new InvalidOperationException("T3 settings were treated as a conversation.");

        WindowTextSnapshot Read(string url, params CachedCodexTranscriptNode[] rows)
            => adapter.ReadDocumentFrame(Root("shared-timeline", rows), url, atLiveEdge: true, generating: false, fingerprint);

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(2);
            session.ProcessSnapshot(snapshot, now, ref lastChange, "fixture");
            session.ProcessSettled(now, ref lastChange);
        }
    }

    private static void CheckRestoredThreadDoesNotReplayCompletedReply(bool sawWorkingUpdate)
    {
        var adapter = new T3ConversationAdapter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var fingerprint = new WindowTextFingerprinter();
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        const string answer = "The existing final answer was already read in this thread.";
        const string fresh = "This is the first new update since the thread was restored.";
        const string view = "settled";
        session.EstablishBaseline(Read(view), now);
        var original = Read(view, User("original-prompt", "The original request."),
            Assistant("original-answer", Text(answer)));
        Feed(original);
        Feed(original);

        Feed(adapter.Unavailable());
        Feed(Read("other"));
        Feed(Read("other"));
        Feed(adapter.LoadingThread());
        Feed(Read(view));
        Feed(Read(view));
        now += TimeSpan.FromSeconds(25);
        var restored = Read(view, User("restored-prompt", "The original request."),
            Assistant("restored-answer", Text(answer)));
        Feed(restored);
        Feed(restored);
        if (!spoken.SequenceEqual(new[] { answer }))
            throw new InvalidOperationException("Reopening a settled T3 thread replayed its final answer: " + string.Join(" | ", spoken));

        if (sawWorkingUpdate)
            Feed(Read(view, User("follow-up", "The next request."), Assistant("working", Text(fresh)), generating: true));
        var completed = Read(view, User("follow-up", "The next request."), Assistant("new-final", Text(answer)));
        Feed(completed);
        Feed(completed);
        if (!spoken.SequenceEqual(sawWorkingUpdate ? [answer, fresh, answer] : [answer, answer]))
            throw new InvalidOperationException("A new T3 turn after restoring a thread lost its reply: " + string.Join(" | ", spoken));

        WindowTextSnapshot Read(string key, CachedCodexTranscriptNode? prompt = null,
            CachedCodexTranscriptNode? reply = null, bool generating = false)
            => adapter.ReadFrame(Root(key, prompt is not null && reply is not null ? [prompt, reply] : []),
                key, atLiveEdge: true, generating, fingerprint);

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(3);
            session.ProcessSnapshot(snapshot, now, ref lastChange, "fixture");
            session.ProcessSettled(now, ref lastChange);
        }
    }

    private static void CheckIdenticalFollowUpAfterLatching()
    {
        using var fixture = new T3PlaybackFixture();
        const string promptText = "Are you ready?";
        const string answerText = "Ready.";
        var oldPrompt = User("old-prompt", promptText);
        var oldAnswer = Assistant("old-answer", Text(answerText));
        fixture.Attach(false, oldPrompt, oldAnswer);
        // Both prompt bubbles can be visible even if the fast reply finishes
        // between captures. Equal text belongs to two distinct prompt rows.
        var newPrompt = User("new-prompt", promptText);
        var newAnswer = Assistant("new-answer", Text(answerText));
        fixture.Feed(false, oldPrompt, oldAnswer, newPrompt, newAnswer);
        fixture.Feed(false, newPrompt, newAnswer);
        if (!fixture.Spoken.SequenceEqual(new[] { answerText }))
            throw new InvalidOperationException("The new identical T3 reply was lost after attaching.");
    }

    private static void CheckShortSentenceAlreadyVisibleAtLatch()
    {
        using var fixture = new T3PlaybackFixture();
        const string shortSentence = "Agreed.";
        const string newUpdate = "The first new update arrives after monitoring starts.";
        var prompt = User("same-prompt", "Please continue.");
        fixture.Attach(true, prompt, Assistant("working", Text("An older progress update was already displayed."), Text(shortSentence)));
        var redraw = Assistant("working", Text(shortSentence), Text(newUpdate));
        fixture.Feed(true, prompt, redraw);
        fixture.Feed(true, prompt, redraw);
        fixture.Feed(true, prompt, Assistant("working", Text(shortSentence), Text(newUpdate), Text(shortSentence)));
        if (!fixture.Spoken.SequenceEqual(new[] { newUpdate, shortSentence }))
            throw new InvalidOperationException("T3 replayed a one-word baseline or dropped a new occurrence.");
    }

    private static void CheckIdenticalFollowUpWithVirtualizedPrompt()
    {
        using var fixture = new T3PlaybackFixture();
        const string promptText = "Are you ready?";
        const string answerText = "Ready.";
        fixture.Attach(false, User("old-prompt", promptText), Assistant("old-answer", Text(answerText)));
        var newPrompt = User("new-prompt", promptText);
        fixture.Feed(true, newPrompt);
        var newAnswer = Assistant("new-answer", Text(answerText));
        fixture.Feed(false, newPrompt, newAnswer);
        fixture.Feed(false, newPrompt, newAnswer);
        if (!fixture.Spoken.SequenceEqual(new[] { answerText }))
            throw new InvalidOperationException("T3 lost an identical reply after observing the new prompt awaiting its answer.");
    }

    private static void CheckGenerationContinuesAfterCompletedReply()
    {
        using var fixture = new T3PlaybackFixture();
        const string live = "The next phase is now running after approval.";
        const string more = "The resumed work has reached the midpoint.";
        const string final = "The resumed work is complete.";
        var prompt = User("same-prompt", "Please continue once approved.");
        var previous = Assistant("previous-answer", Text("The first phase of work has ended."));
        fixture.Attach(false, prompt, previous);
        fixture.Feed(true, prompt, previous);
        var next = Assistant("new-working", Text(live));
        fixture.Feed(true, prompt, previous, next);
        if (!fixture.Spoken.SequenceEqual(new[] { live }))
            throw new InvalidOperationException("T3 replayed the completed reply or delayed the resumed working update.");

        var updated = Assistant("new-working", Text(live), Text(more));
        fixture.Feed(true, updated);
        fixture.Feed(false, updated, Assistant("new-final", Text(final)));
        fixture.Feed(false, updated, Assistant("new-final", Text(final)));
        if (!fixture.Spoken.SequenceEqual(new[] { live, more, final }))
            throw new InvalidOperationException("T3 lost or replayed text after the resumed working update was virtualized.");
    }

    private static void CheckFreshVerbatimRecap()
    {
        using var fixture = new T3PlaybackFixture();
        string[] first = ["The first group passed its runtime validation on the original configuration with the most recent settings loaded.",
            "This result covered the central part of the previous text capture validation for the original monitoring configuration."];
        string[] second = ["The second group ran using the revised settings from the next experiment to verify the new configuration.",
            "This result covered the separate configuration requested for the later test using the revised working state."];
        var initial = User("first-prompt", "Report the first group.");
        var next = User("second-prompt", "Report the second group.");
        fixture.Attach(false, initial);
        fixture.Feed(false, initial, Assistant("first-answer", first.Select(Text).ToArray()));
        fixture.Feed(false, initial, Assistant("first-answer", first.Select(Text).ToArray()));
        fixture.Feed(false, next, Assistant("second-answer", second.Select(Text).ToArray()));
        fixture.Feed(false, next, Assistant("second-answer", second.Select(Text).ToArray()));
        if (!fixture.Spoken.SequenceEqual(first.Concat(second)))
            throw new InvalidOperationException("The earlier T3 reports did not play in the recap fixture.");

        var quote = User("third-prompt", "Repeat both reports verbatim.");
        var repeated = Assistant("new-working", first.Concat(second).Select(Text).ToArray());
        fixture.Feed(true, quote, repeated);
        if (!fixture.Spoken.Skip(first.Length + second.Length).SequenceEqual(first.Concat(second)))
            throw new InvalidOperationException("A fresh verbatim T3 recap was suppressed during streaming.");
    }

    private static void CheckContinuationPreservesPendingFinal()
    {
        using var fixture = new T3PlaybackFixture();
        const string firstFinal = "The first stage has a new final result.";
        const string nextUpdate = "A second step has now started.";
        var prompt = User("same-prompt", "Please run both steps.");
        fixture.Attach(false, prompt);
        var answer = Assistant("first-answer", Text(firstFinal));
        fixture.Feed(false, prompt, answer);
        fixture.FeedAfter(TimeSpan.FromMilliseconds(200), true, prompt, answer, Assistant("next-update", Text(nextUpdate)));
        if (!fixture.Spoken.SequenceEqual(new[] { firstFinal, nextUpdate }))
            throw new InvalidOperationException("A T3 continuation discarded the previous final before settlement.");
    }

    private static void CheckContinuationAfterPreviousFinalVirtualizes(bool promptVisible)
    {
        using var fixture = new T3PlaybackFixture();
        const string oldText = "The first phase of work has ended.";
        const string updateText = "The resumed phase has reached a new result.";
        const string finalText = "The resumed phase finished successfully.";
        var prompt = User("same-prompt", "Please complete both phases.");
        var previous = Assistant("old-final", Text(oldText));
        fixture.Attach(false, prompt, previous);
        fixture.Feed(true, prompt, previous);
        fixture.Feed(true, prompt, Assistant("old-final-remounted", Text(oldText)));
        if (fixture.Spoken.Count != 0)
            throw new InvalidOperationException("T3 replayed the previous final while starting a continuation.");

        var next = Assistant("new-working", Text(updateText));
        fixture.Feed(true, promptVisible ? [prompt, next] : [next]);
        if (!fixture.Spoken.SequenceEqual(new[] { updateText }))
            throw new InvalidOperationException("T3 lost working text when the previous final unmounted.");

        var final = Assistant("new-final", Text(finalText));
        // The same old final can come back with a fresh UIA id while the new
        // work is still visible. It should not enter the new working block.
        var finished = new[] { Assistant("old-final-returning", Text(oldText)), next, final };
        fixture.Feed(false, promptVisible ? [prompt, .. finished] : finished);
        fixture.Feed(false, promptVisible ? [prompt, .. finished] : finished);
        if (!fixture.Spoken.SequenceEqual(new[] { updateText, finalText }))
            throw new InvalidOperationException("T3 lost the final after a virtualized continuation.");
    }

    private static void CheckFastContinuation(bool sawBusyFrame, bool previousVisible)
    {
        using var fixture = new T3PlaybackFixture();
        const string finalText = "The short follow-up has finished successfully.";
        var prompt = User("same-prompt", "Please complete both steps.");
        var previous = Assistant("old-final", Text("The first step has already finished."));
        fixture.Attach(false, prompt, previous);
        if (sawBusyFrame)
        {
            fixture.Feed(true, prompt, previous);
            fixture.Feed(false, prompt, previous); // The stop control disappeared before the new reply mounted.
        }

        var final = Assistant("new-final", Text(finalText));
        CachedCodexTranscriptNode[] finished = previousVisible ? [prompt, previous, final] : [prompt, final];
        fixture.Feed(false, finished);
        fixture.Feed(false, finished);
        if (!fixture.Spoken.SequenceEqual(new[] { finalText }))
            throw new InvalidOperationException("T3 lost a continuation that finished between working snapshots.");
    }

    private static void CheckContinuationFinalCanGrowAfterBusySignal()
    {
        using var fixture = new T3PlaybackFixture();
        const string first = "The short follow-up reached its result.";
        const string added = "The last validation also passed.";
        var prompt = User("same-prompt", "Please complete both steps.");
        var previous = Assistant("old-final", Text("The first step has already finished."));
        fixture.Attach(false, prompt, previous);
        var initialFinal = Assistant("new-final", Text(first));
        fixture.Feed(false, prompt, previous, initialFinal);
        // A late busy signal must not make the same assistant row ineligible
        // when T3 subsequently exposes its full idle body.
        fixture.FeedAfter(TimeSpan.FromMilliseconds(100), true, prompt, previous, initialFinal);
        var completeFinal = Assistant("new-final", Text(first), Text(added));
        fixture.FeedAfter(TimeSpan.FromMilliseconds(100), false, prompt, previous, completeFinal);
        fixture.Feed(false, prompt, previous, completeFinal);
        if (!fixture.Spoken.SequenceEqual(new[] { first, added }))
            throw new InvalidOperationException("A late T3 busy signal prevented the continuation's final row from settling.");
    }

    private static void CheckScrollingPreservesFinalPlayback()
    {
        using var fixture = new T3PlaybackFixture();
        const string final = "The final describes the image and then explains the result.";
        var prompt = User("prompt", "Describe the result.");
        var reply = Assistant("final", Text(final));
        fixture.Attach(false, prompt);
        fixture.Feed(false, prompt, reply); // The final still has to settle.
        var history = Assistant("history", Text("This older reply is visible above the latest answer."));
        fixture.FeedScrolled("follow-up", User("old", "An earlier question."), history);
        if (fixture.ContextChanges.Count != 0 || !fixture.Spoken.SequenceEqual(new[] { final }))
            throw new InvalidOperationException("Scrolling on the same T3 thread canceled the final or exposed older text.");

        fixture.FeedScrolled("follow-up", generating: true, history);
        if (fixture.ContextChanges.Count != 0)
            throw new InvalidOperationException("A later busy signal canceled the already captured T3 final.");

        // Returning from history can remount the visible reply with new IDs.
        fixture.Feed(false, User("remounted-prompt", "Describe the result."), Assistant("remounted-final", Text(final)));
        fixture.Feed(false, User("remounted-prompt", "Describe the result."), Assistant("remounted-final", Text(final)));
        if (fixture.ContextChanges.Count != 0 || !fixture.Spoken.SequenceEqual(new[] { final }))
            throw new InvalidOperationException("Returning to the T3 live edge canceled playback or replayed the final.");

        const string nextFinal = "This is the next reply after returning to the latest messages.";
        var next = User("next-prompt", "What follows?");
        var nextReply = Assistant("next-final", Text(nextFinal));
        fixture.Feed(false, next, nextReply);
        fixture.Feed(false, next, nextReply);
        if (!fixture.Spoken.SequenceEqual(new[] { final, nextFinal }))
            throw new InvalidOperationException("T3 did not resume capture of new replies after scrolling.");

        fixture.FeedScrolled("different-thread", history);
        if (!fixture.ContextChanges.SequenceEqual(new[] { true }))
            throw new InvalidOperationException("Switching to a different T3 thread while scrolled did not suspend playback.");
    }

    private static void CheckImagePreviewPreservesFinalPlayback()
    {
        using var fixture = new T3PlaybackFixture();
        const string final = "The image shows the result of the test.";
        const string previewClasses = "relative flex max-h-[92vh] w-[92vw] [--media-width:92vw]";
        var prompt = User("prompt", "Describe this image.");
        fixture.Attach(false, prompt);
        fixture.Feed(false, prompt, Assistant("final", Text(final)));
        fixture.FeedModal("follow-up", "Expanded image preview", previewClasses);
        if (fixture.ContextChanges.Count != 0 || !fixture.Spoken.SequenceEqual(new[] { final }))
            throw new InvalidOperationException("Opening the T3 image preview interrupted the final reply.");

        var remounted = Assistant("remounted-final", Text(final));
        fixture.Feed(false, User("remounted-prompt", "Describe this image."), remounted);
        fixture.Feed(false, User("remounted-prompt", "Describe this image."), remounted);
        if (fixture.ContextChanges.Count != 0 || !fixture.Spoken.SequenceEqual(new[] { final }))
            throw new InvalidOperationException("Closing the T3 image preview replayed the final reply.");

        fixture.FeedModal("follow-up", "Confirm action", previewClasses);
        if (!fixture.ContextChanges.SequenceEqual(new[] { true }))
            throw new InvalidOperationException("An unrelated T3 modal did not suspend playback.");
    }

    private sealed class T3PlaybackFixture : IDisposable
    {
        private readonly WindowTextMonitoringSession _session = new(new IntPtr(1));
        private readonly WindowTextFingerprinter _fingerprint = new();
        private readonly T3ConversationAdapter _adapter = new();
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private DateTimeOffset _changed = DateTimeOffset.MinValue;

        public T3PlaybackFixture()
        {
            _session.TextAvailable += (_, e) => Spoken.Add(e.Text);
            _session.ContextChanged += (_, e) => ContextChanges.Add(e.Suspended);
        }

        public List<string> Spoken { get; } = [];
        public List<bool> ContextChanges { get; } = [];

        public void Attach(bool generating, params CachedCodexTranscriptNode[] rows)
            => _session.EstablishBaseline(Read(generating, rows), _now);

        public void Feed(bool generating, params CachedCodexTranscriptNode[] rows)
            => FeedAfter(TimeSpan.FromSeconds(3), generating, rows);

        public void FeedAfter(TimeSpan delay, bool generating, params CachedCodexTranscriptNode[] rows)
        {
            _now += delay;
            _session.ProcessSnapshot(Read(generating, rows), _now, ref _changed, "fixture");
            _session.ProcessSettled(_now, ref _changed);
        }

        public void FeedScrolled(string view, params CachedCodexTranscriptNode[] rows)
            => FeedScrolled(view, generating: false, rows);

        public void FeedScrolled(string view, bool generating, params CachedCodexTranscriptNode[] rows)
        {
            _now += TimeSpan.FromSeconds(3);
            var snapshot = _adapter.ReadFrame(Root(view, rows), view, atLiveEdge: false, generating, _fingerprint);
            _session.ProcessSnapshot(snapshot, _now, ref _changed, "fixture");
            _session.ProcessSettled(_now, ref _changed);
        }

        public void FeedModal(string view, string name, string classes)
        {
            _now += TimeSpan.FromSeconds(3);
            _session.ProcessSnapshot(_adapter.ReadModal(view, 50032, name, classes), _now, ref _changed, "fixture");
            _session.ProcessSettled(_now, ref _changed);
        }

        private WindowTextSnapshot Read(bool generating, CachedCodexTranscriptNode[] rows)
            => _adapter.ReadFrame(Root("follow-up", rows), "follow-up", atLiveEdge: true, generating, _fingerprint);

        public void Dispose()
        {
            _session.Dispose();
            _fingerprint.Dispose();
        }
    }

    private static void CheckHistoricalOpeningOnWorkingRedraw()
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        const string view = "t3:history-redraw";
        const string previous = "t3-message:previous";
        const string current = "t3-message:current";
        const string first = "The first historical result was verified before the new request.";
        const string second = "The second historical result was also saved in the report.";
        const string progress = "The current investigation started with a new set of inputs.";
        const string fresh = "The replacement also contains a genuinely new finding.";

        session.EstablishBaseline(new([], Context: new(view)), now);
        Feed(Snapshot(previous, first + " " + second, final: true));
        Feed(Snapshot(previous, first + " " + second, final: true));
        Feed(Snapshot(current, progress));
        Feed(new([], Context: new("")));
        Feed(Snapshot(current, progress));
        Feed(Snapshot(current, progress));
        now += TimeSpan.FromHours(12);
        var replacement = Snapshot(current, string.Join(' ', first, second, progress, fresh));
        Feed(replacement);
        Feed(replacement);
        Feed(replacement);
        if (!spoken.SequenceEqual(new[] { first, second, progress, fresh }))
            throw new InvalidOperationException("A T3 working redraw spoke an old completed answer: " + string.Join(" | ", spoken));

        var repeatedOnPurpose = Snapshot("t3-message:next", first + " " + second);
        Feed(repeatedOnPurpose);
        if (!spoken.TakeLast(2).SequenceEqual(new[] { first, second }))
            throw new InvalidOperationException("A new T3 turn suppressed legitimately repeated wording.");

        WindowTextSnapshot Snapshot(string cycle, string text, bool final = false)
        {
            var key = cycle + (final ? ":answer" : ":working");
            return new([new(key, text, ContainerKey: key, GroupKey: key, IsAggregate: true, IsFinalResponse: final)],
                ResponseCycleKey: cycle, Context: new(view));
        }

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(3);
            session.ProcessSnapshot(snapshot, now, ref lastChange, "fixture");
            session.ProcessSettled(now, ref lastChange);
        }
    }

    private static void CheckBulkReplayAcrossResponseCycles()
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        using var fingerprint = new WindowTextFingerprinter();
        var adapter = new T3ConversationAdapter();
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        const string view = "bulk-replay";
        const string completed = "The resumed work has now finished the additional verification.";
        string[] newUpdates =
        [
            "The resumed work has a genuinely new update to report.",
            "The revised input now reaches the correct stage.",
            "The remaining check uses a separate scenario.",
            "One more observation explains the latest result."
        ];
        string[] first = Enumerable.Range(1, 62)
            .Select(i => $"Historical step {i} was checked against the original record and reported to the user.").ToArray();
        string[] second = Enumerable.Range(1, 6)
            .Select(i => $"Earlier summary item {i} confirmed which part of the test had already finished.").ToArray();
        var firstPrompt = User("first-prompt", "The original work request.");
        var firstReply = Assistant("original-first", Text(string.Join(' ', first)));
        session.EstablishBaseline(Read(false, firstPrompt), now);
        Feed(Read(true, firstPrompt, firstReply));
        var secondReply = Read(false, User("second-prompt", "The next request."), Assistant("original-second", Text(string.Join(' ', second))));
        Feed(secondReply);
        Feed(secondReply);
        if (!spoken.SequenceEqual(first.Concat(second)))
            throw new InvalidOperationException("T3 did not speak the historical fixture before the redraw.");

        now += TimeSpan.FromHours(12);
        var remounted = Read(true, Assistant("remounted-first", Text(string.Join(' ', first))),
            Assistant("remounted-second", Text(string.Join(' ', second))));
        Feed(remounted);
        if (remounted.ResponseCycleKey == secondReply.ResponseCycleKey || spoken.Count != first.Length + second.Length)
            throw new InvalidOperationException($"A new T3 response ID replayed {spoken.Count - first.Length - second.Length} units from earlier turns.");

        // A subsequent replacement dropped much of the remounted history and
        // exposed a new update after the remaining historical prefix.
        var update = Assistant("new-update", Text(string.Join(' ', newUpdates)));
        var updated = Read(true, Assistant("remounted-first", Text(string.Join(' ', first.Skip(20).Take(35)))), update);
        Feed(updated);
        Feed(updated);
        var final = Read(false, update, Assistant("new-final", Text(completed)));
        Feed(final);
        Feed(final);
        if (!spoken.SequenceEqual(first.Concat(second).Concat(newUpdates).Append(completed)))
            throw new InvalidOperationException("A T3 redraw replayed history or lost new text at the end of it.");

        // A new prompt asking for a verbatim repeat of one reply is valid.
        Feed(Read(true, User("repeat-prompt", "Please repeat the original result."),
            Assistant("verbatim", Text(string.Join(' ', first)))));
        if (!spoken.TakeLast(first.Length).SequenceEqual(first))
            throw new InvalidOperationException("T3 suppressed a genuinely new response repeating an earlier result.");

        WindowTextSnapshot Read(bool generating, params CachedCodexTranscriptNode[] rows)
            => adapter.ReadFrame(Root(view, rows), view, atLiveEdge: true, generating, fingerprint);

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(3);
            session.ProcessSnapshot(snapshot, now, ref lastChange, "fixture");
            session.ProcessSettled(now, ref lastChange);
        }
    }

    private static void CheckOneWordOnWorkingRedraw()
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        using var fingerprint = new WindowTextFingerprinter();
        var adapter = new T3ConversationAdapter();
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        const string view = "single-word-redraw";
        const string shortSentence = "Agreed.";
        const string first = "The investigation is still on the first step.";
        const string update = "The next step has reached an important result.";
        var prompt = User("current-prompt", "Please continue with the investigation.");
        var original = Read(Text(first), Text(shortSentence));
        session.EstablishBaseline(Read(), now);
        Feed(original);
        now += TimeSpan.FromHours(2);

        // Virtualization moved an already-spoken short sentence to the front
        // of a replacement aggregate while exposing genuinely new text.
        var redraw = Read(Text(shortSentence), Text(update));
        Feed(redraw);
        Feed(redraw);
        if (!spoken.SequenceEqual(new[] { first, shortSentence, update })
            || original.ResponseCycleKey != redraw.ResponseCycleKey)
            throw new InvalidOperationException("A T3 working redraw replayed a one-word sentence: " + string.Join(" | ", spoken));

        // A new occurrence appended at the live edge is intentional.
        Feed(Read(Text(shortSentence), Text(update), Text(shortSentence)));
        if (!spoken.SequenceEqual(new[] { first, shortSentence, update, shortSentence }))
            throw new InvalidOperationException("T3 suppressed a new one-word update at the live edge.");

        const string next = "The investigation reached another result.";
        var expanded = Read(Text(update), Text(shortSentence), Text(shortSentence), Text(next), Text(shortSentence));
        Feed(expanded);
        Feed(expanded);
        if (!spoken.SequenceEqual(new[] { first, shortSentence, update, shortSentence, next, shortSentence }))
            throw new InvalidOperationException("A T3 redraw lost an additional new occurrence of a one-word sentence.");

        WindowTextSnapshot Read(params CachedCodexTranscriptNode[] body)
        {
            var rows = body.Length == 0 ? new[] { prompt } : [prompt, Assistant("working", body)];
            return adapter.ReadFrame(Root(view, rows), view, atLiveEdge: true, generating: true, fingerprint);
        }

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(3);
            session.ProcessSnapshot(snapshot, now, ref lastChange, "fixture");
            session.ProcessSettled(now, ref lastChange);
        }
    }

    private static void CheckSlowThreadLoad()
    {
        var adapter = new T3ConversationAdapter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var fingerprint = new WindowTextFingerprinter();
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var previous = User("old-prompt", "Prompt in the first thread.");
        var current = User("existing-prompt", "Prompt in the next thread.");
        var history = Assistant("existing-answer", Text("This answer was already present when the thread opened."));
        const string next = "This is a new reply after the thread loaded.";
        session.EstablishBaseline(Read("first", previous, history), now);
        Feed(adapter.LoadingThread()); // Transcript may mount before T3 finishes syncing.
        Feed(adapter.LoadingThread());
        Feed(adapter.LoadingThread());
        Feed(Read("next", current, history));
        Feed(Read("next", current, history));
        Feed(Read("next", current, history));
        var fresh = Read("next", User("fresh-prompt", "A newly submitted prompt."), Assistant("fresh-answer", Text(next)));
        Feed(fresh);
        Feed(fresh);
        if (!spoken.SequenceEqual(new[] { next }))
            throw new InvalidOperationException("T3 spoke history while loading a different thread: " + string.Join(" | ", spoken));

        WindowTextSnapshot Read(string key, params CachedCodexTranscriptNode[] rows)
            => adapter.ReadFrame(Root(key, rows), key, atLiveEdge: true, generating: false, fingerprint);

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(2);
            session.ProcessSnapshot(snapshot, now, ref lastChange, "fixture");
            session.ProcessSettled(now, ref lastChange);
        }
    }

    private static void CheckCompactionDoesNotReplayWorkingReply()
    {
        var adapter = new T3ConversationAdapter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var fingerprint = new WindowTextFingerprinter();
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        const string first = "The assistant already explained the original behaviour and how the first part of the investigation reproduced it under controlled conditions.";
        const string second = "It then reported that the additional verification step matched the expected result and that the remaining work could continue without repeating the experiment.";
        const string next = "This is the only new update after compaction.";
        const string final = "This is the completed answer.";
        var prompt = User("prompt-before-compaction", "The current user prompt.");
        session.EstablishBaseline(Read(true, prompt), now);
        var original = Read(true, prompt, Assistant("first", Text(first)), Assistant("second", Text(second)));
        Feed(original);

        // T3 rebuilt the entire live reply and its prompt with new UIA IDs.
        // The displayed working text itself is identical to the old aggregate.
        var rebuiltPrompt = User("prompt-after-compaction", "The current user prompt.");
        var rebuilt = Read(true, rebuiltPrompt, Assistant("rebuilt-first", Text(first)), Assistant("rebuilt-second", Text(second)));
        Feed(rebuilt);
        Feed(Read(true, rebuiltPrompt, Assistant("rebuilt-first", Text(first)),
            Assistant("rebuilt-second", Text(second)), Assistant("new", Text(next))));
        var completed = Read(false, rebuiltPrompt, Assistant("rebuilt-first", Text(first)),
            Assistant("rebuilt-second", Text(second)), Assistant("new", Text(next)), Assistant("final", Text(final)));
        Feed(completed);
        Feed(completed);
        if (!spoken.SequenceEqual(new[] { first, second, next, final })
            || rebuilt.ResponseCycleKey != original.ResponseCycleKey
            || completed.ResponseCycleKey != original.ResponseCycleKey)
            throw new InvalidOperationException("T3 replayed speech or changed reply identity after compaction: "
                + string.Join(" | ", spoken));

        var followUp = Read(true, User("new-user-prompt", "An unrelated follow-up."),
            Assistant("new-answer", Text("This is a genuinely different response.")));
        if (followUp.ResponseCycleKey == original.ResponseCycleKey)
            throw new InvalidOperationException("A distinct T3 prompt did not start a new response after compaction.");

        WindowTextSnapshot Read(bool generating, params CachedCodexTranscriptNode[] rows)
            => adapter.ReadFrame(Root("compaction", rows), "compaction", atLiveEdge: true, generating, fingerprint);

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(2);
            session.ProcessSnapshot(snapshot, now, ref lastChange, "fixture");
            session.ProcessSettled(now, ref lastChange);
        }
    }

    private static void CheckRemountedOlderPrompt()
    {
        var adapter = new T3ConversationAdapter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var fingerprint = new WindowTextFingerprinter();
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var olderPrompt = User("older-prompt", "An earlier prompt.");
        var olderAnswer = Assistant("older-answer", Text("This historical answer must stay silent."));
        const string opening = "This is the current working reply.";
        const string continuation = "This update arrived after the prompt remounted.";
        const string final = "This is the completed live answer.";
        session.EstablishBaseline(Read(false, olderPrompt, olderAnswer), now);
        var live = Read(true, User("current-prompt", "The current prompt."),
            Assistant("working", Text(opening)));
        Feed(live);

        // The current prompt unmounts while an older one reappears before the
        // live assistant rows. The older prompt must not replace the anchor.
        var remounted = Read(true, olderPrompt, olderAnswer,
            Assistant("working-remounted", Text(opening), Text(continuation)));
        Feed(remounted);
        Feed(Read(false, olderPrompt, olderAnswer));
        var completed = Read(false, olderPrompt, olderAnswer,
            Assistant("working-remounted", Text(opening), Text(continuation)), Assistant("final", Text(final)));
        Feed(completed);
        Feed(completed);
        if (!spoken.SequenceEqual(new[] { opening, continuation, final })
            || remounted.ResponseCycleKey != live.ResponseCycleKey
            || completed.ResponseCycleKey != live.ResponseCycleKey)
            throw new InvalidOperationException("T3 lost the live reply after an older prompt remounted: "
                + string.Join(" | ", spoken));

        WindowTextSnapshot Read(bool generating, params CachedCodexTranscriptNode[] rows)
            => adapter.ReadFrame(Root("remount", rows), "remount", atLiveEdge: true, generating, fingerprint);

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(2);
            session.ProcessSnapshot(snapshot, now, ref lastChange, "fixture");
            session.ProcessSettled(now, ref lastChange);
        }
    }

    private static void CheckVirtualizedFollowUp()
    {
        var adapter = new T3ConversationAdapter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var fingerprint = new WindowTextFingerprinter();
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var previous = Assistant("old", Text("This is a completed reply from earlier."));
        var baseline = Read(false, User("old-user", "Earlier prompt."), previous);
        session.EstablishBaseline(baseline, now);

        // No visible prompt and no surviving runtime IDs. A historic redraw
        // at idle must not be mistaken for a new answer.
        Feed(Read(false, Assistant("history", Text("Older content stays silent."))));
        Feed(Read(false, Assistant("history", Text("Older content stays silent."))));
        if (spoken.Count != 0)
            throw new InvalidOperationException("T3 treated disjoint idle history as new speech.");

        // T3 can start a follow-up after virtualizing every row of the previous
        // turn. Its new user bubble need not be mounted in any captured frame.
        const string update = "Here is the new live update.";
        const string continuation = "The live reply is still progressing.";
        const string answer = "Here is the completed reply.";
        var working = Read(true, Assistant("working", Text(update)));
        Feed(working);
        Feed(Read(true)); // A brief gap during the redraw.
        var remounted = Read(true, Assistant("remounted", Text(update), Text(continuation)));
        Feed(remounted);
        var final = Read(false, Assistant("final", Text(answer)));
        Feed(final);
        Feed(final);
        if (!spoken.SequenceEqual(new[] { update, continuation, answer }))
            throw new InvalidOperationException("T3 stayed silent after a virtualized follow-up lost its prompt and all previous rows: "
                + string.Join(" | ", spoken));
        if (working.ResponseCycleKey == baseline.ResponseCycleKey || remounted.ResponseCycleKey != working.ResponseCycleKey
            || final.ResponseCycleKey != working.ResponseCycleKey)
            throw new InvalidOperationException("T3 did not preserve the recovered turn through its final reply.");

        WindowTextSnapshot Read(bool generating, params CachedCodexTranscriptNode[] rows)
            => adapter.ReadFrame(Root("virtualized", rows), "virtualized", atLiveEdge: true, generating, fingerprint);

        void Feed(WindowTextSnapshot snapshot)
        {
            now += TimeSpan.FromSeconds(2);
            session.ProcessSnapshot(snapshot, now, ref lastChange, "fixture");
            session.ProcessSettled(now, ref lastChange);
        }
    }

    private static CachedCodexTranscriptNode Root(string key, params CachedCodexTranscriptNode[] rows)
        => Node(key, T3ConversationAdapter.TimelineClass, "group", 50026, rows);
    private static CachedCodexTranscriptNode User(string key, string text)
        => Node(key, "bg-message text-message-foreground", "group", 50026,
            [Heading("You"), Text(text)]);
    private static CachedCodexTranscriptNode Assistant(string key, params CachedCodexTranscriptNode[] body)
        => Node(key, "relative min-w-0 px-1 py-0.5", "group", 50026, [Heading("T3 Code"), .. body]);
    private static CachedCodexTranscriptNode Heading(string text)
        => Node("", "sr-only select-none", "heading", 50020, [], text);
    private static CachedCodexTranscriptNode Text(string text)
        => Node("", "", "group", 50026, [Node("", "", "description", 50020, [], text)]);
    private static CachedCodexTranscriptNode Node(string key, string classes, string role, int type,
        IReadOnlyList<CachedCodexTranscriptNode> children, string text = "")
        => new(key, type, text, classes, role, children);
}
