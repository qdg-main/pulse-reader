using System.Diagnostics;
using PulseReader.Monitoring;
using UIA = Interop.UIAutomationClient;

internal static class T3LiveCaptureChecks
{
    internal static void Inspect(IntPtr window)
    {
        var automation = (UIA.IUIAutomation)new UIA.CUIAutomation8();
        var source = automation.ElementFromHandle(window);
        var markers = automation.FindT3ScrollMarkers(source);
        var adapter = new T3ConversationAdapter();
        var watch = Stopwatch.StartNew();
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = WindowTextSnapshotReader.Read(automation, source, new WindowTextFingerprinter(),
            capture: new MonitoringCaptureContext(reportStage: stage => stages.Add(stage)), t3Conversation: adapter);
        if (!adapter.IsRecognized || snapshot.Context?.ViewKey.StartsWith("t3:", StringComparison.Ordinal) != true
            || snapshot.Blocks.Count == 0)
            throw new InvalidOperationException("The T3 chat at the live edge did not expose a readable assistant reply. Stages: "
                + string.Join(", ", stages) + ". " + markers);
        Console.WriteLine($"PASS: live T3 transcript; {snapshot.Blocks.Count} blocks, "
            + $"{snapshot.Blocks.Sum(block => block.Text.Length)} characters, "
            + $"final={snapshot.Blocks.Any(block => block.IsFinalResponse)}; {watch.ElapsedMilliseconds} ms. {markers}");
    }

    private static string FindT3ScrollMarkers(this UIA.IUIAutomation automation, UIA.IUIAutomationElement source)
    {
        const int cls = UIA.UIA_PropertyIds.UIA_ClassNamePropertyId;
        const int scroll = UIA.UIA_PropertyIds.UIA_ScrollVerticalScrollPercentPropertyId;
        var contains = UIA.PropertyConditionFlags.PropertyConditionFlags_MatchSubstring;
        var classes = automation.CreateOrCondition(
            automation.CreatePropertyConditionEx(cls, "messages-timeline-scroll", contains),
            automation.CreatePropertyConditionEx(cls, "chat-scroll-to-bottom", contains));
        var button = automation.CreateAndCondition(
            automation.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId, 50000),
            automation.CreatePropertyConditionEx(UIA.UIA_PropertyIds.UIA_NamePropertyId, "Scroll to end", contains));
        var request = automation.CreateCacheRequest();
        request.TreeFilter = automation.RawViewCondition;
        request.TreeScope = UIA.TreeScope.TreeScope_Element;
        request.AddProperty(cls);
        request.AddProperty(scroll);
        request.AddProperty(UIA.UIA_PropertyIds.UIA_IsOffscreenPropertyId);
        request.AddProperty(UIA.UIA_PropertyIds.UIA_ControlTypePropertyId);
        var matches = source.FindAllBuildCache(UIA.TreeScope.TreeScope_Descendants,
            automation.CreateOrCondition(classes, button), request);
        var result = new List<string>();
        for (var i = 0; i < matches.Length; i++)
        {
            var marker = matches.GetElement(i);
            var tag = marker.CachedControlType == 50000 ? "end-button" :
                marker.CachedClassName.Contains("chat-scroll-to-bottom") ? "end-container" : "timeline";
            result.Add($"{tag}:visible={marker.CachedIsOffscreen == 0}, percent={marker.GetCachedPropertyValue(scroll)}");
        }
        return string.Join("; ", result);
    }
}
