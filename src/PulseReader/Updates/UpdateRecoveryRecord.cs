using System.Text.Json;

namespace PulseReader.Updates;

// A retained capability is issued only after the live authenticated handoff succeeds.
// It is local recovery authority, not download authority or an arbitrary destination.
internal sealed record UpdateRecoveryRecord(string Nonce, UpdateHandoffPacket Packet)
{
    internal static UpdateRecoveryRecord Create(string operation, UpdateHandoffPacket packet)
    {
        var record = new UpdateRecoveryRecord(Guid.NewGuid().ToString("N"), packet);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > UpdateConstants.MaximumMetadataBytes) throw new InvalidDataException("Recovery metadata exceeds its limit.");
        using var pins = new UpdatePathSafety.Pins();
        var path = Path.Combine(operation, "recovery.json");
        pins.Parents(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
        return record;
    }

    internal static UpdateRecoveryRecord Read(Stream input, string nonce)
    {
        if (input.Length is <= 0 or > UpdateConstants.MaximumMetadataBytes)
            throw new InvalidDataException("Recovery metadata exceeds its limit.");
        var record = JsonSerializer.Deserialize<UpdateRecoveryRecord>(input, new JsonSerializerOptions { MaxDepth = 16 })
            ?? throw new InvalidDataException("Invalid recovery metadata.");
        if (!Guid.TryParseExact(record.Nonce, "N", out _) || !record.Nonce.Equals(nonce, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The retained portable recovery capability is invalid.");
        return record;
    }
}
