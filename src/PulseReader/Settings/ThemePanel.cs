namespace PulseReader.Settings;

internal sealed class ThemePanel : Panel
{
    public ThemePanel()
    {
        BackColor = PulseReaderTheme.Surface;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        using var pen = new Pen(PulseReaderTheme.Border);
        eventArgs.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
