using System.Diagnostics;
using UIA = Interop.UIAutomationClient;

namespace PulseReader.Monitoring;

internal sealed class T3ConversationAdapter
{
    internal const string TimelineClass = "messages-timeline-scroll";
    internal const string ImagePreviewName = "Expanded image preview";
    private const int AssistantHistoryLimit = 4096;
    private const int MinimumRebuiltReplyMatchLength = 200;
    private readonly CodexConversationAdapter _conversation = new("t3-message");
    private readonly HashSet<string> _recentAssistantRows = new(StringComparer.Ordinal);
    private readonly Queue<string> _assistantRowOrder = new();
    private readonly HashSet<string> _recentPromptHashes = new(StringComparer.Ordinal);
    private readonly Queue<string> _promptOrder = new();
    private bool? _recognized;
    private string _prompt = "";
    private string _promptHash = "";
    private string _rebuiltAnchor = "";
    private string _workingBody = "";
    private AssistantBoundary? _completedRow;
    private AssistantBoundary? _continuationBoundary;
    private int _generationRevision;
    private bool _continuationPending;
    private bool _confirmedPrompt;
    private bool _assistantAnchored;
    private HashSet<string> _assistantRows = new(StringComparer.Ordinal);

    public bool IsRecognized => _recognized is true;

    public bool Recognize(UIA.IUIAutomationElement source)
    {
        if (_recognized is null)
        {
            try
            {
                using var process = Process.GetProcessById(source.CurrentProcessId);
                _recognized = process.ProcessName.Equals("T3 Code", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName.Equals("T3 Code (Beta)", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { _recognized = false; }
        }
        return IsRecognized;
    }

    internal WindowTextSnapshot Unavailable()
    {
        _conversation.SetView("");
        _prompt = "";
        _promptHash = "";
        _rebuiltAnchor = "";
        _workingBody = "";
        _completedRow = null;
        _continuationBoundary = null;
        _generationRevision = 0;
        _continuationPending = false;
        _confirmedPrompt = false;
        _assistantAnchored = false;
        _assistantRows.Clear();
        _recentAssistantRows.Clear();
        _assistantRowOrder.Clear();
        _recentPromptHashes.Clear();
        _promptOrder.Clear();
        return new([], Context: new(""));
    }

    internal WindowTextSnapshot LoadingThread(MonitoringCaptureContext? capture = null)
    {
        capture?.Stage("capture-t3-unavailable-loading-thread");
        return Unavailable();
    }

    internal WindowTextSnapshot ReadDocumentFrame(CachedCodexTranscriptNode root, string documentUrl,
        bool atLiveEdge, bool generating, WindowTextFingerprinter fingerprinter, MonitoringCaptureContext? capture = null)
        => ReadFrame(root, GetThreadKey(documentUrl), atLiveEdge, generating, fingerprinter, capture);

    internal static string GetThreadKey(string? documentUrl)
    {
        // Electron exposes the active hash route as the root document's UIA
        // Value. The timeline container itself can survive navigation.
        if (!Uri.TryCreate(documentUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "t3code" || uri.Host != "app" || uri.AbsolutePath != "/"
            || !uri.Fragment.StartsWith("#/", StringComparison.Ordinal))
            return "";

        var route = uri.Fragment.AsSpan(2);
        var query = route.IndexOf('?');
        if (query >= 0) route = route[..query];
        var separator = route.IndexOf('/');
        if (separator < 0
            || !Guid.TryParseExact(route[..separator], "D", out var environment)
            || !Guid.TryParseExact(route[(separator + 1)..], "D", out var thread))
            return "";

        return $"{environment:D}/{thread:D}";
    }

    internal WindowTextSnapshot ReadFrame(CachedCodexTranscriptNode root, string viewKey,
        bool atLiveEdge, bool generating, WindowTextFingerprinter fingerprinter,
        MonitoringCaptureContext? capture = null)
    {
        if (root.Hidden || viewKey.Length == 0 || !HasClass(root, TimelineClass))
            return Unavailable();
        if (!atLiveEdge) return ReadHistoryViewport(viewKey, generating);
        var view = "t3:" + viewKey;
        if (_conversation.ViewKey != view)
        {
            Unavailable();
            _conversation.SetView(view);
        }

        var rows = new List<(string Key, string[] Body)>();
        var prompt = "";
        CachedCodexTranscriptNode? promptNode = null;
        var sawPreviousPrompt = false;
        Visit(root);
        var sameRows = rows.Any(row => _assistantRows.Contains(row.Key));
        var previouslyGenerating = _conversation.IsResponseActive;
        var previousTurnSealed = _conversation.CurrentTurnSealed;
        var continuing = _assistantRows.Count > 0 && previouslyGenerating is true
            && !previousTurnSealed;
        var anchor = _prompt;
        var rebuiltAnchor = _rebuiltAnchor;
        var assistantAnchored = _assistantAnchored;
        if (prompt.Length > 0 && (!_assistantAnchored || !sameRows))
        {
            anchor = prompt;
            assistantAnchored = false;
        }
        else if (prompt.Length == 0 && !sameRows)
        {
            if (rows.Count == 0)
            {
                capture?.Stage("capture-t3-no-assistant-rows");
                return new([], Context: new(view));
            }

            // T3 recycles UIA elements at the live edge. Keep the identity of
            // a reply we already saw streaming, including its final frame.
            if (continuing || (previousTurnSealed && _continuationPending))
            {
                capture?.Stage("capture-t3-continue-virtualized-reply");
            }
            else
            {
                // Only active generation can establish a new anchor without
                // a user bubble. Disjoint idle rows may be historical.
                if (!generating)
                {
                    capture?.Stage("capture-t3-unanchored-idle-rows");
                    return new([], Context: new(view));
                }
                anchor = "assistant:" + rows[0].Key;
                assistantAnchored = true;
                capture?.Stage("capture-t3-anchor-live-reply");
            }
        }

        if (anchor != _prompt && _prompt.Length > 0
            && (anchor == _rebuiltAnchor || continuing && MatchesWorkingReply()))
        {
            // Compaction can replace both the prompt and every assistant row
            // without changing the reply. Keep its speech history and remember
            // the replacement anchor as that same reply continues to grow.
            rebuiltAnchor = anchor;
            anchor = _prompt;
            assistantAnchored = _assistantAnchored;
            capture?.Stage("capture-t3-retained-rebuilt-reply");
        }

        var sameAnchor = anchor == _prompt;
        var generationRevision = sameAnchor ? _generationRevision : 0;
        var continuationBoundary = sameAnchor ? _continuationBoundary : null;
        var startsContinuation = false;
        if (sameAnchor && previousTurnSealed && _completedRow is { } completed)
        {
            // T3's busy control and assistant rows can update independently.
            // A new row beyond the completed reply is sufficient, including
            // a final that arrives between busy snapshots. If that reply is
            // no longer mounted, require a previously observed busy state.
            _continuationPending |= generating;
            var lastCompleted = FindBoundary(completed, excludeCurrentRows: false);
            var candidates = rows.Skip(lastCompleted + 1);
            startsContinuation = (lastCompleted >= 0 || _continuationPending)
                && candidates.Any(row => !_recentAssistantRows.Contains(row.Key));
            // The same final row can still receive its last edit after a
            // transient busy frame. Let that idle row settle normally.
            var currentFinalVisible = lastCompleted >= 0 && lastCompleted == rows.Count - 1;
            if (!startsContinuation && _continuationPending && (generating || !currentFinalVisible))
                return new([], ResponseCycleKey: _conversation.ResponseCycleKey, Context: new(view));

            if (startsContinuation)
            {
                continuationBoundary = lastCompleted >= 0
                    ? completed with { Key = rows[lastCompleted].Key }
                    : completed;
                generationRevision++;
            }
        }
        TrimBeforeContinuation();
        _conversation.IsResponseActive = generating;
        var blocks = Assemble(anchor);
        if (!_conversation.CurrentTurnAccepted)
        {
            capture?.Stage("capture-t3-rejected-older-turn");
            if (prompt.Length > 0 && anchor != _prompt)
            {
                // An earlier prompt can remount above live rows when the
                // current prompt scrolls out. Exclude rows from older turns
                // before retrying with the active reply's identity.
                rows.RemoveAll(row => _recentAssistantRows.Contains(row.Key) && !_assistantRows.Contains(row.Key));
                if (rows.Count > 0 && continuing && (generating || sameRows))
                {
                    anchor = _prompt;
                    assistantAnchored = _assistantAnchored;
                    generationRevision = _generationRevision;
                    continuationBoundary = _continuationBoundary;
                    TrimBeforeContinuation();
                    blocks = Assemble(anchor);
                    capture?.Stage("capture-t3-continued-past-older-prompt");
                }
            }
            if (!_conversation.CurrentTurnAccepted)
            {
                _conversation.IsResponseActive = previouslyGenerating;
                return new([], ResponseCycleKey: _conversation.ResponseCycleKey, Context: new(view));
            }
        }
        if (rows.Count > 0 || anchor != _prompt)
            _assistantRows = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        if (anchor != _prompt || startsContinuation)
        {
            _completedRow = null;
            _continuationPending = false;
        }
        if (rows.Count > 0 && blocks.Any(block => block.IsFinalResponse))
            _completedRow = new(rows[^1].Key, RowHash(rows[^1].Body));
        _generationRevision = generationRevision;
        _continuationBoundary = continuationBoundary;
        if (anchor != _prompt)
        {
            _rebuiltAnchor = "";
            _workingBody = "";
            var newPromptHash = ReadPromptHash();
            // Coexisting prompt rows establish a distinct occurrence even
            // when the wording matches. A newly observed prompt body is also
            // evidence when virtualization removed the previous prompt.
            _confirmedPrompt = _prompt.Length > 0 && newPromptHash.Length > 0
                && (sawPreviousPrompt || (previousTurnSealed && generating && rows.Count == 0)
                    || (_promptHash.Length > 0 && !_recentPromptHashes.Contains(newPromptHash)));
            _promptHash = newPromptHash;
        }
        else
        {
            _rebuiltAnchor = rebuiltAnchor;
        }
        if (generating && blocks.FirstOrDefault(block => !block.IsFinalResponse) is { } working)
            _workingBody = working.Text;
        else if (!generating)
            _workingBody = "";
        if (_promptHash.Length == 0) _promptHash = ReadPromptHash();
        if (_promptHash.Length > 0 && _recentPromptHashes.Add(_promptHash))
        {
            _promptOrder.Enqueue(_promptHash);
            if (_promptOrder.Count > AssistantHistoryLimit) _recentPromptHashes.Remove(_promptOrder.Dequeue());
        }
        _prompt = anchor;
        _assistantAnchored = assistantAnchored;
        foreach (var row in rows)
            if (_recentAssistantRows.Add(row.Key)) _assistantRowOrder.Enqueue(row.Key);
        while (_assistantRowOrder.Count > AssistantHistoryLimit)
            _recentAssistantRows.Remove(_assistantRowOrder.Dequeue());
        return new(blocks, ResponseCycleKey: _conversation.ResponseCycleKey, Context: new(view), T3PromptHash: _promptHash,
            T3HasNewPrompt: _confirmedPrompt && prompt == anchor, T3StartsContinuation: startsContinuation);

        string ReadPromptHash()
        {
            if (promptNode is null || anchor != prompt) return "";
            var text = string.Join('\n', promptNode.Children.SelectMany(ReadParagraphs));
            return text.Length == 0 ? "" : fingerprinter.CreateTextHash(text);
        }

        void TrimBeforeContinuation()
        {
            if (continuationBoundary is not { } previous) return;
            var boundary = FindBoundary(previous, excludeCurrentRows: !startsContinuation);
            if (boundary >= 0) rows.RemoveRange(0, boundary + 1);
            rows.RemoveAll(row => _recentAssistantRows.Contains(row.Key)
                && (startsContinuation || !_assistantRows.Contains(row.Key)));
        }

        int FindBoundary(AssistantBoundary boundary, bool excludeCurrentRows)
        {
            var byKey = rows.FindIndex(row => row.Key == boundary.Key);
            if (byKey >= 0) return byKey;
            return rows.FindLastIndex(row => (!excludeCurrentRows || !_assistantRows.Contains(row.Key))
                && RowHash(row.Body) == boundary.BodyHash);
        }

        string RowHash(string[] paragraphs) => fingerprinter.CreateTextHash(string.Join('\n', paragraphs));

        bool MatchesWorkingReply()
        {
            // A short common phrase can appear in unrelated replies. Require
            // either a surviving assistant row or a substantial shared body.
            if (_workingBody.Length == 0 || (!sameRows && _workingBody.Length < MinimumRebuiltReplyMatchLength))
                return false;
            var body = string.Join('\n', rows.SelectMany(row => row.Body));
            return body.StartsWith(_workingBody, StringComparison.Ordinal);
        }

        IReadOnlyList<WindowTextBlock> Assemble(string anchorKey)
        {
            // The bubble identifies a turn without using the prompt text.
            var parts = new List<CodexTextPart> { new(CodexTextPartKind.UserBoundary, ""),
                new(CodexTextPartKind.UserText, anchorKey, UserMessageKey: anchorKey) };
            foreach (var row in rows)
            {
                parts.Add(new(CodexTextPartKind.AssistantBoundary, "", parts.Count, BoundaryKey: row.Key));
                foreach (var paragraph in row.Body)
                    parts.Add(new(CodexTextPartKind.Content, paragraph, parts.Count));
            }
            return _conversation.Assemble(parts, fingerprinter, generationRevision);
        }

        void Visit(CachedCodexTranscriptNode node)
        {
            if (IsChrome(node)) return;
            if (HasClass(node, "bg-message") && HasClass(node, "text-message-foreground")
                && node.Children.Any(child => IsRoleHeading(child, "You")))
            {
                sawPreviousPrompt |= node.Key == _prompt;
                prompt = node.Key;
                promptNode = node;
                rows.Clear();
                return;
            }
            // Chromium flattens T3's markdown wrapper; the role heading and
            // body paragraphs become siblings inside the assistant card.
            if (HasClass(node, "px-1") && HasClass(node, "py-0.5")
                && node.Children.FirstOrDefault() is { } heading && IsRoleHeading(heading, "T3 Code"))
            {
                var body = node.Children.Skip(1).TakeWhile(child => !IsAssistantFooter(child))
                    .SelectMany(ReadParagraphs).Where(text => text.Length > 0 && text != "(empty response)").ToArray();
                if (body.Length > 0) rows.Add((node.Key, body));
                return;
            }
            foreach (var child in node.Children) Visit(child);
        }
    }

    internal WindowTextSnapshot ReadModal(string viewKey, int controlType, string name, string className)
        => controlType == UIA.UIA_ControlTypeIds.UIA_WindowControlTypeId && name == ImagePreviewName
            && WindowTextSnapshotReader.ContainsCssClassToken(className, "[--media-width:92vw]")
            ? PauseTranscriptCapture(viewKey)
            : Unavailable();

    internal WindowTextSnapshot ReadHistoryViewport(string viewKey, bool generating)
        => !generating || _conversation.CurrentTurnSealed ? PauseTranscriptCapture(viewKey) : Unavailable();

    private WindowTextSnapshot PauseTranscriptCapture(string viewKey)
    {
        var view = "t3:" + viewKey;
        return _conversation.ViewKey == view
            ? new([], ResponseCycleKey: _conversation.ResponseCycleKey, Context: new(view), T3CapturePaused: true)
            : Unavailable();
    }

    private readonly record struct AssistantBoundary(string Key, string BodyHash);

    private static bool IsRoleHeading(CachedCodexTranscriptNode node, string label)
        => node.Type == 50020 && node.Role == "heading" && node.Text == label && HasClass(node, "sr-only");

    private static bool IsAssistantFooter(CachedCodexTranscriptNode node)
        => HasClass(node, "@container/changed-files")
           // Chromium can flatten the changed-files wrapper, exposing its sticky header directly.
           || HasClass(node, "sticky") && HasClass(node, "top-2") && HasClass(node, "justify-between")
              && HasClass(node, "rounded-t-lg") && HasClass(node, "bg-secondary")
           || HasClass(node, "tabular-nums") && HasClass(node, "text-xs");

    private static bool IsChrome(CachedCodexTranscriptNode node)
        => node.Password || node.Type is 50000 or 50004 or 50006 or 50030
           || node.Role is "textbox" or "searchbox" or "toolbar" or "status" or "dialog"
           || HasClass(node, "sr-only");

    private static bool HasClass(CachedCodexTranscriptNode node, string token)
        => WindowTextSnapshotReader.ContainsCssClassToken(node.ClassName, token);

    private static IEnumerable<string> ReadParagraphs(CachedCodexTranscriptNode node)
    {
        if (IsChrome(node)) yield break;
        if (node.Role is "list" or "table" or "rowgroup" or "row")
        {
            foreach (var child in node.Children)
            foreach (var paragraph in ReadParagraphs(child))
                yield return paragraph;
            yield break;
        }
        var text = InlineText(node).Trim();
        if (text.Length > 0) yield return text;
    }

    private static string InlineText(CachedCodexTranscriptNode node)
    {
        if (IsChrome(node)) return "";
        // Prefer actual descendants over aggregate accessible names.
        var children = node.Children.Select(InlineText).Where(text => text.Length > 0)
            .Aggregate("", WindowTextChangePolicy.Join);
        return children.Length > 0 ? children : node.Text;
    }
}

internal static partial class WindowTextSnapshotReader
{
    private static WindowTextSnapshot? ReadScopedT3Snapshot(UIA.IUIAutomation automation,
        UIA.IUIAutomationElement source, WindowTextFingerprinter fingerprinter,
        T3ConversationAdapter adapter, MonitoringCaptureContext capture)
    {
        if (!adapter.Recognize(source)) return null;
        var visible = automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId, false);
        var timelineClass = automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
            T3ConversationAdapter.TimelineClass, UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring);
        var button = automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
            UIA.UIA_ControlTypeIds.UIA_ButtonControlTypeId);
        var stop = automation.CreateAndCondition(button,
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_NamePropertyId, "Stop generation"));
        const string scrollControlClass = "chat-scroll-to-bottom";
        var scrollControl = automation.CreateOrCondition(
            automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
                scrollControlClass, UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring),
            automation.CreateAndCondition(button, automation.CreateAndCondition(
                automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
                    "pointer-events-auto", UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring),
                automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_NamePropertyId,
                    "Scroll to end", UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring))));
        // T3 hides its scroll control while the in-thread find bar is open.
        var findControl = automation.CreateAndCondition(
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_AriaRolePropertyId, "search"),
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_NamePropertyId, "Find in thread"));
        var dialog = automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_AriaPropertiesPropertyId,
            "modal=true", UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring);
        var imagePreview = automation.CreateAndCondition(
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
                UIA.UIA_ControlTypeIds.UIA_WindowControlTypeId),
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_NamePropertyId, T3ConversationAdapter.ImagePreviewName));
        var overlays = automation.CreateOrCondition(dialog, imagePreview);
        var loadingText = automation.CreateAndCondition(
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
                UIA.UIA_ControlTypeIds.UIA_TextControlTypeId),
            automation.CreateOrCondition(
                automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_NamePropertyId, "Loading messages..."),
                automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_NamePropertyId, "Syncing messages...")));
        var document = automation.CreateAndCondition(
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
                UIA.UIA_ControlTypeIds.UIA_DocumentControlTypeId),
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_AutomationIdPropertyId, "RootWebArea"));
        const int documentValue = UIA.UIA_PropertyIds.UIA_ValueValuePropertyId;
        var request = automation.CreateCacheRequest();
        request.TreeFilter = automation.RawViewCondition;
        request.TreeScope = UIA.TreeScope.TreeScope_Element;
        foreach (var property in new[] { UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
            UIA.UIA_PropertyIds.UIA_AriaPropertiesPropertyId, UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
            UIA.UIA_PropertyIds.UIA_AriaRolePropertyId, UIA.UIA_PropertyIds.UIA_RuntimeIdPropertyId,
            UIA.UIA_PropertyIds.UIA_NamePropertyId, documentValue }) request.AddProperty(property);
        var markers = capture.Read("capture-t3-landmarks", () => source.FindAllBuildCache(
            UIA.TreeScope.TreeScope_Descendants, automation.CreateAndCondition(visible,
                automation.CreateOrCondition(timelineClass, automation.CreateOrCondition(stop,
                    automation.CreateOrCondition(overlays, automation.CreateOrCondition(scrollControl,
                        automation.CreateOrCondition(findControl, automation.CreateOrCondition(loadingText, document))))))), request));
        UIA.IUIAutomationElement? timeline = null;
        UIA.IUIAutomationElement? threadDocument = null;
        UIA.IUIAutomationElement? modal = null;
        var documentUrl = "";
        var threadKey = "";
        var generating = false;
        var loading = false;
        var scrolledAway = false;
        for (var i = 0; i < markers.Length; i++)
        {
            capture.Check();
            var marker = markers.GetElement(i);
            if ((marker.CachedAriaProperties ?? "").Split(';').Any(value => value.Trim() == "modal=true")
                || marker.CachedControlType == UIA.UIA_ControlTypeIds.UIA_WindowControlTypeId
                    && marker.CachedName == T3ConversationAdapter.ImagePreviewName)
            {
                if (modal is not null) return Unavailable("multiple-modals");
                modal = marker;
                continue;
            }
            var classes = marker.CachedClassName ?? "";
            if (marker.CachedAriaRole == "search")
                return Unavailable("find");
            var buttonName = marker.CachedControlType == 50000 ? marker.CachedName ?? "" : "";
            if (ContainsCssClassToken(classes, scrollControlClass)
                || ContainsCssClassToken(classes, "pointer-events-auto")
                   && buttonName.StartsWith("Scroll to end", StringComparison.Ordinal))
            {
                scrolledAway = true;
                continue;
            }
            if (ContainsCssClassToken(classes, T3ConversationAdapter.TimelineClass))
            {
                if (timeline is not null) return Unavailable("multiple-timelines");
                timeline = marker;
            }
            else if (buttonName == "Stop generation") generating = true;
            else if (marker.CachedControlType == UIA.UIA_ControlTypeIds.UIA_TextControlTypeId
                && marker.CachedName is "Loading messages..." or "Syncing messages...")
                loading |= IsComposerSyncLabel(marker);
            else if (marker.CachedControlType == UIA.UIA_ControlTypeIds.UIA_DocumentControlTypeId
                && marker.GetCachedPropertyValue(documentValue) is string url
                && T3ConversationAdapter.GetThreadKey(url) is { Length: > 0 } key)
            {
                if (threadDocument is not null) return Unavailable("multiple-thread-documents");
                threadDocument = marker;
                documentUrl = url;
                threadKey = key;
            }
        }
        if (loading) return adapter.LoadingThread(capture);
        if (threadDocument is null) return Unavailable("no-thread-route");
        if (modal is not null)
        {
            if (!RouteMatches()) return Unavailable("thread-route-changed-during-capture");
            var paused = adapter.ReadModal(threadKey, modal.CachedControlType, modal.CachedName ?? "", modal.CachedClassName ?? "");
            capture.Stage(paused.T3CapturePaused ? "capture-t3-image-preview" : "capture-t3-unavailable-modal");
            return paused;
        }
        if (timeline is null) return Unavailable("no-timeline");
        if (scrolledAway)
        {
            capture.Stage("capture-t3-history-viewport");
            return RouteMatches() ? adapter.ReadHistoryViewport(threadKey, generating) : Unavailable("thread-route-changed-during-capture");
        }
        // T3's own history control is authoritative. Chromium's virtualized
        // scroll percentage can be far below 100 while T3 still follows live.
        var runtime = timeline.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_RuntimeIdPropertyId) as Array;
        if (runtime is not { Length: > 0 }) return Unavailable("no-runtime-id");
        var timelineKey = string.Join('.', runtime.Cast<object>());
        var tree = CacheCodexTranscript(automation, timeline, timelineKey, capture);
        // A switch during the tree read can pair the new transcript with the
        // old route. Drop that frame and wait for a consistent capture.
        if (!RouteMatches())
            return Unavailable("thread-route-changed-during-capture");
        var snapshot = adapter.ReadDocumentFrame(tree, documentUrl, atLiveEdge: true, generating, fingerprinter, capture);
        if (snapshot.Context?.ViewKey.Length == 0) capture.Stage("capture-t3-unavailable-cached-timeline");
        return snapshot;

        bool RouteMatches()
        {
            var currentUrl = capture.Read("capture-t3-confirm-thread-route",
                () => threadDocument.GetCurrentPropertyValue(documentValue)) as string;
            return T3ConversationAdapter.GetThreadKey(currentUrl) == threadKey;
        }

        WindowTextSnapshot Unavailable(string reason)
        {
            capture.Stage("capture-t3-unavailable-" + reason);
            return adapter.Unavailable();
        }

        bool IsComposerSyncLabel(UIA.IUIAutomationElement label)
        {
            var walker = automation.RawViewWalker;
            for (var depth = 0; depth < 3; depth++)
            {
                var parent = capture.Read("capture-t3-sync-label-parent", () => walker.GetParentElementBuildCache(label, request));
                if (parent is null) return false;
                if (parent.CachedAriaRole == "status"
                    && ContainsCssClassToken(parent.CachedClassName ?? "", "max-w-(--chat-content-max-width)"))
                    return true;
                label = parent;
            }
            return false;
        }
    }
}
