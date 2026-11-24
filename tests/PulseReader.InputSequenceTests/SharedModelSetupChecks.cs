using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReader.Setup;
using PulseReader.Speech;

internal static class SharedModelSetupChecks
{
    internal static async Task RunAsync()
    {
        CheckScopeMarkers();
        CheckGenericPermissionBits();
        CheckUnavailableInspection();
        await CheckSplitSharedStorageAsync();
        await CheckTransportAsync(false);
        await CheckTransportAsync(true);
        await CheckFramingAsync();
        Assert(await SharedModelSetup.RunHelperAsync("kokoro", Guid.NewGuid().ToString("N")) != 0,
            "A development executable accepted shared setup.");
        Console.WriteLine("PASS: installation scope, split shared storage, bounded IPC and disconnect cancellation.");
    }

    private static void CheckGenericPermissionBits()
    {
        var security = new System.Security.AccessControl.DirectorySecurity();
        // AccessControl APIs retain generic rights in raw inherited ACEs. They
        // must not be mistaken for read-only just because specific bits are zero.
        foreach (var generic in new[] { "GA", "GW" })
        {
            security.SetSecurityDescriptorSddlForm("O:BAD:P(A;OICIIO;" + generic + ";;;BU)");
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in
                security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                Assert(InstallationLayout.GrantsMutationRights(rule.FileSystemRights),
                    "The protection policy accepted raw generic-write/all permissions as read-only.");
        }
        Assert(!InstallationLayout.GrantsMutationRights(unchecked((System.Security.AccessControl.FileSystemRights)0x80000000)),
            "Generic read permissions were mistaken for mutation permissions.");
        Assert(InstallationLayout.GrantsMutationRights(System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles),
            "Delete-child permission was accepted as read-only.");
    }

    private static void CheckScopeMarkers()
    {
        using var fixture = new Fixture();
        var app = Path.Combine(fixture.Root, "ProgramFiles", "PulseReader");
        Directory.CreateDirectory(app);
        InstallationLayout Resolve() => InstallationLayout.Resolve(app, Path.Combine(fixture.Root, "User"),
            Path.Combine(fixture.Root, "ProgramFiles"), Path.Combine(fixture.Root, "ProgramData"));
        Assert(Resolve().Scope == InstallationScope.User, "Portable installation did not default to per-user storage.");
        File.WriteAllText(Path.Combine(app, InstallationLayout.ScopeFileName), "{\"SchemaVersion\":1,\"Scope\":\"machine\"}");
        var layout = Resolve();
        Assert(layout.Scope == InstallationScope.Machine && layout.DataRoot == Path.Combine(fixture.Root, "ProgramData", "PulseReader")
            && layout.ContextualRuntimeRoot.StartsWith(app, StringComparison.OrdinalIgnoreCase)
            && layout.ContextualLogRoot.StartsWith(Path.Combine(fixture.Root, "User"), StringComparison.OrdinalIgnoreCase),
            "Shared storage or per-user logging resolved incorrectly.");
        foreach (var invalid in new[] { "{}", "[]", "{\"SchemaVersion\":2,\"Scope\":\"machine\"}",
                     "{\"SchemaVersion\":1,\"Scope\":\"machine\",\"DataRoot\":\"arbitrary\"}",
                     "{\"SchemaVersion\":1,\"Scope\":\"machine\",\"Scope\":\"machine\"}" })
        {
            File.WriteAllText(Path.Combine(app, InstallationLayout.ScopeFileName), invalid);
            Throws<InvalidDataException>(() => Resolve());
        }
        File.WriteAllText(Path.Combine(app, InstallationLayout.ScopeFileName), "{\"SchemaVersion\":1,\"Scope\":\"machine\"}");
        Throws<InvalidDataException>(() => InstallationLayout.Resolve(app, fixture.Root,
            Path.Combine(fixture.Root, "DifferentProgramFiles"), fixture.Root));
    }

    private static void CheckUnavailableInspection()
    {
        using var fixture = new Fixture();
        var store = new ContextualPronunciationModelStore(roots: [fixture.Root]);
        Assert(store.GetAvailability().State == ContextualPronunciationInstallationState.MissingOrInvalid,
            "Missing installation was not classified as missing.");
        var path = Path.Combine(fixture.Root, ContextualPronunciationModelStore.InstallationManifestFileName);
        File.WriteAllText(path, "invalid");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(store.GetAvailability().State == ContextualPronunciationInstallationState.Unavailable,
                "A transient sharing violation was classified as missing.");
        Assert(store.GetAvailability().State == ContextualPronunciationInstallationState.MissingOrInvalid,
            "Invalid manifest was not classified as invalid.");
    }

    private static async Task CheckSplitSharedStorageAsync()
    {
        using var fixture = new Fixture();
        var layout = new InstallationLayout(InstallationScope.Machine, Path.Combine(fixture.Root, "App"),
            Path.Combine(fixture.Root, "Shared"), Path.Combine(fixture.Root, "User"));
        Directory.CreateDirectory(layout.OptionalRuntimeRoot);
        var runtime = Encoding.UTF8.GetBytes("verified executable fixture");
        var model = Encoding.UTF8.GetBytes("verified model fixture");
        byte[] archive;
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            { using var output = zip.CreateEntry("llama-server.exe").Open(); output.Write(runtime); }
            archive = memory.ToArray();
        }
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ContextualPronunciationInstallationManifest(1,
            Hash(archive), "model.bin", Hash(model), [new("llama-server.exe", runtime.Length, Hash(runtime))]));
        var fail = true;
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath == "/runtime.zip" ? archive
                : fail ? Encoding.UTF8.GetBytes("corrupt") : model) });
        }));
        var setup = new ModelSetupService(layout, new VerifiedAssetDownloader(client), Asset("speech.bin", model),
            Asset("runtime.zip", archive), Asset("model.bin", model), () => manifest, Hash(manifest), sharedHelper: true);
        await ThrowsAsync<InvalidDataException>(() => setup.EnsureContextualPronunciationAsync(null, CancellationToken.None));
        Assert(!Directory.Exists(layout.ContextualRoot) && !Directory.EnumerateDirectories(layout.OptionalRuntimeRoot).Any(),
            "A failed shared installation published runtime or model files.");
        fail = false;
        await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
        Assert(File.ReadAllBytes(Path.Combine(layout.OptionalRuntimeRoot, Hash(manifest), "llama-server.exe")).SequenceEqual(runtime)
            && File.ReadAllBytes(Path.Combine(layout.ContextualRoot, "models", "model.bin")).SequenceEqual(model)
            && !Directory.Exists(Path.Combine(layout.ContextualRoot, "runtime")), "Shared runtime was not split into protected app storage.");
        var count = requests;
        await setup.EnsureContextualPronunciationAsync(null, CancellationToken.None);
        Assert(requests == count, "Shared setup redownloaded an already verified installation.");
    }

    private static async Task CheckTransportAsync(bool disconnect)
    {
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes("small speech model fixture");
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            if (disconnect) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var setup = new ModelSetupService(fixture.Root, new VerifiedAssetDownloader(client), Asset("speech.bin", bytes),
            Asset("runtime.bin", bytes), Asset("model.bin", bytes), () => [], "unused");
        var name = "PulseReader-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var helper = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(lifetime.Token), helper.ConnectAsync(lifetime.Token));
        var operation = SharedModelSetup.ServeAsync(helper, "kokoro", setup, lifetime);
        var progress = 0;
        while (true)
        {
            var message = await SharedModelSetup.ReadAsync(server, lifetime.Token);
            if (message.Type == "progress") progress++;
            if (disconnect) { server.Dispose(); break; }
            if (message.Type == "complete") { Assert(message.Code == 0, "Helper returned failure."); break; }
        }
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(progress > 0 && result == (disconnect ? 4 : 0), "Transport omitted progress or disconnect cancellation.");
        Assert(setup.IsKokoroInstalled == !disconnect, "Cancelled IPC setup published model bytes.");
    }

    private static async Task CheckFramingAsync()
    {
        using var bytes = new MemoryStream(BitConverter.GetBytes(4097));
        await ThrowsAsync<InvalidDataException>(() => SharedModelSetup.ReadAsync(bytes, CancellationToken.None));
        using var truncated = new MemoryStream([1, 0]);
        await ThrowsAsync<EndOfStreamException>(() => SharedModelSetup.ReadAsync(truncated, CancellationToken.None));
    }

    private static SetupAsset Asset(string name, byte[] bytes) => new(name, new("https://fixture.invalid/" + name), Hash(bytes), 1024 * 1024);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PulseReader-shared-tests-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => SetupPaths.DeleteTree(Root);
    }
}
