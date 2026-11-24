using PulseReader.Hotkeys;
using PulseReader.Monitoring;
using UIA = Interop.UIAutomationClient;

internal static class LmStudioMonitoringChecks
{
    private static readonly string[] Answer = ["Amber lanterns light the garden path.",
        "A quiet river runs beside the village.", "The listening test is complete."];

    public static async Task RunAsync()
    {
        CheckContinuousConversationFragments();
        CheckCollapsedPreview();
        CheckTableCells();
        CheckThinkingBufferProgress();
        CheckDirectParagraphAnswer();
        CheckThreadFrames();
        CheckRegeneration();
        CheckRapidRegeneration();
        using var fingerprint = new WindowTextFingerprinter();
        var adapter = new LmStudioConversationAdapter();
        var completed = adapter.Assemble("view", Fixture(true), fingerprint);
        if (completed.Blocks is not [{ IsFinalResponse: true }] || completed.Blocks[0].Text != string.Join(' ', Answer))
            throw new InvalidOperationException("LM Studio included user text, model labels, collapsed thinking or footer statistics in the final answer.");
        var tracker = new WindowTextDeltaTracker();
        tracker.EstablishBaseline(completed);
        var recreated = adapter.Assemble("view", Fixture(true, suffix: "recreated"), fingerprint);
        if (tracker.Update(recreated).Count != 0)
            throw new InvalidOperationException("LM Studio replayed the completed message after its accessibility nodes were recreated.");
        var clipped = adapter.Assemble("view", Fixture(true).Select(Hide).ToArray(), fingerprint);
        if (clipped.Blocks.Count != 1 || clipped.Blocks[0].Text != completed.Blocks[0].Text || tracker.Update(clipped).Count != 0)
            throw new InvalidOperationException("Viewport clipping changed LM Studio's turn identity or replayed content.");
        var reopenedThoughts = adapter.Assemble("view", Fixture(false, answer: ""), fingerprint);
        if (reopenedThoughts.Blocks.Count != 0)
            throw new InvalidOperationException("Reopening only the thinking section replayed a sealed turn.");
        var repeated = adapter.Assemble("view", Fixture(true).Concat(Fixture(true, suffix: "second")).ToArray(), fingerprint);
        if (repeated.ResponseCycleKey == completed.ResponseCycleKey || tracker.Update(repeated).Count != 1)
            throw new InvalidOperationException("A genuinely repeated LM Studio turn was mistaken for a redraw.");

        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var streaming = new LmStudioConversationAdapter();
        var output = new List<MonitoredTextEventArgs>();
        var thinkingUpdates = new List<MonitoringThinkingSnapshot>();
        session.TextAvailable += (_, e) => output.Add(e);
        session.ThinkingChanged += (_, e) => { if (e.Snapshot is { } thought) thinkingUpdates.Add(thought); };
        var now = DateTimeOffset.UtcNow;
        var last = DateTimeOffset.MinValue;
        session.ProcessSnapshot(streaming.Assemble("view", [], fingerprint), now.AddSeconds(-2), ref last);
        session.ProcessSnapshot(streaming.Assemble("view", [], fingerprint), now, ref last);
        session.ProcessSnapshot(streaming.Assemble("view", Fixture(false, answer: ""), fingerprint), now.AddSeconds(1), ref last);
        session.ProcessSettled(now.AddSeconds(4), ref last);
        if (output.Count != 0 || !thinkingUpdates.Any(t => t.Expanded && !t.Retired
                && t.Text.Contains("A short public thinking update.", StringComparison.Ordinal))
            || thinkingUpdates.Any(t => t.Text.Contains("Thinking...", StringComparison.Ordinal)))
            throw new InvalidOperationException("LM Studio's thinking prose was lost or its status label was spoken.");
        output.Clear();
        session.ProcessSnapshot(streaming.Assemble("view", Fixture(false, answer: Answer[0]), fingerprint), now.AddSeconds(5), ref last);
        session.ProcessSettled(now.AddSeconds(8), ref last);
        session.ProcessSnapshot(streaming.Assemble("view", Fixture(true, suffix: "collapsed"), fingerprint), now.AddSeconds(9), ref last);
        session.ProcessSettled(now.AddSeconds(12), ref last);
        session.ProcessSnapshot(streaming.Assemble("view", Fixture(true, suffix: "expanded"), fingerprint), now.AddSeconds(13), ref last);
        session.ProcessSettled(now.AddSeconds(16), ref last);
        if (!output.Select(e => e.Text).SequenceEqual(Answer))
            throw new InvalidOperationException("LM Studio's streamed/final handoff duplicated, omitted or polluted the answer: " + string.Join(" | ", output.Select(e => e.Text)));

        var unknown = new LmStudioConversationAdapter().Assemble("view",
            [Group("unknown", Text("Random UI label")), Button("Settings")], fingerprint);
        if (unknown.Blocks.Count != 0) throw new InvalidOperationException("An unknown LM Studio layout was treated as speech.");
        var prose = new LmStudioConversationAdapter().Assemble("view",
            Fixture(true, answer: "Thinking about this takes time. The model name and token rate belong in this sentence."), fingerprint);
        if (prose.Blocks.Count != 1 || !prose.Blocks[0].Text.StartsWith("Thinking about this takes time.", StringComparison.Ordinal))
            throw new InvalidOperationException("LM Studio filtered ordinary answer prose as a thinking label.");

        // The classifier must resolve a hold while every key is still down.
        var pressed = true;
        var detector = new HotkeyHoldGestureDetector(_ => pressed, TimeSpan.FromMilliseconds(40));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        if (await detector.ClassifyAsync(HotkeyDefinition.Default, timeout.Token) != HotkeyGesture.Hold || !pressed)
            throw new InvalidOperationException("Monitoring activation waited for the held keys to be released.");
        var release = detector.WaitForReleaseAsync(HotkeyDefinition.Default, timeout.Token);
        if (release.IsCompleted) throw new InvalidOperationException("Selection copying was allowed before releasing the held chord.");
        pressed = false;
        await release;
        Console.WriteLine("PASS: LM Studio message filtering, redraw identity, thinking/final handoff, repeated turns and held-key activation.");
    }

    internal static void CheckContinuousConversationFragments()
    {
        var filter = new VolatileTextChangeFilter();
        var now = DateTimeOffset.UtcNow;
        const string key = "lmstudio-message:fixture:working";
        string[] chunks = ["A", " complete sentence.", " Another", " complete sentence."];
        var received = new List<string>();
        for (var i = 0; i < chunks.Length; i++)
            received.AddRange(filter.Filter([new(chunks[i], i == 0, true, key)],
                new HashSet<string> { key }, now.AddMilliseconds(i * 100)).Select(c => c.Text));
        if (string.Concat(received) != string.Concat(chunks))
            throw new InvalidOperationException("Continuous conversation fragments stalled while waiting for a quiet interval.");
    }

    internal static void CheckCollapsedPreview()
    {
        using var fp = new WindowTextFingerprinter();
        var nodes = Fixture(false, answer: "");
        LmStudioTextNode Collapse(LmStudioTextNode n) => n with {
            Text = n.Text == "Collapse content" ? "Expand content" : n.Text,
            Expanded = n.Type == 50000 ? false : n.Expanded,
            Children = n.Children.Select(Collapse).ToArray()
        };
        var snapshot = new LmStudioConversationAdapter().Assemble("view", nodes.Select(Collapse).ToArray(), fp);
        if (snapshot.Thinking is not { Expanded: false, Retired: false }
            || snapshot.Blocks.Any(b => b.Text.Contains("public thinking", StringComparison.Ordinal)))
            throw new InvalidOperationException("Collapsed LM Studio preview was eligible for speech.");
        LmStudioTextNode Unknown(LmStudioTextNode n) => n with { Expanded = null, Children = n.Children.Select(Unknown).ToArray() };
        var unknown = new LmStudioConversationAdapter().Assemble("view", nodes.Select(Unknown).ToArray(), fp);
        if (unknown.Thinking?.Expanded != false)
            throw new InvalidOperationException("Thinking was enabled from a button label without an explicit expanded state.");
    }

    internal static void CheckTableCells()
    {
        using var fp = new WindowTextFingerprinter();
        LmStudioTextNode Replace(LmStudioTextNode n) => n.Key == "answer" ? Group("answer",
            new LmStudioTextNode("table", 50036, "", [new("row", 50025, "", [new("cell", 50029, "Radio network", []),
                new("cell2", 50029, "Works without internet", [])])]))
            : n with { Children = n.Children.Select(Replace).ToArray() };
        var snapshot = new LmStudioConversationAdapter().Assemble("view", Fixture(true).Select(Replace).ToArray(), fp);
        if (!snapshot.Blocks.Any(b => b.Text.Contains("Radio network", StringComparison.Ordinal)
            && b.Text.Contains("Works without internet", StringComparison.Ordinal)))
            throw new InvalidOperationException("LM Studio table cell names were omitted from the answer.");
    }

    internal static void CheckDirectParagraphAnswer()
    {
        using var fp = new WindowTextFingerprinter();
        LmStudioTextNode Direct(LmStudioTextNode n) => n.Key == "answer" ? Paragraph(string.Join(' ', Answer))
            : n with { Children = n.Children.Select(Direct).ToArray() };
        var result = new LmStudioConversationAdapter().Assemble("view", Fixture(true).Select(Direct).ToArray(), fp);
        if (result.Blocks.Count != 1 || result.Blocks[0].Text != string.Join(' ', Answer))
            throw new InvalidOperationException("A direct markdown paragraph was omitted from the answer body.");
    }

    private static void CheckThreadFrames()
    {
        using var fp = new WindowTextFingerprinter();
        var adapter = new LmStudioConversationAdapter();
        LmStudioTextNode Frame(string key, LmStudioTextNode[] messages, bool modal = false) =>
            Group("window", new LmStudioTextNode(key, 50026, "",
                [new("message-root", 50026, "", messages, AutomationId: "chat-terminal-messages")],
                AutomationId: "chat-terminal-scroll-container"),
                new("overlay", 50026, "", [], Hidden: !modal, Role: "dialog"));
        var empty = adapter.ReadFrame(Frame("thread-one", []), fp);
        var answered = adapter.ReadFrame(Frame("thread-one", Fixture(true)), fp);
        if (empty.Context?.ViewKey != answered.Context?.ViewKey || string.IsNullOrEmpty(empty.Context?.ViewKey))
            throw new InvalidOperationException("The first message changed the identity of an empty LM Studio chat.");
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var last = DateTimeOffset.MinValue;
        session.ProcessSnapshot(empty, now.AddSeconds(-2), ref last);
        session.ProcessSnapshot(empty, now, ref last);
        session.ProcessSnapshot(answered, now.AddSeconds(1), ref last);
        session.ProcessSettled(now.AddSeconds(4), ref last);
        if (!spoken.SequenceEqual(Answer)) throw new InvalidOperationException("The first short reply was absorbed into the empty-chat baseline.");
        if (adapter.ReadFrame(Frame("thread-one", Fixture(true), modal: true), fp).Context?.ViewKey != "")
            throw new InvalidOperationException("A modal remained eligible for chat reading.");
        if (adapter.ReadFrame(Group("settings", Text("Unrelated UI content")), fp).Blocks.Count != 0)
            throw new InvalidOperationException("Non-thread UI content entered the LM Studio capture.");
    }

    internal static void CheckRegeneration()
    {
        using var fp = new WindowTextFingerprinter();
        var adapter = new LmStudioConversationAdapter();
        LmStudioTextNode Frame(bool generating, bool complete, int version = 2) => Group("window",
            new("thread", 50026, "", [new("messages", 50026, "", WithVersion(Fixture(complete,
                answer: complete ? string.Join(' ', Answer) : ""), version, version), AutomationId: "chat-terminal-messages")],
                AutomationId: "chat-terminal-scroll-container"),
            Button(generating ? "Stop generating" : "Send"));
        var original = adapter.ReadFrame(Frame(false, true, 1), fp);
        var starting = adapter.ReadFrame(Frame(true, true, 1), fp);
        if (starting.ResponseCycleKey != original.ResponseCycleKey)
            throw new InvalidOperationException("The previous answer was reassigned before the replacement body appeared.");
        var restarted = adapter.ReadFrame(Frame(true, false), fp);
        if (restarted.ResponseCycleKey == original.ResponseCycleKey || restarted.Thinking?.Retired != false)
            throw new InvalidOperationException("Regeneration retained the old completed identity or retired its new thinking.");
        var stable = adapter.ReadFrame(Frame(true, false), fp);
        var complete = adapter.ReadFrame(Frame(false, true), fp);
        if (stable.ResponseCycleKey != restarted.ResponseCycleKey || complete.ResponseCycleKey != restarted.ResponseCycleKey
            || complete.Blocks.Count != 1)
            throw new InvalidOperationException("Regeneration changed identity during streaming or lost the completed reply.");
        var redraw = adapter.ReadFrame(Frame(false, false), fp);
        if (redraw.ResponseCycleKey != complete.ResponseCycleKey || redraw.Thinking?.Retired != true)
            throw new InvalidOperationException("Reopening historical thinking was mistaken for regeneration.");
        adapter.ReadFrame(Frame(true, true), fp);
        adapter.ReadFrame(Frame(false, true), fp);
        var abandonedStart = adapter.ReadFrame(Frame(false, false), fp);
        if (abandonedStart.ResponseCycleKey != complete.ResponseCycleKey || abandonedStart.Thinking?.Retired != true)
            throw new InvalidOperationException("An abandoned generation signal made a later historical panel eligible.");

        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var output = new List<string>();
        session.TextAvailable += (_, e) => output.Add(e.Text);
        var now = DateTimeOffset.UtcNow;
        var last = DateTimeOffset.MinValue;
        session.ProcessSnapshot(original, now.AddSeconds(-2), ref last);
        session.ProcessSnapshot(original, now, ref last);
        session.ProcessSnapshot(restarted, now.AddSeconds(1), ref last);
        session.ProcessSnapshot(complete, now.AddSeconds(2), ref last);
        session.ProcessSettled(now.AddSeconds(5), ref last);
        session.ProcessSnapshot(redraw, now.AddSeconds(6), ref last);
        session.ProcessSettled(now.AddSeconds(9), ref last);
        if (!output.SequenceEqual(Answer))
            throw new InvalidOperationException("The regenerated answer was lost, reordered or repeated downstream.");
    }

    internal static void CheckRapidRegeneration()
    {
        using var fp = new WindowTextFingerprinter();
        var adapter = new LmStudioConversationAdapter();
        var first = adapter.Assemble("view", Fixture(true, answer: "Ready."), fp);
        var second = adapter.Assemble("view", WithVersion(Fixture(true, answer: "Ready."), 2, 2), fp);
        if (second.ResponseCycleKey == first.ResponseCycleKey || second.Blocks.Count != 1)
            throw new InvalidOperationException("A completed regeneration between capture samples was lost.");
        var older = adapter.Assemble("view", WithVersion(Fixture(true, answer: "Old answer."), 1, 2), fp);
        if (older.Blocks.Count != 0 || older.Context?.ViewKey != "")
            throw new InvalidOperationException("Browsing an older generated response produced new speech.");
        var current = adapter.Assemble("view", WithVersion(Fixture(true, answer: "Ready."), 2, 2), fp);
        if (current.ResponseCycleKey != second.ResponseCycleKey)
            throw new InvalidOperationException("Returning to the current version restarted its generation.");
    }

    private static LmStudioTextNode[] WithVersion(LmStudioTextNode[] nodes, int selected, int total)
    {
        LmStudioTextNode Version(LmStudioTextNode n) => n.Key == "toolbar"
            && n.Children.Any(c => c.Text == "Continue assistant message")
                ? n with { Children = [Group("version-controls", Button("Previous generated response"),
                    Text($"{selected} / {total}"), Button("Next generated response")), .. n.Children] }
                : n with { Children = n.Children.Select(Version).ToArray() };
        return nodes.Select(Version).ToArray();
    }

    public static void CheckVisibleRecovery(IntPtr window)
    {
        using var fp = new WindowTextFingerprinter();
        var automation = new UIA.CUIAutomation8();
        var adapter = new LmStudioConversationAdapter();
        const string expected = "Copper bells signal a fresh response. Green boats return to the harbour. The recovery test is complete.";
        for (var i = 0; i < 8; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = adapter.TryRead(automation, automation.ElementFromHandle(window), fp);
            if (snapshot is null || snapshot.Blocks.Count != 1 || snapshot.Blocks[0].Text != expected)
                throw new InvalidOperationException($"Recovery reply capture failed at sample {i}, blocks={snapshot?.Blocks.Count}.");
            Console.WriteLine($"Recovery capture {i + 1}: correct, {watch.ElapsedMilliseconds} ms.");
        }
    }

    public static void CheckEmptyThread(IntPtr window)
    {
        using var fp = new WindowTextFingerprinter();
        var automation = new UIA.CUIAutomation8();
        var adapter = new LmStudioConversationAdapter();
        string? key = null;
        for (var i = 0; i < 5; i++)
        {
            var snapshot = adapter.TryRead(automation, automation.ElementFromHandle(window), fp);
            if (snapshot?.Context is not { ViewKey.Length: > 0 } context || snapshot.Blocks.Count != 0)
                throw new InvalidOperationException("An empty LM Studio chat did not expose an eligible, silent thread context.");
            key ??= context.ViewKey;
            if (key != context.ViewKey) throw new InvalidOperationException("The empty thread identity changed without navigation.");
        }
        Console.WriteLine("PASS: empty LM Studio thread is eligible, stable and silent across five captures.");
    }

    private static void CheckThinkingBufferProgress()
    {
        var buffer = new MonitoringThinkingBuffer();
        var text = "First sentence. Second sentence.";
        buffer.Update(new("turn", text, true, false));
        if (buffer.Take()?.Text != "First sentence.") throw new InvalidOperationException("Missing first thinking unit.");
        buffer.Update(null);
        buffer.Update(new("turn", text + " Third sentence.", true, false));
        if (buffer.Take()?.Text != "Second sentence.") throw new InvalidOperationException("A temporary missing panel reset thinking progress.");
        for (var i = 0; i < 100; i++)
        {
            text += $" Another complete sentence number {i}.";
            buffer.Update(new("turn", text, true, false));
        }
        if (buffer.PendingCount < 99) throw new InvalidOperationException("Long thinking stopped producing complete units.");
        buffer.Update(new("turn", text, true, true));
        buffer.Update(new("turn", text, true, false));
        if (buffer.PendingCount != 0) throw new InvalidOperationException("A retired thought stream reopened.");
        if (!buffer.Update(new("oversized", new string('x', 100_001), true, false))
            || buffer.PendingCount != 0) throw new InvalidOperationException("Thinking exceeded its bounded buffer.");
    }

    public static void CheckVisibleTable(IntPtr window)
    {
        using var fingerprint = new WindowTextFingerprinter();
        var automation = new UIA.CUIAutomation8();
        var source = automation.ElementFromHandle(window);
        var snapshot = new LmStudioConversationAdapter().TryRead(automation, source, fingerprint);
        var tables = source.FindAll(UIA.TreeScope.TreeScope_Descendants,
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId, 50036));
        if (tables.Length == 0 || snapshot is null || !snapshot.Blocks.Any(b => b.IsFinalResponse))
            throw new InvalidOperationException("The current LM Studio fixture must have a completed answer and a table.");
        var cells = tables.GetElement(tables.Length - 1).FindAll(UIA.TreeScope.TreeScope_Descendants,
            automation.CreateOrCondition(
                automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId, 50029),
                automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId, 50034)));
        var answer = string.Join('\n', snapshot.Blocks.Where(b => b.IsFinalResponse).Select(b => b.Text));
        var checkedCells = 0;
        for (var i = 0; i < cells.Length; i++)
        {
            var value = cells.GetElement(i).CurrentName?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (!answer.Contains(value.TrimEnd('.', '!', '?'), StringComparison.Ordinal))
                throw new InvalidOperationException($"The captured answer omitted table cell {i}.");
            checkedCells++;
        }
        if (checkedCells < 3) throw new InvalidOperationException("Too few table cells exposed to verify capture.");
        Console.WriteLine($"PASS: live LM Studio answer includes all {checkedCells} exposed table cells/headers; thinking retired={snapshot.Thinking?.Retired}.");
    }

    // Mirrors the raw UIA hierarchy observed in LM Studio, including the
    // collapsed thinking subtree which remains exposed as visible text.
    private static LmStudioTextNode[] Fixture(bool complete, string suffix = "", string? answer = null)
    {
        answer ??= string.Join(' ', Answer);
        var sections = new List<LmStudioTextNode> {
            Group("thinking" + suffix, Button(complete ? "Expand content" : "Collapse content"),
                Text(complete ? "Thought for 3 seconds" : "Thinking..."),
                Group("thought-body" + suffix, Paragraph("A short public thinking update."), Paragraph("An unfinished thought")))
        };
        if (answer.Length > 0) sections.Add(Group("answer" + suffix, Paragraph(answer)));
        if (complete) sections.AddRange([Button("Hover to view message generation info"), Text("33.5 tokens/sec"),
            Group("metrics", Text("120 tokens")), Text("Finished")]);
        return [Group("user-header", Text("You")),
            Group("user" + suffix, Group("toolbar", Button("Copy message"), Button("Delete message")),
                Group("body", Group("markdown", Paragraph("Read three test sentences, not this prompt.")))),
            Text("test-model-name"),
            Group("assistant" + suffix, Group("toolbar", Button("Copy message"), Button("Continue assistant message")),
                Group("content" + suffix, sections.ToArray()))];
    }
    private static LmStudioTextNode Group(string key, params LmStudioTextNode[] children) => new(key, 50026, "", children);
    private static LmStudioTextNode Text(string text) => new(Guid.NewGuid().ToString(), 50020, text, []);
    private static LmStudioTextNode Paragraph(string text) => new(Guid.NewGuid().ToString(), 50020, "", [Text(text)]);
    private static LmStudioTextNode Button(string text) => new(Guid.NewGuid().ToString(), 50000, text, [],
        Expanded: text == "Collapse content" ? true : text == "Expand content" ? false : null);
    private static LmStudioTextNode Hide(LmStudioTextNode node) => node with { Hidden = true, Children = node.Children.Select(Hide).ToArray() };

    public static void CheckVisibleFixture(IntPtr window)
    {
        using var fingerprint = new WindowTextFingerprinter();
        var automation = new UIA.CUIAutomation8();
        var snapshot = new LmStudioConversationAdapter().TryRead(automation, automation.ElementFromHandle(window), fingerprint);
        if (snapshot is null || snapshot.Blocks.Count != 1 || !snapshot.Blocks[0].IsFinalResponse
            || string.Join(' ', snapshot.Blocks[0].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) != string.Join(' ', Answer))
            throw new InvalidOperationException($"LM Studio's visible three-sentence fixture did not produce only its final answer (blocks={snapshot?.Blocks.Count}).");
        Console.WriteLine("PASS: live LM Studio capture contains exactly the three expected answer sentences, without prompt, thoughts, model name or statistics.");
    }
}
