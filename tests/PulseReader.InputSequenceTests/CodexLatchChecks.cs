using PulseReader.Monitoring;

internal static class CodexLatchChecks
{
    // Exercise capture/latch rules without the new sentence timer; production
    // timing and its integration are covered by CodexThoughtSettlementChecks.
    public static async Task RunAsync()
    {
        var failures = new List<string>();
        CheckAlternateConversationView(failures);
        CheckNonModalPopover(failures);
        CheckDescriptionCapture(failures);
        CheckMissingUserHeading(failures);
        CheckMountedPromptIdentity(failures);
        CheckHistoricalScrollThroughAdapter(failures);
        CheckRepeatedPromptMountedSeparately(failures);
        CodexHistoricalFinalChecks.Run(failures);
        CheckShrinkingRedraw(failures);
        CheckRewrittenThoughtTail(failures);
        CheckThoughtRewriteAfterContextReturn(failures);
        await CheckBaselineDeduplicationAsync(failures);
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS: Codex latch baselines, prompt identity, historical finals, and shrinking redraws preserve speech ordering.");
    }

    private static bool Include(string text, bool codex, string role, bool paragraph = false) =>
        WindowTextSnapshotReader.ShouldIncludeObservedText(text, false, false, false, false, codex, role, paragraph);

    private static void CheckThoughtRewriteAfterContextReturn(List<string> failures)
    {
        foreach (var provider in new[] { "codex-message", "lmstudio-message" })
        foreach (var alreadyVisibleAtLatch in new[] { false, true })
            CheckThoughtRewriteAfterBaseline(provider, alreadyVisibleAtLatch, failures);
    }

    private static void CheckThoughtRewriteAfterBaseline(string provider, bool alreadyVisibleAtLatch,
        List<string> failures)
    {
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var cycle = provider + ":context-rewrite";
        const string opening = "The first thought is already complete. The second thought is already complete.";
        const string addition = "A fresh update arrives after returning.";
        const string corrected = "A corrected update arrives after returning.";
        WindowTextSnapshot Snapshot(string view, string text, string? response = null)
        {
            response ??= cycle;
            var key = response + ":working";
            return new([new(key, text, ContainerKey: key, GroupKey: key, IsAggregate: true)],
                ResponseCycleKey: response, Context: new(view));
        }
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(Snapshot("view", alreadyVisibleAtLatch ? opening : ""), now);
        if (!alreadyVisibleAtLatch)
        {
            session.ProcessSnapshot(Snapshot("view", opening), now.AddSeconds(1), ref changedAt);
            session.ProcessSettled(now.AddSeconds(4), ref changedAt);
            session.ProcessSnapshot(Snapshot("", ""), now.AddSeconds(5), ref changedAt);
            session.ProcessSnapshot(Snapshot("view", opening), now.AddSeconds(6), ref changedAt);
            session.ProcessSnapshot(Snapshot("view", opening), now.AddSeconds(8), ref changedAt);
        }
        session.ProcessSnapshot(Snapshot("view", opening + " " + addition), now.AddSeconds(9), ref changedAt);
        session.ProcessSettled(now.AddSeconds(12), ref changedAt);
        session.ProcessSnapshot(Snapshot("view", opening + " " + corrected), now.AddSeconds(13), ref changedAt);
        session.ProcessSettled(now.AddSeconds(16), ref changedAt);
        var expected = new List<string>();
        if (!alreadyVisibleAtLatch) expected.AddRange([
            "The first thought is already complete.", "The second thought is already complete."]);
        expected.AddRange([addition, corrected]);
        if (!spoken.SequenceEqual(expected))
            failures.Add(provider + ": a baselined conversation replayed the unchanged opening during a thought rewrite: "
                + string.Join(" | ", spoken));
        session.ProcessSnapshot(Snapshot("view", opening, cycle + ":generation:1"),
            now.AddSeconds(17), ref changedAt);
        session.ProcessSettled(now.AddSeconds(20), ref changedAt);
        expected.AddRange(["The first thought is already complete.", "The second thought is already complete."]);
        if (!spoken.SequenceEqual(expected))
            failures.Add(provider + ": baseline duplicate suppression blocked an intentionally repeated new response.");
    }

    private static void CheckRewrittenThoughtTail(List<string> failures)
    {
        foreach (var provider in new[] { "codex-message", "lmstudio-message" })
        {
            using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
            var spoken = new List<string>();
            session.TextAvailable += (_, e) => spoken.Add(e.Text);
            var cycle = provider + ":rewritten-tail";
            var key = cycle + ":working";
            const string first = "The opening thought explains how the monitoring test keeps every completed sentence in order while preserving the remaining unfinished text correctly.";
            const string revised = "The following explanation is now complete.";
            WindowTextSnapshot Snapshot(string text) => new([
                new(key, text, ContainerKey: key, GroupKey: key, IsAggregate: true)],
                ResponseCycleKey: cycle, Context: new("view"));
            var now = DateTimeOffset.UtcNow;
            var changedAt = DateTimeOffset.MinValue;
            session.EstablishBaseline(Snapshot(""), now);
            session.ProcessSnapshot(Snapshot(first + " The following explanation remains unfinished"),
                now.AddSeconds(1), ref changedAt);
            // A small edit causes a full replacement, not an appended suffix.
            // The existing unfinished tail must not prefix the repeated opening.
            session.ProcessSnapshot(Snapshot(first + " " + revised), now.AddSeconds(2), ref changedAt);
            session.ProcessSettled(now.AddSeconds(5), ref changedAt);
            if (!spoken.SequenceEqual(new[] { first, revised }))
                failures.Add(provider + ": a rewritten thought joined its obsolete tail onto the opening and replayed it: "
                    + string.Join(" | ", spoken));
        }

        // Ordinary appends and replacements belonging to another container must
        // not discard pending text as a side effect of the conversation fix.
        foreach (var key in new[] { "ordinary-text", "codex-message:guard:working" })
        {
            var accumulator = new WindowTextAccumulator();
            accumulator.Append(new("Keep this unfinished", true, true, key, "original"));
            accumulator.Append(new(" tail intact.", false, true, key, "original"));
            if (!accumulator.TakeCompletedUnits(false).SequenceEqual(new[] { "Keep this unfinished tail intact." }))
                failures.Add("The replacement fix discarded an ordinary appended tail: " + key);
            accumulator.Append(new("Preserve this pending text", true, true, key, "original"));
            accumulator.Append(new("A separate replacement.", true, true, key, "another", IsReplacement: true));
            if (!accumulator.TakeCompletedUnits(true).Any(unit => unit.Contains("Preserve this pending text", StringComparison.Ordinal)))
                failures.Add("A replacement in another container discarded pending text: " + key);
        }
        foreach (var key in new[] { "ordinary-text", "codex-message:guard:answer" })
        {
            var accumulator = new WindowTextAccumulator();
            accumulator.Append(new("Preserve this pending text", true, true, key, "same"));
            accumulator.Append(new("A replacement outside working text.", true, true, key, "same", IsReplacement: true));
            if (!accumulator.TakeCompletedUnits(true).Any(unit => unit.Contains("Preserve this pending text", StringComparison.Ordinal)))
                failures.Add("The working-text replacement fix escaped its scope: " + key);
        }
    }

    private static void CheckNonModalPopover(List<string> failures)
    {
        // This is the dialog ancestor class captured from the thread-status popover.
        const string popup = "text-default z-50 flex origin-[var(--radix-popover-content-transform-origin)] "
            + "flex-col outline-hidden overflow-y-auto rounded-xl shadow-(--shadow-popover-overlay)";
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        var transitions = 0;
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        session.ContextChanged += (_, _) => transitions++;
        const string first = "The current thought has started.";
        const string second = "The current thought continues without interruption.";
        WindowTextSnapshot Snapshot(string text, bool open)
        {
            var view = new CodexConversationView();
            view.Observe("same-thread", "_transcriptContent_test", true);
            return new([new("codex-message:popover:working", text, IsAggregate: true,
                GroupKey: "codex-message:popover:working")], ResponseCycleKey: "codex-message:popover",
                Context: new(view.Resolve(open && CodexConversationView.IsBlockingOverlay("dialog", popup, ""))));
        }
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(Snapshot("", false), now);
        session.ProcessSnapshot(Snapshot(first + " The current thought", false), now.AddSeconds(1), ref changedAt);
        session.ProcessSnapshot(Snapshot(first + " " + second, true), now.AddSeconds(2), ref changedAt);
        session.ProcessSettled(now.AddSeconds(4), ref changedAt);
        session.ProcessSnapshot(Snapshot(first + " " + second, false), now.AddSeconds(5), ref changedAt);
        session.ProcessSnapshot(Snapshot(first + " " + second, false), now.AddSeconds(7), ref changedAt);
        if (transitions != 0 || !spoken.SequenceEqual(new[] { first, second }))
            failures.Add("The non-modal thread-status popover interrupted and rebaselined the current thought.");
        foreach (var overlay in new[] {
            (Role: "dialog", Class: "settings-dialog", Properties: ""),
            (Role: "dialog", Class: popup, Properties: "modal=true"),
            (Role: "group", Class: popup, Properties: "modal=true"),
            (Role: "dialog", Class: popup, Properties: "label=Status; Modal=True;"),
            (Role: "dialog", Class: "origin-[var(--radix-dialog-content-transform-origin)]", Properties: "") })
            if (!CodexConversationView.IsBlockingOverlay(overlay.Role, overlay.Class, overlay.Properties))
                failures.Add("The popover exemption allowed a blocking modal to keep monitoring active.");
        if (CodexConversationView.IsBlockingOverlay("dialog", popup, "modal=false")
            || CodexConversationView.IsBlockingOverlay("dialog", popup, "label=modal=true"))
            failures.Add("A non-modal popover was mistaken for a blocking dialog.");
    }

    private static void CheckMountedPromptIdentity(List<string> failures)
    {
        using var fp = new WindowTextFingerprinter();
        var adapter = new CodexConversationAdapter { IsResponseActive = false };
        adapter.SetView("history");
        CodexTextPart[] Parts(bool fragment, string bubble = "same-bubble") =>
            (fragment ? new[] {
                new CodexTextPart(CodexTextPartKind.UserText, "Old historical prompt.", UserMessageKey: bubble),
                new CodexTextPart(CodexTextPartKind.UserText, "A mounted fragment.", UserMessageKey: bubble)
            } : new[] { new CodexTextPart(CodexTextPartKind.UserText, "Old historical prompt.", UserMessageKey: bubble) })
            .Concat(new[] {
                new CodexTextPart(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
                new CodexTextPart(CodexTextPartKind.Content, "Old final answer.")
            }).ToArray();
        adapter.Assemble(Parts(true), fp);
        var original = adapter.ResponseCycleKey;
        foreach (var fragment in new[] { false, true, false, true })
        {
            adapter.Assemble(Parts(fragment), fp);
            if (adapter.ResponseCycleKey != original)
                failures.Add("Virtualizing a fragment of the same user bubble changed the historical response identity.");
        }
        adapter.Assemble(Parts(true, "redrawn-bubble"), fp);
        if (adapter.ResponseCycleKey != original)
            failures.Add("Remounting the complete user bubble changed its response identity.");
        adapter.Assemble(Parts(false, "redrawn-bubble"), fp);
        if (adapter.ResponseCycleKey != original)
            failures.Add("The remounted bubble lost its remembered identity when a fragment disappeared.");
        adapter.Assemble(Parts(true, "redrawn-bubble"), fp, generationRevision: 1);
        if (adapter.ResponseCycleKey == original)
            failures.Add("Stable bubble identity suppressed explicit response regeneration.");
        adapter.SetView("another-view");
        adapter.Assemble(Parts(false), fp);
        if (adapter.ResponseCycleKey == original)
            failures.Add("Bubble identity leaked across conversation views.");
    }

    private static void CheckHistoricalScrollThroughAdapter(List<string> failures)
    {
        using var fp = new WindowTextFingerprinter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var adapter = new CodexConversationAdapter { IsResponseActive = false };
        adapter.SetView("history");
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        CodexTextPart[] Turn(string bubble, bool fragment, string answer) => new[] {
            new CodexTextPart(CodexTextPartKind.UserText, "The same prompt.", UserMessageKey: bubble)
        }.Concat(fragment ? new[] {
            new CodexTextPart(CodexTextPartKind.UserText, "A mounted fragment.", UserMessageKey: bubble)
        } : []).Concat(new[] {
            new CodexTextPart(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new CodexTextPart(CodexTextPartKind.Content, answer),
            new CodexTextPart(CodexTextPartKind.FinalAnnouncement, "Response complete")
        }).ToArray();
        WindowTextSnapshot Snapshot(CodexTextPart[] parts) => new(adapter.Assemble(parts, fp),
            ResponseCycleKey: adapter.ResponseCycleKey, Context: new("history"));
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(Snapshot(Turn("old", true, "Old closing paragraph.")), now);
        var oldKey = adapter.ResponseCycleKey;
        session.ProcessSnapshot(Snapshot(Turn("old", false,
            "Old opening paragraph. Old closing paragraph.")), now.AddSeconds(1), ref changedAt);
        session.ProcessSettled(now.AddSeconds(4), ref changedAt);
        if (spoken.Count != 0 || adapter.ResponseCycleKey != oldKey)
            failures.Add("Scrolling changed both the mounted prompt and final body and replayed historical speech.");
        var repeated = Turn("old", false, "Old closing paragraph.")
            .Concat(Turn("new", false, "A genuinely new answer.")).ToArray();
        session.ProcessSnapshot(Snapshot(repeated), now.AddSeconds(5), ref changedAt);
        session.ProcessSettled(now.AddSeconds(8), ref changedAt);
        var newKey = adapter.ResponseCycleKey;
        if (newKey == oldKey || !spoken.SequenceEqual(new[] { "A genuinely new answer." }))
            failures.Add("Stable prompt aliases suppressed a distinct repeated prompt or its fresh answer.");
        session.ProcessSnapshot(Snapshot(Turn("old", true, "Old closing paragraph.")),
            now.AddSeconds(9), ref changedAt);
        session.ProcessSettled(now.AddSeconds(12), ref changedAt);
        if (adapter.ResponseCycleKey != newKey || spoken.Count != 1)
            failures.Add("Scrolling back to the previously mounted prompt moved monitoring backwards.");
    }

    private static void CheckRepeatedPromptMountedSeparately(List<string> failures)
    {
        using var fp = new WindowTextFingerprinter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var adapter = new CodexConversationAdapter { IsResponseActive = false };
        adapter.SetView("repeated-prompts");
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        const string oldAnswer = "The old completed answer.";
        const string freshAnswer = "The fresh completed answer.";
        CodexTextPart[] Turn(string bubble, string answer) => [
            new(CodexTextPartKind.UserText, "Please continue.", UserMessageKey: bubble),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new(CodexTextPartKind.Content, answer),
            new(CodexTextPartKind.FinalAnnouncement, "Response complete")];
        WindowTextSnapshot Snapshot(CodexTextPart[] parts, int generation = 0) => new(
            adapter.Assemble(parts, fp, generation), ResponseCycleKey: adapter.ResponseCycleKey,
            Context: new("repeated-prompts"));
        var now = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        session.EstablishBaseline(Snapshot(Turn("old", oldAnswer)), now);
        var oldKey = adapter.ResponseCycleKey;
        // Alone, a new identical bubble is ambiguous with a complete redraw.
        // Mounting both bubbles establishes that these are distinct turns.
        session.ProcessSnapshot(Snapshot(Turn("new", freshAnswer)), now.AddSeconds(1), ref changedAt);
        session.ProcessSettled(now.AddSeconds(3), ref changedAt);
        session.ProcessSnapshot(Snapshot(Turn("old", oldAnswer).Concat(Turn("new", freshAnswer)).ToArray()),
            now.AddSeconds(4), ref changedAt);
        session.ProcessSettled(now.AddSeconds(6), ref changedAt);
        var freshKey = adapter.ResponseCycleKey;
        if (freshKey == oldKey || !spoken.SequenceEqual(new[] { freshAnswer }))
            failures.Add("Separately mounted identical prompts retained a colliding turn identity and lost the fresh answer.");
        session.ProcessSnapshot(Snapshot(Turn("new", freshAnswer)), now.AddSeconds(7), ref changedAt);
        session.ProcessSnapshot(Snapshot(Turn("old", oldAnswer)), now.AddSeconds(8), ref changedAt);
        session.ProcessSettled(now.AddSeconds(10), ref changedAt);
        if (adapter.ResponseCycleKey != freshKey || spoken.Count != 1)
            failures.Add("Repairing repeated prompt aliases replayed history or lost the latest bubble's identity.");
        var redrawn = Turn("redrawn-old", oldAnswer).Concat(Turn("redrawn-new", freshAnswer)).ToArray();
        session.ProcessSnapshot(Snapshot(redrawn), now.AddSeconds(11), ref changedAt);
        session.ProcessSettled(now.AddSeconds(13), ref changedAt);
        if (adapter.ResponseCycleKey != freshKey || spoken.Count != 1)
            failures.Add("A complete redraw changed the repaired repeated prompt identity or replayed the answer.");
        session.ProcessSnapshot(Snapshot(redrawn, generation: 1), now.AddSeconds(14), ref changedAt);
        session.ProcessSettled(now.AddSeconds(16), ref changedAt);
        if (adapter.ResponseCycleKey == freshKey || !spoken.SequenceEqual(new[] { freshAnswer, freshAnswer }))
            failures.Add("Repairing repeated prompt aliases suppressed an explicit regenerated answer.");
    }

    private static void CheckAlternateConversationView(List<string> failures)
    {
        const string transcriptClass = "relative flex flex-1 shrink-0 flex-col _transcriptContent_121j8_2 mx-auto w-full";
        var candidates = new CodexConversationView();
        candidates.Observe("alpha", transcriptClass, true);
        if (!CodexConversationView.IsContainer(transcriptClass) || candidates.Resolve(false) != "alpha")
            failures.Add("The observed Codex transcript without a scroll-container wrapper was not recognised.");
        candidates.Observe("scroll", "thread-scroll-container overflow-y-auto", true);
        if (candidates.Resolve(false) != "alpha") failures.Add("Adding a scrolling wrapper changed transcript identity.");
        if (candidates.Resolve(true) != "") failures.Add("A dialog did not suspend transcript capture.");
        candidates.Observe("beta", transcriptClass, true);
        if (candidates.Resolve(false) != "") failures.Add("Multiple visible transcripts were not treated as ambiguous.");
        foreach (var chrome in new[] { "group/thread-scroll-layout", "_Navigation_1uxfp_2", "ProseMirror", "_transcriptContentFake" })
            if (CodexConversationView.IsContainer(chrome)) failures.Add("Non-transcript chrome recognised: " + chrome);
        var hidden = new CodexConversationView();
        hidden.Observe("hidden", transcriptClass, false);
        if (hidden.Resolve(false) != "") failures.Add("An offscreen transcript supplied the active view.");
        var legacy = new CodexConversationView();
        legacy.Observe("legacy", "thread-scroll-container overflow-y-auto", true);
        if (legacy.Resolve(false) != "legacy") failures.Add("Legacy Codex layouts lost their scroll-container fallback.");
        legacy.Observe("other", "thread-scroll-container", true);
        if (legacy.Resolve(false) != "") failures.Add("Multiple legacy views were not treated as ambiguous.");

        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        using var fp = new WindowTextFingerprinter();
        var adapter = new CodexConversationAdapter { IsResponseActive = true };
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        WindowTextSnapshot Snapshot(string view, string body)
        {
            var observed = new CodexConversationView();
            observed.Observe(view, transcriptClass, true);
            var recognized = CodexConversationView.IsContainer(transcriptClass);
            if (!recognized) return new([new(view, body)]); // actual generic fallback
            adapter.SetView(observed.Resolve(false));
            var blocks = adapter.Assemble([new(CodexTextPartKind.UserText, view + " prompt."),
                new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
                new(CodexTextPartKind.WorkingContent, body)], fp);
            return new(blocks, ResponseCycleKey: adapter.ResponseCycleKey, Context: new(adapter.ViewKey));
        }
        session.EstablishBaseline(Snapshot("alpha", "Amber test chat is ready."), now);
        session.ProcessSnapshot(Snapshot("beta", "Birch test chat is ready."), now.AddSeconds(1), ref lastChange);
        session.ProcessSnapshot(Snapshot("beta", "Birch test chat is ready."), now.AddSeconds(3), ref lastChange);
        session.ProcessSettled(now.AddSeconds(5), ref lastChange);
        if (spoken.Count != 0) failures.Add("Switching to the alternate Codex view spoke existing history.");
        session.ProcessSnapshot(Snapshot("beta", "Birch test chat is ready. A fresh birch sentence arrived."), now.AddSeconds(6), ref lastChange);
        session.ProcessSettled(now.AddSeconds(8), ref lastChange);
        if (!spoken.SequenceEqual(new[] { "A fresh birch sentence arrived." })) failures.Add("Alternate view switching lost fresh speech or replayed history.");
        session.ProcessSnapshot(Snapshot("alpha", "Amber test chat is ready."), now.AddSeconds(9), ref lastChange);
        session.ProcessSnapshot(Snapshot("alpha", "Amber test chat is ready."), now.AddSeconds(11), ref lastChange);
        session.ProcessSnapshot(Snapshot("alpha", "Amber test chat is ready. A fresh amber sentence arrived."), now.AddSeconds(12), ref lastChange);
        if (!spoken.SequenceEqual(new[] { "A fresh birch sentence arrived.", "A fresh amber sentence arrived." }))
            failures.Add("Returning to the alternate Alpha view replayed history or lost its fresh sentence.");
    }

    private static void CheckMissingUserHeading(List<string> failures)
    {
        foreach (var heading in new[] { true, false })
        {
            using var fingerprint = new WindowTextFingerprinter();
            using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
            var adapter = new CodexConversationAdapter { IsResponseActive = true };
            var spoken = new List<string>();
            session.TextAvailable += (_, e) => spoken.Add(e.Text);
            var now = DateTimeOffset.UtcNow;
            var lastChange = DateTimeOffset.MinValue;
            WindowTextSnapshot Snapshot(string body, bool includeHeading)
            {
                var parts = new List<CodexTextPart>();
                if (includeHeading) parts.Add(new(CodexTextPartKind.UserBoundary, "You said:"));
                parts.Add(new(CodexTextPartKind.UserText, "Synthetic current user message."));
                parts.Add(new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"));
                if (body.Length > 0) parts.Add(new(CodexTextPartKind.WorkingContent, body, 3, "paragraph"));
                return new(adapter.Assemble(parts, fingerprint), ResponseCycleKey: adapter.ResponseCycleKey, Context: new("new-view"));
            }
            session.EstablishBaseline(new([], Context: new("old-view")), now);
            session.ProcessSnapshot(Snapshot("", heading), now.AddSeconds(1), ref lastChange);
            session.ProcessSnapshot(Snapshot("", heading), now.AddSeconds(3), ref lastChange);
            var key = adapter.ResponseCycleKey;
            const string body = "The opening thought is ready. The next thought is also ready.";
            for (var tick = 4; tick <= 8; tick++)
            {
                session.ProcessSnapshot(Snapshot(body, heading), now.AddSeconds(tick), ref lastChange);
                session.ProcessSettled(now.AddSeconds(tick), ref lastChange);
            }
            if (!spoken.SequenceEqual(new[] { "The opening thought is ready.", "The next thought is also ready." }))
                failures.Add($"User heading={heading}: expected two thoughts after switching, got: " + string.Join(" | ", spoken));
            session.ProcessSnapshot(Snapshot(body, !heading), now.AddSeconds(9), ref lastChange);
            if (spoken.Count != 2) failures.Add("Restoring/removing a user heading replayed the response.");
            if (key != adapter.ResponseCycleKey) failures.Add("Restoring/removing a user heading changed the response identity.");
        }

        using var fp = new WindowTextFingerprinter();
        var multi = new CodexConversationAdapter { IsResponseActive = true };
        var blocks = multi.Assemble([
            new(CodexTextPartKind.UserBoundary, "You said:"),
            new(CodexTextPartKind.UserText, "Historical prompt."),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new(CodexTextPartKind.Content, "Historical answer."),
            new(CodexTextPartKind.UserText, "Current prompt first paragraph."),
            new(CodexTextPartKind.UserText, "Current prompt second paragraph."),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new(CodexTextPartKind.WorkingContent, "Only the current thought.")], fp);
        if (blocks.Count != 1 || blocks[0].Text != "Only the current thought.")
            failures.Add("A missing heading merged the current response with historical content.");
        if (multi.Assemble([new(CodexTextPartKind.WorkingContent, "Unanchored historical text.")], fp).Count != 0)
            failures.Add("Assistant text without a user message was accepted as a new response.");

        var bubbles = new CodexConversationAdapter { IsResponseActive = true };
        var adjacent = bubbles.Assemble([
            new(CodexTextPartKind.UserText, "Earlier unanswered prompt.", UserMessageKey: "old-bubble"),
            new(CodexTextPartKind.UserText, "Current prompt first paragraph.", UserMessageKey: "new-bubble"),
            new(CodexTextPartKind.UserText, "Current prompt second paragraph.", UserMessageKey: "new-bubble"),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new(CodexTextPartKind.WorkingContent, "The current response.")], fp);
        var currentKey = bubbles.ResponseCycleKey;
        var explicitParts = new CodexTextPart[] {
            new(CodexTextPartKind.UserBoundary, "You said:"),
            new(CodexTextPartKind.UserText, "Current prompt first paragraph.", UserMessageKey: "redrawn-bubble"),
            new(CodexTextPartKind.UserText, "Current prompt second paragraph.", UserMessageKey: "redrawn-bubble"),
            new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"),
            new(CodexTextPartKind.WorkingContent, "The current response.") };
        bubbles.Assemble(explicitParts, fp);
        if (adjacent.Count != 1 || currentKey != bubbles.ResponseCycleKey)
            failures.Add("Adjacent user bubbles without headings were merged, or a redraw changed their text-based identity.");
        bubbles.IsResponseActive = false;
        var final = bubbles.Assemble(explicitParts.Append(new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:"))
            .Append(new(CodexTextPartKind.Content, "The final answer.")).ToArray(), fp);
        if (!final.Any(block => block.IsFinalResponse && block.Text == "The final answer."))
            failures.Add("Heading recovery lost the working-to-final handoff.");
    }

    private static void CheckShrinkingRedraw(List<string> failures)
    {
        foreach (var cut in new[] { 0, 2, 43, 48 })
        {
            using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
            var spoken = new List<string>();
            session.TextAvailable += (_, e) => spoken.Add(e.Text);
            var now = DateTimeOffset.UtcNow;
            var lastChange = DateTimeOffset.MinValue;
            const string cycle = "codex-message:redraw";
            const string first = "I will inspect the captured messages to find where the text stopped progressing toward the speech output queue.";
            const string second = "The diagnostic application will remain running during this check.";
            const string next = "This genuinely new sentence should still be spoken.";
            var full = first + " " + second;
            WindowTextSnapshot Snapshot(string text) => new([new(cycle + ":working", text, GroupKey: cycle + ":working", IsAggregate: true)],
                ResponseCycleKey: cycle, Context: new("view"));
            session.EstablishBaseline(Snapshot(""), now);
            session.ProcessSnapshot(Snapshot(full), now.AddSeconds(1), ref lastChange);
            session.ProcessSnapshot(Snapshot(full[..cut]), now.AddSeconds(2), ref lastChange);
            session.ProcessSnapshot(new([], ResponseCycleKey: cycle, Context: new("view")), now.AddSeconds(3), ref lastChange);
            session.ProcessSnapshot(Snapshot(full), now.AddSeconds(4), ref lastChange);
            session.ProcessSnapshot(Snapshot(full + " " + next), now.AddSeconds(5), ref lastChange);
            if (!spoken.SequenceEqual(new[] { first, second, next }))
                failures.Add($"A redraw shrinking to {cut} characters replayed or lost speech: " + string.Join(" | ", spoken));
        }

        // Do not solve this by swallowing arbitrary sentence tails globally.
        // Real edits and non-conversation controls must still report changes.
        foreach (var key in new[] { "ordinary-text", "codex-message:guards:working", "lmstudio-message:guards:working" })
        {
            var tracker = new WindowTextDeltaTracker();
            WindowTextSnapshot Snapshot(string text, bool final = false) => new([
                new(key, text, IsAggregate: true, IsFinalResponse: final)]);
            const string original = "The existing complete sentence.";
            tracker.EstablishBaseline(Snapshot(original));
            tracker.Update(Snapshot("The existing"));
            var restored = tracker.Update(Snapshot(original));
            if ((restored.Count == 0) != ConversationMessageKey.IsKnown(key))
                failures.Add("Shrinking redraw handling escaped its conversation scope: " + key);
            var edited = tracker.Update(Snapshot("A genuinely corrected statement."));
            if (edited.Count != 1 || edited[0].Text != "A genuinely corrected statement.")
                failures.Add("Redraw protection swallowed a divergent edit: " + key);
            var final = tracker.Update(Snapshot("A genuinely corrected", final: true));
            if (final.Count != 1 || final[0].Text != "A genuinely corrected")
                failures.Add("Redraw protection replaced final answer text with a longer draft: " + key);
        }

        using var pending = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var pendingSpoken = new List<string>();
        pending.TextAvailable += (_, e) => pendingSpoken.Add(e.Text);
        var at = DateTimeOffset.UtcNow;
        var changedAt = DateTimeOffset.MinValue;
        WindowTextSnapshot Pending(string text) => new([
            new("codex-message:pending:working", text, IsAggregate: true, GroupKey: "codex-message:pending:working")],
            ResponseCycleKey: "codex-message:pending", Context: new("view"));
        pending.EstablishBaseline(Pending(""), at);
        pending.ProcessSnapshot(Pending("This unfinished sentence"), at.AddSeconds(1), ref changedAt);
        pending.ProcessSnapshot(Pending("This"), at.AddSeconds(2), ref changedAt);
        pending.ProcessSnapshot(Pending("This unfinished sentence is now complete."), at.AddSeconds(3), ref changedAt);
        if (!pendingSpoken.SequenceEqual(new[] { "This unfinished sentence is now complete." }))
            failures.Add("A shrinking unfinished draft lost or duplicated its pending opening.");
    }

    private static void CheckDescriptionCapture(List<string> failures)
    {
        if (Include("Elapsed time 3s", true, "description")) failures.Add("Codex's standalone changing description was included as response content.");
        if (!Include("A genuine descriptive paragraph.", true, "description", true)
            || !Include("A generic application's description.", false, "description")
            || !Include("A normal response paragraph.", true, "paragraph", true))
            failures.Add("Description exclusion escaped its Codex metadata scope.");

        using var fingerprinter = new WindowTextFingerprinter();
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var adapter = new CodexConversationAdapter { IsResponseActive = true };
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        WindowTextSnapshot Snapshot(string body, int tick)
        {
            var parts = new List<CodexTextPart>
            {
                new(CodexTextPartKind.UserBoundary, "You said:"),
                new(CodexTextPartKind.UserText, "Synthetic status capture test."),
                new(CodexTextPartKind.AssistantBoundary, "ChatGPT said:")
            };
            var label = $"Elapsed time {tick}s";
            if (tick > 0 && Include(label, true, "description"))
                parts.Add(new(CodexTextPartKind.Content, label, 2, "status"));
            if (body.Length > 0 && Include(body, true, "paragraph", true))
                parts.Add(new(CodexTextPartKind.WorkingContent, body, 3, "paragraph"));
            return new(adapter.Assemble(parts, fingerprinter), ResponseCycleKey: adapter.ResponseCycleKey,
                Context: new("returned-view"));
        }
        session.EstablishBaseline(Snapshot("", 0), now);
        session.ProcessSnapshot(Snapshot("The opening", 1), now.AddSeconds(1), ref lastChange);
        session.ProcessSnapshot(Snapshot("The opening thought is ready.", 2), now.AddSeconds(2), ref lastChange);
        for (var tick = 3; tick <= 8; tick++)
        {
            session.ProcessSnapshot(Snapshot("The opening thought is ready. The next thought is also ready.", tick), now.AddSeconds(tick), ref lastChange);
            session.ProcessSettled(now.AddSeconds(tick), ref lastChange);
        }
        if (!spoken.SequenceEqual(new[] { "The opening thought is ready.", "The next thought is also ready." }))
            failures.Add("A changing description prevented new Codex thoughts from reaching speech: " + string.Join(" | ", spoken));
    }

    private static async Task CheckBaselineDeduplicationAsync(List<string> failures)
    {
        var sessions = new List<(WindowTextMonitoringSession Session, List<string> Spoken, string Cycle)>();
        const string first = "The first new update is ready.";
        const string second = "The second new update is ready.";
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        try
        {
            foreach (var cycle in new[] { "codex-message:initial", "codex-message:returned", "lmstudio-message:initial", "" })
            {
                var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
                var spoken = new List<string>();
                session.TextAvailable += (_, e) => spoken.Add(e.Text);
                sessions.Add((session, spoken, cycle));
                WindowTextSnapshot Snapshot(string view, string text) => new(
                    [new(cycle + ":working", text, GroupKey: cycle + ":working", IsAggregate: true)],
                    ResponseCycleKey: cycle, Context: cycle.Length > 0 ? new(view) : null);
                session.EstablishBaseline(Snapshot("old-view", "Existing historical text."), now);
                if (cycle.EndsWith(":returned", StringComparison.Ordinal))
                {
                    session.ProcessSnapshot(Snapshot("new-view", "Other historical text."), now.AddSeconds(1), ref lastChange);
                    session.ProcessSnapshot(Snapshot("new-view", "Other historical text."), now.AddSeconds(3), ref lastChange);
                }
                if (spoken.Count != 0) failures.Add("Baselining spoke existing history.");
                var key = cycle + ":working";
                session.AppendChanges([new(first + " " + second, false, true, key, GroupKey: key)], ref lastChange);
            }

            // Publish() uses wall-clock time; cross the generic two-second
            // duplicate lifetime to reproduce the observed late redraw.
            await Task.Delay(2300);
            foreach (var (session, spoken, cycle) in sessions)
            {
                var key = cycle + ":working";
                session.AppendChanges([new(first + " " + second, false, true, key, GroupKey: key)], ref lastChange);
                var expected = cycle.Length > 0 ? 2 : 4;
                if (spoken.Count != expected) failures.Add($"Baseline '{cycle}' published {spoken.Count} units; expected {expected}.");
                if (cycle.Length == 0) continue;
                var beforeNewTurn = spoken.Count;
                session.ProcessSnapshot(new([], ResponseCycleKey: cycle + ":next", Context: new(cycle.Contains("returned") ? "new-view" : "old-view")), now.AddSeconds(5), ref lastChange);
                session.AppendChanges([new(first, true, true, key + ":next", GroupKey: key + ":next")], ref lastChange);
                if (spoken.Count != beforeNewTurn + 1) failures.Add("A genuinely new turn could not repeat a sentence.");
            }
        }
        finally { foreach (var item in sessions) item.Session.Dispose(); }
    }
}
