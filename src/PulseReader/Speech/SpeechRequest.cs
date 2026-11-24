using PulseReader.TextPreparation;

namespace PulseReader.Speech;

// Attribution is speech, but not part of the body's Markdown/JSON structure.
internal sealed record SpeechRequest(string Text, string Attribution = "")
{
    private PreparedDocument? PreparedBody { get; init; }

    public string DisplayText => Attribution + Text;

    public static SpeechRequest FromPreparedUnit(
        ReadingUnit unit, TextComplexityAssessment complexity, string attribution = "") =>
        new(unit.OriginalText, attribution)
        {
            // Retain the whole-message formatter's decisions, but rebase this
            // independent request's source range and navigation index.
            PreparedBody = new PreparedDocument(unit.OriginalText,
                Array.AsReadOnly(new[] { unit with
                {
                    Index = 0,
                    SourceRange = new(0, unit.OriginalText.Length)
                } }), complexity)
        };

    public PreparedDocument Prepare()
    {
        var body = PreparedBody is { } prepared && prepared.OriginalText == Text
            ? prepared
            : SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(Text));
        if (Attribution.Length == 0) return body;
        if (body.Units.Count == 0)
            return SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(DisplayText));

        var prefix = SpokenTextFormatter.Format(ReadingUnitAnalyzer.Prepare(Attribution)).BuildSpokenText().TrimEnd() + " ";
        var source = DisplayText;
        var units = body.Units.Select((unit, index) => index == 0
            ? unit with
            {
                SourceRange = new(0, Attribution.Length + unit.SourceRange.End),
                OriginalText = source[..(Attribution.Length + unit.SourceRange.End)],
                SpokenText = prefix + unit.SpokenText
            }
            : unit with { SourceRange = unit.SourceRange with { Start = unit.SourceRange.Start + Attribution.Length } }).ToArray();
        return body with { OriginalText = source, Units = Array.AsReadOnly(units) };
    }
}
