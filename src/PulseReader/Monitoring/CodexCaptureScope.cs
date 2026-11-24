namespace PulseReader.Monitoring;

internal sealed record CodexCaptureLandmark<T>(T Element, string Key, string ClassName,
    string Role, string AriaProperties, bool Visible);

// This routing seam deliberately has no access to unrelated text. Discovery
// supplies only landmark metadata; the selected transcript owns the text read.
internal static class CodexCaptureScope
{
    internal static WindowTextSnapshot? Read<T>(IReadOnlyList<CodexCaptureLandmark<T>> landmarks,
        CodexConversationAdapter adapter, Func<T, string, WindowTextSnapshot> readTranscript)
    {
        var views = new CodexConversationView();
        var hasModal = false;
        foreach (var item in landmarks)
        {
            if (!item.Visible) continue;
            if (CodexConversationView.IsContainer(item.ClassName))
            {
                adapter.IsRecognized = true;
                views.Observe(item.Key, item.ClassName, true);
            }
            hasModal |= CodexConversationView.IsBlockingOverlay(item.Role, item.ClassName, item.AriaProperties);
        }
        if (!adapter.IsRecognized) return null;
        var key = views.Resolve(hasModal);
        adapter.SetView(key);
        if (key.Length == 0) return new WindowTextSnapshot([], Context: new(""));
        var target = landmarks.First(item => item.Visible && item.Key == key);
        return readTranscript(target.Element, key);
    }
}
