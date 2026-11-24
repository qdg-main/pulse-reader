namespace PulseReader.Speech;

internal sealed record KokoroSpeechSettings(
    string VoiceName,
    float Speed,
    int Volume,
    bool BlendEnabled = false,
    string SecondaryVoiceName = "af_sky",
    int PrimaryVoiceWeight = 50,
    bool ContextualPronunciationEnabled = false)
{
    public const string DefaultVoice = "af_heart";
    public const string DefaultSecondaryVoice = "af_sky";
    public const int DefaultPrimaryVoiceWeight = 50;
    public const float DefaultSpeed = 1f;
    public const int DefaultVolume = 100;
    public const float MinimumSpeed = 0.75f;
    public const float MaximumSpeed = 1.25f;

    public static KokoroSpeechSettings Default { get; } =
        new(DefaultVoice, DefaultSpeed, DefaultVolume);

    public int SecondaryVoiceWeight => 100 - PrimaryVoiceWeight;

    public static bool IsValid(
        string? voiceName,
        float speed,
        int volume,
        string? secondaryVoiceName = DefaultSecondaryVoice,
        int primaryVoiceWeight = DefaultPrimaryVoiceWeight) =>
        KokoroVoiceCatalog.Contains(voiceName)
        && KokoroVoiceCatalog.Contains(secondaryVoiceName)
        && primaryVoiceWeight is >= 0 and <= 100
        && float.IsFinite(speed)
        && speed >= MinimumSpeed
        && speed <= MaximumSpeed
        && volume is >= 0 and <= 100;
}
