using PulseReader.Playback;
using PulseReader.Speech;

namespace PulseReader.Configuration;

internal sealed record AppConfiguration
{
    public SpeechBackend SpeechBackend { get; init; } = SpeechBackend.Kokoro;

    public string KokoroVoice { get; init; } = KokoroSpeechSettings.DefaultVoice;

    public bool KokoroVoiceBlendEnabled { get; init; }

    public string KokoroSecondaryVoice { get; init; } = KokoroSpeechSettings.DefaultSecondaryVoice;

    public int KokoroPrimaryVoiceWeight { get; init; } = KokoroSpeechSettings.DefaultPrimaryVoiceWeight;

    public float SpeechSpeed { get; init; } = KokoroSpeechSettings.DefaultSpeed;

    public int SpeechVolume { get; init; } = KokoroSpeechSettings.DefaultVolume;

    public bool ContextualPronunciationEnabled { get; init; }

    public string? WindowsVoice { get; init; }

    public int WindowsSpeechRate { get; init; } = WindowsSpeechSettings.DefaultRate;

    public int WindowsSpeechVolume { get; init; } = WindowsSpeechSettings.DefaultVolume;

    public bool ShowPlaybackControls { get; init; } = true;

    public bool CheckForUpdatesAutomatically { get; init; } = true;

    public bool PlaybackControlsPinned { get; init; }

    public float PlaybackControlsPinnedX { get; init; } = 0.5f;

    public float PlaybackControlsPinnedY { get; init; } = 0.9f;

    public PlaybackIslandPinAnchor PlaybackControlsPinAnchor { get; init; }
        = PlaybackIslandPinAnchor.Monitor;

    public float PlaybackControlsWindowPinnedX { get; init; } = 0.5f;

    public float PlaybackControlsWindowPinnedY { get; init; } = 0.9f;

    public HotkeyConfiguration Hotkey { get; init; } = HotkeyConfiguration.Default;

    public static AppConfiguration Default { get; } = new();
}

internal enum SpeechBackend
{
    Kokoro,
    Windows
}

internal sealed record HotkeyConfiguration
{
    public IReadOnlyList<string> Modifiers { get; init; } = ["Ctrl", "Shift"];

    public string Key { get; init; } = "Space";

    public static HotkeyConfiguration Default { get; } = new();
}
