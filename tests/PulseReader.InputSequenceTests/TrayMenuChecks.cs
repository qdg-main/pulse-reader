using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using PulseReader.Settings;
using PulseReader.Tray;
using PulseReader.Updates;

internal static class TrayMenuChecks
{
    public static void Run()
    {
        CheckPlacement();
        CheckFormStateAndCommands();
        Console.WriteLine(
            "PASS: custom tray menu state, working-area placement, and command dispatch validate.");
    }

    public static void SavePreview(string outputPath)
    {
        using var preview = RenderPreview();
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        preview.Save(fullPath, ImageFormat.Png);
        Console.WriteLine($"Saved tray-menu preview to {fullPath}.");
    }

    private static void CheckPlacement()
    {
        var menuSize = TrayMenuForm.MenuSize;
        var bottomTaskbar = TrayMenuPlacement.Calculate(
            menuSize,
            new Point(1900, 1065),
            new Rectangle(0, 0, 1920, 1040));
        Assert(
            bottomTaskbar.Right <= 1912
            && bottomTaskbar.Bottom <= 1032
            && bottomTaskbar.Width == menuSize.Width
            && bottomTaskbar.Height == menuSize.Height,
            "The tray menu escaped the primary monitor working area.");

        var topTaskbar = TrayMenuPlacement.Calculate(
            menuSize,
            new Point(1800, 18),
            new Rectangle(0, 40, 1920, 1040));
        Assert(
            topTaskbar.Top == 48 && topTaskbar.Right <= 1912,
            "The tray menu did not move below a top-aligned taskbar.");

        var leftMonitor = TrayMenuPlacement.Calculate(
            menuSize,
            new Point(-1914, 900),
            new Rectangle(-1920, 0, 1920, 1040));
        Assert(
            leftMonitor.Left >= -1912
            && leftMonitor.Right <= -8
            && leftMonitor.Bottom <= 1032,
            "The tray menu did not clamp correctly on a negative-coordinate monitor.");

        var menuBounds = new Rectangle(100, 100, menuSize.Width, menuSize.Height);
        Assert(
            !TrayMenuForm.IsPointerBeyondDismissDistance(
                menuBounds,
                new Point(menuBounds.Right + TrayMenuForm.BasePointerDismissDistance - 1, 150),
                TrayMenuForm.BasePointerDismissDistance)
            && TrayMenuForm.IsPointerBeyondDismissDistance(
                menuBounds,
                new Point(menuBounds.Right + TrayMenuForm.BasePointerDismissDistance + 1, 150),
                TrayMenuForm.BasePointerDismissDistance),
            "The tray menu pointer-dismiss boundary is not applied at the expected distance.");
        Assert(
            TrayMenuForm.ShouldSuppressToggleAfterDeactivation(TimeSpan.FromMilliseconds(120))
            && !TrayMenuForm.ShouldSuppressToggleAfterDeactivation(TimeSpan.FromSeconds(1)),
            "The tray menu does not suppress only the tray click paired with a recent deactivation.");
    }

    private static void CheckFormStateAndCommands()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = new TrayMenuForm("Ctrl + Shift + Space");
                form.SetDismissOnDeactivateForTest(false);
                Assert(
                    !form.StopSpeakingEnabledForTest
                    && form.HotkeyTextForTest == "HOTKEY   Ctrl + Shift + Space"
                    && form.TopMost
                    && !form.ShowInTaskbar,
                    "The tray menu did not initialize with the expected state.");

                var settingsRequests = 0;
                var stopRequests = 0;
                var stopMonitoringRequests = 0;
                var exitRequests = 0;
                form.SettingsRequested += (_, _) => settingsRequests++;
                form.StopSpeakingRequested += (_, _) => stopRequests++;
                form.StopMonitoringRequested += (_, _) => stopMonitoringRequests++;
                form.ExitRequested += (_, _) => exitRequests++;

                form.ActivateForTest(TrayMenuCommand.StopSpeaking);
                Assert(stopRequests == 0, "The disabled Stop speaking row dispatched a command.");

                form.SetSpeakingState(true);
                form.ActivateForTest(TrayMenuCommand.StopSpeaking);
                form.ActivateForTest(TrayMenuCommand.Settings);
                form.ActivateForTest(TrayMenuCommand.Exit);
                Assert(
                    stopRequests == 1 && settingsRequests == 1 && exitRequests == 1,
                    "The tray menu did not dispatch each enabled command exactly once.");

                form.SetMonitoringState(true);
                form.SetSpeakingState(false);
                Assert(
                    form.StopSpeakingEnabledForTest
                    && form.StopSpeakingTextForTest == "Stop monitoring",
                    "The tray menu did not expose the persistent monitoring state.");
                form.ActivateForTest(TrayMenuCommand.StopSpeaking);
                Assert(
                    stopRequests == 1 && stopMonitoringRequests == 1,
                    "The monitoring tray action did not detach instead of stopping ordinary speech.");

                form.SetMonitoringState(false);
                form.SetSpeakingState(false);
                Assert(
                    !form.StopSpeakingEnabledForTest
                    && form.StopSpeakingTextForTest == "Stop speaking",
                    "The tray menu did not restore its ordinary idle state after detaching.");

                form.SetHotkey("Ctrl + Alt + F8");
                Assert(
                    form.HotkeyTextForTest == "HOTKEY   Ctrl + Alt + F8",
                    "The tray menu did not reflect a changed hotkey.");
                CheckUpdateStates(form);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("The custom tray-menu checks failed.", failure);
        }
    }

    private static void CheckUpdateStates(TrayMenuForm form)
    {
        var rows = form.Controls.OfType<TrayMenuItemControl>().OrderBy(row => row.Top).ToArray();
        Assert(rows.Length == 4 && form.ClientSize == TrayMenuForm.MenuSize,
            "The update menu did not add exactly one compact row.");
        for (var index = 0; index < rows.Length; index++)
        {
            Assert(form.ClientRectangle.Contains(rows[index].Bounds)
                && (index == 0 || rows[index].Top >= rows[index - 1].Bottom),
                "A tray command overlaps another row or escapes the menu.");
        }

        var update = rows[2];
        var requests = 0;
        form.UpdateRequested += (_, _) => requests++;
        foreach (var (mode, text, enabled) in new[]
        {
            (UpdateMenuMode.Unsupported, "Updates unavailable", false),
            (UpdateMenuMode.Idle, "Check for updates", true),
            (UpdateMenuMode.Checking, "Checking for updates…", false),
            (UpdateMenuMode.Available, "Update to 1.2.3", true),
            (UpdateMenuMode.Downloading, "Cancel download", true),
            (UpdateMenuMode.Cancelling, "Cancelling download…", false),
            (UpdateMenuMode.Applying, "Applying update…", false)
        })
        {
            form.SetUpdateState(mode, "1.2.3", 42);
            Assert(update.Text == text && update.AccessibleName == text && update.Enabled == enabled,
                $"The update row presented the wrong state for {mode}.");
            var before = requests;
            form.ActivateForTest(TrayMenuCommand.Update);
            Assert(requests == before + (enabled ? 1 : 0),
                $"The update row dispatched incorrectly for {mode}.");
        }
        form.SetUpdateState(UpdateMenuMode.Downloading, downloadPercent: 140);
        Assert(update.AccessibleDescription?.Contains("100%", StringComparison.Ordinal) == true,
            "Download progress was not clamped and exposed accessibly.");
        form.SetUpdateState(UpdateMenuMode.Idle);
        Assert(update.AccessibleDescription == "Check for updates",
            "The update row retained stale download progress.");

        form.StartPosition = FormStartPosition.Manual;
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        form.Location = new Point(workingArea.Left - form.Width - 100, workingArea.Top);
        form.Show();
        Application.DoEvents();
        form.SetSpeakingState(false);
        rows[1].Select();
        form.ProcessKeyForTest(Keys.Down);
        Assert(update.Focused, "Keyboard navigation skipped the enabled update row.");
        var beforeKeyboard = requests;
        typeof(TrayMenuItemControl).GetMethod("OnKeyDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(update, [new KeyEventArgs(Keys.Enter)]);
        Assert(requests == beforeKeyboard + 1 && !form.Visible,
            "The keyboard update action did not dispatch once and close the menu.");

        form.Show();
        form.SetUpdateState(UpdateMenuMode.Checking);
        rows[1].Select();
        form.ProcessKeyForTest(Keys.Down);
        Assert(rows[3].Focused, "Keyboard navigation did not skip a disabled update row.");
        form.ProcessKeyForTest(Keys.Up);
        Assert(rows[1].Focused, "Reverse navigation did not skip the disabled update row.");
        form.SetUpdateState(UpdateMenuMode.Downloading, downloadPercent: 42);
        update.Select();
        var beforeCancel = requests;
        typeof(TrayMenuItemControl).GetMethod("OnKeyDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(update, [new KeyEventArgs(Keys.Space)]);
        Assert(requests == beforeCancel + 1,
            "The cancel-download keyboard action did not use the update command.");
        form.SetUpdateState(UpdateMenuMode.Cancelling);
        typeof(TrayMenuItemControl).GetMethod("OnKeyDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(update, [new KeyEventArgs(Keys.Enter)]);
        Assert(requests == beforeCancel + 1,
            "A disabled update row dispatched from the keyboard.");
    }

    private static Bitmap RenderPreview()
    {
        Bitmap? preview = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = new TrayMenuForm("Ctrl + Shift + Space");
                form.SetDismissOnDeactivateForTest(false);
                form.SetSpeakingState(true);
                form.SetUpdateState(UpdateMenuMode.Idle);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(
                    SystemInformation.VirtualScreen.Left - form.Width - 32,
                    SystemInformation.VirtualScreen.Top + 32);
                form.Show();
                Application.DoEvents();
                form.PerformLayout();

                using var content = new Bitmap(
                    form.ClientSize.Width,
                    form.ClientSize.Height,
                    PixelFormat.Format32bppPArgb);
                form.DrawToBitmap(content, new Rectangle(Point.Empty, form.ClientSize));

                preview = SettingsWindowChrome.RenderChrome(form.ClientSize);
                using var graphics = Graphics.FromImage(preview);
                using var contentPath = PulseReaderTheme.RoundedRectangle(
                    new RectangleF(
                        SettingsWindowChrome.ContentInset,
                        SettingsWindowChrome.ContentInset,
                        form.ClientSize.Width - SettingsWindowChrome.ContentInset * 2,
                        form.ClientSize.Height - SettingsWindowChrome.ContentInset * 2),
                    SettingsWindowChrome.CornerRadius - SettingsWindowChrome.ContentInset);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.SetClip(contentPath);
                graphics.DrawImageUnscaled(content, Point.Empty);
            }
            catch (Exception exception)
            {
                preview?.Dispose();
                preview = null;
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null || preview is null)
        {
            throw new InvalidOperationException(
                "The tray-menu preview could not be rendered.",
                failure);
        }

        return preview;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
