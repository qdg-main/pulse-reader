using System.Net;
using PulseReader.Setup;
using PulseReader.Speech;

internal static class ModelDownloadTimeoutChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseReaderTimeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var headers in new[] { true, false })
            {
                using var client = new HttpClient(new StallHandler(headers));
                var downloader = new VerifiedAssetDownloader(client, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
                var asset = new SetupAsset("Fixture", new Uri("https://fixture.invalid/model"), new string('0', 64), 1024);
                var destination = Path.Combine(root, headers ? "headers" : "body");
                using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var timedOut = false;
                try { await downloader.EnsureAsync(asset, destination, null, guard.Token); }
                catch (TimeoutException) { timedOut = true; }
                if (!timedOut || File.Exists(destination) || Directory.GetFiles(root, "*.download").Length > 0)
                    throw new InvalidOperationException("A stalled download did not time out cleanly.");

                var setup = new ModelSetupService(root, downloader, asset, asset, asset,
                    () => [], new string('0', 64));
                using var speech = new KokoroSpeechService(new KokoroModelStore(setup));
                var result = await speech.TrySpeakAsync("A stalled model repair remains retryable.");
                if (result.Started || result.Canceled || string.IsNullOrWhiteSpace(result.Error))
                    throw new InvalidOperationException("A model timeout escaped normal Kokoro failure handling.");
            }
        }
        finally { SetupPaths.DeleteTree(root); }
        Console.WriteLine("PASS: stalled model response headers and body reads time out without promoting partial files.");
    }

    private sealed class StallHandler(bool headers) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (headers) await Task.Delay(Timeout.Infinite, cancellation);
            return new(HttpStatusCode.OK) { Content = new StreamContent(new StallStream()) };
        }
    }

    private sealed class StallStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
