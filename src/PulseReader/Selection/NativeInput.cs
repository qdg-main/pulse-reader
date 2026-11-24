using System.Runtime.InteropServices;

namespace PulseReader.Selection;

internal static class NativeInput
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint WmCopy = 0x0301;

    private static readonly KeyboardKey[] ModifierKeys =
    [
        KeyboardKey.LeftShift,
        KeyboardKey.RightShift,
        KeyboardKey.LeftControl,
        KeyboardKey.RightControl,
        KeyboardKey.LeftAlt,
        KeyboardKey.RightAlt,
        KeyboardKey.LeftWindows,
        KeyboardKey.RightWindows
    ];

    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    public static IntPtr GetCaretWindowHandle(IntPtr applicationWindowHandle)
    {
        if (applicationWindowHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var threadId = GetWindowThreadProcessId(applicationWindowHandle, out _);
        var threadInfo = new GuiThreadInfo
        {
            Size = (uint)Marshal.SizeOf<GuiThreadInfo>()
        };

        // Alt-based hotkeys can move keyboard focus into an application's access-key UI.
        // The caret owner still identifies the editor that held the selected text.
        return threadId != 0
               && GetGUIThreadInfo(threadId, ref threadInfo)
               && IsWindowForApplication(
                   applicationWindowHandle,
                   threadInfo.CaretWindow)
            ? threadInfo.CaretWindow
            : IntPtr.Zero;
    }

    public static void RestoreForegroundWindow(IntPtr windowHandle)
    {
        if (windowHandle != IntPtr.Zero && GetForegroundWindow() != windowHandle)
        {
            SetForegroundWindow(windowHandle);
        }
    }

    public static bool SendCopyShortcut() => SendCopyShortcut(GetHeldModifiers(), SendKeyboardSteps);

    internal static bool SendCopyShortcut(IReadOnlySet<KeyboardKey> heldModifiers,
        Func<IReadOnlyList<KeyboardInputStep>, uint> send)
    {
        var steps = BuildCopyShortcutSequence(heldModifiers);
        var inserted = send(steps);
        if (inserted == steps.Count) return true;

        // SendInput inserts events serially and reports how many were inserted.
        // Release only unmatched key-downs from that accepted prefix. Never
        // retry the copy itself or restore the user's previous modifier state.
        var down = new List<KeyboardKey>();
        foreach (var step in steps.Take((int)Math.Min(inserted, (uint)steps.Count)))
        {
            if (step.IsKeyUp) down.Remove(step.Key);
            else if (!down.Contains(step.Key)) down.Add(step.Key);
        }
        var releases = down.AsEnumerable().Reverse().Select(key => new KeyboardInputStep(key, true)).ToArray();
        // Best effort if Windows also blocks cleanup. Keep the original failure
        // visible to the caller, and never spin indefinitely or press more keys.
        for (var attempt = 0; releases.Length > 0 && attempt < 3; attempt++)
        {
            var released = send(releases);
            releases = releases.Skip((int)Math.Min(released, (uint)releases.Length)).ToArray();
        }
        return false;
    }

    private static uint SendKeyboardSteps(IReadOnlyList<KeyboardInputStep> steps)
    {
        var inputs = steps.Select(KeyboardInput).ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    // Posting avoids blocking on another application's UI thread while still addressing
    // the editor directly instead of whichever access-key window currently has focus.
    public static bool PostCopyMessage(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero
        && PostMessage(windowHandle, WmCopy, UIntPtr.Zero, IntPtr.Zero);

    public static bool IsWindowForApplication(
        IntPtr applicationWindowHandle,
        IntPtr candidateWindowHandle)
    {
        return applicationWindowHandle != IntPtr.Zero
               && candidateWindowHandle != IntPtr.Zero
               && IsWindow(candidateWindowHandle)
               && (candidateWindowHandle == applicationWindowHandle
                   || IsChild(applicationWindowHandle, candidateWindowHandle));
    }

    internal static IReadOnlyList<KeyboardInputStep> BuildCopyShortcutSequence(
        IReadOnlySet<KeyboardKey> heldModifiers)
    {
        var steps = new List<KeyboardInputStep>();
        var heldAlts = HeldKeys(
            heldModifiers,
            KeyboardKey.LeftAlt,
            KeyboardKey.RightAlt);

        // Supply our own Ctrl guard: a physically held Ctrl from the snapshot
        // may already have been released. It also supplies Ctrl for the copy.
        // Alt-up and Win-up must not act like standalone access-key gestures.
        steps.Add(new KeyboardInputStep(KeyboardKey.LeftControl, false));

        AddKeyUps(steps, heldAlts);
        AddKeyUps(
            steps,
            HeldKeys(heldModifiers, KeyboardKey.LeftShift, KeyboardKey.RightShift));
        AddKeyUps(
            steps,
            HeldKeys(heldModifiers, KeyboardKey.LeftWindows, KeyboardKey.RightWindows));
        AddKeyUps(steps, HeldKeys(heldModifiers, KeyboardKey.RightControl));
        steps.Add(new KeyboardInputStep(KeyboardKey.C, false));
        steps.Add(new KeyboardInputStep(KeyboardKey.C, true));
        steps.Add(new KeyboardInputStep(KeyboardKey.LeftControl, true));

        // Do not synthesize modifier-down restoration from the old snapshot.
        // The user's physical key-up may already have happened by this point.
        return steps;
    }

    public static uint GetClipboardSequenceNumberValue() => GetClipboardSequenceNumber();

    private static HashSet<KeyboardKey> GetHeldModifiers() =>
        ModifierKeys
            .Where(key => (GetAsyncKeyState((int)key) & 0x8000) != 0)
            .ToHashSet();

    private static List<KeyboardKey> HeldKeys(
        IReadOnlySet<KeyboardKey> heldModifiers,
        params KeyboardKey[] candidates) =>
        candidates.Where(heldModifiers.Contains).ToList();

    private static void AddKeyUps(
        ICollection<KeyboardInputStep> steps,
        IEnumerable<KeyboardKey> keys)
    {
        foreach (var key in keys)
        {
            steps.Add(new KeyboardInputStep(key, true));
        }
    }

    private static Input KeyboardInput(KeyboardInputStep step) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = (ushort)step.Key,
                Flags = (step.IsKeyUp ? KeyEventKeyUp : 0)
                    | (IsExtendedKey(step.Key) ? KeyEventExtendedKey : 0)
            }
        }
    };

    private static bool IsExtendedKey(KeyboardKey key) =>
        key is KeyboardKey.RightControl
            or KeyboardKey.RightAlt
            or KeyboardKey.LeftWindows
            or KeyboardKey.RightWindows;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(
        uint threadId,
        ref GuiThreadInfo threadInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(
        IntPtr parentWindowHandle,
        IntPtr candidateWindowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputData Mouse;

        [FieldOffset(0)]
        public KeyboardInputData Keyboard;

        [FieldOffset(0)]
        public HardwareInputData Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInputData
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public RectangleData CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectangleData
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal enum KeyboardKey : ushort
{
    C = 0x43,
    LeftWindows = 0x5B,
    RightWindows = 0x5C,
    LeftShift = 0xA0,
    RightShift = 0xA1,
    LeftControl = 0xA2,
    RightControl = 0xA3,
    LeftAlt = 0xA4,
    RightAlt = 0xA5
}

internal readonly record struct KeyboardInputStep(KeyboardKey Key, bool IsKeyUp);
