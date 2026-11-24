using KokoroSharp;
using PulseReader.Speech;

internal static class KokoroVoiceBlendChecks
{
    public static void Run()
    {
        var primary = KokoroVoiceStore.GetVoice(KokoroSpeechSettings.DefaultVoice);
        var secondary = KokoroVoiceStore.GetVoice(KokoroSpeechSettings.DefaultSecondaryVoice);
        var settings = new KokoroSpeechSettings(
            primary.Name,
            KokoroSpeechSettings.DefaultSpeed,
            KokoroSpeechSettings.DefaultVolume,
            true,
            secondary.Name,
            65);

        var mixed = KokoroSpeechService.CreateVoice(settings);
        var expected = primary.Features[0, 0, 0] * 0.65f + secondary.Features[0, 0, 0] * 0.35f;
        Assert(
            Math.Abs(mixed.Features[0, 0, 0] - expected) < 0.0001f,
            "Kokoro voice weights were not normalized into the expected blended embedding.");
        Assert(
            mixed.Name.EndsWith("_mix", StringComparison.Ordinal),
            "The Kokoro blend did not preserve the primary voice language prefix.");

        var secondaryOnly = KokoroSpeechService.CreateVoice(settings with { PrimaryVoiceWeight = 0 });
        Assert(
            ReferenceEquals(secondaryOnly, secondary),
            "A zero-percent primary weight did not resolve directly to the secondary voice.");

        Console.WriteLine("PASS: Kokoro voice embeddings blend at the configured normalized weight.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
