using System.Text.RegularExpressions;
using UIA = Interop.UIAutomationClient;

namespace PulseReader.Monitoring;

internal sealed record CachedCodexTranscriptNode(string Key, int Type, string Text,
    string ClassName, string Role, IReadOnlyList<CachedCodexTranscriptNode> Children,
    bool Hidden = false, bool Password = false, bool? ValueAvailable = false,
    bool? ValueReadOnly = true, string AutomationId = "", string AriaProperties = "",
    Rectangle? Bounds = null);

internal static partial class WindowTextSnapshotReader
{
    private const int MaximumCachedTranscriptNodes = 20000;
    private const int MaximumCachedTranscriptDepth = 128;
    private const int MaximumCachedTranscriptCharacters = 2_000_000;
    private static readonly Regex CodexWorkingDuration = new(
        @"\AWorking for [0-9]+[hms](?: [0-9]+[hms])*\z", RegexOptions.NonBacktracking);
    private static readonly Regex CodexCollapsedHistoryLabel = new(
        @"\A[0-9]+ previous messages?\z", RegexOptions.NonBacktracking);

    private static CachedCodexTranscriptNode CacheCodexTranscript(UIA.IUIAutomation automation,
        UIA.IUIAutomationElement transcript, string viewKey, MonitoringCaptureContext capture)
    {
        var request = automation.CreateCacheRequest();
        request.TreeScope = UIA.TreeScope.TreeScope_Subtree;
        request.TreeFilter = automation.RawViewCondition;
        // Cached-only elements cannot accidentally issue live property, pattern,
        // or parent calls during parsing. Only this selected subtree is cached.
        request.AutomationElementMode = UIA.AutomationElementMode.AutomationElementMode_None;
        foreach (var property in new[] { UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
            UIA.UIA_PropertyIds.UIA_NamePropertyId, UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
            UIA.UIA_PropertyIds.UIA_AriaRolePropertyId, UIA.UIA_PropertyIds.UIA_AriaPropertiesPropertyId,
            UIA.UIA_PropertyIds.UIA_AutomationIdPropertyId, UIA.UIA_PropertyIds.UIA_RuntimeIdPropertyId,
            UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId, UIA.UIA_PropertyIds.UIA_IsPasswordPropertyId,
            UIA.UIA_PropertyIds.UIA_BoundingRectanglePropertyId,
            UIA.UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId,
            UIA.UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId }) request.AddProperty(property);
        var cached = capture.Read("capture-cache-transcript", () => transcript.BuildUpdatedCache(request));
        capture.Stage("capture-parse-cached-tree");
        var count = 0;
        var characters = 0;
        return Read(cached, 0, "0");

        CachedCodexTranscriptNode Read(UIA.IUIAutomationElement element, int depth, string path)
        {
            capture.Check();
            if (++count > MaximumCachedTranscriptNodes || depth > MaximumCachedTranscriptDepth)
                throw new InvalidOperationException("Codex transcript exceeded the bounded cached-tree budget.");
            var type = element.CachedControlType;
            var password = element.CachedIsPassword != 0;
            var runtime = element.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_RuntimeIdPropertyId) as Array;
            var key = runtime is { Length: > 0 } ? string.Join('.', runtime.Cast<object>())
                : depth == 0 ? viewKey : $"{viewKey}:cached:{path}";
            var text = !password && type is 50020 or 50007 ? element.CachedName ?? "" : "";
            characters += text.Length;
            if (characters > MaximumCachedTranscriptCharacters)
                throw new InvalidOperationException("Codex transcript exceeded the bounded cached-text budget.");
            var children = new List<CachedCodexTranscriptNode>();
            // An embedded document is not part of the surrounding transcript's
            // message structure. Never parse its text or editable descendants.
            if (!password && (depth == 0 || type != 50030))
            {
                var cachedChildren = element.GetCachedChildren();
                for (var index = 0; cachedChildren is not null && index < cachedChildren.Length; index++)
                    children.Add(Read(cachedChildren.GetElement(index), depth + 1, $"{path}.{index}"));
            }
            var bounds = element.CachedBoundingRectangle;
            var patterns = CachedMonitoringPatterns.FromProperties(
                element.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId),
                element.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId), null);
            return new(key, type, text, element.CachedClassName ?? "", element.CachedAriaRole ?? "", children,
                element.CachedIsOffscreen != 0, password, patterns.ValueAvailable, patterns.ValueReadOnly,
                element.CachedAutomationId ?? "", element.CachedAriaProperties ?? "",
                Rectangle.FromLTRB(bounds.left, bounds.top, bounds.right, bounds.bottom));
        }
    }

    internal static WindowTextSnapshot ReadCachedCodexFrame(CachedCodexTranscriptNode root, string viewKey,
        WindowTextFingerprinter fingerprinter, CodexConversationAdapter adapter,
        bool includeDiagnostics = false, MonitoringCaptureContext? capture = null)
    {
        capture ??= new MonitoringCaptureContext();
        capture.Stage("capture-inspect-cached-conversation");
        if (root.Hidden || root.Key != viewKey || !CodexConversationView.IsContainer(root.ClassName))
            return Unavailable();
        var parts = new List<CodexTextPart>();
        var diagnostics = includeDiagnostics ? new List<WindowTextDiagnosticObservation>() : null;
        var editable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var listItems = new Dictionary<string, string>(StringComparer.Ordinal);
        var views = new CodexConversationView();
        var hasModal = false;
        var path = new List<InspectedElement>();
        var count = 0;
        var characters = 0;
        var order = 0;
        Visit(root, 0, "");
        var resolved = views.Resolve(hasModal);
        if (resolved != viewKey) return Unavailable();
        adapter.SetView(resolved);
        capture.Stage("capture-assemble-conversation");
        var blocks = adapter.Assemble(parts, fingerprinter);
        return new(blocks, diagnostics, editable.Values.Select(values => fingerprinter.Create(string.Join(' ', values))).ToArray(),
            adapter.ResponseCycleKey, new ConversationContext(resolved));

        WindowTextSnapshot Unavailable()
        {
            adapter.SetView("");
            return new([], Context: new(""));
        }

        void Visit(CachedCodexTranscriptNode node, int depth, string assistantGroup)
        {
            capture.Check();
            if (++count > MaximumCachedTranscriptNodes || depth > MaximumCachedTranscriptDepth
                || (characters += node.Text.Length) > MaximumCachedTranscriptCharacters)
                throw new InvalidOperationException("Codex transcript exceeded the bounded cached-frame budget.");
            if (depth > 0 && node.Type == 50030) return;
            if (depth is 1 or 2 && node.Role == "group" && IsFinalResponseContext(node.ClassName, withinCodexThread: true))
                assistantGroup = node.Key;
            views.Observe(node.Key, node.ClassName, !node.Hidden);
            hasModal |= !node.Hidden && CodexConversationView.IsBlockingOverlay(node.Role, node.ClassName, node.AriaProperties);
            // Recognised transcripts are read-only message surfaces. Exclude
            // explicit editors, passwords and writable/uncertain value controls
            // from cached metadata; never inspect whole TextPattern ranges.
            var isEditable = node.Password || node.Type == 50004
                || node.Role is "textbox" or "searchbox"
                || node.ValueAvailable is null
                || node.ValueAvailable is true && node.ValueReadOnly is not true
                || node.AriaProperties.Split(';').Any(value => value.Trim().Equals("readonly=false", StringComparison.OrdinalIgnoreCase));
            var metadata = new WindowTextDiagnosticAncestor(node.Type, node.ClassName, node.Role, node.AutomationId);
            path.Add(new(node.Key, metadata, IsStatusElement(node.Type, node.Role), IsInteractiveChromeElement(node.Type),
                IsAccessibilityOnlyClass(node.ClassName), IsAuxiliaryChromeElement(node.Type, node.ClassName, node.Role),
                isEditable, depth == 0, null));
            if (!node.Password && node.Type is 50020 or 50007 && node.Text.Trim().Length > 0)
            {
                var ancestors = path.AsEnumerable().Reverse().Take(MaximumEditableAncestorDepth).ToArray();
                var bounds = node.Bounds ?? new Rectangle(0, 0, 100, 20);
                var context = CreateElementContext(ancestors, bounds.Width <= 2 || bounds.Height <= 2, includeDiagnostics, capture);
                var text = RemoveNonverbalEdgeLabels(node.Text.Trim());
                var covered = context.AncestorKeys.Skip(1).Any(key => listItems.TryGetValue(key, out var item) && item.Contains(text, StringComparison.Ordinal));
                if (!covered && text.Length > 0)
                {
                    var included = ShouldIncludeObservedText(text, context.Editable, context.InteractiveChrome,
                        context.AccessibilityOnly, context.AuxiliaryChrome, context.WithinCodexThread, node.Role, context.ParagraphKey.Length > 0);
                    if (context.WithinCodexThread && !context.Editable && assistantGroup.Length > 0
                        && context.InteractiveChrome && node.Role == "description" && text.Length <= 40
                        && path.Count >= 3 && path[^2].Diagnostic.ControlType == 50000
                        && ContainsCssClassToken(path[^2].Diagnostic.ClassName, "no-drag")
                        && ContainsCssClassToken(path[^3].Diagnostic.ClassName, "text-secondary")
                        && (CodexCollapsedHistoryLabel.IsMatch(text)
                            || (text.StartsWith("Worked for ", StringComparison.Ordinal) && IsElapsedProgressLabel(text))))
                    {
                        parts.Add(new(CodexTextPartKind.CollapsedAssistantHistory, "", order,
                            AssistantGroupKey: assistantGroup));
                    }
                    else if (context.WithinCodexThread && !context.Editable && !context.InteractiveChrome)
                    {
                        var boundary = node.Role == "heading" && ContainsCssClassToken(node.ClassName, "sr-only");
                        if (boundary && text is "You said:" or "ChatGPT said:")
                            parts.Add(new(text == "You said:" ? CodexTextPartKind.UserBoundary : CodexTextPartKind.AssistantBoundary,
                                text, order, AssistantGroupKey: assistantGroup, IsWorkingBoundary: context.WithinWorkingColumn,
                                BoundaryKey: node.Key,
                                IsDirectAnswerHeading: text == "ChatGPT said:" && !context.WithinWorkingColumn
                                    && assistantGroup.Length > 0 && path[^2].Key == assistantGroup));
                        else if (context.WithinUserMessage)
                            parts.Add(new(CodexTextPartKind.UserText, text, order, UserMessageKey: context.UserMessageKey));
                        else if (assistantGroup.Length > 0 && !node.Hidden && node.Role == "description"
                                 && text.Length <= 64 && text.StartsWith("Working for ", StringComparison.Ordinal)
                                 && ContainsCssClassToken(path[^2].Diagnostic.ClassName, "overflow-visible")
                                 && ContainsCssClassToken(path[^2].Diagnostic.ClassName, "text-size-chat")
                                 && CodexWorkingDuration.IsMatch(text))
                            parts.Add(new(CodexTextPartKind.ActiveAssistant, "", order, AssistantGroupKey: assistantGroup));
                        else if (context.IsStatus && context.IsFinalResponseContext && !context.AuxiliaryChrome)
                            parts.Add(new(CodexTextPartKind.FinalAnnouncement, text, order, AssistantGroupKey: assistantGroup));
                        else if (included)
                            parts.Add(new(context.WithinWorkingColumn ? CodexTextPartKind.WorkingContent : CodexTextPartKind.Content,
                                text, order, context.ParagraphKey, AssistantGroupKey: assistantGroup));
                    }
                    diagnostics?.Add(new(node.Key, text, context.Editable, included, node.Type, node.ClassName,
                        node.Role, node.AutomationId, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, context.Ancestors!));
                    if (context.Editable)
                    {
                        var key = context.EditableContainerKey.Length > 0 ? context.EditableContainerKey : node.Key;
                        if (!editable.TryGetValue(key, out var values)) editable[key] = values = [];
                        values.Add(text);
                    }
                    if (included && node.Type == 50007) listItems[node.Key] = text;
                }
                order++;
            }
            if (!node.Password) foreach (var child in node.Children) Visit(child, depth + 1, assistantGroup);
            path.RemoveAt(path.Count - 1);
        }
    }
}
