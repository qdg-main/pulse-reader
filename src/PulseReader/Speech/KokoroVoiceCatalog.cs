namespace PulseReader.Speech;

internal enum KokoroVoiceGender
{
    Female,
    Male
}

internal sealed record KokoroVoiceDescriptor(
    string Id,
    string Name,
    string Language,
    KokoroVoiceGender Gender,
    string SampleText)
{
    public string DisplayName => $"{Name}  ·  {Id}";
}

internal static class KokoroVoiceCatalog
{
    private const string AmericanEnglish = "American English";
    private const string BritishEnglish = "British English";
    private const string Japanese = "Japanese";
    private const string MandarinChinese = "Mandarin Chinese";
    private const string Spanish = "Spanish";
    private const string French = "French";
    private const string Hindi = "Hindi";
    private const string Italian = "Italian";
    private const string BrazilianPortuguese = "Brazilian Portuguese";

    private static readonly string EnglishSample =
        "Hello, this is the {0} voice in Pulse Reader. Select some text and press your shortcut to hear it read aloud.";

    private static readonly IReadOnlyList<KokoroVoiceDescriptor> VoiceList =
    [
        Voice("af_heart", "Heart", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_alloy", "Alloy", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_aoede", "Aoede", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_bella", "Bella", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_jessica", "Jessica", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_kore", "Kore", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_nicole", "Nicole", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_nova", "Nova", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_river", "River", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_sarah", "Sarah", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("af_sky", "Sky", AmericanEnglish, KokoroVoiceGender.Female),
        Voice("am_adam", "Adam", AmericanEnglish, KokoroVoiceGender.Male),
        Voice("am_echo", "Echo", AmericanEnglish, KokoroVoiceGender.Male),
        Voice("am_eric", "Eric", AmericanEnglish, KokoroVoiceGender.Male),
        Voice("am_fenrir", "Fenrir", AmericanEnglish, KokoroVoiceGender.Male),
        Voice("am_liam", "Liam", AmericanEnglish, KokoroVoiceGender.Male),
        Voice("am_michael", "Michael", AmericanEnglish, KokoroVoiceGender.Male),
        Voice("am_onyx", "Onyx", AmericanEnglish, KokoroVoiceGender.Male),
        Voice("am_puck", "Puck", AmericanEnglish, KokoroVoiceGender.Male),
        Voice("am_santa", "Santa", AmericanEnglish, KokoroVoiceGender.Male),

        Voice("bf_alice", "Alice", BritishEnglish, KokoroVoiceGender.Female),
        Voice("bf_emma", "Emma", BritishEnglish, KokoroVoiceGender.Female),
        Voice("bf_isabella", "Isabella", BritishEnglish, KokoroVoiceGender.Female),
        Voice("bf_lily", "Lily", BritishEnglish, KokoroVoiceGender.Female),
        Voice("bm_daniel", "Daniel", BritishEnglish, KokoroVoiceGender.Male),
        Voice("bm_fable", "Fable", BritishEnglish, KokoroVoiceGender.Male),
        Voice("bm_george", "George", BritishEnglish, KokoroVoiceGender.Male),
        Voice("bm_lewis", "Lewis", BritishEnglish, KokoroVoiceGender.Male),

        Voice("jf_alpha", "Alpha", Japanese, KokoroVoiceGender.Female),
        Voice("jf_gongitsune", "Gongitsune", Japanese, KokoroVoiceGender.Female),
        Voice("jf_nezumi", "Nezumi", Japanese, KokoroVoiceGender.Female),
        Voice("jf_tebukuro", "Tebukuro", Japanese, KokoroVoiceGender.Female),
        Voice("jm_kumo", "Kumo", Japanese, KokoroVoiceGender.Male),

        Voice("zf_xiaobei", "Xiaobei", MandarinChinese, KokoroVoiceGender.Female),
        Voice("zf_xiaoni", "Xiaoni", MandarinChinese, KokoroVoiceGender.Female),
        Voice("zf_xiaoxiao", "Xiaoxiao", MandarinChinese, KokoroVoiceGender.Female),
        Voice("zf_xiaoyi", "Xiaoyi", MandarinChinese, KokoroVoiceGender.Female),
        Voice("zm_yunjian", "Yunjian", MandarinChinese, KokoroVoiceGender.Male),
        Voice("zm_yunxi", "Yunxi", MandarinChinese, KokoroVoiceGender.Male),
        Voice("zm_yunxia", "Yunxia", MandarinChinese, KokoroVoiceGender.Male),
        Voice("zm_yunyang", "Yunyang", MandarinChinese, KokoroVoiceGender.Male),

        Voice("ef_dora", "Dora", Spanish, KokoroVoiceGender.Female),
        Voice("em_alex", "Alex", Spanish, KokoroVoiceGender.Male),
        Voice("em_santa", "Santa", Spanish, KokoroVoiceGender.Male),
        Voice("ff_siwis", "Siwis", French, KokoroVoiceGender.Female),
        Voice("hf_alpha", "Alpha", Hindi, KokoroVoiceGender.Female),
        Voice("hf_beta", "Beta", Hindi, KokoroVoiceGender.Female),
        Voice("hm_omega", "Omega", Hindi, KokoroVoiceGender.Male),
        Voice("hm_psi", "Psi", Hindi, KokoroVoiceGender.Male),
        Voice("if_sara", "Sara", Italian, KokoroVoiceGender.Female),
        Voice("im_nicola", "Nicola", Italian, KokoroVoiceGender.Male),
        Voice("pf_dora", "Dora", BrazilianPortuguese, KokoroVoiceGender.Female),
        Voice("pm_alex", "Alex", BrazilianPortuguese, KokoroVoiceGender.Male),
        Voice("pm_santa", "Santa", BrazilianPortuguese, KokoroVoiceGender.Male)
    ];

    private static readonly IReadOnlyDictionary<string, KokoroVoiceDescriptor> ById =
        VoiceList.ToDictionary(voice => voice.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<KokoroVoiceDescriptor> Voices => VoiceList;

    public static IReadOnlyList<string> Languages { get; } =
        VoiceList.Select(voice => voice.Language).Distinct(StringComparer.Ordinal).ToArray();

    public static bool Contains(string? voiceName) =>
        voiceName is not null && ById.ContainsKey(voiceName);

    public static KokoroVoiceDescriptor Get(string voiceName) =>
        ById.TryGetValue(voiceName, out var voice)
            ? voice
            : ById[KokoroSpeechSettings.DefaultVoice];

    public static IReadOnlyList<KokoroVoiceDescriptor> GetByLanguage(string language) =>
        VoiceList.Where(voice => voice.Language == language).ToArray();

    private static KokoroVoiceDescriptor Voice(
        string id,
        string name,
        string language,
        KokoroVoiceGender gender) =>
        new(id, name, language, gender, Sample(language, name));

    private static string Sample(string language, string voiceName) => language switch
    {
        AmericanEnglish or BritishEnglish => string.Format(EnglishSample, voiceName),
        Japanese => "こんにちは。これは Pulse Reader の音声プレビューです。文章を選択し、ショートカットを押すと読み上げます。",
        MandarinChinese => "你好，这是 Pulse Reader 的语音预览。选择一段文字，然后按下快捷键即可朗读。",
        Spanish => "Hola, esta es una vista previa de voz de Pulse Reader. Selecciona un texto y pulsa el atajo para escucharlo.",
        French => "Bonjour, ceci est un aperçu de la voix de Pulse Reader. Sélectionnez un texte et utilisez le raccourci pour l'écouter.",
        Hindi => "नमस्ते, यह Pulse Reader की आवाज़ का नमूना है। कोई पाठ चुनें और उसे सुनने के लिए शॉर्टकट दबाएँ।",
        Italian => "Ciao, questa è un'anteprima della voce di Pulse Reader. Seleziona un testo e premi la scorciatoia per ascoltarlo.",
        BrazilianPortuguese => "Olá, esta é uma prévia da voz do Pulse Reader. Selecione um texto e pressione o atalho para ouvi-lo.",
        _ => string.Format(EnglishSample, voiceName)
    };
}
