using System.Text.Json;
using System.Text.Json.Serialization;
using PulseReader.Hotkeys;
using PulseReader.Playback;
using PulseReader.Speech;

namespace PulseReader.Configuration;

internal sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };

    public ConfigurationService(string? localApplicationDataPath = null)
    {
        var localAppData = localApplicationDataPath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        ConfigPath = Path.Combine(localAppData, "PulseReader", "config.json");
    }

    public string ConfigPath { get; }

    public ConfigurationLoadResult LoadOrCreate()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var configuration = JsonSerializer.Deserialize<AppConfiguration>(json, SerializerOptions);

                if (configuration is not null
                    && HotkeyDefinition.TryCreate(configuration.Hotkey, out var configuredHotkey)
                    && KokoroSpeechSettings.IsValid(
                        configuration.KokoroVoice,
                        configuration.SpeechSpeed,
                        configuration.SpeechVolume,
                        configuration.KokoroSecondaryVoice,
                        configuration.KokoroPrimaryVoiceWeight)
                    && WindowsSpeechSettings.IsValid(
                        configuration.WindowsVoice,
                        configuration.WindowsSpeechRate,
                        configuration.WindowsSpeechVolume)
                    && new PlaybackIslandPinState(
                        configuration.PlaybackControlsPinned,
                        configuration.PlaybackControlsPinnedX,
                        configuration.PlaybackControlsPinnedY,
                        configuration.PlaybackControlsPinAnchor,
                        configuration.PlaybackControlsWindowPinnedX,
                        configuration.PlaybackControlsWindowPinnedY).IsValid())
                {
                    return new ConfigurationLoadResult(
                        configuration,
                        configuredHotkey,
                        false,
                        null);
                }
            }

            var defaultHotkey = HotkeyDefinition.Default;
            TrySaveDefaults();
            return new ConfigurationLoadResult(
                AppConfiguration.Default,
                defaultHotkey,
                true,
                null);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            TrySaveDefaults();
            return new ConfigurationLoadResult(
                AppConfiguration.Default,
                HotkeyDefinition.Default,
                true,
                "Pulse Reader could not read its configuration and is using Kokoro with Ctrl + Shift + Space.");
        }
    }

    public bool TrySave(AppConfiguration configuration, out string? error)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var temporaryPath = ConfigPath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(configuration, SerializerOptions) + Environment.NewLine);
            File.Move(temporaryPath, ConfigPath, true);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // The next successful save overwrites the temporary file.
            }

            error = "Pulse Reader could not save its settings.";
            return false;
        }
    }

    private void TrySaveDefaults()
    {
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                ConfigPath,
                JsonSerializer.Serialize(AppConfiguration.Default, SerializerOptions) + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The utility can still run with in-memory defaults when this location is unavailable.
        }
    }
}

internal sealed record ConfigurationLoadResult(
    AppConfiguration Configuration,
    HotkeyDefinition Hotkey,
    bool UsedDefaults,
    string? Warning)
{
    public SpeechBackend SpeechBackend => Configuration.SpeechBackend;
}
