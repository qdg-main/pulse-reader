namespace PulseReader.Settings;

internal sealed class GlowButton : Button
{
    private bool _hovered;

    public GlowButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 1;
        FlatAppearance.BorderColor = PulseReaderTheme.Border;
        FlatAppearance.MouseOverBackColor = PulseReaderTheme.SurfaceHover;
        FlatAppearance.MouseDownBackColor = Color.FromArgb(28, 61, 99);
        BackColor = PulseReaderTheme.Surface;
        ForeColor = PulseReaderTheme.Text;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Semibold", 9f);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        FlatAppearance.BorderColor = PulseReaderTheme.Accent;
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        FlatAppearance.BorderColor = PulseReaderTheme.Border;
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (!Enabled)
        {
            eventArgs.Graphics.Clear(PulseReaderTheme.Surface);
            using var borderPen = new Pen(PulseReaderTheme.Border);
            eventArgs.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(
                eventArgs.Graphics,
                Text,
                Font,
                ClientRectangle,
                PulseReaderTheme.MutedText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        base.OnPaint(eventArgs);

        if (_hovered || Focused)
        {
            using var pen = new Pen(Color.FromArgb(90, PulseReaderTheme.Accent), 1f);
            eventArgs.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
    }
}
