using System.Text.Json;
using PulseReader.Setup;
using PulseReader.Updates;

internal static class UpdateLiveNetworkCheck
{
    // Explicit opt-in check. Never part of the offline regression suite and
    // never downloads/installs an application package or changes saved state.
    internal static async Task RunAsync()
    {
        using var client = new UpdateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var identity = new UpdateInstallation(new Version(1, 0, 0), "win-x64",
            UpdatePackageKind.Portable, InstallationScope.User, AppContext.BaseDirectory);
        var result = await client.CheckAsync(identity, deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Endpoint = UpdateConstants.LatestRelease.AbsoluteUri,
            DefaultProductionHttpClient = true, result.NoRelease, result.RetryAfter,
            OfferVersion = result.Offer?.Version.ToString(), Asset = result.Offer?.Asset.Name,
            MetadataValidated = result.Offer is not null,
            PackageDownloaded = false, InstallationChanged = false, Utc = DateTimeOffset.UtcNow,
            Limitation = result.NoRelease
                ? "GitHub returned no public latest release. End-to-end published asset delivery cannot be qualified until a release is available."
                : result.RetryAfter is not null
                    ? "GitHub deferred the request; retry after the reported time."
                    : "This read-only check does not install or download an application package."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
