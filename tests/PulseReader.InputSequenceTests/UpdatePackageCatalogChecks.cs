using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PulseReader.Setup;
using PulseReader.Updates;

// Explicit packaging qualification. Reads a local candidate through the real
// client with a fixture HTTP transport; never publishes or uses the network.
internal static class UpdatePackageCatalogChecks
{
    internal static async Task RunAsync(string candidateRoot)
    {
        var root = Path.GetFullPath(candidateRoot);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, UpdateConstants.ManifestName)));
        if (!UpdateVersionPolicy.TryParseStable(manifest.RootElement.GetProperty("Version").GetString(), false, out var version)
            || version! <= new Version(0, 0, 0)) throw new InvalidDataException("Invalid candidate version.");
        var names = new List<string> { UpdateConstants.ManifestName, UpdateConstants.ChecksumsName };
        foreach (var variant in new[] { "win-x64", "win-x64-framework-dependent" })
        foreach (var kind in new[] { UpdatePackageKind.Installer, UpdatePackageKind.Portable })
            names.Add(UpdateVersionPolicy.AssetName(version!, variant, kind));
        var assets = names.Select((name, index) =>
        {
            var path = Path.Combine(root, name);
            using var file = File.OpenRead(path);
            return new
            {
                id = index + 1L, name, state = "uploaded", size = file.Length,
                digest = "sha256:" + Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(),
                browser_download_url = $"https://github.com/{UpdateConstants.RepositoryPath}/releases/download/v{version}/{name}"
            };
        }).ToArray();
        var release = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = 777L, tag_name = "v" + version, draft = false, prerelease = false,
            html_url = $"https://github.com/{UpdateConstants.RepositoryPath}/releases/tag/v{version}", assets
        });
        using var client = new UpdateClient(new LocalAssets(root, release, names));
        var fixture = Path.Combine(AppContext.BaseDirectory, "update-package-catalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            foreach (var variant in new[] { "win-x64", "win-x64-framework-dependent" })
            foreach (var kind in new[] { UpdatePackageKind.Installer, UpdatePackageKind.Portable })
            {
                var current = new UpdateInstallation(new Version(0, 0, 0), variant, kind, InstallationScope.User, fixture);
                var result = await client.CheckAsync(current, default);
                var offer = result.Offer ?? throw new InvalidDataException("Built candidate was not offered.");
                var directory = Path.Combine(fixture, variant + "-" + kind);
                Directory.CreateDirectory(directory);
                var prepared = await client.PrepareAsync(current, offer, directory, null, default);
                if (new FileInfo(prepared.AssetPath).Length != offer.Asset.Length
                    || offer.Version != version || offer.Asset.Name != UpdateVersionPolicy.AssetName(version!, variant, kind))
                    throw new InvalidDataException("Built package did not retain its exact identity.");
                Console.WriteLine($"PASS: built {variant} {kind} manifest, terms, checksums and package bytes validate through UpdateClient.");
                if ((await client.CheckAsync(current with { Version = version! }, default)).Offer is not null)
                    throw new InvalidDataException("A current-version candidate offered itself as an update.");
            }
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    private sealed class LocalAssets(string root, byte[] release, IReadOnlyCollection<string> names) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(release) });
            var name = Uri.UnescapeDataString(uri.Segments.Last());
            if (uri.Host != "github.com" || !names.Contains(name))
                throw new InvalidDataException("Unexpected package fixture request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(File.OpenRead(Path.Combine(root, name))) });
        }
    }
}
