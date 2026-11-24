using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PulseReader.Setup;
using PulseReader.Updates;

internal static class UpdateCatalogChecks
{
    internal static async Task RunAsync()
    {
        CheckVersions();
        CheckInstallationIdentity();
        await CheckRepositoryOwnershipAsync();
        await CheckCatalogMatrixAsync();
        await CheckNoUpdateAsync();
        await CheckRejectedMetadataAsync();
        await CheckTransportAsync();
        await CheckRateLimitsAsync();
        await CheckPreparationRateLimitsAsync();
        await CheckPreparationAsync();
        await CheckCancellationAndTimeoutsAsync();
        Console.WriteLine("PASS: update installation identity, stable versions, flavour-preserving release catalog, manifest/terms/digest agreement, bounded redirects/downloads, cancellation and rate limits.");
    }

    private static async Task CheckRepositoryOwnershipAsync()
    {
        const string repository = "qdg-main/pulse-reader";
        const string previousRepository = "Mark-A-Richardson/pulse-reader";
        var expectedEndpoint = new Uri($"https://api.github.com/repos/{repository}/releases/latest");
        Require(UpdateConstants.LatestRelease == expectedEndpoint
            && UpdateConstants.ReleasesPage.AbsoluteUri == $"https://github.com/{repository}/releases",
            "The updater still targets a different repository owner.");
        using (var fixture = new CatalogFixture())
        {
            var result = await fixture.Client.CheckAsync(fixture.Installation, default);
            Require(result.Offer is { } offer
                && offer.ReleasePage.AbsoluteUri == $"https://github.com/{repository}/releases/tag/v1.1.0"
                && offer.Asset.DownloadUri.AbsoluteUri == $"https://github.com/{repository}/releases/download/v1.1.0/{fixture.AssetName}"
                && fixture.Requests[0] == expectedEndpoint,
                "The new owner's simulated release did not produce a canonical update offer.");
        }
        foreach (var field in new[] { "release", "asset", "redirect" })
        {
            using var fixture = new CatalogFixture();
            if (field == "release")
                fixture.Release["html_url"] = $"https://github.com/{previousRepository}/releases/tag/v1.1.0";
            else if (field == "asset")
                fixture.SelectedAsset["browser_download_url"] = $"https://github.com/{previousRepository}/releases/download/v1.1.0/{fixture.AssetName}";
            else
                fixture.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(
                    Redirect($"https://api.github.com/repos/{previousRepository}/releases/latest"));
            await ExpectAsync<InvalidDataException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
            Require(fixture.Requests.Count == 1 && fixture.Requests[0] == expectedEndpoint,
                "The updater followed the previous owner's metadata or API redirect.");
        }
    }

    private static void CheckVersions()
    {
        foreach (var text in new[] { "v1.0.0", "v2.10.123", "v0.0.1" })
            Require(UpdateVersionPolicy.TryParseStable(text, true, out _), "A supported stable tag was rejected.");
        foreach (var text in new[] { "1.0.0", "v1.0", "v01.0.0", "v1.01.0", "v1.0.01", "v1.0.0-beta", "v1.0.0+build",
            "v1.0.0.0", "V1.0.0", "v2147483648.0.0", " v1.0.0", "v1.0.0\n", "v-1.0.0" })
            Require(!UpdateVersionPolicy.TryParseStable(text, true, out _), "An ambiguous, overflow or unstable tag was accepted: " + text);
        Require(UpdateVersionPolicy.Normalize(new Version(1, 0, 0, 0)) == new Version(1, 0, 0),
            "Assembly revision zero did not normalize to the public release version.");
        Expect<InvalidDataException>(() => UpdateVersionPolicy.Normalize(new Version(1, 0, 0, 1)));
    }

    private static void CheckInstallationIdentity()
    {
        using var root = new DirectoryFixture();
        var user = Path.Combine(root.Root, "User");
        var machine = Path.Combine(root.Root, "ProgramFiles");
        var portable = Path.Combine(root.Root, "Portable");
        Directory.CreateDirectory(portable);
        bool Read(string path, out UpdateInstallation? installation) => UpdateInstallationDiscovery.TryRead(path,
            new Version(1, 0, 0, 0), out installation, out _, user, machine);
        Require(!Read(portable, out _), "A development directory enabled automatic updates.");
        foreach (var variant in new[] { "win-x64", "win-x64-framework-dependent" })
        {
            WriteInventory(portable, variant);
            Require(Read(portable, out var found) && found!.Kind == UpdatePackageKind.Portable
                && found.Scope == InstallationScope.User && found.Variant == variant,
                "A portable package lost its flavour or was classified as installed.");
            foreach (var scope in new[] { "user", "machine" })
            {
                var installed = scope == "user" ? Path.Combine(user, "Programs", "PulseReader") : Path.Combine(machine, "PulseReader");
                Directory.CreateDirectory(installed);
                WriteInventory(installed, variant);
                WriteScope(installed, scope);
                Require(!Read(installed, out _), "An installation with no uninstaller enabled updates.");
                File.WriteAllBytes(Path.Combine(installed, "unins000.exe"), [0x4d, 0x5a]);
                Require(!Read(installed, out _), "An installation missing uninstaller data enabled updates.");
                File.WriteAllBytes(Path.Combine(installed, "unins000.dat"), [1]);
                Require(Read(installed, out found) && found!.Kind == UpdatePackageKind.Installer
                    && found.Scope == (scope == "user" ? InstallationScope.User : InstallationScope.Machine),
                    "A canonical installation did not retain its scope.");
                File.Delete(Path.Combine(installed, "unins000.exe"));
                File.Delete(Path.Combine(installed, "unins000.dat"));
            }
        }
        WriteScope(portable, "user");
        File.WriteAllBytes(Path.Combine(portable, "unins000.exe"), [0x4d, 0x5a]);
        File.WriteAllBytes(Path.Combine(portable, "unins000.dat"), [1]);
        Require(!Read(portable, out _), "A copied user installation marker bypassed canonical-location validation.");
        WriteScope(portable, "machine");
        Require(!Read(portable, out _), "A copied machine marker bypassed canonical-location validation.");
        foreach (var marker in new[] { "{}", "[]", "{\"SchemaVersion\":1,\"Scope\":\"user\",\"Scope\":\"user\"}",
            "{\"SchemaVersion\":1,\"Scope\":\"user\",\"Other\":1}", new string(' ', 257) })
        {
            File.WriteAllText(Path.Combine(portable, InstallationLayout.ScopeFileName), marker);
            Require(!Read(portable, out _), "Invalid scope metadata enabled updates.");
        }
        File.Delete(Path.Combine(portable, InstallationLayout.ScopeFileName));
        foreach (var inventory in new[] { "[]", "{}", "{\"SchemaVersion\":1,\"AppVersion\":\"1.0.0\",\"Variant\":\"win-arm64\"}",
            "{\"SchemaVersion\":1,\"AppVersion\":\"1.0.1\",\"Variant\":\"win-x64\"}",
            "{\"SchemaVersion\":1,\"AppVersion\":\"1.0.0\",\"Variant\":\"win-x64\",\"Variant\":\"win-x64\"}",
            new string(' ', UpdateConstants.MaximumMetadataBytes + 1) })
        {
            File.WriteAllText(Path.Combine(portable, "dependency-inventory.json"), inventory);
            Require(!Read(portable, out _), "Invalid inventory metadata enabled updates.");
        }
        WriteInventory(portable, "win-x64");
        using (var locked = new FileStream(Path.Combine(portable, "dependency-inventory.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Require(!Read(portable, out _), "Unreadable installation metadata was accepted.");
        var inventoryPath = Path.Combine(portable, "dependency-inventory.json");
        File.Delete(inventoryPath);
        Directory.CreateDirectory(inventoryPath);
        Require(!Read(portable, out _), "A metadata directory was accepted as a regular file.");
    }

    private static void WriteInventory(string path, string variant) => File.WriteAllText(Path.Combine(path, "dependency-inventory.json"),
        JsonSerializer.Serialize(new { SchemaVersion = 1, AppVersion = "1.0.0", Variant = variant }));
    private static void WriteScope(string path, string scope) => File.WriteAllText(Path.Combine(path, InstallationLayout.ScopeFileName),
        JsonSerializer.Serialize(new { SchemaVersion = 1, Scope = scope }));

    private static async Task CheckCatalogMatrixAsync()
    {
        foreach (var variant in new[] { "win-x64", "win-x64-framework-dependent" })
        foreach (var kind in new[] { UpdatePackageKind.Installer, UpdatePackageKind.Portable })
        {
            using var fixture = new CatalogFixture();
            var installation = fixture.Installation with { Variant = variant, Kind = kind };
            var result = await fixture.Client.CheckAsync(installation, default);
            Require(result.Offer is { } offer && offer.Version == new Version(1, 1, 0)
                && offer.Asset.Name == UpdateVersionPolicy.AssetName(offer.Version, variant, kind)
                && offer.TermsText == CatalogFixture.Terms && offer.TermsSha256 == Hash(Encoding.UTF8.GetBytes(CatalogFixture.Terms)),
                "The update catalog did not select the precise package and verified terms.");
            Require(fixture.Requests.Count == 3 && fixture.Requests[0] == UpdateConstants.LatestRelease,
                "Checking for updates downloaded application assets or used an unexpected catalog endpoint.");
        }
    }

    private static async Task CheckNoUpdateAsync()
    {
        foreach (var version in new[] { "1.0.0", "0.9.9", "1.0.0-beta", "1.0", "01.0.0", "2147483648.0.0" })
        {
            using var fixture = new CatalogFixture();
            fixture.Release["tag_name"] = "v" + version;
            Require((await fixture.Client.CheckAsync(fixture.Installation, default)).Offer is null && fixture.Requests.Count == 1,
                "Same, older or unsupported versions generated a false update.");
        }
        foreach (var flag in new[] { "draft", "prerelease" })
        {
            using var fixture = new CatalogFixture();
            fixture.Release[flag] = true;
            Require((await fixture.Client.CheckAsync(fixture.Installation, default)).Offer is null && fixture.Requests.Count == 1,
                "A draft or prerelease was offered.");
        }
        using (var fixture = new CatalogFixture())
        {
            fixture.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.NotFound));
            var result = await fixture.Client.CheckAsync(fixture.Installation, default);
            Require(result.NoRelease && result.Offer is null, "A not-yet-published release was not handled quietly.");
        }
    }

    private static async Task CheckRejectedMetadataAsync()
    {
        var releaseChanges = new Action<CatalogFixture>[]
        {
            f => f.Release["html_url"] = "https://github.com/Other/repo/releases/tag/v1.1.0",
            f => f.Release["id"] = 0,
            f => f.SelectedAsset["digest"] = null,
            f => f.SelectedAsset["digest"] = "sha256:ABC",
            f => f.SelectedAsset["digest"] = "md5:" + new string('a', 64),
            f => f.SelectedAsset["size"] = 0,
            f => f.SelectedAsset["size"] = UpdateConstants.MaximumInstallerBytes + 1,
            f => f.SelectedAsset["state"] = "new",
            f => f.SelectedAsset["browser_download_url"] = "https://github.com/Other/repo/releases/download/v1.1.0/" + f.AssetName,
            f => f.SelectedAsset["browser_download_url"] = "https://github.com/" + UpdateConstants.RepositoryPath + "/archive/refs/tags/v1.1.0.zip",
            f => f.SelectedAsset["browser_download_url"] = f.SelectedAsset["browser_download_url"]!.GetValue<string>() + "?replacement=true",
            f => f.SelectedAsset["name"] = "Source.zip",
            f => ((JsonArray)f.Release["assets"]!).Add(f.SelectedAsset.DeepClone()),
            f => f.Release["assets"] = new JsonObject(),
            f => f.RawRelease = f.Release.ToJsonString().Replace("\"id\":77", "\"id\":77,\"id\":78", StringComparison.Ordinal),
            f => f.RawRelease = "{bad json"
        };
        foreach (var change in releaseChanges)
        {
            using var fixture = new CatalogFixture();
            change(fixture);
            await ExpectAsync<InvalidDataException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
        }
        var manifestChanges = new Action<CatalogFixture>[]
        {
            f => f.Manifest["SchemaVersion"] = 2,
            f => f.Manifest["Version"] = "1.2.0",
            f => f.Manifest["TermsSha256"] = new string('0', 64),
            f => f.Manifest["TermsText"] = "Changed without its hash",
            f => f.Manifest["TermsText"] = "",
            f => f.Manifest["TermsText"] = new string('a', 256 * 1024 + 1),
            f => f.Manifest["Extra"] = true,
            f => ((JsonArray)f.Manifest["Packages"]!).RemoveAt(1),
            f => f.ManifestPackage["Variant"] = "win-arm64",
            f => ((JsonArray)f.Manifest["Packages"]!)[1]!["Variant"] = "win-x64",
            f => f.ManifestPackage["Installer"]!["Length"] = 1,
            f => f.ManifestPackage["Installer"]!["Sha256"] = new string('0', 64),
            f => f.ManifestPackage["Installer"]!["Name"] = "../unsafe.exe"
        };
        foreach (var change in manifestChanges)
        {
            using var fixture = new CatalogFixture();
            change(fixture);
            fixture.RefreshMetadata();
            await ExpectAsync<InvalidDataException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
        }
        foreach (var corrupt in new[] { UpdateConstants.ManifestName, UpdateConstants.ChecksumsName })
        {
            using var fixture = new CatalogFixture();
            fixture.Bodies[corrupt] = Encoding.UTF8.GetBytes("corrupt download");
            await ExpectAsync<InvalidDataException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
        }
        foreach (var entry in new[] { new string('0', 64) + "  {asset}\n", "invalid entry\n", "{valid}{valid}",
            Hash([1]) + "  ../escape.exe\n{valid}" })
        {
            using var fixture = new CatalogFixture();
            var valid = Hash(fixture.Bodies[fixture.AssetName]) + "  " + fixture.AssetName + "\n"
                + Hash(fixture.Bodies[UpdateConstants.ManifestName]) + "  " + UpdateConstants.ManifestName + "\n";
            fixture.Bodies[UpdateConstants.ChecksumsName] = Encoding.UTF8.GetBytes(entry.Replace("{asset}", fixture.AssetName)
                .Replace("{valid}", valid));
            fixture.RefreshChecksumsAsset();
            await ExpectAsync<InvalidDataException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
        }
    }

    private static async Task CheckTransportAsync()
    {
        foreach (var destination in new[] { "http://github.com/asset", "https://github.com.evil.invalid/asset",
            "https://user@github.com/asset", "https://github.com:444/asset", "https://127.0.0.1/asset",
            "https://release-assets.githubusercontent.com/asset#fragment", "https://unknown.githubusercontent.com/asset" })
        {
            using var fixture = new CatalogFixture();
            fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "github.com"
                ? Redirect(destination) : null);
            await ExpectAsync<InvalidDataException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
            Require(fixture.Requests.All(uri => uri.AbsoluteUri != destination), "An unapproved redirect was requested before validation.");
        }
        using (var fixture = new CatalogFixture())
        {
            fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "api.github.com"
                ? Redirect("https://api.github.com/repos/Other/repo/releases/latest") : null);
            await ExpectAsync<InvalidDataException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
        }
        using (var fixture = new CatalogFixture())
        {
            fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "github.com"
                ? Redirect("https://github.com/loop") : null);
            await ExpectAsync<InvalidDataException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
            Require(fixture.Requests.Count == 7, "Redirect loops did not stop after five followed redirects.");
        }
        foreach (var host in new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" })
        {
            using var fixture = new CatalogFixture();
            fixture.Override = (request, _) =>
            {
                var name = Uri.UnescapeDataString(request.RequestUri!.Segments.Last());
                return Task.FromResult<HttpResponseMessage?>(request.RequestUri.Host == "github.com"
                    ? Redirect("https://" + host + "/" + name + "?signed=query")
                    : request.RequestUri.Host == host ? Bytes(fixture.Bodies[name]) : null);
            };
            Require((await fixture.Client.CheckAsync(fixture.Installation, default)).Offer is not null,
                "An approved CDN redirect with signed query failed.");
        }
        foreach (var contentLength in new[] { true, false })
        {
            using var fixture = new CatalogFixture();
            fixture.Override = (_, _) =>
            {
                HttpContent content = contentLength ? new ByteArrayContent(new byte[UpdateConstants.MaximumMetadataBytes + 1])
                    : new StreamContent(new NonSeekStream(new byte[UpdateConstants.MaximumMetadataBytes + 1]));
                if (!contentLength) content.Headers.ContentLength = null;
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = content });
            };
            await ExpectAsync<InvalidDataException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
        }
    }

    private static async Task CheckRateLimitsAsync()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        foreach (var code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        foreach (var delay in new[] { 1, 3600, 172800 })
        {
            using var fixture = new CatalogFixture(now: now);
            fixture.Override = (_, _) =>
            {
                var response = new HttpResponseMessage(code);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(delay));
                return Task.FromResult<HttpResponseMessage?>(response);
            };
            var result = await fixture.Client.CheckAsync(fixture.Installation, default);
            Require(result.Offer is null && result.RetryAfter == now.AddSeconds(Math.Clamp(delay, 60, 86400)),
                "Rate-limit retry scheduling was missing or unbounded.");
        }
        using (var fixture = new CatalogFixture(now: now))
        {
            fixture.Override = (request, _) =>
            {
                if (request.RequestUri!.Host != "github.com") return Task.FromResult<HttpResponseMessage?>(null);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.Add("X-RateLimit-Reset", now.AddHours(2).ToUnixTimeSeconds().ToString());
                return Task.FromResult<HttpResponseMessage?>(response);
            };
            Require((await fixture.Client.CheckAsync(fixture.Installation, default)).RetryAfter == now.AddHours(2),
                "Rate limiting during checksum download did not propagate to the caller.");
        }
    }

    private static async Task CheckPreparationRateLimitsAsync()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        foreach (var stage in new[] { "release", UpdateConstants.ChecksumsName, UpdateConstants.ManifestName, "package" })
        foreach (var delay in new[] { 1, 3600, 172800 })
        {
            using var fixture = new CatalogFixture(now: now);
            using var operation = new DirectoryFixture();
            var unrelated = Path.Combine(operation.Root, "unrelated.partial");
            File.WriteAllText(unrelated, "preserve this fixture");
            var offer = (await fixture.Client.CheckAsync(fixture.Installation, default)).Offer!;
            fixture.Override = (request, _) =>
            {
                var limited = stage == "release" ? request.RequestUri!.Host == "api.github.com"
                    : request.RequestUri!.AbsolutePath.EndsWith("/" + (stage == "package" ? fixture.AssetName : stage), StringComparison.Ordinal);
                if (!limited) return Task.FromResult<HttpResponseMessage?>(null);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(delay));
                return Task.FromResult<HttpResponseMessage?>(response);
            };
            UpdateRateLimitException? limitedException = null;
            try { await fixture.Client.PrepareAsync(fixture.Installation, offer, operation.Root, null, default); }
            catch (UpdateRateLimitException exception) { limitedException = exception; }
            Require(limitedException?.RetryAfter == now.AddSeconds(Math.Clamp(delay, 60, 86400)),
                "Preparation lost typed, bounded retry information during " + stage + " rate limiting.");
            Require(Directory.GetFiles(operation.Root).SequenceEqual([unrelated]) && File.ReadAllText(unrelated) == "preserve this fixture",
                "Rate-limited preparation left a partial file or changed unrelated operation data.");
        }
    }

    private static async Task CheckPreparationAsync()
    {
        foreach (var kind in new[] { UpdatePackageKind.Installer, UpdatePackageKind.Portable })
        {
            using var fixture = new CatalogFixture();
            using var operation = new DirectoryFixture();
            var installation = fixture.Installation with { Kind = kind };
            var offer = (await fixture.Client.CheckAsync(installation, default)).Offer!;
            var prepared = await fixture.Client.PrepareAsync(installation, offer, operation.Root, null, default);
            Require(File.ReadAllBytes(prepared.AssetPath).SequenceEqual(fixture.Bodies[offer.Asset.Name])
                && Directory.GetFiles(operation.Root).Length == 1
                && fixture.Requests.Any(uri => uri.AbsolutePath.EndsWith("/releases/77", StringComparison.Ordinal)),
                "Preparation did not verify and stage the frozen release asset.");
            await ExpectAsync<IOException>(() => fixture.Client.PrepareAsync(installation, offer, operation.Root, null, default));
            Require(File.ReadAllBytes(prepared.AssetPath).SequenceEqual(fixture.Bodies[offer.Asset.Name])
                && Directory.GetFiles(operation.Root).Length == 1, "Preparation overwrote an existing staged asset or retained its own partial file.");
        }
        foreach (var failure in new[] { "hash", "short", "long", "header", "encoding", "changed", "cancel" })
        {
            using var fixture = new CatalogFixture();
            using var operation = new DirectoryFixture();
            File.WriteAllText(Path.Combine(operation.Root, "unrelated.partial"), "preserve this fixture");
            var offer = (await fixture.Client.CheckAsync(fixture.Installation, default)).Offer!;
            fixture.Override = (request, _) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/" + fixture.AssetName, StringComparison.Ordinal))
                    return Task.FromResult<HttpResponseMessage?>(null);
                var data = fixture.Bodies[fixture.AssetName];
                var response = Bytes(failure switch
                {
                    "hash" => data.Select(b => (byte)(b ^ 1)).ToArray(),
                    "short" => data[..^1],
                    "long" => [.. data, 1],
                    _ => data
                });
                if (failure is "short" or "long")
                    response.Content = new StreamContent(new NonSeekStream(failure == "short" ? data[..^1] : [.. data, 1]));
                if (failure == "header") response.Content.Headers.ContentLength = data.Length + 1;
                if (failure == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
                return Task.FromResult<HttpResponseMessage?>(response);
            };
            if (failure == "changed") fixture.Release["id"] = 78;
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(value => { if (failure == "cancel" && value.BytesReceived > 0) cancellation.Cancel(); });
            if (failure == "cancel")
                await ExpectAsync<OperationCanceledException>(() => fixture.Client.PrepareAsync(fixture.Installation, offer,
                    operation.Root, progress, cancellation.Token));
            else
                await ExpectAsync<InvalidDataException>(() => fixture.Client.PrepareAsync(fixture.Installation, offer,
                    operation.Root, progress, default));
            Require(Directory.GetFiles(operation.Root).Select(Path.GetFileName).SequenceEqual(["unrelated.partial"]),
                "Failed preparation retained its partial file or deleted unrelated operation data.");
        }
    }

    private static async Task CheckCancellationAndTimeoutsAsync()
    {
        using (var fixture = new CatalogFixture(headerTimeout: TimeSpan.FromMilliseconds(50)))
        {
            fixture.Override = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; };
            await ExpectAsync<TimeoutException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
        }
        using (var fixture = new CatalogFixture(readTimeout: TimeSpan.FromMilliseconds(50)))
        {
            fixture.Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
            await ExpectAsync<TimeoutException>(() => fixture.Client.CheckAsync(fixture.Installation, default));
        }
        using (var fixture = new CatalogFixture())
        using (var cancellation = new CancellationTokenSource())
        {
            fixture.Override = async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return null; };
            await ExpectAsync<OperationCanceledException>(() => fixture.Client.CheckAsync(fixture.Installation, cancellation.Token));
        }
        using (var fixture = new CatalogFixture(totalTimeout: TimeSpan.FromMilliseconds(50)))
        using (var operation = new DirectoryFixture())
        {
            var offer = (await fixture.Client.CheckAsync(fixture.Installation, default)).Offer!;
            fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/" + fixture.AssetName,
                StringComparison.Ordinal) ? new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) } : null);
            await ExpectAsync<TimeoutException>(() => fixture.Client.PrepareAsync(fixture.Installation, offer, operation.Root, null, default));
            Require(Directory.GetFiles(operation.Root).Length == 0, "Timed-out preparation retained a partial file.");
        }
    }

    private static HttpResponseMessage Redirect(string destination)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(destination);
        return response;
    }
    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class CatalogFixture : IDisposable
    {
        internal const string Terms = "Complete distribution terms fixture.\nSecond line stays visible.\n";
        internal UpdateInstallation Installation { get; } = new(new Version(1, 0, 0), "win-x64", UpdatePackageKind.Installer,
            InstallationScope.User, "C:\\fixture\\PulseReader");
        internal string AssetName => UpdateVersionPolicy.AssetName(new Version(1, 1, 0), Installation.Variant, Installation.Kind);
        internal Dictionary<string, byte[]> Bodies { get; } = new(StringComparer.Ordinal);
        internal JsonObject Release { get; }
        internal JsonObject Manifest { get; }
        internal JsonObject SelectedAsset => (JsonObject)((JsonArray)Release["assets"]!).Single(a => a!["name"]!.GetValue<string>() == AssetName)!;
        internal JsonObject ManifestPackage => (JsonObject)((JsonArray)Manifest["Packages"]!)[0]!;
        internal string? RawRelease { get; set; }
        internal List<Uri> Requests { get; } = [];
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
        internal UpdateClient Client { get; }

        internal CatalogFixture(TimeSpan? headerTimeout = null, TimeSpan? readTimeout = null, TimeSpan? totalTimeout = null,
            DateTimeOffset? now = null)
        {
            var packages = new JsonArray();
            foreach (var variant in new[] { "win-x64", "win-x64-framework-dependent" })
            {
                var package = new JsonObject { ["Variant"] = variant };
                foreach (var kind in new[] { UpdatePackageKind.Installer, UpdatePackageKind.Portable })
                {
                    var name = UpdateVersionPolicy.AssetName(new Version(1, 1, 0), variant, kind);
                    Bodies[name] = Encoding.UTF8.GetBytes("fixture payload bytes: " + name);
                    package[kind.ToString()] = new JsonObject { ["Name"] = name, ["Length"] = Bodies[name].Length, ["Sha256"] = Hash(Bodies[name]) };
                }
                packages.Add(package);
            }
            Manifest = new JsonObject { ["SchemaVersion"] = 1, ["Version"] = "1.1.0", ["TermsSha256"] = Hash(Encoding.UTF8.GetBytes(Terms)),
                ["TermsText"] = Terms, ["Packages"] = packages };
            Release = new JsonObject { ["id"] = 77, ["tag_name"] = "v1.1.0", ["draft"] = false, ["prerelease"] = false,
                ["html_url"] = "https://github.com/" + UpdateConstants.RepositoryPath + "/releases/tag/v1.1.0", ["assets"] = new JsonArray() };
            RefreshMetadata();
            Client = new UpdateClient(new Handler(async (request, token) =>
            {
                Requests.Add(request.RequestUri!);
                Require(request.Headers.Authorization is null && request.Headers.Contains("X-GitHub-Api-Version")
                    && request.Headers.GetValues("X-GitHub-Api-Version").Single() == "2026-03-10" && request.Headers.UserAgent.Count > 0,
                    "Update transport used authentication or omitted its frozen API/User-Agent headers.");
                if (Override is not null && await Override(request, token) is { } overridden) return overridden;
                if (request.RequestUri!.Host == "api.github.com") return Bytes(Encoding.UTF8.GetBytes(RawRelease ?? Release.ToJsonString()));
                var name = Uri.UnescapeDataString(request.RequestUri.Segments.Last());
                return Bodies.TryGetValue(name, out var data) ? Bytes(data) : new(HttpStatusCode.NotFound);
            }), headerTimeout, readTimeout, totalTimeout, now is { } date ? () => date : null);
        }

        internal void RefreshMetadata()
        {
            Bodies[UpdateConstants.ManifestName] = Encoding.UTF8.GetBytes(Manifest.ToJsonString());
            Bodies[UpdateConstants.ChecksumsName] = Encoding.UTF8.GetBytes(string.Join("\n", Bodies.Where(pair => pair.Key != UpdateConstants.ChecksumsName)
                .Select(pair => Hash(pair.Value) + "  " + pair.Key)) + "\n");
            var assets = (JsonArray)Release["assets"]!;
            assets.Clear();
            var id = 100L;
            foreach (var pair in Bodies)
                assets.Add(Metadata(pair.Key, pair.Value, ++id));
        }

        internal void RefreshChecksumsAsset()
        {
            var assets = (JsonArray)Release["assets"]!;
            var old = assets.Single(a => a!["name"]!.GetValue<string>() == UpdateConstants.ChecksumsName)!;
            assets[assets.IndexOf(old)] = Metadata(UpdateConstants.ChecksumsName, Bodies[UpdateConstants.ChecksumsName], old["id"]!.GetValue<long>());
        }

        private static JsonObject Metadata(string name, byte[] bytes, long id) => new()
        {
            ["id"] = id, ["name"] = name, ["state"] = "uploaded", ["size"] = bytes.Length, ["digest"] = "sha256:" + Hash(bytes),
            ["browser_download_url"] = "https://github.com/" + UpdateConstants.RepositoryPath + "/releases/download/v1.1.0/" + name
        };
        public void Dispose() => Client.Dispose();
    }

    private sealed class DirectoryFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "update-catalog-fixtures", Guid.NewGuid().ToString("N"));
        internal DirectoryFixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class InlineProgress(Action<SetupProgress> report) : IProgress<SetupProgress>
    { public void Report(SetupProgress value) => report(value); }
    private sealed class NonSeekStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
