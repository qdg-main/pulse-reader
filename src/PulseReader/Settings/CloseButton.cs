using System.Drawing.Drawing2D;

namespace PulseReader.Settings;

internal sealed class CloseButton : Control
{
    private bool _hovered;

    public CloseButton()
    {
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "Close settings";
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var background = new SolidBrush(
            _hovered ? PulseReaderTheme.SurfaceHover : PulseReaderTheme.Surface);
        using var border = new Pen(
            _hovered ? PulseReaderTheme.Accent : PulseReaderTheme.Border,
            _hovered ? 1.5f : 1f);
        eventArgs.Graphics.FillRectangle(background, ClientRectangle);
        eventArgs.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var centerX = Width / 2f;
        var centerY = Height / 2f;
        using var cross = new Pen(
            _hovered ? PulseReaderTheme.AccentBright : PulseReaderTheme.Text,
            1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        eventArgs.Graphics.DrawLine(cross, centerX - 4, centerY - 4, centerX + 4, centerY + 4);
        eventArgs.Graphics.DrawLine(cross, centerX + 4, centerY - 4, centerX - 4, centerY + 4);
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
}
