using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PulseReader.Hotkeys;

internal sealed class GlobalHotkeyManager : NativeWindow, IDisposable
{
    private const int FirstHotkeyId = 1;
    private const int SecondHotkeyId = 2;
    private const int WmHotkey = 0x0312;

    private int _activeHotkeyId = FirstHotkeyId;
    private HotkeyDefinition? _activeHotkey;
    private bool _registered;
    private bool _suspended;
    private bool _disposed;

    public GlobalHotkeyManager()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Pulse Reader hotkey window"
        });
    }

    public event EventHandler? Pressed;

    public void Register(HotkeyDefinition hotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registered)
        {
            throw new InvalidOperationException("A hotkey is already registered.");
        }

        _activeHotkey = hotkey;
        var modifiers = hotkey.Modifiers | HotkeyModifiers.NoRepeat;
        if (!RegisterHotKey(Handle, _activeHotkeyId, (uint)modifiers, (uint)hotkey.Key))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _registered = true;
    }

    public bool TryReplace(HotkeyDefinition hotkey, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_registered)
        {
            if (_suspended)
            {
                var previousHotkey = _activeHotkey;
                _activeHotkey = hotkey;
                var suspendedModifiers = hotkey.Modifiers | HotkeyModifiers.NoRepeat;
                if (!RegisterHotKey(
                        Handle,
                        _activeHotkeyId,
                        (uint)suspendedModifiers,
                        (uint)hotkey.Key))
                {
                    _activeHotkey = previousHotkey;
                    error = $"{hotkey.DisplayName} is already in use by another application.";
                    return false;
                }

                _registered = true;
                _suspended = false;
                error = null;
                return true;
            }

            try
            {
                Register(hotkey);
                error = null;
                return true;
            }
            catch (Win32Exception)
            {
                error = $"{hotkey.DisplayName} is already in use by another application.";
                return false;
            }
        }

        if (_activeHotkey == hotkey)
        {
            error = null;
            return true;
        }

        var replacementId = _activeHotkeyId == FirstHotkeyId
            ? SecondHotkeyId
            : FirstHotkeyId;
        var modifiers = hotkey.Modifiers | HotkeyModifiers.NoRepeat;

        if (!RegisterHotKey(Handle, replacementId, (uint)modifiers, (uint)hotkey.Key))
        {
            error = $"{hotkey.DisplayName} is already in use by another application.";
            return false;
        }

        if (!UnregisterHotKey(Handle, _activeHotkeyId))
        {
            UnregisterHotKey(Handle, replacementId);
            error = "Windows could not replace the current Pulse Reader hotkey.";
            return false;
        }

        _activeHotkeyId = replacementId;
        _activeHotkey = hotkey;
        error = null;
        return true;
    }

    public bool TrySuspend(out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_suspended)
        {
            error = null;
            return true;
        }

        if (_registered && !UnregisterHotKey(Handle, _activeHotkeyId))
        {
            error = "Windows could not temporarily release the current Pulse Reader hotkey.";
            return false;
        }

        _registered = false;
        _suspended = true;
        error = null;
        return true;
    }

    public bool TryResume(out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_suspended)
        {
            error = null;
            return true;
        }

        if (_activeHotkey is null)
        {
            _suspended = false;
            error = null;
            return true;
        }

        var modifiers = _activeHotkey.Modifiers | HotkeyModifiers.NoRepeat;
        if (!RegisterHotKey(Handle, _activeHotkeyId, (uint)modifiers, (uint)_activeHotkey.Key))
        {
            error = $"{_activeHotkey.DisplayName} could not be restored because another application is using it.";
            return false;
        }

        _registered = true;
        _suspended = false;
        error = null;
        return true;
    }

    protected override void WndProc(ref Message message)
    {
        if (_registered
            && message.Msg == WmHotkey
            && message.WParam.ToInt32() == _activeHotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_registered)
        {
            UnregisterHotKey(Handle, _activeHotkeyId);
            _registered = false;
        }

        DestroyHandle();
        _disposed = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
