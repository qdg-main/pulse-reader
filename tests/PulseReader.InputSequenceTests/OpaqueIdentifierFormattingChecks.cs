using PulseReader.TextPreparation;

internal static class OpaqueIdentifierFormattingChecks
{
    public static void Run()
    {
        var corpus = SpokenTextAbCorpus.LoadIdentifiers();
        foreach (var example in corpus)
        {
            var actual = SpokenTextFormatter.Format(
                    ReadingUnitAnalyzer.Prepare(example.OriginalText))
                .BuildSpokenText();
            Assert(
                string.Equals(example.ExpectedPreparedText, actual, StringComparison.Ordinal),
                $"Identifier case '{example.Id}' did not match the accepted spoken text."
                + $"{Environment.NewLine}Expected: {example.ExpectedPreparedText}"
                + $"{Environment.NewLine}Actual:   {actual}");
        }

        const string uuid = "550e8400-e29b-41d4-a716-446655440000";
        const string commit = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b";
        const string sha256 =
            "e889202c41dafb67b10d67b97f0d8541508036a6090af23425a5c2615d03c47a";
        AssertFormatted(
            $"Session {uuid}.",
            "Session 5 5 0 e, etc.",
            "sentence-final UUID punctuation");
        AssertFormatted(
            "Token: abcdef0123456789abcdef0123456789abcdef01",
            "Token: a b c d, etc.",
            "labelled hexadecimal token precedence");
        AssertFormatted(
            $"Audio hash: {sha256}",
            "Audio hash: e 8 8 9, etc.",
            "SHA-256 tokenizer safety");
        const string longNumericQuantity = "1234567890123456789012345678901234567890";
        AssertFormatted(
            $"The calculated quantity was {longNumericQuantity} units.",
            $"The calculated quantity was {longNumericQuantity} units.",
            "unlabelled numeric quantity control");
        AssertFormatted(
            $"{{\n  \"commit\": \"{commit}\",\n  \"session\": \"{uuid}\"\n}}",
            "Commit: 9 f 8 6 d 0 8. Session: 5 5 0 e, etc.",
            "parsed JSON identifiers");

        var tableActual = Format(
            $"| Session |\n| --- |\n| {uuid} |");
        Assert(
            tableActual.Contains("5 5 0 e, etc.", StringComparison.Ordinal),
            "Table identifiers did not receive the accepted shortening policy."
            + $"{Environment.NewLine}Actual: {tableActual}");

        Console.WriteLine(
            $"PASS: opaque identifier preparation matches {corpus.Count} accepted and control cases across prose, JSON, and tables.");
    }

    private static void AssertFormatted(string source, string expected, string label)
    {
        var actual = Format(source);
        Assert(
            string.Equals(expected, actual, StringComparison.Ordinal),
            $"Opaque identifier formatting failed for {label}."
            + $"{Environment.NewLine}Expected: {expected}"
            + $"{Environment.NewLine}Actual:   {actual}");
    }

    private static string Format(string source) =>
        SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(source)).BuildSpokenText();

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
