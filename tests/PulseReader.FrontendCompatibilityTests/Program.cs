using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

try
{
    if (args.Length != 4) throw new ArgumentException("Expected baseline-directory candidate-directory corpus-directory report-path.");
    var baseline = new Frontend(args[0]);
    var candidate = new Frontend(args[1]);
    var originalHash = "F90EF27EE2D001B15B31270003D2310297847D2B44F7DC2665150F014F9EFE0D";
    if (baseline.Hash != originalHash || candidate.Hash == originalHash) throw new InvalidOperationException("Incorrect baseline/replacement assembly selection.");
    var mismatches = new List<object>();
    var checkedResources = 0;
    foreach (var name in baseline.Misaki.GetManifestResourceNames())
    {
        using var original = baseline.Misaki.GetManifestResourceStream(name)!;
        using var changed = candidate.Misaki.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing resource {name}");
        if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(changed))) throw new InvalidOperationException($"Resource changed: {name}");
        checkedResources++;
    }
    if (checkedResources != 20 || candidate.Misaki.GetManifestResourceNames().Length != 20) throw new InvalidOperationException("Expected twenty unchanged resources.");
    if (candidate.Misaki.GetType("MisakiSharp.EnglishG2P+Num2Words") != null) throw new InvalidOperationException("Original number class remains compiled.");
    var punctuation = candidate.Misaki.GetType("MisakiSharp.EspeakG2P", true)!;
    foreach (var name in new[] { "Preserve", "Restore", "PostprocessChunk", "MarksRegex", "LanguageFlags" })
        if (punctuation.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static) != null) throw new InvalidOperationException($"Removed method still compiled: {name}");
    var texts = new HashSet<string>(StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(args[2], "*.json"))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Collect(document.RootElement, texts);
    }
    texts.UnionWith(new[] { "101 1001 1024 1100 1101 1000001", "The 101st, 1024th, and 1000001st examples.",
        "In 1901, 2001 and 2024, this changed.", "The quantities are 0.01, 1.21, 1024.5 and -0.5.",
        "He said, \"Wait... really?!\" (Then continued.)", "Quendorium, newbrandware, and Xylophonix.",
        "The file is C:\\Users\\ExampleUser\\Documents\\Example Tool.", "Run an A./B comparison of JSON and .NET." });
    var englishChecks = 0;
    foreach (var text in texts)
    foreach (var prepared in new[] { false, true })
    {
        var originalText = prepared ? baseline.Prepare(text) : text;
        var changedText = prepared ? candidate.Prepare(text) : text;
        if (originalText != changedText) mismatches.Add(new { Kind = "Preparation", Input = text, Original = originalText, Candidate = changedText });
        foreach (var language in new[] { "a", "b" })
        {
            Compare(originalText, changedText, language, prepared);
            englishChecks++;
        }
    }
    var multilingualChecks = 0;
    foreach (var (language, samples) in new Dictionary<string, string[]>
    {
        ["e"] = ["¡Hola, mundo! ¿Cómo estás?", "La casa, el árbol y los amigos..."],
        ["f"] = ["Bonjour, les amis! Un petit homme.", "(Bonjour), merci..."],
        ["h"] = ["नमस्ते, दुनिया!", "भारत; भाषा."],
        ["i"] = ["Ciao, mondo!", "Un amico; un altro amico."],
        ["p"] = ["Olá, mundo!", "Os amigos; uma casa."],
        ["j"] = ["こんにちは、世界！", "今日は良い天気です。"],
        ["z"] = ["你好，世界！", "今天的天气很好。"]
    })
    foreach (var text in samples) { Compare(text, text, language, false); multilingualChecks++; }
    var report = new { BaselineDll = baseline.Hash, CandidateDll = candidate.Hash, Resources = checkedResources,
        SourceTexts = texts.Count, EnglishComparisons = englishChecks, MultilingualComparisons = multilingualChecks,
        MismatchCount = mismatches.Count, Mismatches = mismatches };
    File.WriteAllText(args[3], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Frontend compatibility: {englishChecks} English + {multilingualChecks} multilingual comparisons; twenty unchanged resources; {mismatches.Count} mismatches.");
    return mismatches.Count == 0 ? 0 : 1;

    void Compare(string original, string changed, string language, bool prepared)
    {
        var a = baseline.Phonemize(original, language);
        var b = candidate.Phonemize(changed, language);
        if (a != b) mismatches.Add(new { Kind = "Phonemes", Input = original, Language = language, Prepared = prepared, Original = a, Candidate = b });
        var aTokens = baseline.Tokenize(original, language);
        var bTokens = candidate.Tokenize(changed, language);
        if (!aTokens.SequenceEqual(bTokens)) mismatches.Add(new { Kind = "Tokens", Input = original, Language = language, Prepared = prepared, Original = aTokens, Candidate = bTokens });
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static void Collect(JsonElement item, HashSet<string> texts)
{
    if (item.ValueKind == JsonValueKind.Object)
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name is "text" or "originalText" or "expectedPreparedText" && property.Value.ValueKind == JsonValueKind.String)
            { var text = property.Value.GetString(); if (!string.IsNullOrWhiteSpace(text)) texts.Add(text); }
            else Collect(property.Value, texts);
        }
    else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Collect(child, texts);
}

sealed class Frontend
{
    private readonly Assembly app;
    private readonly Type tokenizer;
    public Assembly Misaki { get; }
    public string Hash { get; }
    public Frontend(string directory)
    {
        directory = Path.GetFullPath(directory);
        var context = new IsolatedContext(directory);
        app = context.LoadFromAssemblyPath(Path.Combine(directory, "PulseReader.dll"));
        Misaki = context.LoadFromAssemblyPath(Path.Combine(directory, "MisakiSharp.dll"));
        Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Misaki.Location)));
        tokenizer = context.LoadFromAssemblyPath(Path.Combine(directory, "KokoroSharp.dll")).GetType("KokoroSharp.Processing.Tokenizer", true)!;
    }
    public string Prepare(string text)
    {
        var analyzer = app.GetType("PulseReader.TextPreparation.ReadingUnitAnalyzer", true)!;
        var formatter = app.GetType("PulseReader.TextPreparation.SpokenTextFormatter", true)!;
        var document = analyzer.GetMethod("Prepare")!.Invoke(null, [text])!;
        document = formatter.GetMethod("Format")!.Invoke(null, [document])!;
        return (string)document.GetType().GetMethod("BuildSpokenText")!.Invoke(document, null)!;
    }
    public string Phonemize(string text, string language) => (string)Invoke("Phonemize", text, language)!;
    public int[] Tokenize(string text, string language) => (int[])Invoke("Tokenize", text, language)!;
    private object? Invoke(string name, string text, string language)
    {
        var method = tokenizer.GetMethods().Single(m => m.Name == name && m.GetParameters()[0].ParameterType == typeof(string));
        var parameters = method.GetParameters();
        var values = parameters.Select(p => p.HasDefaultValue ? p.DefaultValue : p.ParameterType == typeof(bool) ? (object)true : null).ToArray();
        values[0] = text;
        if (parameters.Length > 1 && parameters[1].ParameterType == typeof(string)) values[1] = language;
        return method.Invoke(null, values);
    }
}

sealed class IsolatedContext(string directory) : AssemblyLoadContext(isCollectible: false)
{
    protected override Assembly? Load(AssemblyName name)
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}
