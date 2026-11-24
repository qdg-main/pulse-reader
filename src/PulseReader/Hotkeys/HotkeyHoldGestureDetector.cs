using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PulseReader.Hotkeys;

internal enum HotkeyGesture
{
    Tap,
    Hold
}

internal enum HotkeyGestureProgress
{
    Pending,
    Tap,
    Hold
}

internal sealed class HotkeyHoldGestureDetector
{
    internal static readonly TimeSpan DefaultHoldThreshold = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly Func<Keys, bool> _isKeyPressed;
    private readonly TimeSpan _holdThreshold;

    public HotkeyHoldGestureDetector()
        : this(NativeHotkeyKeyState.IsPressed, DefaultHoldThreshold)
    {
    }

    internal HotkeyHoldGestureDetector(
        Func<Keys, bool> isKeyPressed,
        TimeSpan holdThreshold)
    {
        ArgumentNullException.ThrowIfNull(isKeyPressed);
        if (holdThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(holdThreshold));
        }

        _isKeyPressed = isKeyPressed;
        _holdThreshold = holdThreshold;
    }

    public async Task<HotkeyGesture> ClassifyAsync(
        HotkeyDefinition hotkey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hotkey);

        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = Classify(
                Stopwatch.GetElapsedTime(startedAt),
                IsChordPressed(hotkey),
                _holdThreshold);
            if (progress != HotkeyGestureProgress.Pending)
            {
                return progress == HotkeyGestureProgress.Hold
                    ? HotkeyGesture.Hold
                    : HotkeyGesture.Tap;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    public async Task WaitForReleaseAsync(
        HotkeyDefinition hotkey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hotkey);

        while (IsChordPressed(hotkey))
        {
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    internal static HotkeyGestureProgress Classify(
        TimeSpan elapsed,
        bool chordPressed,
        TimeSpan holdThreshold)
    {
        if (!chordPressed)
        {
            return HotkeyGestureProgress.Tap;
        }

        return elapsed >= holdThreshold
            ? HotkeyGestureProgress.Hold
            : HotkeyGestureProgress.Pending;
    }

    private bool IsChordPressed(HotkeyDefinition hotkey)
    {
        if (!_isKeyPressed(hotkey.Key))
        {
            return false;
        }

        return (!hotkey.Modifiers.HasFlag(HotkeyModifiers.Control)
                || _isKeyPressed(Keys.ControlKey)
                || _isKeyPressed(Keys.LControlKey)
                || _isKeyPressed(Keys.RControlKey))
               && (!hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)
                   || _isKeyPressed(Keys.ShiftKey)
                   || _isKeyPressed(Keys.LShiftKey)
                   || _isKeyPressed(Keys.RShiftKey))
               && (!hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)
                   || _isKeyPressed(Keys.Menu)
                   || _isKeyPressed(Keys.LMenu)
                   || _isKeyPressed(Keys.RMenu))
               && (!hotkey.Modifiers.HasFlag(HotkeyModifiers.Windows)
                   || _isKeyPressed(Keys.LWin)
                   || _isKeyPressed(Keys.RWin));
    }
}

internal static class NativeHotkeyKeyState
{
    public static bool IsPressed(Keys key) =>
        (GetAsyncKeyState((int)(key & Keys.KeyCode)) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
