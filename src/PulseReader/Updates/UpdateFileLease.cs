using System.Security.Cryptography;

namespace PulseReader.Updates;

// The same no-write/no-delete handle remains open from verification through execution.
internal sealed class UpdateFileLease : IDisposable
{
    private readonly UpdatePathSafety.Pins _pins;
    private readonly UpdatePathSafety.LockedFile _file;
    internal Stream Stream => _file.Stream;

    private UpdateFileLease(UpdatePathSafety.Pins pins, UpdatePathSafety.LockedFile file)
    { _pins = pins; _file = file; }

    internal static async Task<UpdateFileLease> OpenAsync(string path, long length, string sha256,
        CancellationToken cancellationToken = default)
    {
        if (length <= 0 || !UpdateHandoffProtocol.IsHash(sha256))
            throw new InvalidDataException("The update package length or checksum is invalid.");
        var pins = new UpdatePathSafety.Pins();
        UpdatePathSafety.LockedFile? file = null;
        try
        {
            pins.Parents(path);
            file = new UpdatePathSafety.LockedFile(path, movable: false);
            if (file.Stream.Length != length) throw new InvalidDataException("The update package length changed.");
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file.Stream, cancellationToken).ConfigureAwait(false));
            file.Stream.Position = 0;
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update package checksum changed.");
            return new(pins, file);
        }
        catch { file?.Dispose(); pins.Dispose(); throw; }
    }

    public void Dispose() { _file.Dispose(); _pins.Dispose(); }
}
