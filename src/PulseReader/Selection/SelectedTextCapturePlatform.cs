namespace PulseReader.Selection;

internal interface IClipboardCaptureSnapshot : IDisposable
{
    bool IsComplete { get; }

    void Restore();
}

internal interface ISelectedTextCapturePlatform
{
    IntPtr GetForegroundWindowHandle();

    IntPtr GetCaretWindowHandle(IntPtr applicationWindowHandle);

    uint GetClipboardSequenceNumber();

    bool TryCaptureSnapshot(out IClipboardCaptureSnapshot? snapshot);

    void ClearClipboard();

    string? ReadClipboardText();

    bool PostCopyMessage(IntPtr windowHandle);

    void RestoreForegroundWindow(IntPtr windowHandle);

    bool SendCopyShortcut();
}

internal sealed class WindowsSelectedTextCapturePlatform : ISelectedTextCapturePlatform
{
    public IntPtr GetForegroundWindowHandle() => NativeInput.GetForegroundWindowHandle();

    public IntPtr GetCaretWindowHandle(IntPtr applicationWindowHandle) =>
        NativeInput.GetCaretWindowHandle(applicationWindowHandle);

    public uint GetClipboardSequenceNumber() =>
        NativeInput.GetClipboardSequenceNumberValue();

    public bool TryCaptureSnapshot(out IClipboardCaptureSnapshot? snapshot)
    {
        var captured = ClipboardSnapshot.TryCapture(out var clipboardSnapshot);
        snapshot = clipboardSnapshot;
        return captured;
    }

    public void ClearClipboard() => Clipboard.Clear();

    public string? ReadClipboardText()
    {
        if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
        {
            return Clipboard.GetText(TextDataFormat.UnicodeText);
        }

        return Clipboard.ContainsText(TextDataFormat.Text)
            ? Clipboard.GetText(TextDataFormat.Text)
            : null;
    }

    public bool PostCopyMessage(IntPtr windowHandle) => NativeInput.PostCopyMessage(windowHandle);

    public void RestoreForegroundWindow(IntPtr windowHandle) =>
        NativeInput.RestoreForegroundWindow(windowHandle);

    public bool SendCopyShortcut() => NativeInput.SendCopyShortcut();
}

internal sealed record SelectedTextCaptureTiming(
    int ClipboardRetryCount,
    TimeSpan ClipboardRetryDelay,
    TimeSpan PostedCopyTimeout,
    TimeSpan CopyTimeout,
    TimeSpan CopyPollDelay)
{
    public static SelectedTextCaptureTiming Default { get; } = new(
        10,
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(650),
        TimeSpan.FromMilliseconds(20));

    public void Validate()
    {
        if (ClipboardRetryCount <= 0
            || ClipboardRetryDelay < TimeSpan.Zero
            || PostedCopyTimeout <= TimeSpan.Zero
            || CopyTimeout <= TimeSpan.Zero
            || CopyPollDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ClipboardRetryCount),
                "Clipboard capture retry counts and timeouts must be positive.");
        }
    }
}
