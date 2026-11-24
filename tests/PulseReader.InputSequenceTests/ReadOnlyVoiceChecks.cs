using PulseReader.Speech;
using KokoroSharp.Core;
using NumSharp;

internal static class ReadOnlyVoiceChecks
{
    internal static void RunIsolatedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseReaderReadOnlyVoices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var descriptor in KokoroVoiceCatalog.Voices)
            {
                var source = Path.Combine(AppContext.BaseDirectory, "voices", descriptor.Id + ".npy");
                var path = Path.Combine(root, descriptor.Id + ".npy");
                File.Copy(source, path);
                File.SetAttributes(path, FileAttributes.ReadOnly);
                var loaded = KokoroVoiceStore.ReadVoiceFile(path, descriptor.Id);
                var original = KokoroVoice.FromPath(source);
                if (loaded.Name != original.Name || !loaded.Features.Cast<float>().SequenceEqual(original.Features.Cast<float>()))
                    throw new InvalidOperationException("Read-only loading changed voice embeddings: " + descriptor.Id);
                if (!File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(source)))
                    throw new InvalidOperationException("Loading modified the read-only voice file.");
            }
            var invalid = Path.Combine(root, "invalid.npy");
            np.Save(new float[1, 1, 256], invalid);
            Expect<InvalidDataException>(() => KokoroVoiceStore.ReadVoiceFile(invalid, "af_heart"));
            using (var oversized = File.Create(Path.Combine(root, "oversized.npy"))) oversized.SetLength(1024 * 1024 + 1);
            Expect<InvalidDataException>(() => KokoroVoiceStore.ReadVoiceFile(Path.Combine(root, "oversized.npy"), "af_heart"));
            Expect<ArgumentException>(() => KokoroVoiceStore.GetVoice("../af_heart"));
            if (!ReferenceEquals(KokoroVoiceStore.GetVoice("AF_HEART"), KokoroVoiceStore.GetVoice("af_heart")))
                throw new InvalidOperationException("Voice cache did not canonicalize case-insensitive names.");
            Console.WriteLine("PASS: all 54 read-only voices preserve exact embeddings; size, dimensions and names are validated.");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            Directory.Delete(root, recursive: false); // Remove children explicitly before the isolated root.
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    internal static void Run()
    {
        // Run only in a disposable published-payload copy, never an installed app.
        if (Path.GetFileName(AppContext.BaseDirectory.TrimEnd('\\', '/')) != "readonly-speech-probe21")
            throw new InvalidOperationException("This probe requires the fixed disposable published-payload directory.");
        var attributes = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "voices"), "*.npy")
            .ToDictionary(path => path, File.GetAttributes);
        foreach (var pair in attributes) File.SetAttributes(pair.Key, pair.Value | FileAttributes.ReadOnly);
        try
        {
            _ = KokoroSpeechService.CreateVoice(KokoroSpeechSettings.Default);
            _ = KokoroSpeechService.CreateVoice(KokoroSpeechSettings.Default with { BlendEnabled = true });
            Console.WriteLine("PASS: production default and blended voice selection read immutable published assets.");
        }
        finally { foreach (var pair in attributes) File.SetAttributes(pair.Key, pair.Value); }
    }
}
