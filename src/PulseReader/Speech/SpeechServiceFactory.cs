using PulseReader.Configuration;

namespace PulseReader.Speech;

internal static class SpeechServiceFactory
{
    public static SpeechServiceController Create(
        SpeechBackend backend,
        KokoroSpeechSettings kokoroSettings,
        WindowsSpeechSettings windowsSettings,
        out string? warning)
    {
        var windowsSpeech = CreateWindowsSpeechService(windowsSettings, out var windowsWarning);
        var contextualPronunciation = new ContextualPronunciationService();
        var contextualAvailability = contextualPronunciation.Availability;
        warning = backend == SpeechBackend.Windows
            ? windowsWarning
            : kokoroSettings.ContextualPronunciationEnabled
              && !contextualAvailability.IsInstalled
                ? contextualAvailability.Message
                : null;
        return new SpeechServiceController(
            backend,
            kokoroSettings,
            windowsSettings,
            windowsSpeech,
            new KokoroSpeechService(
                settings: kokoroSettings,
                contextualPronunciation: contextualPronunciation));
    }

    private static ISpeechService CreateWindowsSpeechService(
        WindowsSpeechSettings settings,
        out string? warning)
    {
        try
        {
            var service = new WindowsSpeechService(settings);
            if (service.Voices.Count > 0)
            {
                warning = null;
                return service;
            }

            service.Dispose();
            warning = "Pulse Reader could not find an enabled Windows speech voice.";
            return new UnavailableSpeechService();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ArgumentException
                                          or PlatformNotSupportedException
                                          or TypeInitializationException)
        {
            warning = "Pulse Reader could not initialize a locally installed Windows speech voice.";
            return new UnavailableSpeechService();
        }
    }
}
