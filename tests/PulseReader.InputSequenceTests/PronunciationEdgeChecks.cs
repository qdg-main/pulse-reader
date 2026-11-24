using KokoroSharp;
using KokoroSharp.Processing;
using PulseReader.Speech;
using PulseReader.TextPreparation;

internal static class PronunciationEdgeChecks
{
    internal const string ReportedParagraph = "Stored stacks mean items exist in the agreed snapshot, not proven player ownership. Natural loot may explain some. Bounds are block coordinates. Full cluster evidence and all secondary clusters remain in `runtime/coreprotect-terrain-crossref-20260908-1/prioritized-clusters.json`.";
    internal const string PreparedParagraph = "Stored stacks mean items exist in the agreed snapshot, not proven player ownership. Natural loot may explain some. Bounds are block coordinates. Full cluster evidence and all secondary clusters remain in `runtime/coreprotect-terrain-crossref-two zero two six zero nine zero eight-1/prioritized-clusters dot Jason`.";

    internal static readonly SpokenTextAbCase[] ListeningCases =
    [
        new("iso-date", "Dates", "ISO calendar date", "Listen for twenty-ninth of August, followed by the year. No article is added.", "2026-08-29", "29th of August 2026", true, 0),
        new("short-date", "Dates", "Day and month without a year", "Listen for eleventh of September. No article is added.", "11 September", "11th of September", true, 0),
        new("version", "Versions", "Three-part version", "Listen for one point twenty-one point seven.", "1.21.7", "1 point 21 point 7", true, 0),
        new("folder", "Paths", "Folder with spaces and no file extension", "Listen for C colon backslash, then the folder names.", @"C:\Users\ExampleUser\Documents\Example Tool", @"C colon \Users\ExampleUser\Documents\Example Tool", true, 0)
    ];

    public static void Run()
    {
        var cases = new (string Source, string Expected)[]
        {
            ("2026-08-29", "29th of August 2026"),
            ("11 September", "11th of September"),
            ("On the 2026-08-29 we shipped.", "On the 29th of August 2026 we shipped."),
            ("Meet on the 11 September.", "Meet on the 11th of September."),
            ("Meet on the 11 September 2026.", "Meet on the 11th of September 2026."),
            ("1.21.7", "1 point 21 point 7"),
            (@"C:\Users\ExampleUser\Documents\Example Tool", @"C colon \Users\ExampleUser\Documents\Example Tool"),
            ("On 2024-02-29 we shipped 1.21.7.", "On 29th of February 2024 we shipped 1 point 21 point 7."),
            ("11 September 2026", "11th of September 2026"),
            ("2026-02-29", "2026-02-29"),
            ("31 February", "31 February"),
            ("29 February", "29th of February"),
            ("Use v1.21.7 and 1.02.003.", "Use v1 point 21 point 7 and 1 point zero two point zero zero three."),
            ("Version 1.2024.7", "Version 1 point two zero two four point 7"),
            ("The address is 127.0.0.1 and the value is 1.21.", "The address is 127.0.0.1 and the value is 1.21."),
            (@"Use C:\ or D:\Temp then retry.", @"Use C colon \ or D colon \Temp then retry."),
            ("Keep https://example.com/1.21.7 and build-2026-08-29.txt.", "Keep https colon example.com/1.21.7 and build dash 2026 dash 08 dash 29 dot txt."),
            ("There are 20260908 items.", "There are 20260908 items."),
            ("1.0000001e2", "1.0000001e2"),
            ("The value is 1.0000001e2.", "The value is 1.0000001e2."),
            ("Values: -1.2345678E+02 and +.1234567e-02.", "Values: -1.2345678E+02 and +.1234567e-02."),
            ("value:1.0000001e2", "value:1.0000001e2"),
            ("Use foo-20260908 and 1.0000001e2.", "Use foo-two zero two six zero nine zero eight and 1.0000001e2."),
            ("foo-20260908", "foo-two zero two six zero nine zero eight"),
            ("coreprotect20260908", "coreprotect two zero two six zero nine zero eight"),
            ("report.2026-08-29.log", "report.2026-08-29.log")
        };
        var failures = new List<string>();
        foreach (var (source, expected) in cases)
        {
            var actual = SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(source)).BuildSpokenText();
            if (actual != expected) failures.Add($"Expected '{expected}', got '{actual}'.");
            if (Tokenizer.Tokenize(actual).Length == 0) failures.Add($"No Kokoro tokens for '{source}'.");
        }

        foreach (var example in ListeningCases.Append(new SpokenTextAbCase("fallback", "Paths", "Fallback", "", ReportedParagraph, PreparedParagraph, true, 0)))
        {
            var original = ReadingUnitAnalyzer.Prepare(example.OriginalText);
            var prepared = SpokenTextFormatter.Format(original);
            if (prepared.BuildSpokenText() != example.ExpectedPreparedText)
                failures.Add($"Listening case {example.Id} no longer matches its recorded prepared text.");
            if (!original.Units.Select(u => (u.SourceRange, u.OriginalText, u.Kind))
                    .SequenceEqual(prepared.Units.Select(u => (u.SourceRange, u.OriginalText, u.Kind))))
                failures.Add($"Source/navigation identity changed for {example.Id}.");
        }

        foreach (var source in new[] { ReportedParagraph, "runtime/coreprotect-terrain-crossref-20260908-1/prioritized-clusters.json", "20260908-1", "20260908", "foo-20260908", "{\"path\":\"foo-20260908\"}", "```json\n{\"path\":\"foo-20260908\"}\n```", "| File |\n| --- |\n| foo-20260908 |" })
        {
            try
            {
                var prepared = SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(source));
                var plan = KokoroSpeechService.CreatePlaybackPlan(prepared,
                    KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice),
                    KokoroSpeechService.CreatePipelineConfig(1));
                if (plan.Segments.Count == 0) failures.Add($"No playback segments for '{source}'.");
            }
            catch (Exception exception)
            {
                failures.Add($"Kokoro planning failed for '{source}': {exception}");
            }
        }
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS: reported pronunciation edge cases prepare and tokenize for Kokoro.");
    }
}
