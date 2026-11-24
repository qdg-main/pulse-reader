using PulseReader.Configuration;
using PulseReader.Hotkeys;
using PulseReader.Settings;
using PulseReader.Speech;

internal static class ModelSetupFlowChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckDialogs(); CheckOptionalSetting(); CheckCanceledBackendChange(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            throw new InvalidOperationException("Model setup UI checks did not finish.");
        if (failure is not null) throw new InvalidOperationException("Model setup UI check failed.", failure);
        Console.WriteLine("PASS: model setup dialogs support success, cancellation and retry; the optional setting saves only after successful setup.");
    }

    private static void CheckDialogs()
    {
        var attempts = 0;
        using (var form = new ModelSetupForm(true, async (progress, cancellation) =>
        {
            await Task.Yield();
            if (++attempts == 1) throw new IOException("Fixture network failure.");
            progress.Report(new("Fixture", 10, 10, "Installed"));
        }))
        {
            PutOffscreen(form);
            using var poll = new System.Windows.Forms.Timer { Interval = 25 };
            poll.Tick += (_, _) =>
            {
                var button = form.Controls.OfType<GlowButton>().FirstOrDefault(b => b.Text == "Retry" && b.Visible);
                button?.PerformClick();
            };
            poll.Start();
            if (form.ShowDialog() != DialogResult.OK || attempts != 2)
                throw new InvalidOperationException("Retry did not complete the failed setup.");
        }
        var canceled = false;
        using (var form = new ModelSetupForm(false, async (_, cancellation) =>
        {
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            catch (OperationCanceledException) { canceled = true; throw; }
        }))
        using (var close = new System.Windows.Forms.Timer { Interval = 50 })
        {
            PutOffscreen(form);
            close.Tick += (_, _) => { close.Stop(); form.Close(); };
            close.Start();
            if (form.ShowDialog() != DialogResult.Cancel || !canceled)
                throw new InvalidOperationException("Closing setup did not cancel its active download.");
        }
    }

    private static void CheckOptionalSetting()
    {
        foreach (var succeeds in new[] { false, true })
        {
            var installed = false;
            var downloads = 0;
            AppConfiguration? saved = null;
            using var form = new SettingsForm(AppConfiguration.Default, HotkeyDefinition.Default,
                value => { saved = value; return null; }, _ => null,
                _ => Task.FromResult(SpeechStartResult.Success()), () => { }, () => null, () => null,
                contextualPronunciationAvailability: new(false, "fixture", null),
                installContextualPronunciation: () =>
                {
                    downloads++;
                    installed = succeeds;
                    return Task.FromResult(succeeds);
                },
                getContextualPronunciationAvailability: () => new(installed, "fixture", null));
            var viewport = form.Controls.OfType<SettingsScrollViewport>().Single();
            var check = viewport.ContentControls.OfType<ThemePanel>()
                .SelectMany(panel => panel.Controls.OfType<GlowCheckBox>())
                .Single(box => box.Text == "Use local model for ambiguous words");
            if (!check.Enabled || downloads != 0) throw new InvalidOperationException("Optional download was eager or unavailable.");
            check.Checked = true;
            if (downloads != 1 || check.Checked != succeeds || (saved?.ContextualPronunciationEnabled == true) != succeeds)
                throw new InvalidOperationException("The optional checkbox did not preserve setup success/failure.");
        }
    }

    private static void CheckCanceledBackendChange()
    {
        var attempts = 0;
        using var form = new SettingsForm(
            AppConfiguration.Default with { SpeechBackend = SpeechBackend.Windows },
            HotkeyDefinition.Default,
            _ => { attempts++; return "Fixture: Kokoro setup canceled."; }, _ => null,
            _ => Task.FromResult(SpeechStartResult.Success()), () => { }, () => null, () => null);
        var viewport = form.Controls.OfType<SettingsScrollViewport>().Single();
        var backend = viewport.ContentControls.OfType<ThemePanel>()
            .SelectMany(panel => panel.Controls.OfType<ThemedComboBox>())
            .Single(box => box.Items.Contains(SpeechBackend.Kokoro));
        PutOffscreen(form);
        form.Show();
        backend.OpenDropDown();
        backend.CommitSelection(0);
        if (attempts != 1 || backend.SelectedItem is not SpeechBackend.Windows)
            throw new InvalidOperationException("A canceled Kokoro switch left settings displaying an inactive backend.");
    }

    private static void PutOffscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-10000, -10000);
        form.ShowInTaskbar = false;
    }
}
