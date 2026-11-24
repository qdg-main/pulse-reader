using System.Net;
using System.Security.Cryptography;
using PulseReader.Configuration;
using PulseReader.Setup;
using PulseReader.Speech;

internal static class SpeechValidationFailureChecks
{
    public static async Task RunAsync()
    {
        var failures = new List<string>();
        foreach (var invalidSize in new[] { false, true })
        {
            var kind = invalidSize ? "invalid size" : "invalid checksum";
            foreach (var route in new[] { "direct speech", "Windows fallback", "preview" })
            {
                try { await CheckRouteAsync(invalidSize, route); }
                catch (Exception exception)
                {
                    failures.Add($"{kind}, {route}: {exception.Message}");
                }
            }
        }
        if (failures.Count != 0)
            throw new InvalidOperationException("Speech validation failures were not contained:\n" + string.Join('\n', failures));
        Console.WriteLine("PASS: real Kokoro size/checksum validation returns failure and idle, remains retryable, invokes Windows fallback once per selection, and contains preview failures without fallback.");
    }

    private static async Task CheckRouteAsync(bool invalidSize, string route)
    {
        using var fixture = new Fixture(invalidSize);
        using var windows = new FakeWindowsSpeech();
        using var controller = new SpeechServiceController(SpeechBackend.Kokoro,
            KokoroSpeechSettings.Default, WindowsSpeechSettings.Default, windows, fixture.Speech);
        var stateChanges = new List<SpeechPlaybackState>();
        fixture.Speech.SpeakingStateChanged += (_, _) => stateChanges.Add(fixture.Speech.State);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            SpeechStartResult result;
            var request = new SpeechRequest("An isolated validation fixture.", "Fixture reader. ");
            try
            {
                result = await (route switch
                {
                    "direct speech" => fixture.Speech.TrySpeakAsync(request),
                    "Windows fallback" => controller.TrySpeakAsync(request),
                    _ => controller.TryPreviewAsync(request.DisplayText)
                }).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException(
                    $"InvalidDataException escaped; Kokoro state={fixture.Speech.State}; Windows attempts={windows.Attempts}.",
                    exception);
            }

            Require(fixture.Speech.State == SpeechPlaybackState.Idle,
                "Validation failure left Kokoro preparing or speaking.");
            Require(fixture.Handler.Requests == attempt,
                "A later attempt did not re-enter actual asset validation after failure.");
            Require(stateChanges.Count == attempt * 2
                && stateChanges[^2] == SpeechPlaybackState.Preparing
                && stateChanges[^1] == SpeechPlaybackState.Idle,
                "Validation failure did not notify preparing-to-idle cleanup on each attempt.");
            if (route == "Windows fallback")
            {
                Require(result.Started && !result.Canceled
                    && result.Warning == SpeechServiceController.KokoroFallbackWarning
                    && windows.Attempts == attempt && windows.LastText == request.DisplayText,
                    "Asset validation failure did not invoke Windows fallback exactly once with original attribution.");
                controller.Stop();
            }
            else
            {
                Require(!result.Started && !result.Canceled && !string.IsNullOrWhiteSpace(result.Error)
                    && windows.Attempts == 0,
                    "Direct speech or preview did not contain validation failure without misleading fallback.");
            }
            Require(!File.Exists(fixture.ModelPath)
                && !Directory.EnumerateFiles(fixture.Root, "*.download", SearchOption.AllDirectories).Any(),
                "Invalid model data was promoted or its partial download was retained.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PulseReaderSpeechValidation-" + Guid.NewGuid().ToString("N"));
        internal FixtureHandler Handler { get; }
        internal KokoroSpeechService Speech { get; }
        internal string ModelPath { get; }

        internal Fixture(bool invalidSize)
        {
            Directory.CreateDirectory(Root);
            byte[] expectedBytes = [1, 2, 3, 4];
            Handler = new FixtureHandler(invalidSize ? [4, 3, 2, 1, 0] : [4, 3, 2, 1]);
            _client = new HttpClient(Handler);
            var asset = new SetupAsset("Invalid isolated speech model", new Uri("https://fixture.invalid/kokoro.onnx"),
                Convert.ToHexString(SHA256.HashData(expectedBytes)), 64, expectedBytes.Length);
            var setup = new ModelSetupService(Root, new VerifiedAssetDownloader(_client), asset, asset, asset,
                () => [], new string('0', 64));
            var store = new KokoroModelStore(setup);
            ModelPath = store.ModelPath;
            // Real setup fails validation before KokoroTTS/native model initialization.
            Speech = new KokoroSpeechService(store);
        }

        public void Dispose()
        {
            Speech.Dispose();
            _client.Dispose();
            SetupPaths.DeleteTree(Root);
        }
    }

    private sealed class FixtureHandler(byte[] invalidBytes) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri?.Host == "fixture.invalid", "The fixture attempted an unexpected HTTP destination.");
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(invalidBytes)
            });
        }
    }

    private sealed class FakeWindowsSpeech : ISpeechService
    {
        public SpeechPlaybackState State { get; private set; }
        public bool IsSpeaking => State != SpeechPlaybackState.Idle;
        public event EventHandler? SpeakingStateChanged;
        internal int Attempts { get; private set; }
        internal string? LastText { get; private set; }

        public Task<SpeechStartResult> TrySpeakAsync(string text)
        {
            Attempts++;
            LastText = text;
            State = SpeechPlaybackState.Speaking;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(SpeechStartResult.Success());
        }

        public bool TryPause() => false;
        public bool TryResume() => false;
        public void Stop()
        {
            State = SpeechPlaybackState.Idle;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        }
        public void Dispose() => Stop();
    }
}
