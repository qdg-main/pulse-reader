using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using PulseReader.Speech;
using PulseReader.TextPreparation;

internal static class KokoroSegmentationChecks
{
    public static void Run()
    {
        var voice = KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice);
        var text = string.Join(
            ' ',
            Enumerable.Repeat(
                "Pulse Reader keeps long selections responsive when speech is stopped.",
                80));
        var tokens = Tokenizer.Tokenize(text, voice.GetLangCode(), true);
        var pipeline = KokoroSpeechService.CreatePipelineConfig(1.15f);
        var segments = pipeline.SegmentationFunc(tokens);
        var defaultSegments = new KokoroTTSPipelineConfig().SegmentationFunc(tokens);

        Assert(segments.Count > 1, "The long Kokoro test input was not segmented.");
        Assert(
            segments.First().Length is > 0 and <= 200,
            "The first Kokoro segment exceeded the original 200-token limit.");
        Assert(
            segments.Skip(1).All(segment => segment.Length is > 0 and <= 510),
            "A later Kokoro segment exceeded the model's 510-token limit.");
        Assert(
            segments.Any(segment => segment.Length > 100),
            "Kokoro unexpectedly retained the experimental 100-token segmentation policy.");
        Assert(
            segments.Select(segment => segment.Length)
                .SequenceEqual(defaultSegments.Select(segment => segment.Length)),
            "Pulse Reader no longer follows KokoroSharp's original segmentation policy.");
        Assert(
            segments
                .SelectMany(segment => segment)
                .Where(token => token != Tokenizer.Vocab[' '])
                .SequenceEqual(tokens.Where(token => token != Tokenizer.Vocab[' '])),
            "Kokoro segmentation changed or reordered the non-space input tokens.");
        Assert(
            Math.Abs(pipeline.Speed - 1.15f) < 0.0001f,
            "The Kokoro pipeline did not preserve the configured speed.");

        CheckPreparedPlaybackSegments(voice);
        CheckEmojiPreparation(voice);
        CheckSha256Preparation(voice);
        CheckNavigationPlan(voice);
        CheckNumberedListNavigation(voice);
        CheckNavigationFadeIn();

        Console.WriteLine(
            $"PASS: Kokoro uses the original segmentation policy across {segments.Count} segments "
            + $"(largest: {segments.Max(segment => segment.Length)} tokens) without changing the input.");
    }

    private static void CheckNumberedListNavigation(KokoroVoice voice)
    {
        var pipeline = KokoroSpeechService.CreatePipelineConfig(
            KokoroSpeechSettings.DefaultSpeed);
        const string text =
            "The expanded diagnostic build is running.\n"
            + "Please:\n"
            + "1. Select and play a representative passage.\n"
            + "2. Navigate once.\n"
            + "3. Navigate again until the noise occurs.\n"
            + "4. Exit Pulse Reader from the tray.\n"
            + "The exact token sequence can then be replayed.";
        var plan = KokoroSpeechService.CreatePlaybackPlan(text, voice, pipeline);

        Assert(plan.UnitCount == 7, "The numbered-list regression lost a reading unit.");
        for (var unitIndex = 0; unitIndex < plan.UnitCount; unitIndex++)
        {
            var restarted = plan.CreateSegmentsFromUnit(unitIndex);
            Assert(restarted.Count > 0, $"Numbered-list unit {unitIndex} could not restart.");
            Assert(
                restarted[0].Tokens.Length > 0
                && restarted[0].Tokens[0] >= 0
                && restarted[0].Tokens[0] != Tokenizer.Vocab[' '],
                $"Numbered-list unit {unitIndex} restarted on a boundary-only token.");
            Assert(
                restarted[0].Cues.FirstOrDefault(candidate => candidate.UnitIndex == unitIndex)
                    == new KokoroNavigationCue(unitIndex, 0),
                $"Numbered-list unit {unitIndex} did not move its cue to audible content.");
        }
    }

    private static void CheckNavigationPlan(KokoroVoice voice)
    {
        var pipeline = KokoroSpeechService.CreatePipelineConfig(
            KokoroSpeechSettings.DefaultSpeed);
        const string text =
            "The first sentence establishes the start. "
            + "The second sentence is the current reading unit. "
            + "The third sentence can be selected with next.";
        var plan = KokoroSpeechService.CreatePlaybackPlan(text, voice, pipeline);
        var expectedSegments = SpokenTextFormatter
            .Format(ReadingUnitAnalyzer.Prepare(text))
            .BuildSpeechChunks()
            .SelectMany(chunk =>
            {
                var preparedText = KokoroSpeechService
                    .RemoveUnsupportedTokenizerSymbols(chunk.Text)
                    .Trim();
                var preparedTokens = Tokenizer.Tokenize(
                    preparedText,
                    voice.GetLangCode(),
                    pipeline.PreprocessText);
                return pipeline.SegmentationFunc(preparedTokens);
            })
            .ToArray();
        var cues = plan.Segments
            .SelectMany(segment => segment.Cues)
            .OrderBy(cue => cue.UnitIndex)
            .ToArray();

        Assert(plan.UnitCount == 3, "Kokoro navigation did not retain all three reading units.");
        Assert(
            plan.Segments.Select(segment => segment.Tokens.Length)
                .SequenceEqual(expectedSegments.Select(segment => segment.Length))
            && plan.Segments.SelectMany(segment => segment.Tokens)
                .SequenceEqual(expectedSegments.SelectMany(segment => segment)),
            "Adding navigation cues changed Kokoro's initial token stream or segmentation.");
        Assert(
            cues.Select(cue => cue.UnitIndex).SequenceEqual([0, 1, 2]),
            "Kokoro navigation cues are missing or out of order.");

        var targetUnit = 1;
        var flattenedOffset = 0;
        var targetOffset = -1;
        foreach (var segment in plan.Segments)
        {
            var cue = segment.Cues.FirstOrDefault(candidate => candidate.UnitIndex == targetUnit);
            if (cue.UnitIndex == targetUnit)
            {
                targetOffset = flattenedOffset + cue.TokenOffset;
                break;
            }

            flattenedOffset += segment.Tokens.Length;
        }

        var restarted = plan.CreateSegmentsFromUnit(targetUnit);
        var restartedTokens = restarted.SelectMany(segment => segment.Tokens).ToArray();
        var freshUnitTokens = Tokenizer.Tokenize(
            "The second sentence is the current reading unit. The third sentence can be selected with next.",
            voice.GetLangCode(),
            pipeline.PreprocessText);
        Assert(targetOffset > 0, "The second reading unit did not map to a later token boundary.");
        Assert(
            restartedTokens.SequenceEqual(freshUnitTokens),
            "Restarting a reading unit did not match fresh tokenization at that boundary.");
        Assert(
            restarted[0].Cues[0] == new KokoroNavigationCue(targetUnit, 0),
            "The restarted reading unit does not begin with its navigation cue.");

        var prefix = plan.CreateSegmentsBeforeUnit(targetUnit);
        var prefixTokens = prefix.SelectMany(segment => segment.Tokens).ToArray();
        var freshPrefixTokens = Tokenizer.Tokenize(
            "The first sentence establishes the start.",
            voice.GetLangCode(),
            pipeline.PreprocessText);
        Assert(prefix.Count > 0 && prefixTokens.SequenceEqual(freshPrefixTokens),
            "Progressive playback did not isolate the unaffected prefix at the target boundary.");
        Assert(
            prefix.SelectMany(segment => segment.Cues).All(cue => cue.UnitIndex < targetUnit),
            "Progressive prefix segments retained a cue from an unresolved unit.");
    }

    private static void CheckEmojiPreparation(KokoroVoice voice)
    {
        const string plainText = "That was awkward  but Kokoro should keep speaking.";
        var pipeline = KokoroSpeechService.CreatePipelineConfig(
            KokoroSpeechSettings.DefaultSpeed);
        var plainSegments = KokoroSpeechService.CreatePlaybackSegments(
            plainText,
            voice,
            pipeline);
        string[] emojiTexts =
        [
            "That was awkward 😅 but Kokoro should keep speaking.",
            "That was awkward ❤️ but Kokoro should keep speaking.",
            "That was awkward 👨‍💻 but Kokoro should keep speaking."
        ];

        foreach (var emojiText in emojiTexts)
        {
            var segments = KokoroSpeechService.CreatePlaybackSegments(
                emojiText,
                voice,
                pipeline);

            Assert(segments.Count > 0, "An emoji removed all Kokoro playback segments.");
            Assert(
                segments.SelectMany(segment => segment.Tokens)
                    .SequenceEqual(plainSegments.SelectMany(segment => segment.Tokens)),
                "Removing an emoji changed the surrounding Kokoro speech tokens.");
        }

        var adjacentEmojiSegments = KokoroSpeechService.CreatePlaybackSegments(
            "That was awkward😅but Kokoro should keep speaking.",
            voice,
            pipeline);
        var spacedPlainSegments = KokoroSpeechService.CreatePlaybackSegments(
            "That was awkward but Kokoro should keep speaking.",
            voice,
            pipeline);
        Assert(
            adjacentEmojiSegments.SelectMany(segment => segment.Tokens)
                .SequenceEqual(spacedPlainSegments.SelectMany(segment => segment.Tokens)),
            "Removing an adjacent emoji joined its surrounding words.");
    }

    private static void CheckSha256Preparation(KokoroVoice voice)
    {
        var pipeline = KokoroSpeechService.CreatePipelineConfig(
            KokoroSpeechSettings.DefaultSpeed);
        const string text =
            "`minimax_h3_fl2va_pruned_int8_convrot.safetensors`; "
            + "20,970,379,616; "
            + "`e889202c41dafb67b10d67b97f0d8541508036a6090af23425a5c2615d03c47a`.";

        var plan = KokoroSpeechService.CreatePlaybackPlan(text, voice, pipeline);
        Assert(plan.Segments.Count > 0, "A SHA-256 value prevented Kokoro playback planning.");

        var prepared = SpokenTextFormatter
            .Format(ReadingUnitAnalyzer.Prepare(text))
            .BuildSpokenText();
        Assert(
            prepared.Contains("e 8 8 9, etc.", StringComparison.Ordinal),
            "The SHA-256 value did not receive the accepted opaque-identifier shortening policy.");
        Assert(
            !prepared.Contains("e889202c41dafb67", StringComparison.Ordinal),
            "The full SHA-256 value still reached Kokoro preparation.");
    }

    private static void CheckNavigationFadeIn()
    {
        var samples = Enumerable.Repeat(1f, 1000).ToArray();
        PausableKokoroPlayback.ApplyFadeIn(samples, 20);
        var fadeSampleCount = KokoroPlayback.waveFormat.SampleRate * 20 / 1000;

        Assert(samples[0] == 0f, "A navigation restart did not fade in from silence.");
        Assert(
            Math.Abs(samples[fadeSampleCount - 1] - 1f) < 0.0001f,
            "A navigation restart did not reach full volume at the end of its fade-in.");
        Assert(
            samples[fadeSampleCount] == 1f,
            "The navigation fade-in changed samples after its short transition window.");
    }

    private static void CheckPreparedPlaybackSegments(KokoroVoice voice)
    {
        var pipeline = KokoroSpeechService.CreatePipelineConfig(
            KokoroSpeechSettings.DefaultSpeed);
        const string unchangedText = "A plain sentence remains unchanged.";
        var unchangedTokens = Tokenizer.Tokenize(
            unchangedText,
            voice.GetLangCode(),
            pipeline.PreprocessText);
        var expectedSegments = pipeline.SegmentationFunc(unchangedTokens);
        var actualSegments = KokoroSpeechService.CreatePlaybackSegments(
            unchangedText,
            voice,
            pipeline);

        Assert(
            actualSegments.Select(segment => segment.Tokens.Length)
                .SequenceEqual(expectedSegments.Select(segment => segment.Length)),
            "Kokoro preparation changed segmentation for preserve-original text.");
        Assert(
            actualSegments.SelectMany(segment => segment.Tokens)
                .SequenceEqual(expectedSegments.SelectMany(segment => segment)),
            "Kokoro preparation changed tokens for preserve-original text.");
        Assert(
            actualSegments.All(segment => segment.MinimumTrailingPauseSeconds == 0),
            "Preserve-original text unexpectedly gained an explicit playback pause.");

        var pausedSegments = KokoroSpeechService.CreatePlaybackSegments(
            "Wait...\n\nIs the next voice ready?",
            voice,
            pipeline);
        Assert(
            pausedSegments.Count(segment => segment.MinimumTrailingPauseSeconds > 0) == 1,
            "The prepared ellipsis did not produce exactly one explicit playback pause.");
        Assert(
            pausedSegments.Count >= 2
                && pausedSegments[^1].MinimumTrailingPauseSeconds == 0,
            "The explicit ellipsis pause was not attached to the intended chunk boundary.");
        Assert(
            pausedSegments.Any(
                segment => Math.Abs(
                    segment.MinimumTrailingPauseSeconds
                    - SpokenTextFormatter.EllipsisPauseMilliseconds / 1000f) < 0.0001f),
            "The prepared ellipsis pause was not routed into Kokoro playback timing.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
