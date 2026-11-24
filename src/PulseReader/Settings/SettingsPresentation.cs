using PulseReader.Configuration;
using PulseReader.Speech;

namespace PulseReader.Settings;

internal static class SettingsPresentation
{
    internal const string WindowsPreviewText =
        "Hello, this is the Windows speech voice in Pulse Reader. Select some text and press your shortcut to hear it read aloud.";

    public static string PreviewText(WindowsVoiceDescriptor voice) =>
        $"Hello, this is {voice.Name}. Pulse Reader is using the Windows speech engine.";

    public static string PlayingStatus(WindowsVoiceDescriptor voice) =>
        $"Playing {voice.Name}.";

    public static string PreviewButtonText(SpeechBackend _, bool isPreviewing) =>
        isPreviewing
            ? "■  Stop voice"
            : "▶  Preview voice";

    public static string PreviewText(
        SpeechBackend backend,
        KokoroVoiceDescriptor voice,
        bool blendEnabled = false,
        KokoroVoiceDescriptor? secondaryVoice = null,
        int primaryVoiceWeight = KokoroSpeechSettings.DefaultPrimaryVoiceWeight)
    {
        if (backend == SpeechBackend.Windows)
        {
            return WindowsPreviewText;
        }

        return blendEnabled && secondaryVoice is not null
            ? $"Hello, this is {voice.Name} and {secondaryVoice.Name} blended together in Pulse Reader, "
                + $"with {primaryVoiceWeight} percent {voice.Name} and "
                + $"{100 - primaryVoiceWeight} percent {secondaryVoice.Name}."
            : voice.SampleText;
    }

    public static string PlayingStatus(
        SpeechBackend backend,
        KokoroVoiceDescriptor voice,
        bool blendEnabled = false,
        KokoroVoiceDescriptor? secondaryVoice = null,
        int primaryVoiceWeight = KokoroSpeechSettings.DefaultPrimaryVoiceWeight)
    {
        if (backend == SpeechBackend.Windows)
        {
            return "Playing the Windows speech voice.";
        }

        return blendEnabled && secondaryVoice is not null
            ? $"Playing {voice.Name} {primaryVoiceWeight}% + {secondaryVoice.Name} {100 - primaryVoiceWeight}%."
            : $"Playing {voice.Name}.";
    }
}
