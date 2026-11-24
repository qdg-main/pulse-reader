using PulseReader.Monitoring;

internal static class CachedCodexTranscriptChecks
{
    internal static void Run()
    {
        CheckAssistantOnlyTurn(updatedLayout: false);
        CheckAssistantOnlyTurn(updatedLayout: true);
        CheckFinalAfterCollapsedHistory("95 previous messages");
        CheckFinalAfterCollapsedHistory("Worked for 1h 48m 34s");
        CheckFinalAfterCollapsedHistory(label: null);
        using var fp = new WindowTextFingerprinter();
        CachedCodexTranscriptNode Text(string key, string text) => new(key, 50020, text, "", "", []);
        CachedCodexTranscriptNode Group(string key, string cls, params CachedCodexTranscriptNode[] children)
            => new(key, 50026, "", cls, "", children);
        var prompt = Group("user", "bg-user-message", Text("prompt", "Please describe the synthetic test."));
        var editor = Group("editor", "", Text("draft", "Private unsent draft.")) with { Role = "textbox" };
        var password = Text("password", "Private password.") with { Password = true };
        var browser = new CachedCodexTranscriptNode("browser", 50030, "", "", "document", [Text("page", "Unrelated browser content.")]);
        var list = new CachedCodexTranscriptNode("list", 50007, "First list sentence. Second list sentence.", "", "listitem",
            [Text("list-text", "First list sentence. Second list sentence.")]);
        var response = Group("response", "relative shrink-0",
            Group("paragraph", "", Text("answer", "A complete synthetic answer.")), list);
        var root = Group("view", "_transcriptContent_fixture", prompt, response, editor, password, browser);
        WindowTextSnapshot Read(CachedCodexTranscriptNode node, MonitoringCaptureContext? capture = null)
            => WindowTextSnapshotReader.ReadCachedCodexFrame(node, "view", fp,
                new CodexConversationAdapter { IsRecognized = true, IsResponseActive = false }, capture: capture);
        var snapshot = Read(root);
        var spoken = string.Join('\n', snapshot.Blocks.Select(block => block.Text));
        Require(snapshot.Context?.ViewKey == "view" && spoken.Contains("A complete synthetic answer.", StringComparison.Ordinal)
            && spoken.Contains("First list sentence.", StringComparison.Ordinal)
            && spoken.Contains("Second list sentence.", StringComparison.Ordinal), "Cached transcript lost its answer or list content.");
        Require(!spoken.Contains("Private", StringComparison.Ordinal) && !spoken.Contains("browser", StringComparison.OrdinalIgnoreCase)
            && !spoken.Contains("Please describe", StringComparison.Ordinal), "Cached transcript exposed user/editor/password/embedded-document text.");
        Require(spoken.Split("First list sentence.", StringSplitOptions.None).Length == 2,
            "Cached list aggregate and child text were both included.");
        Require(Read(root with { Hidden = true }).Context?.ViewKey == ""
            && Read(root with { Key = "other-view" }).Context?.ViewKey == "", "Stale or hidden cached transcript remained eligible.");
        var modal = Group("modal", "") with { Role = "dialog" };
        Require(Read(root with { Children = [prompt, response, modal] }).Context?.ViewKey == "", "Cached modal did not suspend capture.");
        var writable = response with { ValueAvailable = true, ValueReadOnly = false };
        Require(Read(root with { Children = [prompt, writable] }).Blocks.Count == 0,
            "Cached writable ValuePattern was not excluded.");
        Require(Read(root with { Children = [prompt, writable with { ValueReadOnly = null }] }).Blocks.Count == 0,
            "Uncertain cached writable-value metadata was assumed read-only.");
        Require(Read(root with { Children = [prompt, writable with { ValueAvailable = null }] }).Blocks.Count == 0,
            "Unknown cached pattern availability was assumed safe for transcript speech.");
        Require(Read(root with { Children = [prompt, writable with { ValueReadOnly = true }] }).Blocks.Count > 0,
            "Read-only state from a new cached frame was ignored.");
        var offscreen = response with { Hidden = true };
        Require(Read(root with { Children = [prompt, offscreen] }).Blocks.Count > 0,
            "Mounted conversation content below the viewport was lost.");
        var working = Group("working", "group flex-col min-w-0",
            Group("thought-paragraph", "", Text("thought", "A synthetic working update.")));
        Require(Read(root with { Children = [prompt, working] }).Blocks.Any(block =>
            block.Text.Contains("A synthetic working update.", StringComparison.Ordinal) && !block.IsFinalResponse),
            "Cached ancestry lost the working-column classification.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { Read(root, new MonitoringCaptureContext(canceled.Token)); throw new InvalidOperationException("Cached parsing ignored cancellation."); }
        catch (OperationCanceledException) { }
        var deep = Group("deep", "");
        for (var i = 0; i < 130; i++) deep = Group("deep-" + i, "", deep);
        try { Read(root with { Children = [deep] }); throw new InvalidOperationException("Unbounded cached depth was accepted."); }
        catch (InvalidOperationException error) when (error.Message.Contains("bounded cached-frame budget", StringComparison.Ordinal)) { }
        try { Read(root with { Children = Enumerable.Repeat(Group("empty", ""), 20001).ToArray() }); throw new InvalidOperationException("Unbounded cached node count was accepted."); }
        catch (InvalidOperationException error) when (error.Message.Contains("bounded cached-frame budget", StringComparison.Ordinal)) { }
        Console.WriteLine("PASS: cached-only transcript frames preserve answers/lists and local ancestry, exclude editors/browser documents, and enforce identity, visibility, cancellation and depth limits.");
    }

    private static void CheckAssistantOnlyTurn(bool updatedLayout)
    {
        using var fp = new WindowTextFingerprinter();
        var adapter = new CodexConversationAdapter { IsRecognized = true };
        CachedCodexTranscriptNode Text(string key, string text, string role = "", string cls = "")
            => new(key, 50020, text, cls, role, []);
        CachedCodexTranscriptNode Group(string key, string cls, params CachedCodexTranscriptNode[] children)
            => new(key, 50026, "", cls, "group", children);
        CachedCodexTranscriptNode Reply(string key, string body, bool working, bool liveStatus = true)
        {
            var heading = Text(key + (working ? "-working-heading" : "-final-heading"), "ChatGPT said:", "heading", "sr-only");
            var paragraph = Group(key + "-paragraph", updatedLayout ? "Paragraph-kKnbIo" : "_Paragraph_fixture",
                Text(key + "-text", body, "description"));
            return working
                ? Group(key, "relative shrink-0",
                    Group(key + "-status", "min-w-0 text-size-chat relative overflow-visible py-0",
                        Text(key + "-timer", liveStatus ? "Working for 1m 24s" : "Worked for 1m 24s", "description")),
                    Group(key + "-working", "group flex min-w-0 flex-col", heading, paragraph))
                : Group(key, "relative shrink-0", heading, paragraph);
        }
        WindowTextSnapshot Read(bool? active, params CachedCodexTranscriptNode[] replies)
        {
            adapter.IsResponseActive = active;
            // The live goal thread mounts several completed answers and the
            // next active section inside a single outer assistant group.
            var shared = Group("shared-reply", "relative shrink-0",
                replies.Where(reply => reply.ClassName != "bg-user-message").SelectMany(reply => reply.Children).ToArray());
            var root = Group("view", updatedLayout ? "transcriptContent-l_FOQD" : "_transcriptContent_fixture",
                Group("rows", "relative flex flex-col gap-3",
                    [.. replies.Where(reply => reply.ClassName == "bg-user-message"), shared]));
            return WindowTextSnapshotReader.ReadCachedCodexFrame(root, "view", fp, adapter);
        }

        var historical = Reply("history", "A historical answer must remain silent.", working: false);
        var oldExpanded = Reply("older-work", "An expanded historical thought must remain silent.", working: true, liveStatus: false);
        Require(Read(true, historical, oldExpanded).Blocks.Count == 0,
            "An expanded old response was treated as the active assistant-only response.");

        const string opening = "A new assistant update arrived without a user bubble.";
        var draft = Reply("current", opening, working: true);
        Require(Read(null, historical, draft).Blocks.Count == 0,
            "An unknown composer state admitted an unanchored assistant response.");
        var working = Read(true, historical, draft);
        Require(working.Blocks is [{ IsFinalResponse: false, Text: opening }],
            "An assistant-only live response was lost or mixed with history.");
        Require(Read(true, historical, oldExpanded).Blocks.Count == 0,
            "Scrolling away from the live response replayed an older assistant group.");
        Require(Read(true, historical, draft).ResponseCycleKey == working.ResponseCycleKey,
            "Restoring the live assistant group changed the response identity.");

        const string ending = "The same assistant response now has a final answer.";
        var streamingFinal = Read(true, historical, draft, Reply("current", ending, working: false));
        Require(streamingFinal.ResponseCycleKey == working.ResponseCycleKey
            && streamingFinal.Blocks is [{ IsFinalResponse: true, Text: ending }],
            "A live timer before a direct final heading classified the final answer as working text.");
        using (var streamingSession = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false))
        {
            var finalSpeech = new List<string>();
            streamingSession.TextAvailable += (_, args) => finalSpeech.Add(args.Text);
            var start = DateTimeOffset.UtcNow;
            var changedAt = DateTimeOffset.MinValue;
            streamingSession.EstablishBaseline(working, start);
            streamingSession.ProcessSnapshot(streamingFinal, start.AddSeconds(1), ref changedAt);
            streamingSession.ProcessSettled(start.AddSeconds(2), ref changedAt);
            Require(finalSpeech.Count == 0, "The final bypassed settlement while it could still be streaming.");
            streamingSession.ProcessSettled(start.AddSeconds(3), ref changedAt);
            Require(finalSpeech.SequenceEqual([ending]), "The final remained silent while the working timer was mounted.");
        }
        var final = Read(true, historical, Reply("current", ending, working: false));
        Require(final.ResponseCycleKey == working.ResponseCycleKey
            && final.Blocks is [{ IsFinalResponse: true, Text: ending }],
            "Completing an assistant-only response lost its identity or waited for a globally idle composer.");
        var following = Reply("next", "A second assistant continuation is running.", working: true);
        var confirmed = Read(true, historical, Reply("current", ending, working: false), following);
        Require(confirmed.Blocks is [{ IsConfirmedFinalHandoff: true, Text: ending }],
            "Starting the next continuation did not confirm the previous final.");
        var next = Read(true, historical, Reply("current", ending, working: false), following);
        Require(next.ResponseCycleKey != working.ResponseCycleKey
            && next.Blocks is [{ Text: "A second assistant continuation is running." }],
            "A subsequent assistant-only continuation did not get its own response identity.");
        Require(Read(true, historical, Reply("current", ending, working: false)).Blocks.Count == 0,
            "The previous final was replayed when the active section left the viewport.");

        adapter.SetView("another-view");
        var prompt = Group("user", "bg-user-message", Text("prompt", "Please continue the synthetic test."));
        var withPrompt = Read(true, prompt, draft);
        Require(withPrompt.Blocks is [{ Text: opening }], "The ordinary prompt-anchored response was lost.");
        var withoutPrompt = Read(true, draft);
        Require(withoutPrompt.ResponseCycleKey == withPrompt.ResponseCycleKey
            && withoutPrompt.Blocks is [{ Text: opening }],
            "Virtualizing a known user prompt changed the response identity.");
        Require(Read(true, prompt, draft).ResponseCycleKey == withPrompt.ResponseCycleKey,
            "Restoring a known user prompt changed the response identity.");

        adapter.SetView("another-view");
        using var session = new WindowTextMonitoringSession(new IntPtr(1), settleCodexThoughts: false);
        var spoken = new List<string>();
        session.TextAvailable += (_, args) => spoken.Add(args.Text);
        var now = DateTimeOffset.UtcNow;
        var lastChange = DateTimeOffset.MinValue;
        var continuation = Read(true, prompt, historical, draft);
        Require(continuation.Blocks is [{ Text: opening }],
            "A mounted historical prompt took priority over the live continuation.");
        session.EstablishBaseline(continuation, now);
        var currentFinal = Reply("current", ending, working: false);
        const string nextOpening = "The next continuation has a fresh update.";
        var nextDraft = Reply("next", nextOpening, working: true);
        var handoff = Read(true, prompt, historical, currentFinal, nextDraft);
        Require(handoff.ResponseCycleKey == continuation.ResponseCycleKey
            && handoff.Blocks is [{ IsFinalResponse: true, Text: ending }],
            "The next working section skipped the previous final in the same frame.");
        session.ProcessSnapshot(handoff, now.AddSeconds(1), ref lastChange);
        var afterHandoff = Read(true, prompt, historical, currentFinal, nextDraft);
        Require(afterHandoff.ResponseCycleKey != continuation.ResponseCycleKey
            && afterHandoff.Blocks is [{ Text: nextOpening }],
            "Publishing the final prevented the next continuation from starting.");
        session.ProcessSnapshot(afterHandoff, now.AddMilliseconds(1100), ref lastChange);
        Require(spoken.SequenceEqual([ending, nextOpening]),
            "The session lost or reordered speech when the final and next continuation arrived together.");
    }

    private static void CheckFinalAfterCollapsedHistory(string? label)
    {
        using var fingerprint = new WindowTextFingerprinter();
        CachedCodexTranscriptNode Text(string key, string text, string role = "description", string cls = "")
            => new(key, 50020, text, cls, role, []);
        CachedCodexTranscriptNode Group(string key, string cls, params CachedCodexTranscriptNode[] children)
            => new(key, 50026, "", cls, "group", children);
        var prompt = Group("prompt", "bg-user-message", Text("user", "Compare the results of these synthetic checks."));
        var history = Group("history", "text-size-chat text-secondary",
            new CachedCodexTranscriptNode("toggle", 50000, "", "no-drag cursor-interaction", "button",
                [Text("label", label ?? "Worked for 1m 24s")]));
        var heading = Text("answer-heading", "ChatGPT said:", "heading", "sr-only");
        string[] sentences = [
            "The first paragraph explains how the fixture works.",
            "The second paragraph provides a different result.",
            "The last paragraph gives the conclusion."
        ];
        foreach (var composerActive in new bool?[] { true, null })
        {
            var adapter = new CodexConversationAdapter { IsRecognized = true, IsResponseActive = composerActive };
            WindowTextSnapshot Read(params CachedCodexTranscriptNode[] assistant) =>
                WindowTextSnapshotReader.ReadCachedCodexFrame(
                    Group("view", "transcriptContent-fixture",
                        Group("rows", "flex flex-col", prompt, Group("response", "relative shrink-0", assistant))),
                    "view", fingerprint, adapter);
            var now = DateTimeOffset.UtcNow;
            var changedAt = DateTimeOffset.MinValue;
            var spoken = new List<string>();
            using var session = new WindowTextMonitoringSession(new IntPtr(1));
            session.TextAvailable += (_, args) => spoken.Add(args.Text);
            var paragraphs = sentences.Select((text, i) =>
                Group($"paragraph-{i}", "Paragraph-fixture", Text($"text-{i}", text))).ToArray();
            var uncertain = Group("unknown-wrapper", "", heading, paragraphs[0]);
            Require(Read(Text("ordinary-label", label ?? "Worked for 1m 24s"), uncertain)
                .Blocks is [{ IsFinalResponse: false }], "Plain text was mistaken for a collapsed-history control.");
            Require(Read(history, Group("working", "group flex min-w-0 flex-col", heading, paragraphs[0]))
                .Blocks is [{ IsFinalResponse: false }], "A working heading below collapsed history was treated as final.");
            Require(Read(heading, paragraphs[0],
                    Group("later-work", "group flex min-w-0 flex-col",
                        heading with { Key = "later-working-heading" }, paragraphs[1]))
                .Blocks is [{ IsFinalResponse: false }], "An earlier direct heading made later working text final.");
            session.EstablishBaseline(Read(), now);
            var initial = label is null ? Read(heading, paragraphs[0]) : Read(history, heading, paragraphs[0]);
            session.ProcessSnapshot(initial, now.AddSeconds(1), ref changedAt);
            // An answer can push the history label offscreen while the nodes
            // remain mounted in the same captured transcript.
            var offscreenHistory = history with
            {
                Children = [history.Children[0] with
                {
                    Children = [history.Children[0].Children[0] with { Hidden = true }]
                }]
            };
            var complete = label is null ? Read([heading, .. paragraphs]) : Read([offscreenHistory, heading, .. paragraphs]);
            Require(complete.Blocks is [{ IsFinalResponse: true }] && complete.Blocks[0].Text == string.Join('\n', sentences),
                $"The direct answer heading (history label: {label ?? "unmounted"}) did not identify the full final reply.");
            session.ProcessSnapshot(complete, now.AddSeconds(2), ref changedAt);
            session.ProcessSnapshot(Read(), now.AddSeconds(2.2), ref changedAt);
            session.ProcessSettled(now.AddSeconds(3), ref changedAt);
            Require(spoken.Count == 0, "The final was published before it settled.");
            session.ProcessSettled(now.AddSeconds(4), ref changedAt);
            Require(spoken.SequenceEqual(sentences), "The last paragraphs were lost when the final temporarily unmounted.");

            adapter.SetView("");
            using var lateMarkerSession = new WindowTextMonitoringSession(new IntPtr(1));
            var lateMarkerSpeech = new List<string>();
            lateMarkerSession.TextAvailable += (_, args) => lateMarkerSpeech.Add(args.Text);
            changedAt = DateTimeOffset.MinValue;
            lateMarkerSession.EstablishBaseline(Read(), now);
            lateMarkerSession.ProcessSnapshot(Read(Group("working-draft", "group flex min-w-0 flex-col",
                heading with { Key = "working-heading" }, paragraphs[0], paragraphs[1])),
                now.AddSeconds(1), ref changedAt);
            lateMarkerSession.ProcessSettled(now.AddSeconds(4), ref changedAt);
            Require(lateMarkerSpeech.SequenceEqual(sentences.Take(2)),
                "The fixture did not publish the opening before the completed-work control appeared.");
            lateMarkerSession.ProcessSnapshot(complete,
                now.AddSeconds(5), ref changedAt);
            lateMarkerSession.ProcessSettled(now.AddSeconds(7), ref changedAt);
            Require(lateMarkerSpeech.SequenceEqual(sentences),
                "A late completed-work control replayed the opening or lost the unread ending.");
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
