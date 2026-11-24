using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using PulseReader.Hotkeys;

namespace PulseReader.Settings;

internal sealed class HotkeyCaptureBox : Control
{
    private bool _hovered;
    private bool _capturing;

    public HotkeyCaptureBox()
    {
        BackColor = PulseReaderTheme.Surface;
        ForeColor = PulseReaderTheme.Text;
        Font = new Font("Segoe UI Semibold", 10f);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.Text;
        AccessibleName = "Global hotkey capture";
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
    }

    public HotkeyDefinition? Hotkey { get; private set; }

    internal bool IsCapturing => _capturing;

    public event EventHandler? HotkeyChanged;

    public event EventHandler? CaptureStarted;

    public event EventHandler? CaptureEnded;

    public void SetHotkey(HotkeyDefinition hotkey)
    {
        Hotkey = hotkey;
        Text = hotkey.DisplayName;
        Invalidate();
    }

    internal void CancelCapture() => EndCapture();

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var active = Focused || _hovered;
        using var background = new SolidBrush(
            active ? PulseReaderTheme.SurfaceHover : PulseReaderTheme.Surface);
        using var border = new Pen(
            active ? PulseReaderTheme.Accent : PulseReaderTheme.Border,
            active ? 1.5f : 1f);
        eventArgs.Graphics.FillRectangle(background, ClientRectangle);
        eventArgs.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            ClientRectangle,
            _capturing ? PulseReaderTheme.AccentBright : PulseReaderTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        Focus();
        base.OnClick(eventArgs);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnEnter(EventArgs eventArgs)
    {
        _capturing = true;
        Text = "Press shortcut…";
        Invalidate();
        CaptureStarted?.Invoke(this, EventArgs.Empty);
        base.OnEnter(eventArgs);
    }

    protected override void OnLeave(EventArgs eventArgs)
    {
        EndCapture();
        base.OnLeave(eventArgs);
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (!_capturing)
        {
            base.OnKeyDown(eventArgs);
            return;
        }

        eventArgs.SuppressKeyPress = true;
        eventArgs.Handled = true;

        if (eventArgs.KeyCode == Keys.Escape)
        {
            EndCapture();
            Parent?.SelectNextControl(this, true, true, true, true);
        }
        else
        {
            TryCapture(eventArgs.KeyData, IsWindowsKeyDown());
        }

        base.OnKeyDown(eventArgs);
    }

    internal bool TryCapture(Keys keyData, bool windowsKeyDown = false)
    {
        var key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
            || key == Keys.None)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        if ((keyData & Keys.Control) == Keys.Control)
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if ((keyData & Keys.Alt) == Keys.Alt)
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if ((keyData & Keys.Shift) == Keys.Shift)
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (windowsKeyDown)
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            Text = "Include Ctrl, Alt, Shift, or Win";
            Invalidate();
            return false;
        }

        Hotkey = new HotkeyDefinition(modifiers, key);
        Text = Hotkey.DisplayName;
        Invalidate();
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void EndCapture()
    {
        if (!_capturing)
        {
            return;
        }

        _capturing = false;
        Text = Hotkey?.DisplayName ?? "Press shortcut";
        Invalidate();
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWindowsKeyDown() =>
        GetKeyState((int)Keys.LWin) < 0 || GetKeyState((int)Keys.RWin) < 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
