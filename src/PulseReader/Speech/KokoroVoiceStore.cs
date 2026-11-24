using System.Collections.Concurrent;
using KokoroSharp.Core;
using NumSharp;

namespace PulseReader.Speech;

// NumSharp's path overload requests read/write access. Installed voice assets
// are immutable, so pass an explicitly read-only stream instead.
internal static class KokoroVoiceStore
{
    private static readonly ConcurrentDictionary<string, Lazy<KokoroVoice>> Voices =
        new(StringComparer.OrdinalIgnoreCase);

    internal static KokoroVoice GetVoice(string name)
    {
        if (!KokoroVoiceCatalog.Contains(name)) throw new ArgumentException("Unknown Kokoro voice.", nameof(name));
        var id = KokoroVoiceCatalog.Get(name).Id;
        var load = Voices.GetOrAdd(id, value => new(() => ReadVoiceFile(
            Path.Combine(AppContext.BaseDirectory, "voices", value + ".npy"), value)));
        try { return load.Value; }
        catch
        {
            // A repaired installation can retry instead of caching a failed load.
            ((ICollection<KeyValuePair<string, Lazy<KokoroVoice>>>)Voices).Remove(new(id, load));
            throw;
        }
    }

    internal static KokoroVoice ReadVoiceFile(string path, string name)
    {
        if (!KokoroVoiceCatalog.Contains(name)) throw new ArgumentException("Unknown Kokoro voice.", nameof(name));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("Invalid Kokoro voice file size.");
        var features = np.Load<float[,,]>(stream);
        if (features.GetLength(0) != 510 || features.GetLength(1) != 1 || features.GetLength(2) != 256)
            throw new InvalidDataException("Invalid Kokoro voice embedding dimensions.");
        return new KokoroVoice { Name = KokoroVoiceCatalog.Get(name).Id, Features = features };
    }
}
