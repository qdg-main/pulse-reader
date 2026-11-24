using PulseReader.Monitoring;
using UIA = Interop.UIAutomationClient;

internal static class DiscordMonitoringChecks
{
    public static void Run()
    {
        CheckReviewRegressions();
        CheckAttributedFormatting();
        CheckStructuredMessagePreparation();
        CheckNewcomerMetadata();
        CheckInlineMentions();
        CheckSubmittedDraft();
        var now = DateTimeOffset.UtcNow;
        var id = Snowflake(now.AddSeconds(2));
        var old = Snowflake(now.AddMinutes(-2));
        var reply = Node(id: "message-reply-context-" + id, children: [
            Node(id: "message-content-123", children: [Text("Quoted old reply, do not speak.")])]);
        var first = Message(id, "Alice", "First message.", reply: reply);
        var second = Message(id + 1, "Bob", "Second message.");
        var frame = Frame(first, second);
        var snapshot = DiscordConversationAdapter.ReadFrame(frame);
        Assert(snapshot.Context?.ViewKey == "discord:42", "Conversation identity did not use the channel ID.");
        Assert(snapshot.Blocks.Select(b => b.Text).SequenceEqual(["Alice. First message.", "Bob. Second message."]),
            "Author/body extraction included reply, role, timestamp, or surrounding UI.");
        var grouped = Message(id + 2, "Alice", "Another line.", grouped: true);
        Assert(DiscordConversationAdapter.ReadFrame(Frame(first, grouped)).Blocks.Last().Text == "Alice. Another line.",
            "A grouped message without its visible header lost its author.");
        var own = Message(id + 3, "Local User", "My own message.");
        Assert(DiscordConversationAdapter.ReadFrame(Frame(own)).Blocks.Single().Text == "Local User. My own message.",
            "Display-name equality alone suppressed a message without submission evidence.");
        var edited = first with { Children = [first.Children[0] with { Children = [
            first.Children[0].Children.First(n => n.Role == "heading"),
            Node(id: "message-content-" + id, children: [Text("Text"), Node(classes: "edited_c19a55", children: [Text("(edited)")]),
                Node(classes: "hiddenVisually_b18fe2", children: [Text("hidden timestamp")])]) ] }] };
        Assert(DiscordConversationAdapter.ReadFrame(Frame(edited)).Blocks.Single().Text == "Alice. Text", "Edited timestamp metadata leaked.");
        Assert(DiscordConversationAdapter.ReadFrame(Node(children: [frame, frame])).Context?.ViewKey == "", "Ambiguous message lists were accepted.");
        Assert(DiscordConversationAdapter.ReadFrame(Node(children: [frame, Node(role: "dialog")])).Context?.ViewKey == "", "A dialog did not suspend capture.");
        Assert(DiscordConversationAdapter.ReadFrame(Node(children: [first])).Context?.ViewKey == "", "A row outside the conversation list was accepted.");
        Assert(DiscordConversationAdapter.ReadFrame(Frame(first, Message(id + 5, "Elsewhere", "Other channel", channel: "43"))).Context?.ViewKey == "",
            "Mixed channel identities were accepted.");

        var tracker = new DiscordMessageTracker();
        tracker.EstablishBaseline(DiscordConversationAdapter.ReadFrame(Frame(Message(old, "Old", "History."))), now);
        tracker.Observe(snapshot, now.AddSeconds(3));
        Assert(tracker.TakeReady(now.AddSeconds(3)).Count == 0, "Posts were released before settling.");
        Assert(tracker.TakeReady(now.AddSeconds(4)).Select(r => r.DisplayText).SequenceEqual(["Alice. First message.", "Bob. Second message."]), "Posts were lost or reordered.");
        tracker.Observe(DiscordConversationAdapter.ReadFrame(Frame(edited, second)), now.AddSeconds(5));
        Assert(tracker.TakeReady(now.AddSeconds(6)).Count == 0, "Editing an existing post replayed it.");
        tracker.Observe(DiscordConversationAdapter.ReadFrame(Frame(Message(old + 1, "Older", "Scrolled history."))), now.AddSeconds(7));
        Assert(tracker.TakeReady(now.AddSeconds(8)).Count == 0, "Previously unseen historical posts were read.");
        var repeated = DiscordConversationAdapter.ReadFrame(Frame(Message(id + 8, "Alice", "First message."), Message(id + 9, "Alice", "First message.")));
        tracker.Observe(repeated, now.AddSeconds(9));
        Assert(tracker.TakeReady(now.AddSeconds(10)).Count == 2, "Distinct identical posts were incorrectly deduplicated.");
        tracker.EstablishBaseline(repeated, now.AddSeconds(11));
        tracker.Observe(repeated, now.AddSeconds(12));
        Assert(tracker.TakeReady(now.AddSeconds(13)).Count == 0, "Returning to a channel replayed its history.");
        var laterId = Snowflake(now.AddSeconds(14));
        var pendingPost = DiscordConversationAdapter.ReadFrame(Frame(Message(laterId, "Alice", "Unfinished")));
        tracker.Observe(pendingPost, now.AddSeconds(14));
        tracker.Observe(DiscordConversationAdapter.ReadFrame(Frame(Message(laterId, "Alice", "Finished sentence."))), now.AddSeconds(14.5));
        Assert(tracker.TakeReady(now.AddSeconds(15)).Count == 0, "A changing post was released before its final body settled.");
        Assert(tracker.TakeReady(now.AddSeconds(16)).Select(r => r.DisplayText).SequenceEqual(["Alice. Finished sentence."]), "A pending post did not use its settled body.");
        tracker.Observe(DiscordConversationAdapter.ReadFrame(Frame(Message(laterId + 1, "Alice", "Deleted before speech."))), now.AddSeconds(17));
        tracker.Observe(new([], Context: new("discord:42"), IsDiscreteFeed: true), now.AddSeconds(17.2));
        Assert(tracker.TakeReady(now.AddSeconds(18)).Count == 0, "A removed pending message was spoken.");

        // Full session path: baseline, messages, context disruption, new channel.
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var output = new List<string>();
        session.TextAvailable += (_, e) =>
        {
            Assert(e.IsDiscreteMessage, "A Discord post lost its discrete-message queue marker.");
            output.Add(e.Text);
        };
        var last = DateTimeOffset.MinValue;
        var baseline = DiscordConversationAdapter.ReadFrame(Frame(Message(old, "Old", "History.")));
        session.ProcessSnapshot(baseline, now.AddSeconds(-2), ref last);
        session.ProcessSnapshot(baseline, now, ref last);
        session.ProcessSnapshot(snapshot, now.AddSeconds(3), ref last);
        session.ProcessSettled(now.AddSeconds(4), ref last);
        session.ProcessSnapshot(repeated, now.AddSeconds(9), ref last);
        session.ProcessSettled(now.AddSeconds(10), ref last);
        Assert(output.SequenceEqual(["Alice. First message.", "Bob. Second message.", "Alice. First message.", "Alice. First message."]),
            "The shared session dropped or combined discrete posts.");
        session.ProcessSnapshot(DiscordConversationAdapter.ReadFrame(Frame(Message(id + 10, "Pending", "Do not read."))), now.AddSeconds(11), ref last);
        session.ProcessSnapshot(DiscordConversationAdapter.ReadFrame(Node()), now.AddSeconds(11.1), ref last);
        session.ProcessSettled(now.AddSeconds(12), ref last);
        Assert(output.Count == 4, "Pending old-channel speech escaped context suspension.");
        var next = DiscordConversationAdapter.ReadFrame(Frame(Message(id + 11, "Baseline", "Other channel history.", channel: "43")));
        session.ProcessSnapshot(next, now.AddSeconds(13), ref last);
        session.ProcessSnapshot(next, now.AddSeconds(15), ref last);
        var freshId = Snowflake(now.AddSeconds(16));
        session.ProcessSnapshot(DiscordConversationAdapter.ReadFrame(Frame(Message(freshId, "Charlie", "New channel post.", channel: "43"))), now.AddSeconds(17), ref last);
        session.ProcessSettled(now.AddSeconds(18), ref last);
        Assert(output.Count == 5 && output[^1] == "Charlie. New channel post.", "Channel switching did not resume on new arrivals only.");
        Console.WriteLine("PASS: Discord author/body scope, grouped names, own messages, history, edits, repeated posts, ordering, dialogs and channel switching.");
    }

    private static void CheckAttributedFormatting()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (body, expected) in new[] {
            ("{\n  \"status\": \"ready\",\n  \"count\": 3\n}", "Alice. Status: ready. Count: 3."),
            ("```cs\nvar x = 1;\nConsole.WriteLine(x);\n```", "Alice. Code block."),
            ("# Heading", "Alice. Heading"),
            ("- [x] Finished item", "Alice. Finished item"),
            ("Plain sentence.", "Alice. Plain sentence.") })
        {
            var tracker = new DiscordMessageTracker();
            tracker.EstablishBaseline(new([]), now);
            tracker.Observe(DiscordConversationAdapter.ReadFrame(Frame(Message(Snowflake(now.AddSeconds(1)), "Alice", body))), now.AddSeconds(1));
            var request = tracker.TakeReady(now.AddSeconds(2)).Single();
            var prepared = request.Prepare();
            Assert(prepared.BuildSpokenText() == expected,
                "The author prefix changed body formatting: " + prepared.BuildSpokenText());
            Assert(prepared.Units.All(u => u.SourceRange.Slice(prepared.OriginalText) == u.OriginalText),
                "Attribution broke source-range mapping.");
            Assert(string.Concat(prepared.BuildNavigableSpeechChunks().Select(c => c.Text)) == expected,
                "The formatted body did not reach navigable synthesis chunks intact.");
        }
    }

    private static void CheckStructuredMessagePreparation()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var body in new[] {
            "| Name | Status |\n| --- | --- |\n| Alice | Ready |\n| Bob | Waiting |",
            "> First quotation.\n> Second quotation.",
            "Ten voices available\nTwo backends installed\nNo errors reported",
            "Wait... now continue.",
            "# Heading\n- [x] First task\n- [ ] Second task\n\n```cs\nvar x = 1;\n```" })
        {
            var tracker = new DiscordMessageTracker();
            tracker.EstablishBaseline(new([]), now);
            tracker.Observe(DiscordConversationAdapter.ReadFrame(Frame(Message(Snowflake(now.AddSeconds(1)), "Alice", body))), now.AddSeconds(1));
            var requests = tracker.TakeReady(now.AddSeconds(2));
            var original = PulseReader.TextPreparation.SpokenTextFormatter.Format(
                PulseReader.TextPreparation.ReadingUnitAnalyzer.Prepare(body));
            var expectedUnits = original.Units.Where(u => !string.IsNullOrWhiteSpace(u.SpokenText)).ToArray();
            var expected = expectedUnits.Select((u, i) => (i == 0 ? "Alice. " : "") + u.SpokenText).ToArray();
            var prepared = requests.Select(r => r.Prepare()).ToArray();
            Assert(prepared.Select(p => p.BuildSpokenText()).SequenceEqual(expected),
                "Splitting Discord text lost whole-message formatting: " + string.Join(" | ", prepared.Select(p => p.BuildSpokenText())));
            Assert(requests.Count(r => r.Attribution.Length > 0) == 1, "The author was not preserved exactly once.");
            for (var i = 0; i < prepared.Length; i++)
            {
                var unit = prepared[i].Units.Single();
                Assert(unit.Index == 0 && unit.SourceRange.Slice(prepared[i].OriginalText) == unit.OriginalText,
                    "A prepared Discord unit retained an invalid source range or navigation index.");
                Assert(unit.Kind == expectedUnits[i].Kind && unit.PauseAfterMilliseconds == expectedUnits[i].PauseAfterMilliseconds,
                    "Splitting Discord text lost unit kind or pause metadata.");
            }
        }
    }

    private static void CheckReviewRegressions()
    {
        var now = DateTimeOffset.UtcNow;
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var last = DateTimeOffset.MinValue;
        DiscordTextNode Routed(params DiscordTextNode[] rows) => Node(type: 50030, id: "RootWebArea", role: "document", children: [Frame(rows)])
            with { DocumentUrl = "https://discord.com/channels/@me/42" };
        var empty = DiscordConversationAdapter.ReadFrame(Routed());
        Assert(empty.Context?.ViewKey == "discord:42", "An empty Discord conversation did not expose its channel identity.");
        session.ProcessSnapshot(empty, now.AddSeconds(-2), ref last);
        session.ProcessSnapshot(empty, now, ref last);
        var row = Message(Snowflake(now.AddSeconds(1)), "Local User", "First sentence. Second sentence. Third sentence.");
        session.ProcessSnapshot(DiscordConversationAdapter.ReadFrame(Routed(row)), now.AddSeconds(1), ref last);
        session.ProcessSettled(now.AddSeconds(2), ref last);
        Assert(spoken.SequenceEqual(["Local User. First sentence.", "Second sentence.", "Third sentence."]),
            "The first post was lost, a matching display name was suppressed, or a long post was not split into reading units.");
        session.ProcessSnapshot(DiscordConversationAdapter.ReadFrame(Routed(row)), now.AddSeconds(3), ref last);
        session.ProcessSettled(now.AddSeconds(4), ref last);
        Assert(spoken.Count == 3, "Splitting a Discord post replayed its units on redraw.");
        foreach (var route in new[] { "", "https://discord.com/channels/@me", "https://example.com/channels/@me/42",
            "https://discord.com/channels/@me/not-a-channel" })
            Assert(DiscordConversationAdapter.ReadFrame(Routed() with { DocumentUrl = route }).Context?.ViewKey == "",
                "An invalid empty-conversation route was accepted.");
        var conflicting = Node(children: [Routed(), Node(type: 50030, id: "RootWebArea") with
            { DocumentUrl = "https://discord.com/channels/@me/43" }]);
        Assert(DiscordConversationAdapter.ReadFrame(conflicting).Context?.ViewKey == "",
            "Ambiguous document routes were accepted.");

        // Body boundaries are shared with selected text, including abbreviations,
        // lists and code. Dots in the display name must not create separate units.
        foreach (var body in new[] { "Dr. Smith arrived. Next sentence.", "First item\nSecond item\nThird item",
            "```cs\nvar x = 1;\nConsole.WriteLine(x);\n```\nDone.", string.Join(" ", Enumerable.Repeat("A lengthy sentence.", 80)) })
        {
            var tracker = new DiscordMessageTracker();
            tracker.EstablishBaseline(empty, now);
            tracker.Observe(DiscordConversationAdapter.ReadFrame(Routed(Message(Snowflake(now.AddSeconds(1)), "A.B. Person", body))), now.AddSeconds(1));
            var units = PulseReader.TextPreparation.ReadingUnitAnalyzer.Prepare(body).Units.Select(u => u.OriginalText).ToArray();
            units[0] = "A.B. Person. " + units[0];
            Assert(tracker.TakeReady(now.AddSeconds(2)).Select(r => r.DisplayText).SequenceEqual(units),
                "Discord did not preserve selected-text boundaries or the single author prefix.");
        }
    }

    internal static WindowTextSnapshot QueueFixture(DateTimeOffset now, bool includePost,
        string body = "First sentence. Second sentence. Third sentence.") =>
        DiscordConversationAdapter.ReadFrame(Node(type: 50030, id: "RootWebArea", children: [
            includePost ? Frame(Message(Snowflake(now), "Alice", body)) : Frame()])
            with { DocumentUrl = "https://discord.com/channels/@me/42" });

    private static void CheckSubmittedDraft()
    {
        using var fingerprints = new WindowTextFingerprinter();
        var now = DateTimeOffset.UtcNow;
        var old = Message(Snowflake(now.AddMinutes(-1)), "Other", "Old message.");
        var own = Message(Snowflake(now.AddSeconds(3)), "Different server nickname", "This is my submitted draft.");
        using var session = new WindowTextMonitoringSession(new IntPtr(1));
        var spoken = new List<string>();
        session.TextAvailable += (_, e) => spoken.Add(e.Text);
        var last = DateTimeOffset.MinValue;
        WindowTextSnapshot Read(string draft, params DiscordTextNode[] rows) =>
            DiscordConversationAdapter.ReadFrame(WithComposer(draft, rows), fingerprinter: fingerprints);
        session.ProcessSnapshot(Read("", old), now.AddSeconds(-2), ref last);
        session.ProcessSnapshot(Read("", old), now, ref last);
        session.ProcessSnapshot(Read("This is my submitted draft.", old), now.AddSeconds(1), ref last);
        session.ProcessSnapshot(Read("", old, own), now.AddSeconds(3), ref last);
        session.ProcessSettled(now.AddSeconds(4), ref last);
        Assert(spoken.Count == 0, "A submitted Discord draft was read back under a different server nickname.");
        // A second post with the same text is not another copy of our submission.
        var repeated = Message(Snowflake(now.AddSeconds(5)), "Other Person", "This is my submitted draft.");
        session.ProcessSnapshot(Read("", old, own, repeated), now.AddSeconds(5), ref last);
        session.ProcessSettled(now.AddSeconds(6), ref last);
        Assert(spoken.SequenceEqual(["Other Person. This is my submitted draft."]), "One submitted draft suppressed multiple new messages.");

        var filter = new DiscordSubmittedDraftFilter();
        var draft = fingerprints.Create("**This is my submitted draft.**");
        var body = fingerprints.Create("This is my submitted draft.");
        filter.Reset([]);
        filter.Observe([draft], now);
        Assert(!filter.Consume(body, now, now), "An unsent draft suppressed a matching incoming message.");
        filter.Observe([fingerprints.Create("Revised draft")], now.AddSeconds(1));
        Assert(!filter.Consume(body, now.AddSeconds(1), now.AddSeconds(1)), "Editing a draft was mistaken for sending it.");
        filter.Observe([draft], now.AddSeconds(2));
        filter.Observe([], now.AddSeconds(3));
        Assert(filter.Consume(body, now.AddSeconds(3), now.AddSeconds(4)), "Formatted draft did not match the complete rendered message text.");
        Assert(!filter.Consume(body, now.AddSeconds(4), now.AddSeconds(4)), "Submission evidence was not single-use.");
        filter.Observe([draft], now.AddSeconds(5));
        filter.Observe([], now.AddSeconds(6));
        Assert(!filter.Consume(body, now.AddSeconds(12), now.AddSeconds(12)), "Expired draft suppressed a later message.");
        filter.Observe([draft], now.AddSeconds(13));
        filter.Observe(null, now.AddSeconds(14));
        filter.Observe([], now.AddSeconds(15));
        Assert(!filter.Consume(body, now.AddSeconds(15), now.AddSeconds(15)), "An unavailable composer was treated as a sent draft.");
        filter.Observe([draft], now.AddSeconds(16));
        filter.Observe([], now.AddSeconds(17));
        filter.Reset([]);
        Assert(!filter.Consume(body, now.AddSeconds(18), now.AddSeconds(18)), "Submission evidence survived a context reset.");
        var unknownComposer = DiscordConversationAdapter.ReadFrame(Frame(old), fingerprinter: fingerprints);
        Assert(unknownComposer.EditableFingerprints is null, "An unrelated editor was treated as the Discord composer.");
        var multiple = WithComposer("first", old);
        multiple = multiple with { Children = multiple.Children.Append(WithComposer("second").Children.Last()).ToArray() };
        Assert(DiscordConversationAdapter.ReadFrame(multiple, fingerprinter: fingerprints).EditableFingerprints is null,
            "Ambiguous composers were treated as one draft.");

        var fast = new DiscordMessageTracker();
        fast.EstablishBaseline(Read("", old), now);
        fast.ObserveComposer([draft], now.AddSeconds(1));
        fast.ObserveComposer([], now.AddSeconds(2));
        fast.Observe(Read("", old, own), now.AddSeconds(3));
        Assert(fast.TakeReady(now.AddSeconds(4)).Count == 0, "Focused-composer sampling was not connected to discrete message suppression.");
    }

    private static DiscordTextNode WithComposer(string draft, params DiscordTextNode[] rows)
    {
        var frame = Frame(rows);
        return frame with { Children = frame.Children.Append(Node(type: 50004, role: "textbox", classes: "markup__75297 editor__1b31f slateTextArea_ec4baf", children: [Text(draft)])).ToArray() };
    }

    private static void CheckInlineMentions()
    {
        foreach (var children in new[] { Array.Empty<DiscordTextNode>(), new[] { Text("@Mentioned Person") } })
        {
            var row = Message(123, "Author", "placeholder");
            var article = row.Children.Single();
            row = row with { Children = [article with { Children = article.Children.Select(n =>
                n.Id == "message-content-123" ? n with { Children = [
                    Node(type: 50000, role: "button", classes: "mention wrapper_f61d60 interactive", text: "@Mentioned Person", children: children),
                    Text(" spamming"), Node(type: 50000, role: "button", text: "Unrelated action") ] } : n).ToArray() }] };
            var output = DiscordConversationAdapter.ReadFrame(Frame(row)).Blocks.Single().Text;
            Assert(output == "Author. @Mentioned Person spamming", "Inline mention was omitted, repeated, or mixed with another control: " + output);
        }
    }

    private static void CheckNewcomerMetadata()
    {
        const string badge = "I'm new here, say hi!";
        var header = Message(122, "New Person", "First message.");
        header = header with { Children = [header.Children[0] with
        { Text = "New Person " + badge + " , First message. , 14:21" }] };
        var row = Message(123, "New Person", "Second message.", grouped: true);
        row = row with { Children = [row.Children[0] with
        { Text = "New Person " + badge + " , Second message. , 14:21" }] };
        var spoken = DiscordConversationAdapter.ReadFrame(Frame(header, row)).Blocks.Last().Text;
        Assert(spoken == "New Person. Second message.",
            "Grouped newcomer message included the badge description in its author: " + spoken);
        var ordinary = DiscordConversationAdapter.ReadFrame(Frame(Message(124, "Alice", badge))).Blocks.Single().Text;
        Assert(ordinary == "Alice. " + badge, "Filtering a badge removed the same words from a genuine message.");
        foreach (var decoration in new[] { "Community guide", "Server Booster", "Unknown future label", "Role icon, Moderator Moderator" })
        {
            var decoratedHeader = header with { Children = [header.Children[0] with { Text = "New Person " + decoration + " , First message. , 14:21" }] };
            var decoratedRow = row with { Children = [row.Children[0] with { Text = "New Person " + decoration + " , Second message. , 14:21" }] };
            var memory = new DiscordAuthorMemory();
            DiscordConversationAdapter.ReadFrame(Frame(decoratedHeader), memory);
            Assert(DiscordConversationAdapter.ReadFrame(Frame(decoratedRow), memory).Blocks.Single().Text == "New Person. Second message.",
                "Arbitrary header metadata leaked when its username header was virtualized.");
            DiscordConversationAdapter.ReadFrame(Frame(Message(125, "Other", "Other channel", channel: "43")), memory);
            Assert(DiscordConversationAdapter.ReadFrame(Frame(decoratedRow), memory).Blocks.Single().Text.Length == 0,
                "A clean author name from another channel was reused.");
        }
        Assert(DiscordConversationAdapter.ReadFrame(Frame(row)).Blocks.Single().Text.Length == 0,
            "An unverified combined header label was spoken as an author name.");
        var replyHeader = header with { Children = [header.Children[0] with { Text = "New Person replying to Alice , First message. , 14:21" }] };
        Assert(DiscordConversationAdapter.ReadFrame(Frame(replyHeader, row)).Blocks.Last().Text == "New Person. Second message.",
            "A reply's different accessibility label broke the following grouped post.");
    }

    public static async Task LiveAsync(IntPtr window)
    {
        var automation = new UIA.CUIAutomation8();
        var adapter = new DiscordConversationAdapter();
        var initial = adapter.TryRead(automation, automation.ElementFromHandle(window));
        Assert(initial?.Context?.ViewKey.StartsWith("discord:", StringComparison.Ordinal) == true, "Discord did not expose one recognized conversation.");
        Assert(initial!.Blocks.Any(b => b.Text.Length > 0), "Discord exposed no author/body messages.");
        var tracker = new DiscordMessageTracker();
        tracker.EstablishBaseline(initial, DateTimeOffset.UtcNow);
        var count = 0;
        var hashes = new HashSet<string>();
        using var fingerprinter = new WindowTextFingerprinter();
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(1000);
            var snapshot = adapter.TryRead(automation, automation.ElementFromHandle(window))!;
            Assert(snapshot.Context == initial.Context, "The active Discord conversation changed during the bounded check.");
            tracker.Observe(snapshot, DateTimeOffset.UtcNow);
            foreach (var text in tracker.TakeReady(DateTimeOffset.UtcNow))
            {
                count++;
                hashes.Add(fingerprinter.Create(text.DisplayText).Hash);
            }
        }
        Console.WriteLine($"PASS: live Discord scope stable; baseline={initial.Blocks.Count}; new posts={count}; distinct text fingerprints={hashes.Count}. No messages sent or text logged.");
        Assert(count > 0, "No new message arrived during this sample; live arrival behavior remains unverified.");
    }

    public static void CheckLiveComposer(IntPtr window)
    {
        var automation = new UIA.CUIAutomation8();
        using var fingerprinter = new WindowTextFingerprinter();
        var snapshot = new DiscordConversationAdapter().TryRead(automation, automation.ElementFromHandle(window), fingerprinter);
        Assert(snapshot?.Context?.ViewKey.StartsWith("discord:", StringComparison.Ordinal) == true, "No active Discord conversation was recognized.");
        Assert(snapshot!.EditableFingerprints is { Count: <= 1 }, "No unique visible Discord composer was captured.");
        Assert(snapshot.Blocks.Any(b => b.DiscreteBodyFingerprint is not null), "Message body fingerprints were missing.");
        Console.WriteLine("PASS: one live Discord composer and message-body fingerprints captured; no draft or message text logged.");
    }

    public static void CheckLiveMentions(IntPtr window)
    {
        var automation = new UIA.CUIAutomation8();
        var tree = new DiscordConversationAdapter().Capture(automation, automation.ElementFromHandle(window))
            ?? throw new InvalidOperationException("The selected window is not Discord.");
        var snapshot = DiscordConversationAdapter.ReadFrame(tree);
        var checkedCount = 0;
        var missing = 0;
        foreach (var row in DiscordConversationAdapter.Descendants(tree))
        {
            if (!DiscordConversationAdapter.TryMessageId(row.Id, out _, out var id)) continue;
            var article = row.Children.FirstOrDefault(n => n.Role == "article");
            var body = article?.Children.FirstOrDefault(n => n.Id == "message-content-" + id);
            if (body is null) continue;
            foreach (var mention in DiscordConversationAdapter.Descendants(body).Where(n => n.ClassName.Contains("mention", StringComparison.OrdinalIgnoreCase)))
            {
                var expected = mention.Text.Length > 0 ? mention.Text : string.Concat(DiscordConversationAdapter.Descendants(mention)
                    .Where(n => n.Type == 50020 && n.Children.Count == 0).Select(n => n.Text));
                if (expected.Length == 0) continue;
                checkedCount++;
                var block = snapshot.Blocks.FirstOrDefault(b => b.Key == row.Id);
                if (block is null || !block.Text.Contains(expected, StringComparison.Ordinal)) missing++;
            }
        }
        Assert(checkedCount > 0, "No inline mention is exposed in the loaded conversation.");
        Assert(missing == 0, $"Discord capture omitted {missing} of {checkedCount} inline mention labels.");
        Console.WriteLine($"PASS: {checkedCount} live inline mention labels are retained in message bodies.");
    }

    public static void CheckLiveNewcomer(IntPtr window)
    {
        var automation = new UIA.CUIAutomation8();
        var tree = new DiscordConversationAdapter().Capture(automation, automation.ElementFromHandle(window))
            ?? throw new InvalidOperationException("The selected window is not Discord.");
        var snapshot = DiscordConversationAdapter.ReadFrame(tree);
        var rows = DiscordConversationAdapter.Descendants(tree).Where(n => n.Id.StartsWith("chat-messages-", StringComparison.Ordinal)
            && DiscordConversationAdapter.Descendants(n).Any(c => c.Text.Contains("new here", StringComparison.OrdinalIgnoreCase)
                || c.ClassName.Contains("newMember", StringComparison.OrdinalIgnoreCase))).ToArray();
        Assert(rows.Length > 0, "No newcomer-badged message is currently exposed; the live reproduction needs that message in the loaded channel.");
        var matching = snapshot.Blocks.Where(b => rows.Any(r => r.Id == b.Key)).ToArray();
        Assert(matching.Length > 0 && matching.Any(b => b.Text.Length > 0), "Newcomer messages lost their author/body output.");
        Assert(matching.All(b => !b.Text.Contains("I'm new here, say hi!", StringComparison.Ordinal)), "Discord newcomer badge description leaked between the author and message body.");
        Console.WriteLine("PASS: live newcomer messages retain author and body without the badge description.");
    }

    private static ulong Snowflake(DateTimeOffset time) => (ulong)(time.ToUnixTimeMilliseconds() - 1420070400000L) << 22;
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static DiscordTextNode Node(int type = 50026, string id = "", string role = "group", string classes = "", string text = "", params DiscordTextNode[] children)
        => new(type, id, role, classes, text, children);
    private static DiscordTextNode Text(string text) => Node(type: 50020, role: "description", text: text);
    private static DiscordTextNode Message(ulong id, string author, string text, string channel = "42", bool grouped = false, DiscordTextNode? reply = null)
    {
        var parts = new List<DiscordTextNode>();
        if (reply is not null) parts.Add(reply);
        if (!grouped) parts.Add(Node(type: 50020, role: "heading", children: [Node(id: "message-username-" + id, children: [
            Node(type: 50000, classes: "username_c19a55", text: author), Node(type: 50000, text: "Server Tag: TEST")])]));
        parts.Add(Node(role: "time", text: "12:34"));
        parts.Add(Node(id: "message-content-" + id, children: [Text(text)]));
        parts.Add(Node(text: "Reactions and attachment metadata"));
        return Node(type: 50007, id: $"chat-messages-{channel}-{id}", role: "listitem", children: [
            Node(role: "article", classes: grouped ? "message__5126c" : "message__5126c groupStart__5126c", text: author + " Role icon, Moderator Moderator , " + text + " , 12:34", children: parts.ToArray())]);
    }
    private static DiscordTextNode Frame(params DiscordTextNode[] messages) => Node(children: [
        Node(type: 50008, role: "list", classes: "scrollerInner__36d07", children: messages),
        Node(role: "region", text: "User status and settings", children: [Node(classes: "title_b6c092", children: [Text("Local User")])]),
        Node(role: "navigation", children: [Text("Sidebar updates and DMs")]), Node(type: 50004, text: "Private draft"),
        Node(type: 50008, children: [Text("Member list updates")])]);

    public static void Inspect(IntPtr window)
    {
        var automation = new UIA.CUIAutomation8();
        var tree = new DiscordConversationAdapter().Capture(automation, automation.ElementFromHandle(window))
            ?? throw new InvalidOperationException("The selected window is not Discord.");
        var nodes = DiscordConversationAdapter.Descendants(tree).ToArray();
        foreach (var document in nodes.Where(n => n.DocumentUrl.Length > 0))
        {
            var route = Uri.TryCreate(document.DocumentUrl, UriKind.Absolute, out var url) ? url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) : [];
            var rowChannels = nodes.Where(n => DiscordConversationAdapter.TryMessageId(n.Id, out _, out _)).Select(n => n.Id.Split('-')[2]).Distinct().ToArray();
            Console.WriteLine($"ROUTE channelsPath={route.FirstOrDefault() == "channels"} hasChannel={route.Length >= 3} agreesWithMessages={route.Length >= 3 && rowChannels.Contains(route[2])}");
        }
        var rows = nodes.Where(n => n.Id.StartsWith("chat-messages-", StringComparison.Ordinal)).Take(3).ToArray();
        foreach (var row in rows) Print(row, 0);
        foreach (var node in nodes.Where(n => n.Type == 50008 && DiscordConversationAdapter.Descendants(n).Any(c => c.Id.StartsWith("chat-messages-", StringComparison.Ordinal))))
            Console.WriteLine($"CONVERSATION LIST role={node.Role} id={node.Id} class={node.ClassName} hidden={node.Hidden}");
        foreach (var row in nodes.Where(n => n.Id.StartsWith("chat-messages-", StringComparison.Ordinal) && !DiscordConversationAdapter.Descendants(n).Any(c => c.Id.StartsWith("message-username-", StringComparison.Ordinal))).Take(1)) Print(row, 0);
        foreach (var node in nodes.Where(n => n.Text == "User status and settings")) Print(node, 0);
        Console.WriteLine($"Message rows: {nodes.Count(n => n.Id.StartsWith("chat-messages-", StringComparison.Ordinal))}");

        static void Print(DiscordTextNode node, int depth)
        {
            var id = System.Text.RegularExpressions.Regex.Replace(node.Id, @"\d+", "N");
            Console.WriteLine($"{new string(' ', depth * 2)}type={node.Type} role={node.Role} id={id} class={node.ClassName} chars={node.Text.Length} hidden={node.Hidden}");
            foreach (var child in node.Children) Print(child, depth + 1);
        }
    }
}
