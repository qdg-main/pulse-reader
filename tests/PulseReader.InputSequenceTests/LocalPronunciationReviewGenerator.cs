using System.Net;
using System.Text;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Utilities;
using NAudio.Wave;
using PulseReader.Speech;

internal static class LocalPronunciationReviewGenerator
{
    public static async Task RunAsync(string outputDirectory)
    {
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        var modelPath = await new KokoroModelStore().GetModelPathAsync(CancellationToken.None);
        var voice = KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice);
        var results = new List<PronunciationReviewResult>(Cases.Length);

        using var synthesizer = new KokoroWavSynthesizer(modelPath);
        for (var index = 0; index < Cases.Length; index++)
        {
            var example = Cases[index];
            var prefix = $"{index + 1:00}-{example.Id}";
            var originalFile = prefix + "-a-original.wav";
            var phonemeFile = prefix + "-b-phoneme.wav";
            var originalAudio = WrapWave(
                await synthesizer.SynthesizeAsync(example.OriginalText, voice));
            var phonemeAudio = WrapWave(
                await synthesizer.SynthesizeAsync(example.PhonemeText, voice));
            await File.WriteAllBytesAsync(
                Path.Combine(fullOutputDirectory, originalFile),
                originalAudio);
            await File.WriteAllBytesAsync(
                Path.Combine(fullOutputDirectory, phonemeFile),
                phonemeAudio);
            results.Add(new PronunciationReviewResult(
                index + 1,
                example,
                originalFile,
                phonemeFile,
                DurationSeconds(originalAudio),
                DurationSeconds(phonemeAudio)));
        }

        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "index.html"),
            BuildHtml(results),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"Pronunciation review page: {Path.Combine(fullOutputDirectory, "index.html")}");
    }

    private static byte[] WrapWave(byte[] pcm)
    {
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, KokoroPlayback.waveFormat))
        {
            writer.Write(pcm, 0, pcm.Length);
        }

        return output.ToArray();
    }

    private static double DurationSeconds(byte[] wave)
    {
        using var stream = new MemoryStream(wave, writable: false);
        using var reader = new WaveFileReader(stream);
        return reader.TotalTime.TotalSeconds;
    }

    private static string BuildHtml(IReadOnlyList<PronunciationReviewResult> results)
    {
        var html = new StringBuilder();
        html.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Pulse Reader contextual pronunciation review</title>
<style>
body{margin:0;background:#071225;color:#eef7ff;font:15px/1.5 Segoe UI,Arial,sans-serif}.page{max-width:1050px;margin:auto;padding:34px 24px 80px}h1{margin:0;color:#fff;font-size:30px}.intro{color:#a9bdd4;max-width:850px}.case{margin:22px 0;padding:20px;border:1px solid #205d89;border-radius:18px;background:#091a31;box-shadow:0 0 24px #009dff18}.case h2{margin:0 0 4px}.goal{color:#b9c9d9}.pair{display:grid;grid-template-columns:1fr 1fr;gap:14px}.variant{border:1px solid #173f61;border-radius:12px;padding:14px;background:#071428}.variant h3{margin:0 0 8px;font-size:14px;color:#8edfff}.text{min-height:50px;color:#f4f8fc}.phonemes{color:#7795b1;font-size:12px;margin-top:8px}audio{width:100%;margin-top:10px}.duration{color:#7891aa;font-size:12px}@media(max-width:720px){.pair{grid-template-columns:1fr}}
</style></head><body><main class="page">
<h1>Contextual pronunciation candidates</h1>
<p class="intro">Every pair uses Kokoro's af_heart voice. A is current playback. B changes only the target word using KokoroSharp's explicit phoneme-literal support. The local model is not generating or rewriting any spoken text.</p>
""");
        foreach (var result in results)
        {
            html.Append("<section class=\"case\"><h2>")
                .Append(result.Number.ToString("00")).Append(". ")
                .Append(E(result.Example.Title)).Append("</h2><p class=\"goal\">")
                .Append(E(result.Example.Goal)).Append("</p><div class=\"pair\">");
            Variant(html, "A · Current pronunciation", result.Example.OriginalText,
                null, result.OriginalFile, result.OriginalDurationSeconds);
            Variant(html, "B · Explicit phoneme", result.Example.DisplayText,
                result.Example.Phonemes, result.PhonemeFile, result.PhonemeDurationSeconds);
            html.Append("</div></section>");
        }

        html.Append("</main></body></html>");
        return html.ToString();
    }

    private static void Variant(
        StringBuilder html,
        string heading,
        string text,
        string? phonemes,
        string file,
        double durationSeconds)
    {
        html.Append("<div class=\"variant\"><h3>").Append(E(heading))
            .Append("</h3><div class=\"text\">").Append(E(text)).Append("</div>");
        if (phonemes is not null)
        {
            html.Append("<div class=\"phonemes\">Target phonemes: ")
                .Append(E(phonemes)).Append("</div>");
        }

        html.Append("<audio controls preload=\"metadata\" src=\"").Append(E(file))
            .Append("\"></audio><div class=\"duration\">")
            .Append(durationSeconds.ToString("0.000")).Append(" seconds</div></div>");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static readonly PronunciationReviewCase[] Cases =
    [
        new(
            "axes-tools",
            "Axes as cutting tools",
            "Listen for the plural of axe rather than the plural of axis.",
            "Sharpen both axes before splitting the logs.",
            "Sharpen both [axes](/ˈæksɪz/) before splitting the logs.",
            "Sharpen both axes before splitting the logs.",
            "ˈæksɪz"),
        new(
            "bass-audio",
            "Bass as low-frequency sound",
            "Listen for base rather than the fish pronunciation.",
            "Reduce the bass slightly, then listen to the speakers again.",
            "Reduce the [bass](/bˈAs/) slightly, then listen to the speakers again.",
            "Reduce the bass slightly, then listen to the speakers again.",
            "bˈAs"),
        new(
            "lead-metal",
            "Lead as the metal",
            "Listen for led rather than leed.",
            "The old pipe contains lead, so wear gloves before moving it.",
            "The old pipe contains [lead](/lˈɛd/), so wear gloves before moving it.",
            "The old pipe contains lead, so wear gloves before moving it.",
            "lˈɛd"),
        new(
            "wind-action",
            "Wind as a turning action",
            "Listen for wined rather than moving air.",
            "They watched the rope wind around the drum.",
            "They watched the rope [wind](/wˈInd/) around the drum.",
            "They watched the rope wind around the drum.",
            "wˈInd"),
        new(
            "live-presenter",
            "Go live",
            "Listen for the long-i pronunciation used for a real-time broadcast.",
            "The presenter will go live in five minutes.",
            "The presenter will go [live](/lˈIv/) in five minutes.",
            "The presenter will go live in five minutes.",
            "lˈIv"),
        new(
            "live-studio-second",
            "Live broadcast after live as reside",
            "The first occurrence should mean reside; only the second should use the long-i pronunciation.",
            "I live near the studio where they broadcast the programme live.",
            "I live near the studio where they broadcast the programme [live](/lˈIv/).",
            "I live near the studio where they broadcast the programme live.",
            "lˈIv"),
        new(
            "live-musicians-second",
            "Perform live after live as reside",
            "The first occurrence should mean reside; only the second should use the long-i pronunciation.",
            "The musicians who live nearby will perform live tonight.",
            "The musicians who live nearby will perform [live](/lˈIv/) tonight.",
            "The musicians who live nearby will perform live tonight.",
            "lˈIv"),
        new(
            "live-manchester-second",
            "Watch live after live as reside",
            "The first occurrence should mean reside; only the second should use the long-i pronunciation.",
            "We live in Manchester and watch every match live.",
            "We live in Manchester and watch every match [live](/lˈIv/).",
            "We live in Manchester and watch every match live.",
            "lˈIv"),
        new(
            "live-venue-second",
            "Live online after live as reside",
            "The first occurrence should mean reside; only the second should use the long-i pronunciation.",
            "I would rather live near the venue than watch its concerts live online.",
            "I would rather live near the venue than watch its concerts [live](/lˈIv/) online.",
            "I would rather live near the venue than watch its concerts live online.",
            "lˈIv")
    ];
}

internal sealed record PronunciationReviewCase(
    string Id,
    string Title,
    string Goal,
    string OriginalText,
    string PhonemeText,
    string DisplayText,
    string Phonemes);

internal sealed record PronunciationReviewResult(
    int Number,
    PronunciationReviewCase Example,
    string OriginalFile,
    string PhonemeFile,
    double OriginalDurationSeconds,
    double PhonemeDurationSeconds);
