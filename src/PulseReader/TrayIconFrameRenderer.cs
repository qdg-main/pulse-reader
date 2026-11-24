using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PulseReader;

internal static class TrayIconFrameRenderer
{
    private static readonly RectangleF SourceCrop = new(112, 111, 800, 800);

    internal const string IdleIconResourceName =
        "PulseReader.Assets.TrayIcon.pulse-reader-idle.png";

    private static readonly string[] SpeakingIconResourceNames =
    [
        "PulseReader.Assets.TrayIcon.pulse-reader-logo.png",
        "PulseReader.Assets.TrayIcon.pulse-reader-speaking-02.png",
        "PulseReader.Assets.TrayIcon.pulse-reader-speaking-03.png"
    ];

    internal static int SpeakingFrameCount => SpeakingIconResourceNames.Length;

    internal static Bitmap LoadIdleSource() => LoadEmbeddedSource(IdleIconResourceName);

    internal static Bitmap LoadSpeakingSource(int frame)
    {
        if (frame < 0 || frame >= SpeakingFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        return LoadEmbeddedSource(SpeakingIconResourceNames[frame]);
    }

    internal static Bitmap Render(Bitmap source, int size)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, size, size),
            SourceCrop,
            GraphicsUnit.Pixel);
        return output;
    }

    private static Bitmap LoadEmbeddedSource(string resourceName)
    {
        using var stream = typeof(TrayIconFrameRenderer).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The Pulse Reader tray icon resource '{resourceName}' is missing.");
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }
}
