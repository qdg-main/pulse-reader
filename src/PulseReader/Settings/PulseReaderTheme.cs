using System.Drawing.Drawing2D;

namespace PulseReader.Settings;

internal static class PulseReaderTheme
{
    public static readonly Color Window = Color.FromArgb(10, 18, 35);
    public static readonly Color Surface = Color.FromArgb(16, 29, 53);
    public static readonly Color SurfaceHover = Color.FromArgb(22, 44, 78);
    public static readonly Color DisabledSurface = Color.FromArgb(13, 22, 39);
    public static readonly Color Border = Color.FromArgb(38, 91, 142);
    public static readonly Color Separator = Color.FromArgb(29, 65, 103);
    public static readonly Color DisabledBorder = Color.FromArgb(31, 48, 68);
    public static readonly Color Accent = Color.FromArgb(72, 211, 255);
    public static readonly Color AccentBright = Color.FromArgb(190, 244, 255);
    public static readonly Color Text = Color.FromArgb(239, 247, 255);
    public static readonly Color MutedText = Color.FromArgb(145, 170, 197);
    public static readonly Color DisabledText = Color.FromArgb(83, 101, 122);
    public static readonly Color Error = Color.FromArgb(255, 153, 165);

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        => RoundedRectangle(bounds, (float)radius);

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
