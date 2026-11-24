using PulseReader.Configuration;
using PulseReader.Hotkeys;
using PulseReader.Playback;
using PulseReader.Speech;

internal static class ConfigurationChecks
{
    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "PulseReader.ConfigurationChecks",
            Guid.NewGuid().ToString("N"));

        try
        {
            var service = new ConfigurationService(testRoot);
            var defaultResult = service.LoadOrCreate();

            Assert(
                defaultResult.SpeechBackend == SpeechBackend.Kokoro
                && defaultResult.Hotkey == HotkeyDefinition.Default
                && defaultResult.Hotkey.DisplayName == "Ctrl + Shift + Space",
                "A new configuration did not use the default backend and hotkey.");

            var defaultJson = File.ReadAllText(service.ConfigPath);
            Assert(
                defaultJson.Contains("\"speechBackend\": \"kokoro\"", StringComparison.Ordinal),
                "The default backend was not written as the kokoro config value.");
            Assert(
                defaultJson.Contains("\"kokoroVoice\": \"af_heart\"", StringComparison.Ordinal)
                && defaultJson.Contains("\"kokoroVoiceBlendEnabled\": false", StringComparison.Ordinal)
                && defaultJson.Contains("\"kokoroSecondaryVoice\": \"af_sky\"", StringComparison.Ordinal)
                && defaultJson.Contains("\"kokoroPrimaryVoiceWeight\": 50", StringComparison.Ordinal)
                && defaultJson.Contains("\"speechSpeed\": 1", StringComparison.Ordinal)
                && defaultJson.Contains("\"speechVolume\": 100", StringComparison.Ordinal)
                && defaultJson.Contains("\"contextualPronunciationEnabled\": false", StringComparison.Ordinal)
                && defaultJson.Contains("\"windowsVoice\": null", StringComparison.Ordinal)
                && defaultJson.Contains("\"windowsSpeechRate\": 0", StringComparison.Ordinal)
                && defaultJson.Contains("\"windowsSpeechVolume\": 100", StringComparison.Ordinal)
                && defaultJson.Contains("\"showPlaybackControls\": true", StringComparison.Ordinal)
                && defaultJson.Contains("\"playbackControlsPinned\": false", StringComparison.Ordinal)
                && defaultJson.Contains("\"playbackControlsPinnedX\": 0.5", StringComparison.Ordinal)
                && defaultJson.Contains("\"playbackControlsPinnedY\": 0.9", StringComparison.Ordinal)
                && defaultJson.Contains("\"playbackControlsPinAnchor\": \"monitor\"", StringComparison.Ordinal)
                && defaultJson.Contains("\"playbackControlsWindowPinnedX\": 0.5", StringComparison.Ordinal)
                && defaultJson.Contains("\"playbackControlsWindowPinnedY\": 0.9", StringComparison.Ordinal)
                && defaultJson.Contains("\"modifiers\": [", StringComparison.Ordinal)
                && defaultJson.Contains("\"Ctrl\"", StringComparison.Ordinal)
                && defaultJson.Contains("\"Shift\"", StringComparison.Ordinal)
                && defaultJson.Contains("\"key\": \"Space\"", StringComparison.Ordinal),
                "The default speech settings were not written to configuration.");

            File.WriteAllText(
                service.ConfigPath,
                """
                {
                  "speechBackend": "windows",
                  "hotkey": {
                    "modifiers": ["Ctrl"],
                    "key": "F8"
                  }
                }
                """);

            var windowsResult = service.LoadOrCreate();
            Assert(
                windowsResult.SpeechBackend == SpeechBackend.Windows,
                "The windows backend config value was not loaded.");
            Assert(
                windowsResult.Hotkey.DisplayName == "Ctrl + F8",
                "The hotkey changed while loading the speech backend.");
            Assert(
                windowsResult.Configuration.KokoroVoice == KokoroSpeechSettings.DefaultVoice,
                "An older configuration did not inherit the default Kokoro voice.");
            Assert(
                windowsResult.Configuration.WindowsVoice is null
                && windowsResult.Configuration.WindowsSpeechRate == WindowsSpeechSettings.DefaultRate
                && windowsResult.Configuration.WindowsSpeechVolume == WindowsSpeechSettings.DefaultVolume
                && !windowsResult.Configuration.ContextualPronunciationEnabled
                && windowsResult.Configuration.ShowPlaybackControls
                && windowsResult.Configuration.PlaybackControlsPinAnchor
                   == PlaybackIslandPinAnchor.Monitor
                && Math.Abs(windowsResult.Configuration.PlaybackControlsWindowPinnedX - 0.5f) < 0.001f
                && Math.Abs(windowsResult.Configuration.PlaybackControlsWindowPinnedY - 0.9f) < 0.001f,
                "An older configuration did not inherit the new default settings.");

            var savedConfiguration = windowsResult.Configuration with
            {
                SpeechBackend = SpeechBackend.Kokoro,
                KokoroVoice = "bf_emma",
                KokoroVoiceBlendEnabled = true,
                KokoroSecondaryVoice = "bm_fable",
                KokoroPrimaryVoiceWeight = 70,
                SpeechSpeed = 1.12f,
                SpeechVolume = 72,
                ContextualPronunciationEnabled = true,
                WindowsVoice = "Microsoft Hazel Desktop",
                WindowsSpeechRate = 3,
                WindowsSpeechVolume = 68,
                ShowPlaybackControls = false,
                PlaybackControlsPinned = true,
                PlaybackControlsPinnedX = 0.18f,
                PlaybackControlsPinnedY = 0.72f,
                PlaybackControlsPinAnchor = PlaybackIslandPinAnchor.Window,
                PlaybackControlsWindowPinnedX = 0.36f,
                PlaybackControlsWindowPinnedY = 0.81f
            };
            Assert(
                service.TrySave(savedConfiguration, out var saveError) && saveError is null,
                "A valid configuration could not be saved.");

            var savedResult = service.LoadOrCreate();
            Assert(
                savedResult.Configuration.KokoroVoice == "bf_emma"
                && savedResult.Configuration.KokoroVoiceBlendEnabled
                && savedResult.Configuration.KokoroSecondaryVoice == "bm_fable"
                && savedResult.Configuration.KokoroPrimaryVoiceWeight == 70
                && Math.Abs(savedResult.Configuration.SpeechSpeed - 1.12f) < 0.001f
                && savedResult.Configuration.SpeechVolume == 72
                && savedResult.Configuration.ContextualPronunciationEnabled
                && savedResult.Configuration.WindowsVoice == "Microsoft Hazel Desktop"
                && savedResult.Configuration.WindowsSpeechRate == 3
                && savedResult.Configuration.WindowsSpeechVolume == 68
                && !savedResult.Configuration.ShowPlaybackControls
                && savedResult.Configuration.PlaybackControlsPinned
                && Math.Abs(savedResult.Configuration.PlaybackControlsPinnedX - 0.18f) < 0.001f
                && Math.Abs(savedResult.Configuration.PlaybackControlsPinnedY - 0.72f) < 0.001f
                && savedResult.Configuration.PlaybackControlsPinAnchor
                   == PlaybackIslandPinAnchor.Window
                && Math.Abs(savedResult.Configuration.PlaybackControlsWindowPinnedX - 0.36f) < 0.001f
                && Math.Abs(savedResult.Configuration.PlaybackControlsWindowPinnedY - 0.81f) < 0.001f,
                "Saved speech settings did not round-trip.");

            File.WriteAllText(
                service.ConfigPath,
                defaultJson.Replace("\"kokoro\"", "\"unknown\"", StringComparison.Ordinal));

            var invalidResult = service.LoadOrCreate();
            Assert(
                invalidResult.UsedDefaults
                && invalidResult.SpeechBackend == SpeechBackend.Kokoro
                && invalidResult.Warning is not null,
                "An invalid speech backend did not fall back to the default configuration.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }

        Assert(KokoroVoiceCatalog.Voices.Count == 54, "The Kokoro v1.0 voice catalog is incomplete.");
        Assert(
            KokoroVoiceCatalog.Languages.Count == 9,
            "The Kokoro voice catalog does not expose all supported language groups.");
        Assert(
            KokoroVoiceCatalog.Get(KokoroSpeechSettings.DefaultVoice).Name == "Heart",
            "af_heart is not the default catalog voice.");
        Assert(
            !KokoroSpeechSettings.IsValid("af_heart", 1f, 100, "missing_voice", 50)
            && !KokoroSpeechSettings.IsValid("af_heart", 1f, 100, "af_sky", 101),
            "Invalid secondary voice settings were accepted.");
        Assert(
            WindowsSpeechSettings.IsValid(null, 0, 100)
            && WindowsSpeechSettings.IsValid("Microsoft Hazel Desktop", -10, 0)
            && !WindowsSpeechSettings.IsValid("", 0, 100)
            && !WindowsSpeechSettings.IsValid(null, 11, 100)
            && !WindowsSpeechSettings.IsValid(null, 0, 101),
            "Windows speech setting bounds were not enforced.");
        Assert(
            PlaybackIslandPinState.IsValid(0f, 1f)
            && !PlaybackIslandPinState.IsValid(-0.01f, 0.5f)
            && !PlaybackIslandPinState.IsValid(0.5f, float.NaN),
            "Invalid pinned playback positions were accepted.");

        Console.WriteLine("PASS: settings configuration and all 54 Kokoro voices validate.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
