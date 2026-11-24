using PulseReader.Monitoring;

internal static class CodexCaptureScopeChecks
{
    internal static void Run()
    {
        var adapter = new CodexConversationAdapter();
        var reads = new List<string>();
        WindowTextSnapshot Read(string root, string key)
        {
            reads.Add(root);
            if (root != "chat") throw new InvalidOperationException("Capture inspected text outside the transcript.");
            return new WindowTextSnapshot([new("sentence", "A new assistant sentence.")], Context: new(key));
        }
        CodexCaptureLandmark<string> Item(string node, string key, string cls = "", string role = "", string aria = "", bool visible = true)
            => new(node, key, cls, role, aria, visible);
        var transcript = Item("chat", "chat-1", "_transcriptContent_fixture");
        var snapshot = CodexCaptureScope.Read([Item("browser", "web", role: "document"),
            Item("sidebar", "sidebar"), Item("scroll", "scroll-1", "thread-scroll-container"), transcript], adapter, Read);
        Require(snapshot?.Context?.ViewKey == "chat-1" && reads.SequenceEqual(["chat"]),
            "Capture did not select only the preferred transcript subtree.");
        reads.Clear();
        Require(CodexCaptureScope.Read([transcript, Item("other", "chat-2", "_transcriptContent_other")], adapter, Read)?.Context?.ViewKey == ""
            && reads.Count == 0, "Ambiguous transcripts permitted content capture.");
        Require(CodexCaptureScope.Read([Item("settings", "settings")], adapter, Read)?.Context?.ViewKey == ""
            && reads.Count == 0, "Known Codex fell back to unrelated settings/browser text.");
        Require(CodexCaptureScope.Read([transcript, Item("modal", "modal", role: "dialog")], adapter, Read)?.Context?.ViewKey == ""
            && reads.Count == 0, "A blocking dialog left transcript text eligible.");
        Require(CodexCaptureScope.Read([transcript, Item("popover", "popover",
            "origin-[var(--radix-popover-content-transform-origin)]", "dialog")], adapter, Read)?.Context?.ViewKey == "chat-1"
            && reads.SequenceEqual(["chat"]), "A nonblocking popover disrupted transcript capture.");
        reads.Clear();
        Require(CodexCaptureScope.Read([Item("old-chat", "old", "_transcriptContent_hidden", visible: false)], adapter, Read)?.Context?.ViewKey == ""
            && reads.Count == 0, "A hidden transcript was captured.");
        Require(CodexCaptureScope.Read([Item("generic", "generic")], new CodexConversationAdapter(), Read) is null,
            "Unrecognised applications lost generic capture.");
        Console.WriteLine("PASS: Codex text capture selects only a unique visible transcript; browser/settings/hidden/modal/ambiguous views remain outside content capture.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
