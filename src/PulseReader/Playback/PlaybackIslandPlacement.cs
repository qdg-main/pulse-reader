using System.Runtime.InteropServices;

namespace PulseReader.Playback;

internal readonly record struct PlaybackIslandPlacement(
    Rectangle WorkingArea,
    Rectangle SourceWindowBounds,
    IntPtr SourceWindowHandle = default);

internal enum PlaybackIslandPinAnchor
{
    Monitor,
    Window
}

internal readonly record struct PlaybackIslandPinState(
    bool IsPinned,
    float NormalizedX,
    float NormalizedY,
    PlaybackIslandPinAnchor Anchor = PlaybackIslandPinAnchor.Monitor,
    float WindowNormalizedX = 0.5f,
    float WindowNormalizedY = 0.9f)
{
    public static PlaybackIslandPinState Default { get; } = new(
        false,
        0.5f,
        0.9f,
        PlaybackIslandPinAnchor.Monitor,
        0.5f,
        0.9f);

    public static bool IsValid(float normalizedX, float normalizedY) =>
        float.IsFinite(normalizedX)
        && float.IsFinite(normalizedY)
        && normalizedX is >= 0f and <= 1f
        && normalizedY is >= 0f and <= 1f;

    public bool IsValid() =>
        Enum.IsDefined(Anchor)
        && IsValid(NormalizedX, NormalizedY)
        && IsValid(WindowNormalizedX, WindowNormalizedY);
}

internal static class PlaybackIslandPlacementCapture
{
    private const int DwmExtendedFrameBounds = 9;

    public static PlaybackIslandPlacement Capture(
        IntPtr sourceWindow,
        Point fallbackPoint)
    {
        var hasSourceBounds = TryGetWindowBounds(sourceWindow, out var bounds);
        var workingArea = hasSourceBounds
            ? Screen.FromRectangle(bounds).WorkingArea
            : Screen.FromPoint(fallbackPoint).WorkingArea;
        var sourceBounds = hasSourceBounds
                           && Rectangle.Intersect(bounds, workingArea) is { Width: > 0, Height: > 0 }
            ? bounds
            : Rectangle.Empty;

        return new PlaybackIslandPlacement(
            workingArea,
            sourceBounds,
            hasSourceBounds ? sourceWindow : IntPtr.Zero);
    }

    public static PlaybackIslandPlacement RefreshSourceWindow(
        PlaybackIslandPlacement placement)
    {
        if (!TryGetWindowBounds(placement.SourceWindowHandle, out var bounds))
        {
            return placement with { SourceWindowBounds = Rectangle.Empty };
        }

        var workingArea = Screen.FromRectangle(bounds).WorkingArea;
        var sourceBounds = Rectangle.Intersect(bounds, workingArea) is { Width: > 0, Height: > 0 }
            ? bounds
            : Rectangle.Empty;
        return new PlaybackIslandPlacement(
            workingArea,
            sourceBounds,
            placement.SourceWindowHandle);
    }

    private static bool TryGetWindowBounds(IntPtr windowHandle, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (windowHandle == IntPtr.Zero
            || NativeMethods.IsIconic(windowHandle))
        {
            return false;
        }

        var hasBounds = NativeMethods.DwmGetWindowAttribute(
                            windowHandle,
                            DwmExtendedFrameBounds,
                            out var nativeBounds,
                            Marshal.SizeOf<NativeRectangle>()) == 0
                        && HasArea(nativeBounds);
        if (!hasBounds)
        {
            hasBounds = NativeMethods.GetWindowRect(windowHandle, out nativeBounds)
                        && HasArea(nativeBounds);
        }

        if (!hasBounds)
        {
            return false;
        }

        bounds = Rectangle.FromLTRB(
            nativeBounds.Left,
            nativeBounds.Top,
            nativeBounds.Right,
            nativeBounds.Bottom);
        return true;
    }

    private static bool HasArea(NativeRectangle bounds) =>
        bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            out NativeRectangle attributeValue,
            int attributeSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(
            IntPtr windowHandle,
            out NativeRectangle bounds);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr windowHandle);
    }
}
