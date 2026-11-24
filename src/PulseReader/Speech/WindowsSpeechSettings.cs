namespace PulseReader.Speech;

internal sealed record WindowsSpeechSettings(
    string? VoiceName,
    int Rate,
    int Volume)
{
    public const int DefaultRate = 0;
    public const int DefaultVolume = 100;
    public const int MinimumRate = -10;
    public const int MaximumRate = 10;

    public static WindowsSpeechSettings Default { get; } =
        new(null, DefaultRate, DefaultVolume);

    public static bool IsValid(string? voiceName, int rate, int volume) =>
        (voiceName is null || !string.IsNullOrWhiteSpace(voiceName))
        && rate is >= MinimumRate and <= MaximumRate
        && volume is >= 0 and <= 100;
}

internal sealed record WindowsVoiceDescriptor(
    string Id,
    string Name,
    string Language,
    string CultureName,
    bool IsDefault)
{
    public string DisplayName => Name;
}
