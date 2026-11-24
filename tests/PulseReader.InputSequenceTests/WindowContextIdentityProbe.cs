using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UIA = Interop.UIAutomationClient;

// Explicit, bounded diagnostic only. It neither navigates nor changes monitoring.
// Names, identifiers and provider-specific metadata are salted in memory;
// the salt and original values are never written to disk.
internal static class WindowContextIdentityProbe
{
    public static void Run(IntPtr handle, string outputPath, int seconds)
    {
        if (handle == IntPtr.Zero || seconds is < 1 or > 600)
            throw new ArgumentException("Specify a source window and a duration from 1 to 600 seconds.");
        using var salt = new ProbeFingerprinter();
        var automation = new UIA.CUIAutomation8();
        var source = automation.ElementFromHandle(handle)
            ?? throw new InvalidOperationException("The source window is unavailable.");
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var writer = new StreamWriter(new FileStream(fullPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(new { kind = "header", sourceWindow = handle.ToInt64(),
            startedUtc = DateTimeOffset.UtcNow, seconds,
            privacy = "Salted metadata fingerprints only; no message text, field values or salt stored." }));
        var clock = Stopwatch.StartNew();
        string? previous = null;
        var snapshots = 0;
        do
        {
            var scan = Stopwatch.StartNew();
            var nodes = new List<object>();
            var ancestors = new List<object>();
            var inspectedAncestors = new HashSet<string>(StringComparer.Ordinal);
            var skipped = 0;
            var elements = source.FindAll(UIA.TreeScope.TreeScope_Descendants, automation.CreateTrueCondition());
            var count = elements.Length;
            for (var i = 0; i < Math.Min(count, 5000); i++)
            {
                try
                {
                    var element = elements.GetElement(i);
                    var type = element.CurrentControlType;
                    // Raw-view containers may not be returned by FindAll.
                    // Inspect shared ancestry once without reading any ancestor names.
                    var ancestor = automation.RawViewWalker.GetParentElement(element);
                    for (var depth = 0; ancestor is not null && depth < 24; depth++)
                    {
                        var runtime = RuntimeId(ancestor);
                        if (!inspectedAncestors.Add(runtime)) break;
                        var ancestorClass = ancestor.CurrentClassName ?? "";
                        var ancestorId = ancestor.CurrentAutomationId ?? "";
                        var ancestorRole = ancestor.CurrentAriaRole ?? "";
                        var ancestorAria = ancestor.CurrentAriaProperties ?? "";
                        var ancestorParent = automation.CompareElements(ancestor, source) != 0
                            ? null : automation.RawViewWalker.GetParentElement(ancestor);
                        ancestors.Add(new {
                            runtimeHash = salt.Hash(runtime),
                            parentHash = ancestorParent is null ? null : salt.Hash(RuntimeId(ancestorParent)),
                            classHash = salt.Hash(ancestorClass),
                            threadContainer = ancestorClass.Split(' ').Contains("thread-scroll-container", StringComparer.Ordinal),
                            navigation = ancestorRole == "navigation", dialog = ancestorRole == "dialog",
                            ariaModal = Regex.IsMatch(ancestorAria, @"(?:^|;)modal=true(?:;|$)"),
                            idHash = salt.Hash(ancestorId), idLength = ancestorId.Length,
                            idContainsGuid = Regex.IsMatch(ancestorId, @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}"),
                            offscreen = ancestor.CurrentIsOffscreen != 0
                        });
                        ancestor = ancestorParent;
                    }
                    // Do not inspect the names/values of message text or editable fields.
                    if (type is UIA.UIA_ControlTypeIds.UIA_TextControlTypeId
                        or UIA.UIA_ControlTypeIds.UIA_EditControlTypeId) continue;
                    var role = element.CurrentAriaRole ?? "";
                    var classes = element.CurrentClassName ?? "";
                    var id = element.CurrentAutomationId ?? "";
                    var aria = element.CurrentAriaProperties ?? "";
                    var thread = classes.Split(' ').Contains("thread-scroll-container", StringComparer.Ordinal);
                    var selected = element.GetCurrentPropertyValue(UIA.UIA_PropertyIds.UIA_SelectionItemIsSelectedPropertyId);
                    var isSelected = selected is bool value && value;
                    var ariaState = Regex.Matches(aria, @"(?:^|;)(?:current|selected|modal|hidden|busy|expanded)=[^;]*")
                        .Select(match => match.Value.TrimStart(';')).ToArray();
                    if (!thread && id.Length == 0 && !isSelected && ariaState.Length == 0
                        && type is not (UIA.UIA_ControlTypeIds.UIA_ButtonControlTypeId
                            or UIA.UIA_ControlTypeIds.UIA_HyperlinkControlTypeId
                            or UIA.UIA_ControlTypeIds.UIA_DocumentControlTypeId
                            or UIA.UIA_ControlTypeIds.UIA_TabItemControlTypeId
                            or UIA.UIA_ControlTypeIds.UIA_ListItemControlTypeId)
                        && role is not ("navigation" or "dialog" or "main" or "tab" or "treeitem")) continue;
                    var parent = automation.RawViewWalker.GetParentElement(element);
                    var name = element.CurrentName ?? "";
                    var linkValue = type is UIA.UIA_ControlTypeIds.UIA_HyperlinkControlTypeId
                        or UIA.UIA_ControlTypeIds.UIA_DocumentControlTypeId
                        ? element.GetCurrentPropertyValue(UIA.UIA_PropertyIds.UIA_ValueValuePropertyId) as string ?? ""
                        : "";
                    nodes.Add(new {
                        index = i, type, roleHash = salt.Hash(role), classHash = salt.Hash(classes),
                        threadContainer = thread, navigation = role == "navigation", dialog = role == "dialog",
                        offscreen = element.CurrentIsOffscreen != 0,
                        selected = isSelected,
                        ariaCurrent = Regex.IsMatch(aria, @"(?:^|;)current=(?:true|page)(?:;|$)"),
                        ariaSelected = Regex.IsMatch(aria, @"(?:^|;)selected=true(?:;|$)"),
                        ariaModal = Regex.IsMatch(aria, @"(?:^|;)modal=true(?:;|$)"),
                        ariaState = ariaState.Select(salt.Hash).ToArray(),
                        ariaHash = salt.Hash(aria),
                        nameHash = salt.Hash(name), nameLength = name.Length,
                        idHash = salt.Hash(id), idLength = id.Length,
                        idContainsGuid = Regex.IsMatch(id, @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}"),
                        linkValueHash = salt.Hash(linkValue), linkValueLength = linkValue.Length,
                        linkValueContainsGuid = Regex.IsMatch(linkValue, @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}"),
                        runtimeHash = salt.Hash(RuntimeId(element)),
                        parentHash = parent is null ? null : salt.Hash(RuntimeId(parent))
                    });
                }
                catch (COMException) { skipped++; }
            }
            var signature = JsonSerializer.Serialize(new { count, truncated = count > 5000, skipped, nodes, ancestors });
            if (signature != previous)
            {
                writer.WriteLine(JsonSerializer.Serialize(new { kind = "snapshot", timestampUtc = DateTimeOffset.UtcNow,
                    elapsedMs = clock.ElapsedMilliseconds, scanMs = scan.ElapsedMilliseconds,
                    count, truncated = count > 5000, skipped, nodes, ancestors }));
                previous = signature;
                snapshots++;
                Console.WriteLine($"Context snapshot {snapshots}: {nodes.Count} metadata candidates; {scan.ElapsedMilliseconds} ms.");
            }
            if (writer.BaseStream.Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("Context probe reached its 8 MiB capture limit.");
            if (clock.Elapsed.TotalSeconds >= seconds) break;
            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(2000, seconds * 1000 - clock.Elapsed.TotalMilliseconds)));
        } while (clock.Elapsed.TotalSeconds < seconds);
        writer.WriteLine(JsonSerializer.Serialize(new { kind = "finished", timestampUtc = DateTimeOffset.UtcNow, snapshots }));
    }

    private static string RuntimeId(UIA.IUIAutomationElement element) =>
        string.Join(',', element.GetRuntimeId().Cast<object>());

    private sealed class ProbeFingerprinter : IDisposable
    {
        private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
        public string Hash(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var input = new byte[_salt.Length + bytes.Length];
            _salt.CopyTo(input, 0);
            bytes.CopyTo(input, _salt.Length);
            return Convert.ToHexString(SHA256.HashData(input))[..20];
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(_salt);
    }
}
