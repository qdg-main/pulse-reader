namespace PulseReader.Setup;

internal sealed record SetupProgress(string AssetName, long BytesReceived, long? TotalBytes, string Stage);

internal sealed record SetupAsset(string Name, Uri Uri, string Sha256, long MaximumBytes, long? Length = null);
