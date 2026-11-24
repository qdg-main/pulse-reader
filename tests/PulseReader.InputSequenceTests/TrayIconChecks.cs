using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using PulseReader;

internal static class TrayIconChecks
{
    internal static void SaveFrames(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        using var idleSource = TrayIconFrameRenderer.LoadIdleSource();
        using var idle = TrayIconFrameRenderer.Render(idleSource, TrayIconAnimator.RenderSize);
        idle.Save(Path.Combine(outputDirectory, "idle.png"), ImageFormat.Png);

        for (var frame = 0; frame < TrayIconFrameRenderer.SpeakingFrameCount; frame++)
        {
            using var speakingSource = TrayIconFrameRenderer.LoadSpeakingSource(frame);
            using var speaking = TrayIconFrameRenderer.Render(
                speakingSource,
                TrayIconAnimator.RenderSize);
            speaking.Save(
                Path.Combine(outputDirectory, $"speaking-{frame + 1}.png"),
                ImageFormat.Png);
        }

        Console.WriteLine($"Saved tray icon review frames to {Path.GetFullPath(outputDirectory)}.");
    }

    internal static void Run()
    {
        using var idleSource = TrayIconFrameRenderer.LoadIdleSource();
        AssertSource(idleSource, "idle");
        Assert(
            TrayIconFrameRenderer.SpeakingFrameCount == 3,
            "The tray icon should contain exactly three speaking frames.");

        var renderSize = TrayIconAnimator.RenderSize;
        var nativeTraySize = TrayIconAnimator.NativeTraySize;
        Assert(
            renderSize == nativeTraySize,
            $"The tray icon renders at {renderSize}px before Windows scales it to {nativeTraySize}px.");

        using var idle = TrayIconFrameRenderer.Render(idleSource, nativeTraySize);
        var frames = Enumerable.Range(0, TrayIconFrameRenderer.SpeakingFrameCount)
            .Select(RenderSpeakingFrame)
            .ToArray();

        try
        {
            var visibleBounds = FindVisibleBounds(idle, minimumAlpha: 128);
            Assert(
                visibleBounds.Width == nativeTraySize
                && visibleBounds.Height == nativeTraySize,
                $"The tray artwork leaves unused native pixels at {visibleBounds.Width}x{visibleBounds.Height}px of the {nativeTraySize}px icon.");

            Assert(idle.GetPixel(0, 0).A == 0, "The idle tray frame lost its transparent background.");
            Assert(
                frames.All(frame => frame.GetPixel(0, 0).A == 0),
                "A speaking frame lost its transparent background.");

            var hashes = frames.Select(Hash).ToHashSet(StringComparer.Ordinal);
            Assert(
                hashes.Count == TrayIconFrameRenderer.SpeakingFrameCount,
                "The speaking animation frames are not distinct.");
            Assert(
                frames.All(frame => AverageBrightness(frame) > AverageBrightness(idle)),
                "A speaking icon is not brighter than the idle icon.");
            Assert(
                frames.All(frame => CountBrightColumnRuns(frame) == 3),
                "The speaking equaliser bars merge at the native tray size.");
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }

        Console.WriteLine(
            $"PASS: transparent idle icon and {TrayIconFrameRenderer.SpeakingFrameCount} supplied speaking frames rendered.");

        Bitmap RenderSpeakingFrame(int frame)
        {
            using var source = TrayIconFrameRenderer.LoadSpeakingSource(frame);
            AssertSource(source, $"speaking frame {frame + 1}");
            return TrayIconFrameRenderer.Render(source, nativeTraySize);
        }
    }

    private static void AssertSource(Bitmap source, string description)
    {
        Assert(
            source.Width == 1024 && source.Height == 1024,
            $"The {description} tray artwork has the wrong dimensions.");
        Assert(
            source.GetPixel(0, 0).A == 0,
            $"The {description} tray artwork has no transparent canvas.");
    }

    private static Rectangle FindVisibleBounds(Bitmap bitmap, byte minimumAlpha)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A < minimumAlpha)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left || bottom < top
            ? Rectangle.Empty
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static int CountBrightColumnRuns(Bitmap bitmap)
    {
        const int minimumBrightness = 180;
        var runs = 0;
        var insideRun = false;
        var y = bitmap.Height / 2;

        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            var brightness = (color.R + color.G + color.B) / 3;
            var isBright = color.A >= 128 && brightness >= minimumBrightness;

            if (isBright && !insideRun)
            {
                runs++;
            }

            insideRun = isBright;
        }

        return runs;
    }

    private static string Hash(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static double AverageBrightness(Bitmap bitmap)
    {
        double total = 0;
        var visiblePixels = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.A == 0)
                {
                    continue;
                }

                total += (color.R + color.G + color.B) / 3d;
                visiblePixels++;
            }
        }

        return total / visiblePixels;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
