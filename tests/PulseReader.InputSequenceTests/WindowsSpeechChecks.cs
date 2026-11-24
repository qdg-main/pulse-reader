using System.Speech.Synthesis;
using PulseReader.Speech;

internal static class WindowsSpeechChecks
{
    public static async Task RunSmokeAsync()
    {
        using var service = new WindowsSpeechService(WindowsSpeechSettings.Default);
        Assert(service.Voices.Count > 0, "Windows did not expose an enabled System.Speech voice.");

        long generatedBytes = 0;
        using (var synthesizer = new SpeechSynthesizer())
        {
            foreach (var voice in service.Voices)
            {
                using var wave = new MemoryStream();
                synthesizer.SelectVoice(voice.Id);
                synthesizer.Rate = voice.IsDefault ? -1 : 1;
                synthesizer.Volume = 64;
                synthesizer.SetOutputToWaveStream(wave);
                synthesizer.Speak("Pulse Reader Windows voice control test.");
                Assert(
                    wave.Length > 44,
                    $"Windows voice {voice.Name} did not produce usable WAV audio.");
                generatedBytes += wave.Length;
            }

            synthesizer.SetOutputToNull();
        }

        var selectedVoice = service.Voices.FirstOrDefault(voice => !voice.IsDefault)
            ?? service.Voices[0];
        service.UpdateSettings(new WindowsSpeechSettings(selectedVoice.Id, 2, 70));
        var startResult = await service.TrySpeakAsync(
            "Pulse Reader Windows speech playback and stop are working.");
        Assert(startResult.Started, startResult.Error ?? "Windows speech playback did not start.");
        await Task.Delay(200);
        Assert(service.TryPause(), "Windows speech did not accept pause during playback.");
        Assert(
            service.State == SpeechPlaybackState.Paused,
            "Windows speech did not expose its paused state.");
        await Task.Delay(150);
        Assert(service.TryResume(), "Windows speech did not resume after pause.");
        service.Stop();
        Assert(!service.IsSpeaking, "Windows speech did not stop immediately.");

        Console.WriteLine(
            $"PASS: {service.Voices.Count} enabled Windows voices generated {generatedBytes} WAV bytes; "
            + $"{selectedVoice.Name} paused, resumed, and stopped cleanly.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
