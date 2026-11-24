using System.Diagnostics;
using UIA = Interop.UIAutomationClient;

namespace PulseReader.Monitoring;

internal sealed record DiscordTextNode(int Type, string Id, string Role, string ClassName,
    string Text, IReadOnlyList<DiscordTextNode> Children, bool Hidden = false, string DocumentUrl = "");

internal sealed class DiscordConversationAdapter
{
    private bool? _recognized;
    private readonly DiscordAuthorMemory _authors = new();
    public bool IsRecognized => _recognized is true;

    public WindowTextSnapshot? TryRead(UIA.IUIAutomation automation, UIA.IUIAutomationElement source,
        WindowTextFingerprinter? fingerprinter = null)
    {
        var frame = Capture(automation, source);
        return frame is null ? null : ReadFrame(frame, _authors, fingerprinter);
    }

    internal static WindowTextSnapshot ReadFrame(DiscordTextNode frame, DiscordAuthorMemory? authors = null,
        WindowTextFingerprinter? fingerprinter = null)
    {
        var nodes = Descendants(frame).ToArray();
        if (nodes.Any(n => !n.Hidden && n.Role is "dialog" or "alertdialog")) return Unavailable();
        var lists = nodes.Where(n => !n.Hidden && n.Type == 50008 && HasClass(n, "scrollerInner")).ToArray();
        if (lists.Length != 1) return Unavailable();
        var rows = lists[0].Children.Where(n => n.Type == 50007 && TryMessageId(n.Id, out _, out _)).ToArray();
        var channels = rows.Select(n => n.Id.Split('-')[2]).Distinct(StringComparer.Ordinal).ToArray();
        // Empty lists have no message IDs yet. The document's actual route
        // supplies identity before the first arrival; sidebar labels do not.
        if (channels.Length == 0)
            channels = nodes.Where(n => !n.Hidden && n.Type == 50030 && n.Id == "RootWebArea")
                .Select(n => ChannelFromRoute(n.DocumentUrl)).Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal).ToArray();
        // Do not guess a conversation from window titles or sidebar selection.
        if (channels.Length != 1) return Unavailable();
        authors ??= new();
        authors.SetChannel(channels[0]);
        foreach (var row in rows)
        {
            TryMessageId(row.Id, out _, out var id);
            var article = row.Children.SingleOrDefault(n => n.Role == "article");
            if (article is null) continue;
            var cleanName = ReadExplicitAuthor(article, id);
            if (cleanName.Length > 0) authors.Remember(AuthorLabel(article.Text), cleanName);
        }
        var blocks = new List<WindowTextBlock>();
        var previousAuthor = "";
        foreach (var row in rows)
        {
            TryMessageId(row.Id, out _, out var messageId);
            var article = row.Children.SingleOrDefault(n => n.Role == "article");
            var text = "";
            var authorPrefixLength = 0;
            WindowTextFingerprint? bodyFingerprint = null;
            if (article is not null)
            {
                var author = ReadExplicitAuthor(article, messageId);
                // Discord omits the username only for continuations of the
                // preceding message group. Reply labels can differ from the
                // group's header label, so prefer that structural relationship.
                if (author.Length == 0 && !HasClass(article, "groupStart")) author = previousAuthor;
                if (author.Length == 0) author = authors.Resolve(AuthorLabel(article.Text));
                previousAuthor = author;
                // Match the current message's exact content ID, never a quoted reply.
                var body = article.Children.SingleOrDefault(n => n.Id == "message-content-" + messageId);
                var content = body is null ? "" : ReadBody(body).Trim();
                if (content.Length > 0) bodyFingerprint = fingerprinter?.Create(content);
                if (author.Length > 0 && content.Length > 0)
                {
                    var prefix = author.TrimEnd('.', '!', '?') + ". ";
                    authorPrefixLength = prefix.Length;
                    text = prefix + content;
                }
            }
            else previousAuthor = "";
            // Empty bodies still carry identity: attachments and own posts must
            // not become new speech when edited or when metadata changes later.
            blocks.Add(new(row.Id, text, Order: blocks.Count, DiscreteBodyFingerprint: bodyFingerprint,
                DiscreteAuthorPrefixLength: authorPrefixLength));
        }
        // Only the observed message composer, never search, nickname editing,
        // or unrelated editable controls. Null means unavailable, [] means empty.
        var composers = nodes.Where(n => !n.Hidden && n.Type == 50004 && n.Role == "textbox" && HasClass(n, "slateTextArea")).ToArray();
        IReadOnlyList<WindowTextFingerprint>? drafts = null;
        if (composers.Length == 1 && fingerprinter is not null)
        {
            var draft = fingerprinter.Create(string.Concat(composers[0].Children.Select(ReadBody)));
            drafts = draft.WordCount > 0 ? [draft] : [];
        }
        return new(blocks, EditableFingerprints: drafts, Context: new("discord:" + channels[0]), IsDiscreteFeed: true);
    }

    internal static bool TryMessageId(string key, out string channel, out ulong id)
    {
        channel = "";
        id = 0;
        var parts = key.Split('-');
        if (parts.Length != 4 || parts[0] != "chat" || parts[1] != "messages"
            || !ulong.TryParse(parts[2], out _) || !ulong.TryParse(parts[3], out id)) return false;
        channel = parts[2];
        return true;
    }

    private static string ChannelFromRoute(string route)
    {
        if (!Uri.TryCreate(route, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host is not ("discord.com" or "canary.discord.com" or "ptb.discord.com")) return "";
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "channels"
            && (parts[1] == "@me" || ulong.TryParse(parts[1], out _))
            && ulong.TryParse(parts[2], out _) ? parts[2] : "";
    }

    private static string ReadExplicitAuthor(DiscordTextNode article, ulong id)
    {
        var username = Descendants(article).FirstOrDefault(n => n.Id == "message-username-" + id);
        return username is null ? "" : Descendants(username)
            .FirstOrDefault(n => n.Type == 50000 && HasClass(n, "username"))?.Text.Trim() ?? "";
    }

    private static string AuthorLabel(string label)
    {
        // This is a lookup key only, NEVER speech. Consecutive posts inherit
        // the header's combined accessibility label, including arbitrary badges.
        var end = label.IndexOf(" , ", StringComparison.Ordinal);
        if (end < 1) return "";
        return label[..end].Trim();
    }

    private static string ReadBody(DiscordTextNode node)
    {
        if (node.Type == 50004 || node.Role == "time" || HasClass(node, "hiddenVisually")
            || HasClass(node, "edited") || HasClass(node, "spoilerHidden")) return "";
        // Inline mentions can expose only a button/link's accessible name,
        // with no Text children. Read that name once, not both it and its children.
        if (HasClass(node, "mention") && node.Text.Length > 0) return node.Text;
        if (node.Type == 50000 && !HasClass(node, "mention")) return "";
        if (node.Type == 50006) return ""; // decorative/emoji images, not attachment descriptions
        if (node.Type == 50020 && node.Children.Count == 0) return node.Text;
        // Text leaves retain punctuation and inline spacing. Adding a space per
        // node would break links, code, and formatted words.
        return string.Concat(node.Children.Select(ReadBody));
    }

    private static bool HasClass(DiscordTextNode node, string token) => node.ClassName
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(c => c == token || c.StartsWith(token + "_", StringComparison.Ordinal));
    private static WindowTextSnapshot Unavailable() => new([], Context: new(""), IsDiscreteFeed: true);

    internal DiscordTextNode? Capture(UIA.IUIAutomation automation, UIA.IUIAutomationElement source)
    {
        _recognized ??= IsDiscordProcess(source.CurrentProcessId);
        if (!IsRecognized) return null;
        var request = automation.CreateCacheRequest();
        request.TreeScope = UIA.TreeScope.TreeScope_Subtree;
        request.TreeFilter = automation.RawViewCondition;
        request.AutomationElementMode = UIA.AutomationElementMode.AutomationElementMode_None;
        foreach (var property in new[] { UIA.UIA_PropertyIds.UIA_ControlTypePropertyId,
            UIA.UIA_PropertyIds.UIA_NamePropertyId, UIA.UIA_PropertyIds.UIA_AutomationIdPropertyId,
            UIA.UIA_PropertyIds.UIA_AriaRolePropertyId, UIA.UIA_PropertyIds.UIA_ClassNamePropertyId,
            UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId, UIA.UIA_PropertyIds.UIA_IsPasswordPropertyId,
            UIA.UIA_PropertyIds.UIA_ValueValuePropertyId })
            request.AddProperty(property);
        var count = 0;
        var characters = 0;
        return Read(source.BuildUpdatedCache(request), 0, false);

        DiscordTextNode Read(UIA.IUIAutomationElement element, int depth, bool inConversation)
        {
            if (++count > 20000 || depth > 80)
                throw new InvalidOperationException("Discord's accessibility tree exceeded the capture budget.");
            var type = element.CachedControlType;
            var id = element.CachedAutomationId ?? "";
            var role = element.CachedAriaRole ?? "";
            var name = element.CachedName ?? "";
            var password = element.CachedIsPassword != 0;
            var classes = element.CachedClassName ?? "";
            var composer = type == 50004 && role == "textbox"
                && classes.Split(' ').Any(c => c.StartsWith("slateTextArea_", StringComparison.Ordinal));
            inConversation |= id.StartsWith("chat-messages-", StringComparison.Ordinal)
                || composer;
            var text = inConversation && !password && type != 50004 ? name : "";
            characters += text.Length;
            if (characters > 1_000_000) throw new InvalidOperationException("Discord's message text exceeded the capture budget.");
            var children = new List<DiscordTextNode>();
            if (!password && (type != 50004 || composer))
            {
                var cached = element.GetCachedChildren();
                for (var i = 0; cached is not null && i < cached.Length; i++)
                    children.Add(Read(cached.GetElement(i), depth + 1, inConversation));
            }
            return new(type, id, role, classes, text, children,
                element.CachedIsOffscreen != 0 || password,
                type == 50030 && id == "RootWebArea" && !password
                    ? element.GetCachedPropertyValue(UIA.UIA_PropertyIds.UIA_ValueValuePropertyId) as string ?? "" : "");
        }
    }

    internal static IEnumerable<DiscordTextNode> Descendants(DiscordTextNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var nested in Descendants(child)) yield return nested;
    }

    private static bool IsDiscordProcess(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.ProcessName.Equals("Discord", StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}

// Keep clean names across message-list virtualization, but never across channels.
// Ambiguous decorated labels are withheld instead of attributing to the wrong author.
internal sealed class DiscordAuthorMemory
{
    private string _channel = "";
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    public void SetChannel(string channel)
    {
        if (_channel == channel) return;
        _channel = channel;
        _names.Clear();
    }
    public void Remember(string label, string name)
    {
        if (label.Length == 0) return;
        if (_names.TryGetValue(label, out var previous))
        {
            if (previous != name) _names[label] = "";
            return;
        }
        if (_names.Count >= 256) _names.Remove(_names.Keys.First());
        _names[label] = name;
    }
    public string Resolve(string label) => _names.GetValueOrDefault(label, "");
}
