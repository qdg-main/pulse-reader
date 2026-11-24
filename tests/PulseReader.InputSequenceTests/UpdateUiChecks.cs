using System.Drawing;
using System.Windows.Forms;
using PulseReader;
using PulseReader.Configuration;
using PulseReader.Hotkeys;
using PulseReader.Settings;
using PulseReader.Speech;

internal static class UpdateUiChecks
{
    public static void Run()
    {
        CheckConfigurationPersistence();
        RunSta(CheckSettings);
        RunSta(CheckBadge);
        Console.WriteLine("PASS: automatic update defaults, persistence, settings rollback, scrolling, and tray badges validate.");
    }

    private static void CheckConfigurationPersistence()
    {
        var testRoot = Path.Combine(AppContext.BaseDirectory, "UpdateUiChecks", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ConfigurationService(testRoot);
            Assert(service.LoadOrCreate().Configuration.CheckForUpdatesAutomatically,
                "A new configuration did not enable automatic update checks.");
            var optedOut = AppConfiguration.Default with { CheckForUpdatesAutomatically = false };
            Assert(service.TrySave(optedOut, out var saveError),
                $"The automatic update opt-out could not be saved: {saveError}");
            var loaded = service.LoadOrCreate();
            Assert(!loaded.Configuration.CheckForUpdatesAutomatically,
                $"The automatic update opt-out did not persist. Defaults={loaded.UsedDefaults}, warning={loaded.Warning}");
            var copied = optedOut with { SpeechVolume = 75 };
            Assert(!copied.CheckForUpdatesAutomatically,
                "An unrelated configuration change discarded the automatic update opt-out.");
            File.WriteAllText(service.ConfigPath, "{}");
            Assert(service.LoadOrCreate().Configuration.CheckForUpdatesAutomatically,
                "An older configuration did not inherit the automatic update default.");
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
    }

    private static void CheckSettings()
    {
        Application.EnableVisualStyles();
        var saves = 0;
        var failSave = false;
        AppConfiguration? saved = null;
        using var form = CreateSettings(AppConfiguration.Default, next =>
        {
            saves++;
            saved = next;
            return failSave ? "Update preference test failure." : null;
        });
        var viewport = form.Controls.OfType<SettingsScrollViewport>().Single();
        var updatePanel = viewport.ContentControls.OfType<ThemePanel>().Single(panel =>
            panel.Controls.OfType<GlowCheckBox>().Any(box => box.Text == "Check for updates automatically"));
        var updates = updatePanel.Controls.OfType<GlowCheckBox>().Single();
        Assert(updates.Checked && saves == 0,
            "Settings initialization saved or lost the automatic update default.");
        updates.Checked = false;
        Assert(saves == 1 && saved is { CheckForUpdatesAutomatically: false } && !updates.Checked,
            "The automatic update checkbox did not save the opt-out through its callback.");
        failSave = true;
        updates.Checked = true;
        var status = viewport.ContentControls.OfType<Label>().Single();
        Assert(saves == 2 && !updates.Checked && status.Text == "Update preference test failure."
            && status.ForeColor == PulseReaderTheme.Error,
            "A failed update preference save did not roll back once and display the error.");
        failSave = false;
        updates.Checked = true;
        Assert(saves == 3 && saved is { CheckForUpdatesAutomatically: true } && updates.Checked,
            "The update preference did not recover after a failed save.");

        form.StartPosition = FormStartPosition.Manual;
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        form.Location = new Point(workingArea.Left - form.Width - 100, workingArea.Top);
        form.Show();
        form.ApplyWorkingAreaForTest(new Rectangle(0, 0, 1366, 600));
        Application.DoEvents();
        var panels = viewport.ContentControls.OfType<ThemePanel>().OrderBy(panel => panel.Top).ToArray();
        Assert(panels.Length == 6 && updatePanel.Top == panels[^2].Bottom + 10
            && status.Top == updatePanel.Bottom + 8 && viewport.ContentHeight >= status.Bottom,
            "The update section overlaps the existing settings or escapes the scroll content.");
        Assert(viewport.ScrollBarVisible, "The constrained settings window did not offer scrolling.");
        updates.Focus();
        Application.DoEvents();
        Assert(updates.Focused && viewport.IsControlFullyVisible(updates),
            "Keyboard focus did not scroll the update preference into view.");
        var voicePanel = panels[0];
        voicePanel.Controls.OfType<BlendSectionHeader>().Single().Expanded = true;
        form.ApplyWorkingAreaForTest(new Rectangle(0, 0, 1366, 600));
        viewport.EnsureVisible(updates);
        Assert(updatePanel.Top == panels[^2].Bottom + 10 && viewport.IsControlFullyVisible(updates),
            "Expanding voice settings made the update preference unreachable.");
        form.Hide();

        using var optedOut = CreateSettings(AppConfiguration.Default with { CheckForUpdatesAutomatically = false },
            _ => throw new InvalidOperationException("Initializing settings must not save configuration."));
        var optedOutViewport = optedOut.Controls.OfType<SettingsScrollViewport>().Single();
        Assert(!optedOutViewport.ContentControls.OfType<ThemePanel>()
            .SelectMany(panel => panel.Controls.OfType<GlowCheckBox>())
            .Single(box => box.Text == "Check for updates automatically").Checked,
            "Reopened settings did not display the stored opt-out.");
    }

    private static SettingsForm CreateSettings(AppConfiguration configuration,
        Func<AppConfiguration, string?> apply) => new(configuration, HotkeyDefinition.Default,
        apply, _ => null, _ => Task.FromResult(SpeechStartResult.Success()), () => { }, () => null, () => null);

    private static void CheckBadge()
    {
        foreach (var size in new[] { 16, 20, 24, 32, 48 })
        {
            for (var index = -1; index < TrayIconFrameRenderer.SpeakingFrameCount; index++)
            {
                using var source = index < 0 ? TrayIconFrameRenderer.LoadIdleSource()
                    : TrayIconFrameRenderer.LoadSpeakingSource(index);
                using var plain = TrayIconAnimator.RenderFrame(source, size, false);
                using var badged = TrayIconAnimator.RenderFrame(source, size, true);
                var changed = 0;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    if (plain.GetPixel(x, y) == badged.GetPixel(x, y)) continue;
                    changed++;
                    Assert(x >= size * 0.55f && y >= size * 0.55f,
                        "The update badge changed artwork outside its lower-right corner.");
                }
                Assert(changed > 0 && badged.GetPixel(0, 0).A == 0,
                    "The update badge was missing or removed tray transparency.");
            }
        }

        using var notification = new NotifyIcon { Visible = false };
        var animator = new TrayIconAnimator(notification);
        var plainIdle = notification.Icon;
        animator.SetUpdateAvailable(true);
        var updateIdle = notification.Icon;
        Assert(!ReferenceEquals(plainIdle, updateIdle), "The idle update badge did not reach the notification icon.");
        animator.SetMonitoringState(true);
        var updateMonitoring = notification.Icon;
        animator.SetPlaybackState(SpeechPlaybackState.Speaking);
        Assert(ReferenceEquals(updateMonitoring, notification.Icon),
            "Monitoring and speaking did not use the same badged first frame.");
        animator.SetUpdateAvailable(false);
        Assert(!ReferenceEquals(updateMonitoring, notification.Icon),
            "Clearing the update badge did not restore the speaking artwork.");
        var plainSpeaking = notification.Icon;
        var tick = typeof(TrayIconAnimator).GetMethod("OnAnimationTick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        tick.Invoke(animator, [null, EventArgs.Empty]);
        var nextPlainFrame = notification.Icon;
        Assert(!ReferenceEquals(plainSpeaking, nextPlainFrame),
            "The speaking animation did not advance.");
        animator.SetUpdateAvailable(true);
        tick.Invoke(animator, [null, EventArgs.Empty]);
        animator.SetUpdateAvailable(false);
        tick.Invoke(animator, [null, EventArgs.Empty]);
        Assert(ReferenceEquals(nextPlainFrame, notification.Icon),
            "Changing the update badge reset the speaking animation sequence.");
        tick.Invoke(animator, [null, EventArgs.Empty]);
        Assert(ReferenceEquals(plainSpeaking, notification.Icon),
            "The speaking animation did not retain its existing four-step cycle.");
        animator.SetPlaybackState(SpeechPlaybackState.Paused);
        animator.SetUpdateAvailable(true);
        Assert(ReferenceEquals(updateMonitoring, notification.Icon),
            "Paused playback did not retain the update badge.");
        animator.SetUpdateAvailable(false);
        animator.SetPlaybackState(SpeechPlaybackState.Idle);
        animator.SetMonitoringState(false);
        Assert(ReferenceEquals(plainIdle, notification.Icon),
            "Ending playback and monitoring did not restore the original idle icon.");
        animator.Dispose();
        animator.Dispose();
        animator.SetUpdateAvailable(true);
        notification.Icon = null;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("The update UI checks failed.", failure);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
