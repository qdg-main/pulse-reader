using PulseReader.Configuration;
using PulseReader.Hotkeys;
using PulseReader.Settings;
using PulseReader.Setup;
using PulseReader.Speech;

internal static class OptionalModelStartupChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseReader.OptionalModelStartupChecks",
            Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ConfigurationService(root);
            var enabled = AppConfiguration.Default with
            {
                ContextualPronunciationEnabled = true,
                Hotkey = new HotkeyConfiguration { Modifiers = ["Ctrl", "Alt"], Key = "F8" },
                SpeechSpeed = 1.1f,
                SpeechVolume = 61,
                ShowPlaybackControls = false,
                PlaybackControlsPinned = true,
                PlaybackControlsPinnedX = 0.23f
            };
            Assert(service.TrySave(enabled, out _), "Could not save the isolated startup fixture.");
            var loaded = service.LoadOrCreate();
            var result = OptionalModelStartupPolicy.Apply(loaded.Configuration,
                () => new ContextualPronunciationModelStore(roots: [Path.Combine(root, "missing")]).GetAvailability(),
                value => service.TrySave(value, out var error) ? null : error);
            Assert(!result.Configuration.ContextualPronunciationEnabled
                && !service.LoadOrCreate().Configuration.ContextualPronunciationEnabled,
                "An enabled setting with a missing optional installation stayed enabled at startup.");
            Assert(result.Configuration == loaded.Configuration with { ContextualPronunciationEnabled = false },
                "The missing optional-model reset changed other settings.");
            var reloaded = service.LoadOrCreate();
            Assert(reloaded.Hotkey == loaded.Hotkey
                && reloaded.Configuration.SpeechSpeed == loaded.Configuration.SpeechSpeed
                && reloaded.Configuration.SpeechVolume == loaded.Configuration.SpeechVolume
                && reloaded.Configuration.ShowPlaybackControls == loaded.Configuration.ShowPlaybackControls
                && reloaded.Configuration.PlaybackControlsPinned == loaded.Configuration.PlaybackControlsPinned
                && reloaded.Configuration.PlaybackControlsPinnedX == loaded.Configuration.PlaybackControlsPinnedX,
                "The persisted optional-model reset lost other saved settings or the hotkey.");
            Assert(!result.RuntimeContextualPronunciationEnabled && result.Warning is not null,
                "Startup did not disable the missing optional model and explain Settings re-enablement.");
            Assert(result.Warning!.Contains("Settings", StringComparison.Ordinal)
                && result.Warning.Contains("download", StringComparison.OrdinalIgnoreCase),
                "The reset warning did not explain how to download and re-enable in Settings.");
            CheckSettings(result, loaded.Hotkey, expectedChecked: false);

            var saveCalls = 0;
            var repeat = OptionalModelStartupPolicy.Apply(service.LoadOrCreate().Configuration,
                () => throw new InvalidOperationException("A disabled feature inspected its installation."),
                _ => { saveCalls++; return null; });
            Assert(repeat.Warning is null && saveCalls == 0,
                "A successfully reset setting warned or saved again on the next startup.");

            Assert(service.TrySave(enabled, out _), "Could not save the persistence-failure fixture.");
            AppConfiguration? attemptedSave = null;
            var beforeFailure = service.LoadOrCreate().Configuration;
            OptionalModelStartupResult failed;
            using (var lockedConfig = new FileStream(service.ConfigPath, FileMode.Open,
                       FileAccess.Read, FileShare.None))
            {
                failed = OptionalModelStartupPolicy.Apply(beforeFailure,
                    () => new ContextualPronunciationModelStore(roots: [Path.Combine(root, "missing")]).GetAvailability(),
                    value =>
                    {
                        attemptedSave = value;
                        return service.TrySave(value, out var error) ? null : error;
                    });
            }
            Assert(attemptedSave is { ContextualPronunciationEnabled: false }
                && failed.Configuration.ContextualPronunciationEnabled
                && service.LoadOrCreate().Configuration.ContextualPronunciationEnabled,
                "A failed save pretended the optional setting had been persisted off.");
            Assert(!failed.RuntimeContextualPronunciationEnabled
                && failed.Warning is not null
                && failed.Warning.Contains("could not", StringComparison.OrdinalIgnoreCase)
                && failed.Warning.Contains("Settings", StringComparison.Ordinal),
                "A failed startup reset did not explain its temporary runtime state and retry action.");
            CheckSettings(failed, loaded.Hotkey, expectedChecked: true);
            var retried = OptionalModelStartupPolicy.Apply(service.LoadOrCreate().Configuration,
                () => new ContextualPronunciationModelStore(roots: [Path.Combine(root, "missing")]).GetAvailability(),
                value => service.TrySave(value, out var error) ? null : error);
            Assert(!retried.Configuration.ContextualPronunciationEnabled
                && !service.LoadOrCreate().Configuration.ContextualPronunciationEnabled,
                "The startup reset did not retry after a temporary settings-save failure cleared.");

            CheckPreservedStates(enabled, root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine("PASS: optional startup resets missing/invalid installations, preserves unavailable or disabled intent, and never downloads eagerly.");
    }

    private static void CheckPreservedStates(AppConfiguration enabled, string root)
    {
        foreach (var state in new[] { ContextualPronunciationInstallationState.Installed,
                     ContextualPronunciationInstallationState.Unavailable })
        {
            var result = OptionalModelStartupPolicy.Apply(enabled,
                () => new ContextualPronunciationAvailability(
                    state == ContextualPronunciationInstallationState.Installed, root, "fixture") { State = state },
                _ => throw new InvalidOperationException("Startup reset an installed or temporarily unavailable installation."));
            Assert(result.Configuration == enabled && result.RuntimeContextualPronunciationEnabled
                && result.Warning is null,
                "Startup changed optional intent for an installed or temporarily unavailable model.");
        }

        var installedFiles = Path.Combine(root, "installed-files");
        Directory.CreateDirectory(installedFiles);
        var sentinel = Path.Combine(installedFiles, "model-fixture.gguf");
        File.WriteAllText(sentinel, "keep this optional installation");
        var disabled = enabled with { ContextualPronunciationEnabled = false };
        var disabledResult = OptionalModelStartupPolicy.Apply(disabled,
            () => throw new InvalidOperationException("A disabled feature inspected its installation."),
            _ => throw new InvalidOperationException("A disabled feature was saved unnecessarily."));
        Assert(disabledResult.Configuration == disabled && !disabledResult.RuntimeContextualPronunciationEnabled
            && disabledResult.Warning is null && File.ReadAllText(sentinel) == "keep this optional installation",
            "Disabling the optional feature changed or deleted its installed files.");

        var invalid = Path.Combine(root, "invalid");
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, ContextualPronunciationModelStore.InstallationManifestFileName),
            "not the pinned manifest");
        var resetInvalid = OptionalModelStartupPolicy.Apply(enabled,
            () => new ContextualPronunciationModelStore(roots: [invalid]).GetAvailability(), _ => null);
        Assert(!resetInvalid.Configuration.ContextualPronunciationEnabled
            && File.Exists(Path.Combine(invalid, ContextualPronunciationModelStore.InstallationManifestFileName)),
            "A broken optional manifest did not reset the setting without deleting model files.");
    }

    private static void CheckSettings(OptionalModelStartupResult result, HotkeyDefinition hotkey,
        bool expectedChecked)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var downloads = 0;
                using var form = new SettingsForm(result.Configuration, hotkey,
                    _ => null, _ => null, _ => Task.FromResult(SpeechStartResult.Success()),
                    () => { }, () => null, () => null,
                    contextualPronunciationAvailability: new(false, "fixture", null),
                    installContextualPronunciation: () => { downloads++; return Task.FromResult(false); });
                var checkbox = form.Controls.OfType<SettingsScrollViewport>().Single().ContentControls
                    .OfType<ThemePanel>().SelectMany(panel => panel.Controls.OfType<GlowCheckBox>())
                    .Single(box => box.Text == "Use local model for ambiguous words");
                Assert(checkbox.Checked == expectedChecked,
                    "The Settings checkbox misrepresented the saved optional setting.");
                Assert(downloads == 0, "Constructing Settings downloaded the optional model at startup.");
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(10)), "The optional startup Settings fixture did not finish.");
        if (failure is not null) throw new InvalidOperationException("Startup Settings fixture failed.", failure);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
