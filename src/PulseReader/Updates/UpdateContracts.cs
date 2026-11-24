using PulseReader.Setup;

namespace PulseReader.Updates;

internal enum UpdatePackageKind { Installer, Portable }
internal enum UpdateMenuMode { Unsupported, Idle, Checking, Available, Downloading, Cancelling, Applying }

internal sealed record UpdateInstallation(
    Version Version,
    string Variant,
    UpdatePackageKind Kind,
    InstallationScope Scope,
    string ApplicationRoot);

internal sealed record UpdateAsset(
    long Id,
    string Name,
    Uri DownloadUri,
    long Length,
    string Sha256);

internal sealed record UpdateOffer(
    long ReleaseId,
    Version Version,
    Uri ReleasePage,
    UpdateAsset Asset,
    string TermsSha256,
    string TermsText);

internal sealed record PreparedUpdate(
    UpdateInstallation Installation,
    UpdateOffer Offer,
    string OperationDirectory,
    string AssetPath);

internal sealed record UpdateCheckResult(UpdateOffer? Offer, DateTimeOffset? RetryAfter = null,
    bool NoRelease = false);

internal sealed record UpdateCheckState(DateTimeOffset? LastAttempt = null,
    DateTimeOffset? LastSuccess = null, DateTimeOffset? RetryAfter = null,
    string? AvailableVersion = null);

internal sealed class UpdateRateLimitException(DateTimeOffset retryAfter)
    : HttpRequestException("GitHub temporarily rate-limited the update request. Please try again later.")
{
    internal DateTimeOffset RetryAfter { get; } = retryAfter;
}

internal static class UpdateConstants
{
    internal const string RepositoryPath = "qdg-main/pulse-reader";
    internal const string ManifestName = "update-manifest.json";
    internal const string ChecksumsName = "SHA256SUMS.txt";
    internal static readonly Uri LatestRelease = new("https://api.github.com/repos/" + RepositoryPath + "/releases/latest");
    internal static readonly Uri ReleasesPage = new("https://github.com/" + RepositoryPath + "/releases");
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    internal const int MaximumMetadataBytes = 1024 * 1024;
    internal const int MaximumChecksumsBytes = 256 * 1024;
    internal const long MaximumInstallerBytes = 512L * 1024 * 1024;
    internal const long MaximumPortableBytes = 1024L * 1024 * 1024;
}
