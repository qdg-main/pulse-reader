using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using PulseReader.Playback;
using PulseReader.Speech;

internal static class PlaybackIslandChecks
{
    private static readonly Size PreviewSize = PlaybackIslandForm.PreviewSize;
    private static readonly PlaybackNavigationState ActiveNavigation = new(true, 1, 3);

    public static void Run()
    {
        using var preparing = Render(SpeechPlaybackState.Preparing);
        using var speaking = Render(SpeechPlaybackState.Speaking);
        using var paused = Render(SpeechPlaybackState.Paused);
        using var pressed = PlaybackIslandForm.RenderFrame(
            PreviewSize,
            SpeechPlaybackState.Speaking,
            false,
            false,
            true,
            true,
            0,
            ActiveNavigation);
        using var pausedPressed = PlaybackIslandForm.RenderFrame(
            PreviewSize,
            SpeechPlaybackState.Paused,
            false,
            false,
            true,
            true,
            0,
            ActiveNavigation);
        using var closeVisible = PlaybackIslandForm.RenderFrame(
            PreviewSize,
            SpeechPlaybackState.Speaking,
            true,
            false,
            false,
            false,
            0,
            ActiveNavigation);
        using var closeFading = PlaybackIslandForm.RenderFrame(
            PreviewSize,
            SpeechPlaybackState.Speaking,
            true,
            false,
            false,
            false,
            0,
            ActiveNavigation,
            closeOpacity: 0.45f);
        using var candidatePin = PlaybackIslandForm.RenderFrame(
            PreviewSize,
            SpeechPlaybackState.Speaking,
            false,
            false,
            false,
            false,
            0,
            ActiveNavigation,
            pinVisible: true);
        using var pinnedPin = PlaybackIslandForm.RenderFrame(
            PreviewSize,
            SpeechPlaybackState.Speaking,
            false,
            false,
            false,
            false,
            0,
            ActiveNavigation,
            pinVisible: true,
            pinPinned: true);
        using var fadingPinnedPin = PlaybackIslandForm.RenderFrame(
            PreviewSize,
            SpeechPlaybackState.Speaking,
            false,
            false,
            false,
            false,
            0,
            ActiveNavigation,
            pinVisible: true,
            pinPinned: true,
            pinOpacity: 0.45f);
        using var monitoringIdle = PlaybackIslandForm.RenderFrame(
            PreviewSize,
            SpeechPlaybackState.Idle,
            false,
            false,
            false,
            false,
            0,
            PlaybackNavigationState.Unavailable,
            monitoring: true);
        using var monitoringPaused = PlaybackIslandForm.RenderFrame(
            PreviewSize,
            SpeechPlaybackState.Idle,
            false,
            false,
            false,
            false,
            0,
            PlaybackNavigationState.Unavailable,
            monitoring: true,
            monitoringPaused: true);

        Assert(
            preparing.GetPixel(0, 0).A == 0
            && speaking.GetPixel(0, 0).A == 0
            && paused.GetPixel(0, 0).A == 0,
            "The floating playback island lost its transparent outer canvas.");
        Assert(
            new[] { Hash(preparing), Hash(speaking), Hash(paused) }
                .Distinct(StringComparer.Ordinal)
                .Count() == 3,
            "Preparing, speaking, and paused island states do not render distinctly.");
        Assert(
            Hash(pressed) != Hash(speaking),
            "Pressing a transport control does not visibly depress its button face.");
        var pressedPlaybackCenter = pressed.GetPixel(
            PlaybackIslandForm.PreviewPlaybackButtonCenter.X,
            PlaybackIslandForm.PreviewPlaybackButtonCenter.Y);
        Assert(
            pressedPlaybackCenter.R + pressedPlaybackCenter.G + pressedPlaybackCenter.B > 450,
            "Pressing pause does not immediately preview the play glyph before release.");
        Assert(
            Hash(pausedPressed) != Hash(paused),
            "Pressing the latched pause control does not depress it beyond its held state.");
        Assert(
            VisiblePixelCount(closeVisible) > VisiblePixelCount(speaking),
            "The hover stop button does not add a visible hit target.");
        Assert(
            AlphaTotal(closeVisible) > AlphaTotal(closeFading)
            && AlphaTotal(closeFading) > AlphaTotal(speaking),
            "The hover stop button does not render a genuine intermediate fade frame.");
        Assert(
            Hash(candidatePin) != Hash(pinnedPin)
            && VisiblePixelCount(candidatePin) > VisiblePixelCount(speaking),
            "The pending and pinned position controls do not render distinctly.");
        var pinHitSurfacePixel = candidatePin.GetPixel(1, 1);
        Assert(
            pinHitSurfacePixel.A is > 0 and <= 6,
            "The background-free pin does not retain its full layered-window hit surface.");
        Assert(
            AlphaTotal(pinnedPin) > AlphaTotal(fadingPinnedPin)
            && AlphaTotal(fadingPinnedPin) > AlphaTotal(speaking),
            "The pinned position control does not render a genuine intermediate fade frame.");
        Assert(
            Hash(monitoringIdle) != Hash(monitoringPaused)
            && Hash(monitoringIdle) != Hash(speaking),
            "Monitoring idle and paused states do not render as distinct control states.");
        Assert(
            PlaybackIslandForm.CloseButtonLingerMilliseconds >= 1000,
            "The hover stop button disappears too quickly to reach.");
        Assert(
            !PlaybackIslandForm.PreviewCloseButtonBounds.IntersectsWith(
                PlaybackIslandForm.PreviewTransportBounds),
            "The hover stop hit target overlaps a transport control.");
        Assert(
            !PlaybackIslandForm.PreviewPinButtonBounds.IntersectsWith(
                PlaybackIslandForm.PreviewTransportBounds),
            "The pin hit target overlaps a transport control.");
        Assert(
            PlaybackIslandForm.PreviewCloseButtonHitBounds.Contains(
                PlaybackIslandForm.PreviewCloseButtonBounds)
            && PlaybackIslandForm.PreviewCloseButtonHitBounds.Width
               > PlaybackIslandForm.PreviewCloseButtonBounds.Width
            && PlaybackIslandForm.PreviewPinButtonHitBounds.Contains(
                PlaybackIslandForm.PreviewPinButtonBounds)
            && PlaybackIslandForm.PreviewPinButtonHitBounds.Width
               > PlaybackIslandForm.PreviewPinButtonBounds.Width,
            "The corner controls do not provide enlarged invisible click targets.");
        Assert(
            !PlaybackIslandForm.PreviewCloseButtonHitBounds.IntersectsWith(
                PlaybackIslandForm.PreviewTransportBounds)
            && !PlaybackIslandForm.PreviewPinButtonHitBounds.IntersectsWith(
                PlaybackIslandForm.PreviewTransportBounds),
            "An enlarged corner-control click target overlaps a transport control.");
        AssertPlacementBounds();
        AssertUnpinPreservesSessionPosition();
        AssertRestartFromBeginningFiresWhileHeld();
        Console.WriteLine("PASS: floating playback island renders its control states and anchors safely to the source window.");
    }

    public static void SaveFrames(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        Save("preparing.png", Render(SpeechPlaybackState.Preparing));
        Save("speaking.png", Render(SpeechPlaybackState.Speaking));
        Save("paused.png", Render(SpeechPlaybackState.Paused));
        Save(
            "speaking-pressed.png",
            PlaybackIslandForm.RenderFrame(
                PreviewSize,
                SpeechPlaybackState.Speaking,
                false,
                false,
                true,
                true,
                0,
                ActiveNavigation));
        Save(
            "paused-pressed.png",
            PlaybackIslandForm.RenderFrame(
                PreviewSize,
                SpeechPlaybackState.Paused,
                false,
                false,
                true,
                true,
                0,
                ActiveNavigation));
        Save(
            "speaking-hover-stop.png",
            PlaybackIslandForm.RenderFrame(
                PreviewSize,
                SpeechPlaybackState.Speaking,
                true,
                true,
                true,
                false,
                0,
                ActiveNavigation));
        Save(
            "speaking-fading-stop.png",
            PlaybackIslandForm.RenderFrame(
                PreviewSize,
                SpeechPlaybackState.Speaking,
                true,
                false,
                true,
                false,
                0,
                ActiveNavigation,
                closeOpacity: 0.45f));
        Save(
            "speaking-pin-candidate.png",
            PlaybackIslandForm.RenderFrame(
                PreviewSize,
                SpeechPlaybackState.Speaking,
                false,
                false,
                false,
                false,
                0,
                ActiveNavigation,
                pinVisible: true));
        Save(
            "speaking-pinned.png",
            PlaybackIslandForm.RenderFrame(
                PreviewSize,
                SpeechPlaybackState.Speaking,
                false,
                false,
                false,
                false,
                0,
                ActiveNavigation,
                pinVisible: true,
                pinPinned: true));
        Console.WriteLine($"Saved playback island review frames to {Path.GetFullPath(outputDirectory)}.");

        void Save(string fileName, Bitmap bitmap)
        {
            using (bitmap)
            {
                bitmap.Save(Path.Combine(outputDirectory, fileName), ImageFormat.Png);
            }
        }
    }

    public static void SaveReadmeFrame(string outputPath)
    {
        const float scale = 1.5f;
        var size = new Size(
            (int)Math.Round(PreviewSize.Width * scale),
            (int)Math.Round(PreviewSize.Height * scale));
        using var fullFrame = PlaybackIslandForm.RenderFrame(
            size,
            SpeechPlaybackState.Speaking,
            closeVisible: false,
            closeHovered: false,
            playbackHovered: false,
            playbackPressed: false,
            preparingFrame: 0,
            navigationState: ActiveNavigation,
            scale: scale,
            pinVisible: false,
            pinPinned: false);
        using var bitmap = CropTransparentPadding(fullFrame, 2);
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        bitmap.Save(fullPath, ImageFormat.Png);
    }

    private static Bitmap CropTransparentPadding(Bitmap source, int padding)
    {
        var left = source.Width;
        var top = source.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).A == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
        {
            return new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        }

        var crop = Rectangle.FromLTRB(
            Math.Max(0, left - padding),
            Math.Max(0, top - padding),
            Math.Min(source.Width, right + padding + 1),
            Math.Min(source.Height, bottom + padding + 1));
        var result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImageUnscaled(source, -crop.Left, -crop.Top);
        return result;
    }

    private static Bitmap Render(SpeechPlaybackState state) =>
        PlaybackIslandForm.RenderFrame(
            PreviewSize,
            state,
            false,
            false,
            false,
            false,
            0,
            state == SpeechPlaybackState.Preparing
                ? PlaybackNavigationState.Unavailable
                : ActiveNavigation);

    private static string Hash(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static int VisiblePixelCount(Bitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A > 20)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static long AlphaTotal(Bitmap bitmap)
    {
        long total = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                total += bitmap.GetPixel(x, y).A;
            }
        }

        return total;
    }

    private static void AssertPlacementBounds()
    {
        var workingArea = new Rectangle(0, 0, 1920, 1040);
        var sourceWindow = new Rectangle(320, 120, 1080, 760);
        var anchored = PlaybackIslandForm.CalculateBounds(
            new PlaybackIslandPlacement(workingArea, sourceWindow),
            PreviewSize,
            bottomMargin: 22,
            edgeMargin: 8);
        Assert(
            anchored.X == sourceWindow.Left + (sourceWindow.Width - PreviewSize.Width) / 2
            && anchored.Y == sourceWindow.Bottom - PreviewSize.Height - 22,
            "The playback island is not anchored at the source window's bottom centre.");

        var partlyOffscreenSource = new Rectangle(-300, 100, 500, 900);
        var clamped = PlaybackIslandForm.CalculateBounds(
            new PlaybackIslandPlacement(workingArea, partlyOffscreenSource),
            PreviewSize,
            bottomMargin: 22,
            edgeMargin: 8);
        Assert(
            clamped.Left >= workingArea.Left + 8
            && clamped.Right <= workingArea.Right - 8
            && clamped.Bottom <= workingArea.Bottom - 22,
            "The playback island is not clamped inside the monitor working area.");

        var fallback = PlaybackIslandForm.CalculateBounds(
            new PlaybackIslandPlacement(workingArea, Rectangle.Empty),
            PreviewSize,
            bottomMargin: 22,
            edgeMargin: 8);
        Assert(
            fallback.X == workingArea.Left + (workingArea.Width - PreviewSize.Width) / 2
            && fallback.Y == workingArea.Bottom - PreviewSize.Height - 22,
            "The playback island does not fall back to the active monitor's bottom centre.");

        var pinned = PlaybackIslandForm.CalculatePinnedBounds(
            workingArea,
            PreviewSize,
            0.22f,
            0.76f,
            edgeMargin: 8);
        var persisted = PlaybackIslandForm.CreatePinnedState(
            pinned,
            workingArea,
            edgeMargin: 8);
        var restored = PlaybackIslandForm.CalculatePinnedBounds(
            workingArea,
            PreviewSize,
            persisted.NormalizedX,
            persisted.NormalizedY,
            edgeMargin: 8);
        Assert(
            persisted.IsPinned
            && Math.Abs(restored.X - pinned.X) <= 1
            && Math.Abs(restored.Y - pinned.Y) <= 1,
            "A pinned monitor-relative position did not round-trip.");

        var windowPinned = new Rectangle(510, 650, PreviewSize.Width, PreviewSize.Height);
        var windowPinState = PlaybackIslandForm.CreatePinnedState(
            windowPinned,
            workingArea,
            new PlaybackIslandPlacement(workingArea, sourceWindow),
            PlaybackIslandPinState.Default,
            edgeMargin: 8);
        var movedSourceWindow = new Rectangle(620, 180, 960, 680);
        var windowRestored = PlaybackIslandForm.CalculatePinnedBounds(
            new PlaybackIslandPlacement(workingArea, movedSourceWindow),
            PreviewSize,
            windowPinState,
            edgeMargin: 8);
        var expectedWindowRestored = PlaybackIslandForm.CalculatePinnedBounds(
            movedSourceWindow,
            PreviewSize,
            windowPinState.WindowNormalizedX,
            windowPinState.WindowNormalizedY,
            edgeMargin: 0);
        Assert(
            windowPinState.Anchor == PlaybackIslandPinAnchor.Window
            && windowRestored == expectedWindowRestored,
            "A position pinned inside the source window did not follow that window on a new read.");

        var monitorFallback = PlaybackIslandForm.CalculatePinnedBounds(
            new PlaybackIslandPlacement(workingArea, Rectangle.Empty),
            PreviewSize,
            windowPinState,
            edgeMargin: 8);
        var expectedMonitorFallback = PlaybackIslandForm.CalculatePinnedBounds(
            workingArea,
            PreviewSize,
            windowPinState.NormalizedX,
            windowPinState.NormalizedY,
            edgeMargin: 8);
        Assert(
            monitorFallback == expectedMonitorFallback,
            "A window-relative pin did not use its monitor fallback without a source window.");

        var outsideWindow = new Rectangle(1450, 820, PreviewSize.Width, PreviewSize.Height);
        var monitorPinState = PlaybackIslandForm.CreatePinnedState(
            outsideWindow,
            workingArea,
            new PlaybackIslandPlacement(workingArea, sourceWindow),
            windowPinState,
            edgeMargin: 8);
        Assert(
            monitorPinState.Anchor == PlaybackIslandPinAnchor.Monitor,
            "Dragging outside the source window did not switch the pin to monitor-relative placement.");

        var dragSize = new Size(8, 8);
        Assert(
            !PlaybackIslandForm.HasExceededDragThreshold(
                new Point(100, 100),
                new Point(
                    100 + PlaybackIslandForm.DragActivationMinimumPixels - 1,
                    100 + PlaybackIslandForm.DragActivationMinimumPixels - 1),
                dragSize)
            && PlaybackIslandForm.HasExceededDragThreshold(
                new Point(100, 100),
                new Point(100 + PlaybackIslandForm.DragActivationMinimumPixels, 100),
                dragSize),
            "The drag threshold does not distinguish a click from a deliberate move.");
    }

    private static void AssertRestartFromBeginningFiresWhileHeld()
    {
        using var form = new PlaybackIslandForm();
        var navigationStateField = typeof(PlaybackIslandForm).GetField(
            "_navigationState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Playback island navigation state was not found.");
        var mouseDown = typeof(PlaybackIslandForm).GetMethod(
            "OnMouseDown",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Playback island mouse-down handler was not found.");
        var mouseUp = typeof(PlaybackIslandForm).GetMethod(
            "OnMouseUp",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Playback island mouse-up handler was not found.");

        navigationStateField.SetValue(form, ActiveNavigation);
        var restartFromBeginningCount = 0;
        form.RestartFromBeginningRequested += (_, _) => restartFromBeginningCount++;
        var previousButtonPoint = PlaybackIslandForm.PreviewPreviousButtonCenter;
        mouseDown.Invoke(
            form,
            [new MouseEventArgs(MouseButtons.Left, 1, previousButtonPoint.X, previousButtonPoint.Y, 0)]);

        var deadline = DateTime.UtcNow.AddMilliseconds(
            PlaybackIslandForm.RestartFromBeginningHoldMilliseconds + 250);
        while (DateTime.UtcNow < deadline && restartFromBeginningCount == 0)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Assert(
            restartFromBeginningCount == 1,
            "Holding back did not restart from the beginning until the button was released.");
        mouseUp.Invoke(
            form,
            [new MouseEventArgs(MouseButtons.Left, 1, previousButtonPoint.X, previousButtonPoint.Y, 0)]);
        Assert(
            restartFromBeginningCount == 1,
            "Releasing back repeated the hold action.");
    }

    private static void AssertUnpinPreservesSessionPosition()
    {
        PlaybackIslandPinState? savedState = null;
        using var form = new PlaybackIslandForm(
            new PlaybackIslandPinState(true, 0.25f, 0.75f),
            state =>
            {
                savedState = state;
                return true;
            });
        var togglePin = typeof(PlaybackIslandForm).GetMethod(
            "TogglePinnedPosition",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Playback island pin toggle was not found.");
        var manualPosition = typeof(PlaybackIslandForm).GetField(
            "_manualPositionActive",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Playback island manual-position state was not found.");

        togglePin.Invoke(form, null);

        Assert(
            savedState is { IsPinned: false }
            && manualPosition.GetValue(form) is true,
            "Unpinning did not preserve the current position for the rest of the playback session.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
