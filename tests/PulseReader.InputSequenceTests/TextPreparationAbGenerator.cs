using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Utilities;
using NAudio.Wave;
using PulseReader.Speech;
using PulseReader.TextPreparation;

internal static class TextPreparationAbGenerator
{
    public static Task RunFeetAndInchesAsync(string outputDirectory) =>
        RunCorpusAsync(outputDirectory, MeasurementUnitFormattingChecks.FeetAndInchesListeningCases,
            "Pulse Reader feet and inches",
            "A uses the original text; B expands paired feet/inches marks, including an omitted final inch mark. Both use af_heart at speed 1.",
            caseId: null, baselineHeading: "A · Previous playback", showSelectedSource: true);

    public static async Task RunPronunciationEdgesAsync(string outputDirectory)
    {
        PronunciationEdgeChecks.Run();
        await RunCorpusAsync(outputDirectory, PronunciationEdgeChecks.ListeningCases,
            "Pulse Reader dates, versions and paths",
            "A uses the previous playback wording; B uses the proposed correction. "
            + "Both use af_heart at speed 1. The final paragraph is a prepared-only preview: its previous wording crashes Kokoro's phonemizer.",
            caseId: null, baselineHeading: "A · Previous playback", showSelectedSource: true);

        var prepared = SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(PronunciationEdgeChecks.ReportedParagraph));
        // Verify the actual production plan as well as the offline synthesis path.
        var voice = KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice);
        KokoroSpeechService.CreatePlaybackPlan(prepared, voice, KokoroSpeechService.CreatePipelineConfig(1));
        using var synthesizer = new KokoroWavSynthesizer(await new KokoroModelStore().GetModelPathAsync(CancellationToken.None));
        var audio = WrapPcmAsWave(await SynthesizePreparedPcmAsync(synthesizer, prepared, voice));
        ValidateAudio(audio, "reported-fallback-paragraph", "prepared");
        var root = Path.GetFullPath(outputDirectory);
        const string file = "05-fallback-paragraph-b-prepared.wav";
        await File.WriteAllBytesAsync(Path.Combine(root, file), audio);
        var htmlPath = Path.Combine(root, "index.html");
        var html = await File.ReadAllTextAsync(htmlPath);
        var section = new StringBuilder("<section class=\"case\"><h2>05 · Previously failing paragraph</h2>");
        section.AppendLine("<p>No A audio: previous Kokoro preparation throws IndexOutOfRangeException in MisakiSharp.EspeakReplay.NumberWords. No Windows audio is substituted here.</p>");
        section.AppendLine($"<p class=\"text\">{Encode(PronunciationEdgeChecks.ReportedParagraph)}</p>");
        AppendVariant(section, "B · Prepared", prepared.BuildSpokenText(), file, DurationSeconds(audio), 0);
        section.AppendLine("</section>");
        await File.WriteAllTextAsync(htmlPath, html.Replace("</main>", section + "</main>", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(root, "fallback-preview.json"), JsonSerializer.Serialize(new
        {
            source = PronunciationEdgeChecks.ReportedParagraph,
            prepared = prepared.BuildSpokenText(),
            baselineUnavailable = "MisakiSharp.EspeakReplay.NumberWords throws IndexOutOfRangeException",
            voice = KokoroSpeechSettings.DefaultVoice,
            file,
            durationSeconds = DurationSeconds(audio),
            sha256 = Convert.ToHexString(SHA256.HashData(audio))
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[05/05] Previously failing paragraph: {audio.Length:N0} bytes, Kokoro only.");
    }

    public static Task RunAsync(string outputDirectory, string? caseId = null) =>
        RunCorpusAsync(
            outputDirectory,
            SpokenTextAbCorpus.LoadAccepted(),
            "Pulse Reader spoken-text A/B corpus",
            "Every pair uses Kokoro's <code>af_heart</code> voice. "
            + "A is the selected text exactly as supplied. B applies the accepted preparation policy.",
            caseId,
            baselineHeading: "A · Original",
            showSelectedSource: false);

    public static Task RunEdgeCasesAsync(string outputDirectory, string? caseId = null) =>
        RunCorpusAsync(
            outputDirectory,
            SpokenTextAbCorpus.LoadEdgeCases(),
            "Pulse Reader edge-case listening round",
            "Every pair uses Kokoro's <code>af_heart</code> voice. "
            + "A is the reported edge case unchanged. B applies the proposed scoped refinement.",
            caseId,
            baselineHeading: "A · Original",
            showSelectedSource: false);

    public static Task RunLineBoundariesAsync(string outputDirectory, string? caseId = null) =>
        RunCorpusAsync(
            outputDirectory,
            SpokenTextAbCorpus.LoadLineBoundaries(),
            "Pulse Reader line-boundary listening round",
            "Every pair uses Kokoro's <code>af_heart</code> voice. "
            + "The selected source is shown above each pair. A reproduces the previous production "
            + "formatter; B applies the proposed conservative unmarked-list and ambiguous-line policy.",
            caseId,
            baselineHeading: "A · Previous production behavior",
            showSelectedSource: true);

    public static Task RunIdentifiersAsync(string outputDirectory, string? caseId = null) =>
        RunCorpusAsync(
            outputDirectory,
            SpokenTextAbCorpus.LoadIdentifiers(),
            "Pulse Reader identifier listening round",
            "Every pair uses Kokoro's <code>af_heart</code> voice. "
            + "A is unprepared playback. B applies Pulse Reader's accepted conservative "
            + "identifier policy.",
            caseId,
            baselineHeading: "A · Current playback",
            showSelectedSource: true);

    public static Task RunMeasurementUnitsAsync(string outputDirectory, string? caseId = null) =>
        RunCorpusAsync(
            outputDirectory,
            SpokenTextAbCorpus.LoadMeasurementUnits(),
            "Pulse Reader measurement-unit listening round",
            "Every pair uses Kokoro's <code>af_heart</code> voice. "
            + "A is current playback. B applies the proposed quantity-aware unit names and "
            + "explicit binary-unit phonemes.",
            caseId,
            baselineHeading: "A · Current playback",
            showSelectedSource: true);

    public static Task RunFourDigitNumbersAsync(string outputDirectory, string? caseId = null) =>
        RunCorpusAsync(
            outputDirectory,
            SpokenTextAbCorpus.LoadFourDigitNumbers(),
            "Pulse Reader four-digit number listening round",
            "Every pair uses Kokoro's <code>af_heart</code> voice. "
            + "A is current playback. B applies the proposed cardinal-number policy while "
            + "preserving explicit years and labelled identifiers.",
            caseId,
            baselineHeading: "A · Current playback",
            showSelectedSource: true);

    private static async Task RunCorpusAsync(
        string outputDirectory,
        IReadOnlyList<SpokenTextAbCase> sourceCorpus,
        string title,
        string introduction,
        string? caseId,
        string baselineHeading,
        bool showSelectedSource)
    {
        var fullOutputPath = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputPath);

        var corpus = sourceCorpus;
        if (!string.IsNullOrWhiteSpace(caseId))
        {
            var requestedIds = caseId.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            corpus = corpus
                .Where(example => requestedIds.Contains(example.Id))
                .ToArray();
            if (corpus.Count != requestedIds.Count)
            {
                throw new InvalidOperationException(
                    $"The selected spoken-text A/B corpus does not contain every requested case: {caseId}.");
            }
        }
        var modelPath = await new KokoroModelStore().GetModelPathAsync(CancellationToken.None);
        var voice = KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice);
        var results = new List<AbAudioResult>(corpus.Count);

        using (var synthesizer = new KokoroWavSynthesizer(modelPath))
        {
            for (var index = 0; index < corpus.Count; index++)
            {
                var example = corpus[index];
                var prepared = SpokenTextFormatter.Format(
                    ReadingUnitAnalyzer.Prepare(example.OriginalText));
                var preparedText = prepared.BuildSpokenText();
                var baselineText = BuildOriginalSynthesisText(example);
                if (preparedText != example.ExpectedPreparedText)
                {
                    throw new InvalidOperationException(
                        $"A/B case '{example.Id}' no longer matches its reviewed prepared text.");
                }

                var filePrefix = $"{index + 1:00}-{example.Id}";
                var originalFile = filePrefix + "-a-original.wav";
                var preparedFile = filePrefix + "-b-prepared.wav";

                var originalAudio = WrapPcmAsWave(
                    await synthesizer.SynthesizeAsync(baselineText, voice));
                var preparedAudio = WrapPcmAsWave(
                    await SynthesizePreparedPcmAsync(synthesizer, prepared, voice));
                ValidateAudio(originalAudio, example.Id, "original");
                ValidateAudio(preparedAudio, example.Id, "prepared");

                await File.WriteAllBytesAsync(
                    Path.Combine(fullOutputPath, originalFile),
                    originalAudio);
                await File.WriteAllBytesAsync(
                    Path.Combine(fullOutputPath, preparedFile),
                    preparedAudio);

                results.Add(
                    new AbAudioResult(
                        index + 1,
                        example,
                        originalFile,
                        preparedFile,
                        DurationSeconds(originalAudio),
                        DurationSeconds(preparedAudio),
                        Convert.ToHexString(SHA256.HashData(originalAudio)).ToLowerInvariant(),
                        Convert.ToHexString(SHA256.HashData(preparedAudio)).ToLowerInvariant()));
                Console.WriteLine(
                    $"[{index + 1:00}/{corpus.Count:00}] {example.Id}: "
                    + $"{originalAudio.Length:N0} / {preparedAudio.Length:N0} bytes");
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(fullOutputPath, "index.html"),
            BuildHtml(results, title, introduction, baselineHeading, showSelectedSource),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputPath, "README.md"),
            BuildMarkdown(results, title, baselineHeading, showSelectedSource),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputPath, "manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    generatedUtc = DateTimeOffset.UtcNow,
                    voice = KokoroSpeechSettings.DefaultVoice,
                    modelPath,
                    cases = results.Select(
                        result => new
                        {
                            result.Number,
                            result.Example.Id,
                            result.Example.Category,
                            result.Example.Title,
                            result.Example.Rationale,
                            result.Example.OriginalText,
                            baselineText = BuildOriginalSynthesisText(result.Example),
                            preparedText = result.Example.ExpectedPreparedText,
                            result.Example.ExpectChange,
                            result.Example.ExpectedPauseMilliseconds,
                            result.OriginalFile,
                            result.PreparedFile,
                            result.OriginalDurationSeconds,
                            result.PreparedDurationSeconds,
                            result.OriginalSha256,
                            result.PreparedSha256
                        })
                },
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine($"A/B review page: {Path.Combine(fullOutputPath, "index.html")}");
    }

    private static void ValidateAudio(byte[] audio, string id, string variant)
    {
        if (audio.Length < 1000)
        {
            throw new InvalidOperationException(
                $"A/B case '{id}' returned an invalid {variant} WAV file.");
        }
    }

    private static byte[] WrapPcmAsWave(byte[] pcm)
    {
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, KokoroPlayback.waveFormat))
        {
            writer.Write(pcm, 0, pcm.Length);
        }

        return output.ToArray();
    }

    private static async Task<byte[]> SynthesizePreparedPcmAsync(
        KokoroWavSynthesizer synthesizer,
        PreparedDocument document,
        KokoroVoice voice)
    {
        using var result = new MemoryStream();
        foreach (var chunk in BuildPreparedSynthesisChunks(document))
        {
            if (!string.IsNullOrWhiteSpace(chunk.Text))
            {
                var audio = await synthesizer.SynthesizeAsync(chunk.Text.Trim(), voice);
                await result.WriteAsync(audio);
            }

            if (chunk.PauseAfterMilliseconds > 0)
            {
                var silenceBytes = KokoroPlayback.waveFormat.AverageBytesPerSecond
                    * chunk.PauseAfterMilliseconds
                    / 1000;
                silenceBytes -= silenceBytes % KokoroPlayback.waveFormat.BlockAlign;
                await result.WriteAsync(new byte[silenceBytes]);
            }
        }

        return result.ToArray();
    }

    internal static IReadOnlyList<PreparedSpeechChunk> BuildPreparedSynthesisChunks(
        PreparedDocument document) =>
        document.BuildSpeechChunks();

    internal static string BuildOriginalSynthesisText(SpokenTextAbCase example) =>
        example.BaselineText ?? example.OriginalText;

    private static double DurationSeconds(byte[] audio)
    {
        using var stream = new MemoryStream(audio, writable: false);
        using var reader = new WaveFileReader(stream);
        return Math.Round(reader.TotalTime.TotalSeconds, 3);
    }

    private static string BuildHtml(
        IReadOnlyList<AbAudioResult> results,
        string title,
        string introduction,
        string baselineHeading,
        bool showSelectedSource)
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine($"<title>{Encode(title)}</title>");
        html.AppendLine("<style>");
        html.AppendLine(
            "body{margin:0;background:#071225;color:#eef7ff;font:15px/1.5 Segoe UI,Arial,sans-serif}"
            + ".page{max-width:1100px;margin:auto;padding:34px 24px 80px}"
            + "h1{margin:0;color:#fff;font-size:30px}.intro{color:#a9bdd4;max-width:850px}"
            + ".summary{display:flex;gap:10px;flex-wrap:wrap;margin:18px 0 28px}"
            + ".chip{border:1px solid #2476ad;border-radius:999px;padding:5px 11px;background:#0b2039}"
            + ".case{margin:0 0 22px;padding:20px;border:1px solid #205d89;border-radius:18px;background:#091a31;box-shadow:0 0 24px #009dff18}"
            + ".case h2{margin:0 0 2px;font-size:20px}.meta{color:#70d9ff;font-size:13px;text-transform:uppercase;letter-spacing:.06em}"
            + ".why{color:#b9c9d9;margin:8px 0 16px}.pair{display:grid;grid-template-columns:1fr 1fr;gap:14px}"
            + ".variant{border:1px solid #173f61;border-radius:12px;padding:14px;background:#071428}"
            + ".variant h3{margin:0 0 8px;font-size:14px;color:#8edfff}.text{white-space:pre-wrap;min-height:68px;color:#f4f8fc}"
            + "audio{width:100%;margin-top:10px}.duration{color:#7891aa;font-size:12px;margin-top:5px}"
            + ".changed{color:#8ef2c1}.control{color:#ffda8a}@media(max-width:720px){.pair{grid-template-columns:1fr}}"
        );
        html.AppendLine("</style></head><body><main class=\"page\">");
        html.AppendLine($"<h1>{Encode(title)}</h1>");
        html.AppendLine($"<p class=\"intro\">{introduction}</p>");
        html.AppendLine("<div class=\"summary\">");
        html.AppendLine($"<span class=\"chip\">{results.Count} pairs</span>");
        html.AppendLine(
            $"<span class=\"chip changed\">{results.Count(result => result.Example.ExpectChange)} prepared</span>");
        html.AppendLine(
            $"<span class=\"chip control\">{results.Count(result => !result.Example.ExpectChange)} preserved</span>");
        html.AppendLine(
            $"<span class=\"chip\">{results.Count(result => result.Example.ExpectedPauseMilliseconds > 0)} explicit-pause test</span>");
        html.AppendLine("</div>");

        foreach (var result in results)
        {
            var example = result.Example;
            var hasAudioPreparation = example.ExpectChange
                || example.ExpectedPauseMilliseconds > 0;
            var stateClass = hasAudioPreparation ? "changed" : "control";
            var stateText = hasAudioPreparation ? "Prepared candidate" : "Preserve original";
            html.AppendLine("<section class=\"case\">");
            html.AppendLine(
                $"<div class=\"meta\">{result.Number:00} · {Encode(example.Category)} · "
                + $"<span class=\"{stateClass}\">{stateText}</span></div>");
            html.AppendLine($"<h2>{Encode(example.Title)}</h2>");
            html.AppendLine($"<p class=\"why\">{Encode(example.Rationale)}</p>");
            if (showSelectedSource)
            {
                html.AppendLine("<div class=\"variant source\">");
                html.AppendLine("<h3>Selected source</h3>");
                html.AppendLine($"<div class=\"text\">{Encode(example.OriginalText)}</div>");
                html.AppendLine("</div>");
            }
            html.AppendLine("<div class=\"pair\">");
            AppendVariant(
                html,
                baselineHeading,
                BuildOriginalSynthesisText(example),
                result.OriginalFile,
                result.OriginalDurationSeconds,
                pauseMilliseconds: 0);
            AppendVariant(
                html,
                "B · Prepared",
                example.ExpectedPreparedText,
                result.PreparedFile,
                result.PreparedDurationSeconds,
                example.ExpectedPauseMilliseconds);
            html.AppendLine("</div></section>");
        }

        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    private static void AppendVariant(
        StringBuilder html,
        string heading,
        string text,
        string file,
        double durationSeconds,
        int pauseMilliseconds)
    {
        html.AppendLine("<div class=\"variant\">");
        html.AppendLine($"<h3>{Encode(heading)}</h3>");
        html.AppendLine($"<div class=\"text\">{Encode(text)}</div>");
        html.AppendLine($"<audio controls preload=\"metadata\" src=\"{Encode(file)}\"></audio>");
        var pauseText = pauseMilliseconds > 0
            ? $" · includes {pauseMilliseconds} ms explicit pause"
            : string.Empty;
        html.AppendLine(
            $"<div class=\"duration\">{durationSeconds:0.000} seconds{pauseText}</div>");
        html.AppendLine("</div>");
    }

    private static string BuildMarkdown(
        IReadOnlyList<AbAudioResult> results,
        string title,
        string baselineHeading,
        bool showSelectedSource)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine($"# {title}");
        markdown.AppendLine();
        markdown.AppendLine(
            $"{results.Count} original-versus-prepared pairs using `af_heart`.");
        markdown.AppendLine();
        markdown.AppendLine("Open `index.html` for the side-by-side audio player.");

        foreach (var result in results)
        {
            var example = result.Example;
            markdown.AppendLine();
            markdown.AppendLine($"## {result.Number:00}. {example.Title}");
            markdown.AppendLine();
            markdown.AppendLine(
                $"{example.Category}. "
                + (example.ExpectChange ? "Prepared candidate." : "Preserve original."));
            markdown.AppendLine();
            markdown.AppendLine(example.Rationale);
            markdown.AppendLine();
            if (showSelectedSource)
            {
                markdown.AppendLine("**Selected source**");
                markdown.AppendLine();
                markdown.AppendLine("```text");
                markdown.AppendLine(example.OriginalText);
                markdown.AppendLine("```");
                markdown.AppendLine();
            }

            markdown.AppendLine($"**{baselineHeading}**");
            markdown.AppendLine();
            markdown.AppendLine("```text");
            markdown.AppendLine(BuildOriginalSynthesisText(example));
            markdown.AppendLine("```");
            markdown.AppendLine($"[Listen to A]({result.OriginalFile})");
            markdown.AppendLine();
            markdown.AppendLine("**B, prepared**");
            markdown.AppendLine();
            markdown.AppendLine("```text");
            markdown.AppendLine(example.ExpectedPreparedText);
            markdown.AppendLine("```");
            markdown.AppendLine($"[Listen to B]({result.PreparedFile})");
            if (example.ExpectedPauseMilliseconds > 0)
            {
                markdown.AppendLine();
                markdown.AppendLine(
                    $"B includes a {example.ExpectedPauseMilliseconds} ms explicit playback pause.");
            }
        }

        return markdown.ToString();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private sealed record AbAudioResult(
        int Number,
        SpokenTextAbCase Example,
        string OriginalFile,
        string PreparedFile,
        double OriginalDurationSeconds,
        double PreparedDurationSeconds,
        string OriginalSha256,
        string PreparedSha256);
}
