using PulseReader.TextPreparation;

internal static class TitleFormattingChecks
{
    public static void Run()
    {
        var cases = new (string Id, string Source, string Expected)[]
        {
            ("ms-surname", "Ms Smith approved the report.", "Ms. Smith approved the report."),
            ("ms-full-name", "Ms Jane Smith approved the report.", "Ms. Jane Smith approved the report."),
            ("ms-dotted-control", "Ms. Smith approved the report.", "Ms. Smith approved the report."),
            ("prof-surname", "Prof Smith approved the report.", "Professor Smith approved the report."),
            ("prof-dotted-full-name", "Prof. Daniel Jones approved the report.", "Professor Daniel Jones approved the report."),
            ("rev-surname", "Rev Smith approved the report.", "Reverend Smith approved the report."),
            ("rev-dotted-full-name", "Rev. Anna Brown approved the report.", "Reverend Anna Brown approved the report."),
            ("mx-surname", "Mx Smith approved the report.", "Mix Smith approved the report."),
            ("mx-dotted-full-name", "Mx. Taylor Smith approved the report.", "Mix Taylor Smith approved the report."),
            ("mrs-control", "Mrs Smith approved the report.", "Mrs Smith approved the report."),
            ("miss-control", "Miss Smith approved the report.", "Miss Smith approved the report."),
            ("non-name-control", "The Prof setting and Rev counter remain unchanged.", "The Prof setting and Rev counter remain unchanged."),
            ("uppercase-control", "MS SMITH is an imported identifier.", "MS SMITH is an imported identifier.")
        };

        foreach (var example in cases)
        {
            var actual = SpokenTextFormatter
                .Format(ReadingUnitAnalyzer.Prepare(example.Source))
                .BuildSpokenText();
            Assert(
                string.Equals(example.Expected, actual, StringComparison.Ordinal),
                $"Title case '{example.Id}' produced unexpected prepared text."
                + $"{Environment.NewLine}Expected: {example.Expected}"
                + $"{Environment.NewLine}Actual:   {actual}");
        }

        Console.WriteLine($"PASS: title preparation matches {cases.Length} name-position and control cases.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
