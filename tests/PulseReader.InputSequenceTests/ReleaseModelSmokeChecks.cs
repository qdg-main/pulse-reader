using KokoroSharp.Utilities;
using KokoroSharp;
using PulseReader.TextPreparation;
using PulseReader.Speech;
using PulseReader.Setup;
using System.Reflection;

internal static class ReleaseModelSmokeChecks
{
    public static async Task RunAsync()
    {
        var frontend = typeof(MisakiSharp.EnglishG2P).Assembly;
        if (!frontend.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Any(attribute => attribute.Key == "PulseReader.FrontendPatch"
                    && attribute.Value == "number-and-punctuation-v1")
            || frontend.GetType("MisakiSharp.EnglishG2P+Num2Words") is not null
            || frontend.GetManifestResourceNames().Length != 20)
            throw new InvalidOperationException("Installed payload does not contain the qualified replacement frontend.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var store = new KokoroModelStore();
        if (!File.Exists(store.ModelPath))
            throw new InvalidOperationException("The installer first-run test must download Kokoro before this offline smoke check.");
        // This executable is not the installed reader and must not request an
        // elevated shared setup. The actual reader already completed setup;
        // independently verify the cached model before testing its native payload.
        var setup = new ModelSetupService();
        if (!await setup.VerifyKokoroInstalledAsync(timeout.Token))
            throw new InvalidOperationException("The cached release model failed its pinned checksum verification.");
        var model = store.ModelPath;
        using var synthesizer = new KokoroWavSynthesizer(model);
        // Use production voice selection, not the upstream writable-path loader.
        var voice = KokoroSpeechService.CreateVoice(KokoroSpeechSettings.Default);
        // The pinned KokoroSharp API returns raw 24 kHz, 16-bit mono PCM, not
        // a RIFF container. Its SaveAudioToFile method adds the WAV header.
        var samples = await synthesizer.SynthesizeAsync("Pulse Reader is ready for its first release.", voice);
        if (samples.Length < 48000 || samples.Length % 2 != 0 || !samples.Any(value => value != 0))
            throw new InvalidOperationException($"The release payload did not produce usable PCM speech ({samples.Length} bytes).");
        Console.WriteLine($"PASS: cached Kokoro and published native dependencies produced {samples.Length} PCM bytes without audio playback.");
    }
}
