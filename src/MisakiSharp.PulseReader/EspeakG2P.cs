namespace MisakiSharp;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

/// <summary> Espeak-backed G2P for the languages misaki has no native frontend for (es, fr-fr, hi, it, pt-br), ported from misaki 0.9.4. </summary>
/// <remarks>
/// Misaki wraps phonemizer's espeak backend (tie'd IPA, punctuation hidden from espeak and restored after) and remaps the output into Kokoro's
/// phoneme conventions -- the voices were trained on THAT output, so raw espeak IPA is not equivalent. Espeak itself stays external: inject a
/// provider turning one punctuation-free chunk into raw tie'd IPA, e.g. <see cref="LibraryProvider"/> (byte-identical to phonemizer's calls).
/// </remarks>
public sealed partial class EspeakG2P {
    static readonly (string, string)[] e2m = [("a^ɪ", "I"), ("a^ʊ", "W"), ("d^z", "ʣ"), ("d^ʒ", "ʤ"), ("e^ɪ", "A"), ("o^ʊ", "O"), ("s^s", "S"), ("t^s", "ʦ"), ("t^ʃ", "ʧ"), ("ɔ^ɪ", "Y"), ("ə^ʊ", "Q")];

    readonly Func<string, string> espeak;

    public EspeakG2P(Func<string, string> espeakProvider) => espeak = espeakProvider;

    /// <summary> Espeak-free instance for "es", "fr-fr", "it", "pt-br", or "hi", backed by <see cref="EspeakReplay"/>. </summary>
    public EspeakG2P(string language) : this(EspeakReplay.Provider(language)) { }

    /// <summary> Converts text to Kokoro-convention IPA. Parens ride through espeak disguised as guillemets, diphthongs/affricates collapse to single chars. </summary>
    public string Phonemize(string text) {
        text = text.Replace('«', '“').Replace('»', '”').Replace('(', '«').Replace(')', '»');
        var ps = PhonemizeWithPunctuation(text, espeak, removeLanguageFlags: true);
        if (ps is null) { return ""; }
        ps = ps.Trim();
        foreach (var (old, neo) in e2m) { ps = ps.Replace(old, neo); }
        return ps.Replace("^", "").Replace("-", "").Replace('«', '(').Replace('»', ')');
    }

    /// <summary> P/Invokes espeak_TextToPhonemes on a shared espeak-ng library -- the exact call phonemizer makes, so output is misaki-byte-exact. </summary>
    /// <remarks> One espeak per process (like phonemizer); providers for different languages share it and switch voices on the fly, under a global lock. </remarks>
    public static Func<string, string> LibraryProvider(string language, string espeakLibraryPath, string espeakDataPath) {
        EspeakLibrary.Load(espeakLibraryPath, espeakDataPath);
        return chunk => EspeakLibrary.TextToPhonemes(language, chunk);
    }

    /// <summary> Spawns espeak-ng per chunk with `--ipa --tie=^`. Portable stopgap when no shared espeak library is around -- the CLI rides the synthesis
    /// path, which stresses lone function words differently from <see cref="LibraryProvider"/> on ~1% of chunks (otherwise byte-identical on 1.52.0). </summary>
    public static Func<string, string> ProcessProvider(string language, string espeakExePath, string espeakDataPath = null) => chunk => {
        var info = new ProcessStartInfo(espeakExePath, $"--ipa --tie=^ -q -b 1 -v {language} --stdin") {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        if (espeakDataPath is not null) { info.EnvironmentVariables["ESPEAK_DATA_PATH"] = espeakDataPath; }
        using var process = Process.Start(info);
        Task.Run(() => { process.StandardInput.Write(chunk + "\n"); process.StandardInput.Close(); });
        return process.StandardOutput.ReadToEnd();
    };

    /// <summary>Retains literal source spans around provider output using Pulse Reader's new implementation.</summary>
    internal static string PhonemizeWithPunctuation(string line, Func<string, string> espeak, bool removeLanguageFlags)
        => PermissivePunctuation.Phonemize(line, espeak, removeLanguageFlags);


    /// <summary> Minimal espeak-ng interop: initialize once per process, then espeak_TextToPhonemes with phonemizer's exact tie'd-IPA mode (0x36182). </summary>
    static class EspeakLibrary {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn(int output, int bufLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string dataPath, int options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetVoiceByNameFn([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetVoiceByPropertiesFn(ref EspeakVoice voice);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr TextToPhonemesFn(ref IntPtr textCursor, int textMode, int phonemeMode);
        [StructLayout(LayoutKind.Sequential)] struct EspeakVoice { public IntPtr Name, Languages, Identifier; public byte Gender, Age, Variant, Xx1; public int Score; public IntPtr Spare; }

        static readonly object gate = new();
        static SetVoiceByNameFn setVoiceByName;
        static SetVoiceByPropertiesFn setVoiceByProperties;
        static TextToPhonemesFn textToPhonemes;
        static string activeVoice;

        internal static void Load(string libraryPath, string dataPath) {
            lock (gate) {
                if (textToPhonemes is not null) { return; }
                var lib = NativeLibrary.Load(libraryPath);
                T Bind<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
                if (Bind<InitializeFn>("espeak_Initialize")(0x02, 0, dataPath, 0) <= 0) { throw new InvalidOperationException($"espeak_Initialize failed for '{libraryPath}'"); }
                (setVoiceByName, setVoiceByProperties, textToPhonemes) = (Bind<SetVoiceByNameFn>("espeak_SetVoiceByName"), Bind<SetVoiceByPropertiesFn>("espeak_SetVoiceByProperties"), Bind<TextToPhonemesFn>("espeak_TextToPhonemes"));
            }
        }

        internal static string TextToPhonemes(string voice, string text) {
            lock (gate) {
                if (voice != activeVoice) { SetVoice(voice); activeVoice = voice; }
                var utf8 = new byte[Encoding.UTF8.GetByteCount(text) + 1];
                Encoding.UTF8.GetBytes(text, utf8);
                var buffer = Marshal.AllocHGlobal(utf8.Length);
                Marshal.Copy(utf8, 0, buffer, utf8.Length);
                var (cursor, parts) = (buffer, new List<string>());
                while (cursor != IntPtr.Zero) {
                    if (Marshal.PtrToStringUTF8(textToPhonemes(ref cursor, 1, 0x02 | 0x01 << 7 | 0x361 << 8)) is { Length: > 0 } phonemes) { parts.Add(phonemes); }
                }
                Marshal.FreeHGlobal(buffer);
                return string.Join(' ', parts);
            }
        }

        /// <summary> Voice codes like "en-gb" aren't direct voice names -- fall back to espeak's language matcher, same route the CLI takes. </summary>
        static void SetVoice(string voice) {
            if (setVoiceByName(voice) == 0) { return; }
            var languages = Marshal.StringToCoTaskMemUTF8(voice);
            var byLanguage = new EspeakVoice() { Languages = languages };
            int status = setVoiceByProperties(ref byLanguage);
            Marshal.FreeCoTaskMem(languages);
            if (status != 0) { throw new ArgumentException($"espeak has no voice for '{voice}'"); }
        }
    }
}

/// <summary> Espeak-backed last resort for English words the lexicon can't resolve, ported from misaki 0.9.4 -- plug into <see cref="EnglishG2P"/>'s espeakFallback. </summary>
public sealed partial class EspeakFallback {
    static readonly (string, string)[] e2m = [
        ("ʔˌn\u0329", "ʔn"), ("ʔn\u0329", "ʔn"), ("a^ɪ", "I"), ("a^ʊ", "W"), ("d^ʒ", "ʤ"), ("e^ɪ", "A"), ("t^ʃ", "ʧ"), ("ɔ^ɪ", "Y"), ("ə^l", "ᵊl"),
        ("ʲo", "jo"), ("ʲə", "jə"), ("e", "A"), ("ʲ", ""), ("ɚ", "əɹ"), ("r", "ɹ"), ("x", "k"), ("ç", "k"), ("ɐ", "ə"), ("ɬ", "l"), ("\u0303", ""),
    ];

    readonly Func<string, string> espeak;
    readonly bool british, flapsToT;

    /// <param name="flapsToT"> Misaki rewrites 'ɾ'->'T' and 'ʔ'->'t' for every model version except 2.0 -- disable for v2 vocabs. </param>
    public EspeakFallback(bool british, Func<string, string> espeakProvider, bool flapsToT = true)
        => (this.british, espeak, this.flapsToT) = (british, espeakProvider, flapsToT);

    /// <summary> Converts one word to misaki-convention IPA, or null when espeak produced nothing (the word then renders as unk). </summary>
    public string Phonemize(string word) {
        var ps = EspeakG2P.PhonemizeWithPunctuation(word, espeak, removeLanguageFlags: false);
        if (ps is null) { return null; }
        ps = ps.Trim();
        foreach (var (old, neo) in e2m) { ps = ps.Replace(old, neo); }
        ps = SyllabicConsonant().Replace(ps, "ᵊ$1").Replace("\u0329", "");
        if (british) { ps = ps.Replace("e^ə", "ɛː").Replace("iə", "ɪə").Replace("ə^ʊ", "Q"); }
        else { ps = ps.Replace("o^ʊ", "O").Replace("ɜːɹ", "ɜɹ").Replace("ɜː", "ɜɹ").Replace("ɪə", "iə").Replace("ː", ""); }
        ps = ps.Replace("o", "ɔ");
        if (flapsToT) { ps = ps.Replace("ɾ", "T").Replace("ʔ", "t"); }
        return ps.Replace("^", "");
    }

    [GeneratedRegex(@"(\S)\u0329")] private static partial Regex SyllabicConsonant();
}
