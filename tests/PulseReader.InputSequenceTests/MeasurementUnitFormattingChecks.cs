using PulseReader.TextPreparation;

internal static class MeasurementUnitFormattingChecks
{
    internal static readonly SpokenTextAbCase[] FeetAndInchesListeningCases =
    [
        new("feet-spaced", "Measurements", "Spaced feet and inches", "The marks should be spoken as feet and inches.", "12' 6\"", "12 feet 6 inches", true, 0),
        new("feet-compact", "Measurements", "Compact feet and inches", "The two quantities must stay separate, not become 126.", "12'6\"", "12 feet 6 inches", true, 0),
        new("feet-inferred", "Measurements", "Missing inch marks in a sentence", "Both compact and spaced shorthand should infer inches.", "He is 6'2, and she is 5' 6.", "He is 6 feet 2 inches, and she is 5 feet 6 inches.", true, 0),
        new("feet-primes", "Measurements", "Typographic marks", "Prime and smart-quote forms should work too.", "He is 6′2″ tall. The board is 1’1” long.", "He is 6 feet 2 inches tall. The board is 1 foot 1 inch long.", true, 0)
    ];

    public static void Run()
    {
        var cases = new (string Id, string Source, string Expected)[]
        {
            ("feet-inches-spaced", "12' 6\"", "12 feet 6 inches"),
            ("feet-inches-compact", "12'6\"", "12 feet 6 inches"),
            ("feet-inches-inferred", "12'6", "12 feet 6 inches"),
            ("feet-inches-inferred-spaced", "12' 6", "12 feet 6 inches"),
            ("height-prose", "I'm 5'11\" tall, and she's 5' 6.", "I'm 5 feet 11 inches tall, and she's 5 feet 6 inches."),
            ("height-primes", "He is 6′2″ tall.", "He is 6 feet 2 inches tall."),
            ("height-smart-quotes", "The board is 1’1” long.", "The board is 1 foot 1 inch long."),
            ("height-decimal-inches", "The height is 5'6.5\".", "The height is 5 feet 6.5 inches."),
            ("height-zero-inches", "The height is 6'0\".", "The height is 6 feet 0 inches."),
            ("preserve-quoted-numbers", "They wrote '12' and \"6\".", "They wrote '12' and \"6\"."),
            ("preserve-apostrophes", "It's the players' room.", "It's the players' room."),
            ("preserve-angle", "51°30′26″ north", "51°30′26″ north"),
            ("preserve-spaced-coordinate", "51° 30′6″ north", "51° 30′6″ north"),
            ("preserve-coordinate-thin-space", "51°\u200930'6\" north", "51°\u200930'6\" north"),
            ("coordinate-and-height", "51° 30′6″ north. He is 6'2\" tall.", "51° 30′6″ north. He is 6 feet 2 inches tall."),
            ("decimal-storage", "The files use 1 MB and 4 GB of storage.", "The files use 1 megabyte and 4 gigabytes of storage."),
            ("binary-storage", "The cache grew from 1 MiB to 4 GiB.", "The cache grew from 1 [mebibyte](/mˈɛbɪbˌIt/) to 4 [gibibytes](/ɡˈɪbɪbˌIts/)."),
            ("data-rates", "The link reaches 100 Mbps and the disk reaches 5 GB/s.", "The link reaches 100 megabits per second and the disk reaches 5 gigabytes per second."),
            ("metric-length", "The gap measures 5 mm, the edge measures 20 cm, and the desk measures 2 m.", "The gap measures 5 millimetres, the edge measures 20 centimetres, and the desk measures 2 metres."),
            ("metric-distance", "The route is 1 km, then another 5 km.", "The route is 1 kilometre, then another 5 kilometres."),
            ("mass-volume", "Add 250 mL to 2 L, then weigh 1 kg and 500 g.", "Add 250 millilitres to 2 litres, then weigh 1 kilogram and 500 grams."),
            ("time", "Wait 5 ns, 20 ms, 1 s, 10 min, and 2 hr.", "Wait 5 nanoseconds, 20 milliseconds, 1 second, 10 minutes, and 2 hours."),
            ("frequency", "The CPU uses 4 GHz while the signal uses 500 MHz.", "The CPU uses 4 gigahertz while the signal uses 500 megahertz."),
            ("electrical", "The supply is 12 V, the current is 2 A, and the power is 2 kW.", "The supply is 12 volts, the current is 2 amps, and the power is 2 kilowatts."),
            ("energy", "The battery stores 1 kWh or 80 kWh.", "The battery stores 1 kilowatt-hour or 80 kilowatt-hours."),
            ("speed", "The limits are 1 mph, 30 mph, and 50 km/h.", "The limits are 1 mile per hour, 30 miles per hour, and 50 kilometres per hour."),
            ("temperature", "The room moved from 1 °C to 20 °C.", "The room moved from 1 degree Celsius to 20 degrees Celsius."),
            ("compact", "The enclosure is 20cm wide and stores 4GB.", "The enclosure is 20 centimetres wide and stores 4 gigabytes."),
            ("attributive", "Copy the 4 GB file after a 5 ms delay using this 12 V adapter.", "Copy the 4 gigabyte file after a 5 millisecond delay using this 12 volt adapter."),
            ("attributive-length", "Use a 20 cm cable, but confirm that it is 20 cm long.", "Use a 20 centimetre cable, but confirm that it is 20 centimetres long."),
            ("decimal-quantities", "The images occupy 0.5 GB and 1.0 GB.", "The images occupy 0.5 gigabytes and 1.0 gigabyte."),
            ("separators-and-compact-rate", "The cache uses 1,024 MB and streams at 500MB/s.", "The cache uses one thousand and twenty-four megabytes and streams at 500 megabytes per second."),
            ("grouped-quantities", "The values are 1,000 MB, 12,345 MB, and 1,024.5 MB.", "The values are 1,000 megabytes, 12,345 megabytes, and one thousand and twenty-four point five megabytes."),
            ("preserve-lbs", "The parcel weighs 2 lbs.", "The parcel weighs 2 lbs."),
            ("preserve-mod", "Install the MOD before launch.", "Install the MOD before launch."),
            ("title-and-unit-control", "Ms Smith approved it.", "Ms. Smith approved it."),
            ("preserve-standalone", "GB and cm are symbols.", "GB and cm are symbols.")
        };

        foreach (var example in cases)
        {
            var actual = SpokenTextFormatter
                .Format(ReadingUnitAnalyzer.Prepare(example.Source))
                .BuildSpokenText();
            Assert(
                string.Equals(example.Expected, actual, StringComparison.Ordinal),
                $"Measurement-unit case '{example.Id}' produced unexpected prepared text."
                + $"{Environment.NewLine}Expected: {example.Expected}"
                + $"{Environment.NewLine}Actual:   {actual}");
        }

        var listeningCases = SpokenTextAbCorpus.LoadMeasurementUnits().Concat(FeetAndInchesListeningCases).ToArray();
        foreach (var example in listeningCases)
        {
            var actual = SpokenTextFormatter
                .Format(ReadingUnitAnalyzer.Prepare(example.OriginalText))
                .BuildSpokenText();
            Assert(
                string.Equals(example.ExpectedPreparedText, actual, StringComparison.Ordinal),
                $"Measurement listening case '{example.Id}' produced unexpected prepared text."
                + $"{Environment.NewLine}Expected: {example.ExpectedPreparedText}"
                + $"{Environment.NewLine}Actual:   {actual}");
        }

        Console.WriteLine(
            $"PASS: measurement-unit preparation matches {cases.Length} regression and "
            + $"{listeningCases.Length} listening cases.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
