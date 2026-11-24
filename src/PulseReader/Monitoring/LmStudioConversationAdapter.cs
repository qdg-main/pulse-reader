using System.Diagnostics;
using System.Text.RegularExpressions;
using UIA = Interop.UIAutomationClient;

namespace PulseReader.Monitoring;

// An in-memory projection of LM Studio's raw accessibility tree. Chat text is
// never written by this reader; the normal diagnostic sink fingerprints it.
internal sealed record LmStudioTextNode(string Key, int Type, string Text,
    IReadOnlyList<LmStudioTextNode> Children, bool Hidden = false, bool? Expanded = null,
    string AutomationId = "", string Role = "");

internal sealed class LmStudioConversationAdapter
{
    private readonly CodexConversationAdapter _conversation = new("lmstudio-message");
    private bool? _isLmStudio;
    public bool IsRecognized => _isLmStudio is true;

    public WindowTextSnapshot? TryRead(UIA.IUIAutomation automation,
        UIA.IUIAutomationElement source, WindowTextFingerprinter fingerprinter)
    {
        _isLmStudio ??= IsLmStudioProcess(source.CurrentProcessId);
        if (!IsRecognized) return null;
        // Capture raw-view structure and the small property set in one provider
        // request. Per-property COM round trips made generation/layout churn
        // exceed the old one-second scan limit and repeatedly baseline answers.
        var request = automation.CreateCacheRequest();
        request.TreeScope = UIA.TreeScope.TreeScope_Subtree;
        request.TreeFilter = automation.RawViewCondition;
        request.AutomationElementMode = UIA.AutomationElementMode.AutomationElementMode_None;
        foreach (var property in new[] {
            UIA.UIA_PropertyIds.UIA_ControlTypePropertyId, UIA.UIA_PropertyIds.UIA_NamePropertyId,
            UIA.UIA_PropertyIds.UIA_AutomationIdPropertyId, UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId,
            UIA.UIA_PropertyIds.UIA_IsPasswordPropertyId, UIA.UIA_PropertyIds.UIA_RuntimeIdPropertyId,
            UIA.UIA_PropertyIds.UIA_AriaRolePropertyId, UIA.UIA_PropertyIds.UIA_ExpandCollapseExpandCollapseStatePropertyId })
            request.AddProperty(property);
        var cached = source.BuildUpdatedCache(request);
        var count = 0;
        var characters = 0;
        var tree = ReadNode(cached, 0, false);
        return ReadFrame(tree, fingerprinter);

        LmStudioTextNode ReadNode(UIA.IUIAutomationElement element, int depth, bool withinMessages)
        {
            if (++count > 12000 || depth > 64)
                throw new InvalidOperationException("LM Studio's message tree exceeded the bounded capture budget.");
            var type = element.CachedControlType;
            var password = element.CachedIsPassword != 0;
            var hidden = element.CachedIsOffscreen != 0 || password;
            var id = element.CachedAutomationId ?? "";
            withinMessages |= id == "chat-terminal-messages";
            var runtime = element.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_RuntimeIdPropertyId) as Array;
            var key = runtime is null ? "" : string.Join(',', runtime.Cast<object>());
            // Buttons are landmarks, never speech. Editable content is never read.
            // Outside the chat subtree retain structural metadata only.
            var text = withinMessages && !password && type is 50020 or 50000 or 50029 or 50034 ? element.CachedName ?? "" : "";
            bool? expanded = null;
            if (type == 50000 && text is "Expand content" or "Collapse content"
                && element.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_ExpandCollapseExpandCollapseStatePropertyId) is int expansion)
                expanded = expansion == (int)UIA.ExpandCollapseState.ExpandCollapseState_Expanded;
            characters += text.Length;
            if (characters > 1_000_000) throw new InvalidOperationException("LM Studio's message text exceeded the capture budget.");
            var children = new List<LmStudioTextNode>();
            if (!password && type is not (50004 or 50000 or 50006))
            {
                var cachedChildren = element.GetCachedChildren();
                for (var i = 0; cachedChildren is not null && i < cachedChildren.Length; i++)
                    children.Add(ReadNode(cachedChildren.GetElement(i), depth + 1, withinMessages));
            }
            return new(key, type, text, children, hidden, expanded, id, element.CachedAriaRole ?? "");
        }
    }

    internal WindowTextSnapshot ReadFrame(LmStudioTextNode frame, WindowTextFingerprinter fingerprinter)
    {
        var nodes = Descendants(frame).ToArray();
        if (nodes.Any(n => !n.Hidden && n.Role == "dialog")) return Unavailable();
        // This exists even in a new empty chat. Its identity must not depend on
        // the appearance of the first message, the sidebar or the composer.
        var threads = nodes.Where(n => !n.Hidden && n.AutomationId == "chat-terminal-scroll-container").ToArray();
        if (threads.Length != 1 || threads[0].Key.Length == 0) return Unavailable();
        var messages = Descendants(threads[0]).Where(n => n.AutomationId == "chat-terminal-messages").ToArray();
        if (messages.Length > 1) return Unavailable();
        return Assemble(threads[0].Key, messages.Length == 0 ? [] : messages[0].Children, fingerprinter);
    }

    internal WindowTextSnapshot Assemble(string viewKey, IReadOnlyList<LmStudioTextNode> nodes,
        WindowTextFingerprinter fingerprinter)
    {
        _conversation.SetView("lmstudio:" + viewKey);
        var parts = new List<CodexTextPart>();
        var expectingUser = false;
        var expectingAssistant = false;
        var completed = false;
        var thoughts = new List<string>();
        var hasAnswer = false;
        var thinkingExpanded = false;
        var order = 0;
        var version = (Selected: 1, Total: 1);
        foreach (var node in nodes)
        {
            var buttons = Descendants(node).Where(n => n.Type == 50000).Select(n => n.Text).ToArray();
            var isBody = buttons.Contains("Copy message", StringComparer.Ordinal)
                || buttons.Contains("Delete message", StringComparer.Ordinal);
            if (!isBody)
            {
                // The observed chat list alternates a role/model header and a
                // message body. Never publish the header itself.
                var header = string.Join(' ', Paragraphs(node)).Trim();
                if (header.Length > 0)
                {
                    expectingUser = header == "You";
                    expectingAssistant = !expectingUser && header != "System";
                }
                continue;
            }

            var assistantActions = buttons.Any(name => name.StartsWith("Regenerate message", StringComparison.Ordinal)
                || name == "Continue assistant message");
            var user = expectingUser && !assistantActions;
            if (!user && !expectingAssistant && !assistantActions) continue;
            var content = node.Children.Where(child => !Descendants(child).Any(n => n.Type == 50000
                && n.Text is "Copy message" or "Delete message" or "Edit message")).ToArray();
            if (user)
            {
                parts.Add(new(CodexTextPartKind.UserBoundary, "", order++));
                foreach (var paragraph in content.SelectMany(child => Paragraphs(child)))
                    parts.Add(new(CodexTextPartKind.UserText, paragraph, order++));
                completed = false;
                thoughts.Clear();
                hasAnswer = false;
                thinkingExpanded = false;
                version = (1, 1);
            }
            else
            {
                version = ReadResponseVersion(node);
                parts.Add(new(CodexTextPartKind.AssistantBoundary, "", order++));
                foreach (var container in content)
                {
                    foreach (var section in container.Children)
                    {
                        if (section.Type == 50000 && section.Text == "Hover to view message generation info")
                        { completed = true; break; } // everything after this is performance metadata
                        if (section.Type is not (50026 or 50036 or 50008 or 50007)
                            && !(section.Type == 50020 && section.Text.Length == 0 && section.Children.Count > 0))
                            continue; // no model/status/statistic labels
                        var thinking = IsThinkingSection(section);
                        if (thinking) thinkingExpanded = Descendants(section).Any(n =>
                            n.Type == 50000 && n.Text is "Expand content" or "Collapse content" && n.Expanded == true);
                        // A paragraph is an anonymous Text wrapper containing
                        // text leaves. Only these content roots are eligible.
                        foreach (var paragraph in Paragraphs(section, thinking))
                        {
                            if (thinking) thoughts.Add(paragraph);
                            else
                            {
                                parts.Add(new(CodexTextPartKind.Content, paragraph, order++));
                                hasAnswer = true;
                            }
                        }
                    }
                }
            }
            expectingUser = expectingAssistant = false;
        }
        // Browsing an older alternative disrupts the visible live response.
        // Suspend and rebaseline on return rather than replaying that history.
        if (version.Selected < 1 || version.Selected != version.Total) return Unavailable();
        _conversation.IsResponseActive = !completed;
        if (completed) parts.Add(new(CodexTextPartKind.FinalAnnouncement, "", order));
        var blocks = _conversation.Assemble(parts, fingerprinter, version.Total - 1).ToList();
        var thinkingState = _conversation.CurrentTurnAccepted && _conversation.ResponseCycleKey.Length > 0
            ? new MonitoringThinkingSnapshot(_conversation.ResponseCycleKey, string.Join('\n', thoughts),
                thinkingExpanded, completed || hasAnswer || _conversation.CurrentTurnSealed) : null;
        return new(blocks, ResponseCycleKey: _conversation.ResponseCycleKey,
            Context: new(_conversation.ViewKey), Thinking: thinkingState);
    }

    private static (int Selected, int Total) ReadResponseVersion(LmStudioTextNode body)
    {
        static bool Has(LmStudioTextNode node, string name) => Descendants(node)
            .Any(n => n.Type == 50000 && n.Text == name);
        const string previous = "Previous generated response", next = "Next generated response";
        if (!Has(body, previous) && !Has(body, next)) return (1, 1);
        // Locate the smallest common container of the two navigation buttons.
        // Its numeric label is metadata, never speech or model-generated prose.
        var group = Descendants(body).LastOrDefault(n => Has(n, previous) && Has(n, next));
        if (group is null) return (0, 0);
        var label = string.Join(' ', group.Children.SelectMany(c => Paragraphs(c))).Trim();
        var match = Regex.Match(label, @"^(\d+)\s*/\s*(\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var selected)
            || !int.TryParse(match.Groups[2].Value, out var total) || selected < 1 || selected > total)
            return (0, 0);
        return (selected, total);
    }

    private static bool IsThinkingSection(LmStudioTextNode node) => Descendants(node).Any(n =>
        n.Type == 50000 && n.Text is "Expand content" or "Collapse content")
        || (node.Children.Any(n => n.Type == 50026)
            && node.Children.Any(n => n.Type == 50020 && Paragraphs(n).Any(text =>
                Regex.IsMatch(text, @"^(?:Thinking(?:\.{3}|…)?|Thought for \d+(?:\.\d+)? (?:seconds?|minutes?))$",
                    RegexOptions.CultureInvariant))));

    private static IEnumerable<string> Paragraphs(LmStudioTextNode node, bool thinking = false)
    {
        // Viewport clipping is not a message boundary. The named chat list may
        // expose offscreen paragraphs; only the latest turn can be published.
        if (node.Type is 50000 or 50004 or 50006) yield break;
        if (node.Type == 50036)
        {
            foreach (var row in Descendants(node).Where(n => n.Children.Any(c => c.Type is 50029 or 50034)))
            {
                var cells = row.Children.Where(c => c.Type is 50029 or 50034)
                    .Select(c => string.Join(' ', Paragraphs(c)).Trim().TrimEnd('.', '!', '?'))
                    .Where(text => text.Length > 0).Select(text => text + ".");
                var text = string.Join(' ', cells);
                if (text.Length > 0) yield return text;
            }
            yield break;
        }
        if (node.Type is 50020 or 50029 or 50034)
        {
            var text = node.Text.Length > 0 ? node.Text : string.Join(' ', node.Children.SelectMany(child => Paragraphs(child)));
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
            yield break;
        }
        foreach (var child in node.Children)
        {
            // Thinking's direct Text child is its elapsed-time/status label;
            // actual prose belongs to the inner content container.
            if (thinking && child.Type == 50020) continue;
            foreach (var text in Paragraphs(child)) yield return text;
        }
    }

    private static IEnumerable<LmStudioTextNode> Descendants(LmStudioTextNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var nested in Descendants(child)) yield return nested;
    }

    private static WindowTextSnapshot Unavailable() => new([], Context: new(""));
    private static bool IsLmStudioProcess(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.ProcessName.Equals("LM Studio", StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
