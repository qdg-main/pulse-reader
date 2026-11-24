using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

internal static class ReadmeScreenshotChecks
{
    private static readonly Size OverviewSize = new(880, 500);

    public static void SaveAll(string outputDirectory)
    {
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);

        var playbackPath = Path.Combine(fullOutputDirectory, "playback-controls.png");
        var settingsPath = Path.Combine(fullOutputDirectory, "settings.png");
        var trayMenuPath = Path.Combine(fullOutputDirectory, "tray-menu.png");
        var overviewPath = Path.Combine(fullOutputDirectory, "interface-overview.png");

        PlaybackIslandChecks.SaveReadmeFrame(playbackPath);
        SettingsInteractionChecks.SaveReadmeScreenshot(settingsPath);
        TrayMenuChecks.SavePreview(trayMenuPath);
        SaveOverview(playbackPath, settingsPath, trayMenuPath, overviewPath);

        Console.WriteLine($"Saved README screenshots to {fullOutputDirectory}.");
    }

    private static void SaveOverview(
        string playbackPath,
        string settingsPath,
        string trayMenuPath,
        string outputPath)
    {
        using var playback = new Bitmap(playbackPath);
        using var settings = new Bitmap(settingsPath);
        using var trayMenu = new Bitmap(trayMenuPath);
        using var overview = new Bitmap(
            OverviewSize.Width,
            OverviewSize.Height,
            PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(overview);
        graphics.Clear(Color.Transparent);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;

        const int edge = 20;
        const int columnGap = 42;
        const int settingsHeight = 460;
        var settingsSize = ScaleToHeight(settings.Size, settingsHeight);
        var interfaceScale = settingsSize.Height / (double)settings.Height;
        var trayMenuSize = Scale(trayMenu.Size, interfaceScale);
        var playbackSize = Scale(playback.Size, interfaceScale / 1.5d);

        var settingsBounds = new Rectangle(
            edge,
            edge,
            settingsSize.Width,
            settingsSize.Height);
        var rightColumnLeft = settingsBounds.Right + columnGap;
        var trayMenuBounds = new Rectangle(
            rightColumnLeft,
            settingsBounds.Bottom - trayMenuSize.Height,
            trayMenuSize.Width,
            trayMenuSize.Height);
        var playbackBounds = new Rectangle(
            rightColumnLeft,
            trayMenuBounds.Top - columnGap - playbackSize.Height,
            playbackSize.Width,
            playbackSize.Height);

        graphics.DrawImage(settings, settingsBounds);
        graphics.DrawImage(trayMenu, trayMenuBounds);
        graphics.DrawImage(playback, playbackBounds);
        overview.Save(outputPath, ImageFormat.Png);
    }

    private static Size ScaleToHeight(Size source, int height) =>
        new((int)Math.Round(source.Width * height / (double)source.Height), height);

    private static Size Scale(Size source, double scale) =>
        new(
            (int)Math.Round(source.Width * scale),
            (int)Math.Round(source.Height * scale));
}
