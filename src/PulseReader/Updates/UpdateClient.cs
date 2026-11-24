using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReader.Setup;

namespace PulseReader.Updates;

internal sealed class UpdateClient : IDisposable
{
    private const int MaximumRedirects = 5;
    private const int MaximumTermsBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _client;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _totalTimeout;
    private readonly Func<DateTimeOffset> _utcNow;

    internal UpdateClient(HttpMessageHandler? handler = null, TimeSpan? headerTimeout = null,
        TimeSpan? readTimeout = null, TimeSpan? totalTimeout = null, Func<DateTimeOffset>? utcNow = null)
    {
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        _headerTimeout = headerTimeout ?? TimeSpan.FromSeconds(30);
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
        _totalTimeout = totalTimeout ?? TimeSpan.FromMinutes(30);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        if (_headerTimeout <= TimeSpan.Zero || _readTimeout <= TimeSpan.Zero || _totalTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(headerTimeout));
    }

    public void Dispose() => _client.Dispose();

    internal async Task<UpdateCheckResult> CheckAsync(UpdateInstallation installation, CancellationToken cancellationToken)
        => await CheckAtAsync(installation, UpdateConstants.LatestRelease, cancellationToken).ConfigureAwait(false);

    private async Task<UpdateCheckResult> CheckAtAsync(UpdateInstallation installation, Uri endpoint,
        CancellationToken cancellationToken)
    {
        ValidateInstallation(installation);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var response = await SendAsync(endpoint, json: true, lifetime.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, NoRelease: true);
            if (IsRateLimit(response)) return new(null, RetryAfter: RetryAfter(response));
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await ReadBytesAsync(response, UpdateConstants.MaximumMetadataBytes,
                lifetime.Token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 16 });
            var root = release.RootElement;
            if (Property(root, "draft").GetBoolean() || Property(root, "prerelease").GetBoolean()) return new(null);
            if (!UpdateVersionPolicy.TryParseStable(Property(root, "tag_name").GetString(), true, out var version)) return new(null);
            if (version! <= UpdateVersionPolicy.Normalize(installation.Version)) return new(null);
            var releaseId = PositiveInt64(Property(root, "id"), "release id");
            var page = ReadUri(Property(root, "html_url"));
            var expectedPage = $"https://github.com/{UpdateConstants.RepositoryPath}/releases/tag/v{version}";
            if (!string.Equals(page.AbsoluteUri, expectedPage, StringComparison.Ordinal))
                throw new InvalidDataException("The update release page belongs to an unexpected location.");
            var assets = Property(root, "assets");
            if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 1000)
                throw new InvalidDataException("The release asset list is invalid or exceeds its limit.");
            var selected = ReadAsset(assets, UpdateVersionPolicy.AssetName(version!, installation.Variant, installation.Kind), version!,
                installation.Kind == UpdatePackageKind.Installer ? UpdateConstants.MaximumInstallerBytes : UpdateConstants.MaximumPortableBytes);
            var sumsAsset = ReadAsset(assets, UpdateConstants.ChecksumsName, version!, UpdateConstants.MaximumChecksumsBytes);
            var manifestAsset = ReadAsset(assets, UpdateConstants.ManifestName, version!, UpdateConstants.MaximumMetadataBytes);
            var sums = ParseChecksums(await DownloadMetadataAsync(sumsAsset, UpdateConstants.MaximumChecksumsBytes, lifetime.Token)
                .ConfigureAwait(false));
            RequireChecksum(sums, selected);
            RequireChecksum(sums, manifestAsset);
            // SHA256SUMS cannot list its own hash. Its GitHub digest authenticates this byte comparison.
            using var manifest = JsonDocument.Parse(await DownloadMetadataAsync(manifestAsset, UpdateConstants.MaximumMetadataBytes,
                lifetime.Token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 8 });
            var terms = ValidateManifest(manifest.RootElement, version!, installation, selected);
            return new(new UpdateOffer(releaseId, version!, page, selected, terms.Hash, terms.Text));
        }
        catch (UpdateRateLimitException exception) { return new(null, RetryAfter: exception.RetryAfter); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The update check timed out. Please retry."); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException
            or DecoderFallbackException or EncoderFallbackException)
        { throw new InvalidDataException("The update release metadata is invalid.", exception); }
    }

    internal async Task<PreparedUpdate> PrepareAsync(UpdateInstallation installation, UpdateOffer offer,
        string operationDirectory, IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        // Re-read the selected release by its immutable numeric identity. Never silently change the target version.
        if (offer.ReleaseId <= 0) throw new InvalidDataException("The update release identity is invalid.");
        var checkedOffer = await CheckAtAsync(installation,
            new Uri($"https://api.github.com/repos/{UpdateConstants.RepositoryPath}/releases/{offer.ReleaseId}"), cancellationToken)
            .ConfigureAwait(false);
        if (checkedOffer.RetryAfter is { } retryAfter) throw new UpdateRateLimitException(retryAfter);
        if (checkedOffer.Offer != offer)
            throw new InvalidDataException("The selected release changed or is no longer available. Check for updates again.");
        var directory = Path.GetFullPath(operationDirectory);
        SetupPaths.AssertSafe(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The update operation directory does not exist.");
        var assetPath = SetupPaths.Child(directory, offer.Asset.Name);
        var partialPath = SetupPaths.Child(directory, "." + Guid.NewGuid().ToString("N") + ".partial");
        var ownsPartial = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(_totalTimeout);
        try
        {
            using var response = await SendAsync(offer.Asset.DownloadUri, json: false, lifetime.Token).ConfigureAwait(false);
            if (IsRateLimit(response)) throw new UpdateRateLimitException(RetryAfter(response));
            response.EnsureSuccessStatusCode();
            AssertResponseLength(response, offer.Asset.Length);
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsPartial = true;
                await CopyVerifiedAsync(response, output, offer.Asset, progress, lifetime.Token).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            SetupPaths.AssertSafe(directory);
            SetupPaths.AssertSafe(partialPath);
            SetupPaths.AssertSafe(assetPath);
            File.Move(partialPath, assetPath, overwrite: false);
            ownsPartial = false;
            progress?.Report(new(offer.Asset.Name, offer.Asset.Length, offer.Asset.Length, "Ready"));
            return new(installation, offer, directory, assetPath);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The update download timed out. Please retry."); }
        finally
        {
            if (ownsPartial)
            {
                SetupPaths.AssertSafe(partialPath);
                File.Delete(partialPath);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool json, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            ValidateTransportUri(uri);
            if (json && (uri.Host != "api.github.com" || uri.Query.Length != 0
                || !uri.AbsolutePath.StartsWith($"/repos/{UpdateConstants.RepositoryPath}/releases/", StringComparison.Ordinal)))
                throw new InvalidDataException("The update API redirected outside the expected repository.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PulseReader/1.0 (release-update-check)");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(json ? "application/vnd.github+json" : "application/octet-stream"));
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_headerTimeout);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)) return response;
            try
            {
                if (redirects >= MaximumRedirects || response.Headers.Location is not { } location)
                    throw new InvalidDataException("The update download exceeded its redirect limit or has no redirect location.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            }
            finally { response.Dispose(); }
        }
    }

    private async Task<byte[]> DownloadMetadataAsync(UpdateAsset asset, int maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(asset.DownloadUri, json: false, cancellationToken).ConfigureAwait(false);
        if (IsRateLimit(response)) throw new UpdateRateLimitException(RetryAfter(response));
        response.EnsureSuccessStatusCode();
        AssertResponseLength(response, asset.Length);
        var bytes = await ReadBytesAsync(response, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != asset.Length || !Hash(bytes).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The update metadata failed integrity verification: {asset.Name}.");
        return bytes;
    }

    private async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("The update metadata exceeds its size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        while (true)
        {
            var count = await ReadAsync(source, bytes, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes) throw new InvalidDataException("The update metadata exceeds its size limit.");
            buffer.Write(bytes, 0, count);
        }
        return buffer.ToArray();
    }

    private async Task CopyVerifiedAsync(HttpResponseMessage response, Stream output, UpdateAsset asset,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        progress?.Report(new(asset.Name, 0, asset.Length, "Downloading"));
        while (true)
        {
            var count = await ReadAsync(source, buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            received += count;
            if (received > asset.Length) throw new InvalidDataException("The update download exceeded its declared size.");
            hash.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            progress?.Report(new(asset.Name, received, asset.Length, "Downloading"));
        }
        progress?.Report(new(asset.Name, received, asset.Length, "Verifying"));
        if (received != asset.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update download failed its length or SHA-256 check.");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ReadAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readTimeout);
        return await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false);
    }

    private static void AssertResponseLength(HttpResponseMessage response, long length)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared != length)
            throw new InvalidDataException("The update response length differs from its release metadata.");
        if (response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("Encoded update assets are unsupported.");
    }

    private static void ValidateInstallation(UpdateInstallation installation)
    {
        _ = UpdateVersionPolicy.Normalize(installation.Version);
        if (!UpdateVersionPolicy.IsVariant(installation.Variant) || !Enum.IsDefined(installation.Kind) || !Enum.IsDefined(installation.Scope))
            throw new InvalidDataException("The update installation identity is unsupported.");
    }

    private static UpdateAsset ReadAsset(JsonElement assets, string name, Version version, long maximumBytes)
    {
        var matches = assets.EnumerateArray().Where(a => Property(a, "name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"The release must contain exactly one {name} asset.");
        var asset = matches[0];
        if (Property(asset, "state").GetString() != "uploaded") throw new InvalidDataException("The update asset is not fully uploaded.");
        var id = PositiveInt64(Property(asset, "id"), "asset id");
        var length = PositiveInt64(Property(asset, "size"), "asset length");
        if (length > maximumBytes) throw new InvalidDataException("The update asset exceeds its size limit.");
        var digest = Property(asset, "digest").GetString();
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsHash(digest[7..]))
            throw new InvalidDataException("The update asset has no valid GitHub SHA-256 digest.");
        var uri = ReadUri(Property(asset, "browser_download_url"));
        var expected = $"https://github.com/{UpdateConstants.RepositoryPath}/releases/download/v{version}/{name}";
        if (uri.AbsoluteUri != expected) throw new InvalidDataException("The update asset URL does not match the expected repository, version and filename.");
        return new(id, name, uri, length, digest[7..].ToLowerInvariant());
    }

    private static (string Hash, string Text) ValidateManifest(JsonElement root, Version version,
        UpdateInstallation installation, UpdateAsset asset)
    {
        RequireFields(root, "SchemaVersion", "Version", "TermsSha256", "TermsText", "Packages");
        if (!Property(root, "SchemaVersion").TryGetInt32(out var schema) || schema != 1
            || Property(root, "Version").GetString() != version.ToString())
            throw new InvalidDataException("The update manifest version or schema is invalid.");
        var text = Property(root, "TermsText").GetString();
        var hash = Property(root, "TermsSha256").GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || text.Length > MaximumTermsBytes || !IsHash(hash))
            throw new InvalidDataException("The update distribution terms are invalid.");
        var bytes = StrictUtf8.GetBytes(text);
        if (bytes.Length > MaximumTermsBytes || !Hash(bytes).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update distribution terms failed their SHA-256 check.");
        var packages = Property(root, "Packages");
        if (packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() != 2)
            throw new InvalidDataException("The update manifest must contain both supported package flavours.");
        var variants = new HashSet<string>(StringComparer.Ordinal);
        var selectedFound = false;
        foreach (var package in packages.EnumerateArray())
        {
            RequireFields(package, "Variant", "Installer", "Portable");
            var variant = Property(package, "Variant").GetString();
            if (!UpdateVersionPolicy.IsVariant(variant) || !variants.Add(variant!))
                throw new InvalidDataException("The update manifest contains an unsupported or duplicate flavour.");
            foreach (var kind in new[] { UpdatePackageKind.Installer, UpdatePackageKind.Portable })
            {
                var entry = Property(package, kind.ToString());
                RequireFields(entry, "Name", "Length", "Sha256");
                var name = Property(entry, "Name").GetString();
                var length = PositiveInt64(Property(entry, "Length"), "manifest package length");
                var sha = Property(entry, "Sha256").GetString();
                if (name != UpdateVersionPolicy.AssetName(version, variant!, kind) || !IsHash(sha)
                    || length > (kind == UpdatePackageKind.Installer ? UpdateConstants.MaximumInstallerBytes : UpdateConstants.MaximumPortableBytes))
                    throw new InvalidDataException("The update manifest package identity is invalid.");
                if (variant == installation.Variant && kind == installation.Kind)
                {
                    if (name != asset.Name || length != asset.Length || !string.Equals(sha, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The update manifest disagrees with the GitHub asset metadata.");
                    selectedFound = true;
                }
            }
        }
        if (!selectedFound) throw new InvalidDataException("The update manifest omits the installed package flavour.");
        return (hash!.ToLowerInvariant(), text);
    }

    private static Dictionary<string, string> ParseChecksums(byte[] bytes)
    {
        var text = StrictUtf8.GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            if (line.Length < 67 || line[64..66] != "  " || !IsHash(line[..64]))
                throw new InvalidDataException("The update checksum list contains an invalid entry.");
            var name = line[66..];
            if (name.Length > 200 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                || name is "." or ".." || !sums.TryAdd(name, line[..64].ToLowerInvariant()))
                throw new InvalidDataException("The update checksum list contains an unsafe or duplicate filename.");
        }
        return sums;
    }

    private static void RequireChecksum(Dictionary<string, string> sums, UpdateAsset asset)
    {
        if (!sums.TryGetValue(asset.Name, out var checksum) || checksum != asset.Sha256)
            throw new InvalidDataException($"The checksum list disagrees with the GitHub digest for {asset.Name}.");
    }

    private static JsonElement Property(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count(p => p.NameEquals(name)) != 1)
            throw new InvalidDataException($"Update metadata must contain exactly one {name} field.");
        return root.GetProperty(name);
    }

    private static void RequireFields(JsonElement root, params string[] fields)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length)
            throw new InvalidDataException("The update manifest contains unexpected or missing fields.");
        foreach (var field in fields) _ = Property(root, field);
    }

    private static long PositiveInt64(JsonElement element, string name)
    {
        if (!element.TryGetInt64(out var value) || value <= 0) throw new InvalidDataException($"The update {name} is invalid.");
        return value;
    }

    private static Uri ReadUri(JsonElement element)
    {
        if (!Uri.TryCreate(element.GetString(), UriKind.Absolute, out var uri))
            throw new InvalidDataException("The update metadata contains an invalid URL.");
        ValidateTransportUri(uri);
        return uri;
    }

    private static void ValidateTransportUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com"
                or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
            throw new InvalidDataException("The update request must use an approved HTTPS GitHub endpoint.");
    }

    private bool IsRateLimit(HttpResponseMessage response) => response.StatusCode == HttpStatusCode.TooManyRequests
        || response.StatusCode == HttpStatusCode.Forbidden;

    private DateTimeOffset RetryAfter(HttpResponseMessage response)
    {
        var now = _utcNow();
        var maximum = now.AddDays(1);
        var retry = now.AddMinutes(1);
        if (response.Headers.RetryAfter?.Delta is { } delta)
            retry = delta <= TimeSpan.Zero ? retry : delta >= TimeSpan.FromDays(1) ? maximum : now + delta;
        else if (response.Headers.RetryAfter?.Date is { } date) retry = date;
        else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var epoch))
        {
            try { retry = DateTimeOffset.FromUnixTimeSeconds(epoch); }
            catch (ArgumentOutOfRangeException) { retry = maximum; }
        }
        return retry < now.AddMinutes(1) ? now.AddMinutes(1) : retry > maximum ? maximum : retry;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
