using PulseReader.Speech;

internal static class SpeechServiceChecks
{
    public static async Task RunAsync()
    {
        await UsesFallbackAfterPrimaryFailureAsync();
        await UsesFallbackAfterPrimaryExceptionAsync();
        await DoesNotFallbackAfterCancellationAsync();
        await DoesNotFallbackAfterCancellationExceptionAsync();
        await ControllerSwitchesBackendsAndKeepsPreviewHonestAsync();
        await ControllerRoutesPauseAndResumeAsync();
        await ControllerPreservesAttributionAsync();
        Console.WriteLine("PASS: speech fallback, live backend switching, preview, pause, resume, and stop behavior validate.");
    }

    private static async Task ControllerPreservesAttributionAsync()
    {
        using var kokoro = new FakeSpeechService(_ => Task.FromResult(SpeechStartResult.Failure("Unavailable")));
        using var windows = new FakeSpeechService(_ => Task.FromResult(SpeechStartResult.Success()));
        using var controller = new SpeechServiceController(PulseReader.Configuration.SpeechBackend.Kokoro,
            KokoroSpeechSettings.Default, WindowsSpeechSettings.Default, windows, kokoro);
        var request = new SpeechRequest("{\n\"status\": \"ready\"\n}", "Alice. ");
        var result = await controller.TrySpeakAsync(request);
        Assert(ReferenceEquals(kokoro.LastRequest, request), "The controller flattened attribution before Kokoro could prepare the body.");
        Assert(result.Started && windows.LastText == request.DisplayText, "Windows fallback lost or duplicated attribution.");
        windows.Stop();
        var quotation = PulseReader.TextPreparation.SpokenTextFormatter.Format(
            PulseReader.TextPreparation.ReadingUnitAnalyzer.Prepare("> First quotation.\n> Second quotation."));
        var preparedRequest = SpeechRequest.FromPreparedUnit(quotation.Units[1], quotation.Complexity);
        await controller.TrySpeakAsync(preparedRequest);
        Assert(ReferenceEquals(kokoro.LastRequest, preparedRequest)
            && kokoro.LastRequest.Prepare().BuildSpokenText() == "Second quotation.",
            "The backend controller lost a prepared unit's whole-message context.");
    }

    private static async Task UsesFallbackAfterPrimaryFailureAsync()
    {
        using var primary = new FakeSpeechService(
            _ => Task.FromResult(SpeechStartResult.Failure("Primary failed.")));
        using var fallback = new FakeSpeechService(
            _ => Task.FromResult(SpeechStartResult.Success()));
        using var service = new FallbackSpeechService(
            primary,
            fallback,
            "Used Windows speech.");

        var result = await service.TrySpeakAsync("Hello");

        Assert(result.Started, "Fallback speech did not start after primary failure.");
        Assert(result.Warning == "Used Windows speech.", "Fallback warning was not returned.");
        Assert(fallback.SpeakAttempts == 1, "Fallback speech was not attempted exactly once.");
        Assert(service.IsSpeaking, "Fallback speaking state was not exposed.");

        service.Stop();
        Assert(fallback.StopCalls == 1, "Stop was not routed to the active fallback backend.");
        Assert(!service.IsSpeaking, "Fallback speaking state did not clear after stop.");
    }

    private static async Task UsesFallbackAfterPrimaryExceptionAsync()
    {
        using var primary = new FakeSpeechService(
            _ => throw new InvalidOperationException("Unexpected backend failure."));
        using var fallback = new FakeSpeechService(
            _ => Task.FromResult(SpeechStartResult.Success()));
        using var service = new FallbackSpeechService(
            primary,
            fallback,
            "Used Windows speech.");

        var result = await service.TrySpeakAsync("Hello");

        Assert(result.Started, "Fallback speech did not start after a primary exception.");
        Assert(fallback.SpeakAttempts == 1, "Primary exception did not reach fallback speech.");
    }

    private static async Task DoesNotFallbackAfterCancellationAsync()
    {
        using var primary = new FakeSpeechService(
            _ => Task.FromResult(SpeechStartResult.CanceledResult));
        using var fallback = new FakeSpeechService(
            _ => Task.FromResult(SpeechStartResult.Success()));
        using var service = new FallbackSpeechService(
            primary,
            fallback,
            "Used Windows speech.");

        var result = await service.TrySpeakAsync("Hello");

        Assert(result.Canceled, "Primary cancellation was not preserved.");
        Assert(fallback.SpeakAttempts == 0, "Cancellation incorrectly started fallback speech.");
    }

    private static async Task DoesNotFallbackAfterCancellationExceptionAsync()
    {
        using var primary = new FakeSpeechService(
            _ => throw new OperationCanceledException());
        using var fallback = new FakeSpeechService(
            _ => Task.FromResult(SpeechStartResult.Success()));
        using var service = new FallbackSpeechService(
            primary,
            fallback,
            "Used Windows speech.");

        var result = await service.TrySpeakAsync("Hello");

        Assert(result.Canceled, "A backend cancellation exception was not preserved.");
        Assert(
            fallback.SpeakAttempts == 0,
            "A backend cancellation exception incorrectly started fallback speech.");
    }

    private static async Task ControllerSwitchesBackendsAndKeepsPreviewHonestAsync()
    {
        using var kokoro = new FakeSpeechService(
            text => Task.FromResult(
                text is "preview" or "Open https://example.com/pulse-reader."
                    ? SpeechStartResult.Failure("Kokoro failed.")
                    : SpeechStartResult.Success()));
        using var windows = new FakeSpeechService(
            _ => Task.FromResult(SpeechStartResult.Success()));
        using var controller = new SpeechServiceController(
            PulseReader.Configuration.SpeechBackend.Windows,
            KokoroSpeechSettings.Default,
            WindowsSpeechSettings.Default,
            windows,
            kokoro);

        var windowsResult = await controller.TrySpeakAsync("windows text");
        Assert(windowsResult.Started, "Windows speech did not start through the controller.");
        Assert(windows.LastText == "windows text", "The controller changed Windows speech text.");

        controller.Stop();
        var customSettings = new KokoroSpeechSettings(
            "bf_emma",
            1.1f,
            65,
            ContextualPronunciationEnabled: true);
        var customWindowsSettings = new WindowsSpeechSettings("Test Windows Voice", 3, 72);
        controller.UpdateSettings(
            PulseReader.Configuration.SpeechBackend.Kokoro,
            customSettings,
            customWindowsSettings);
        Assert(kokoro.Settings == customSettings, "Kokoro settings were not updated live.");
        Assert(
            windows.WindowsSettings == customWindowsSettings,
            "Windows settings were not updated live.");

        var kokoroResult = await controller.TrySpeakAsync("kokoro text");
        Assert(kokoroResult.Started, "Kokoro speech did not start after switching backends.");
        Assert(kokoro.LastText == "kokoro text", "The controller changed Kokoro speech text.");

        controller.Stop();
        const string fallbackText = "Open https://example.com/pulse-reader.";
        var fallbackResult = await controller.TrySpeakAsync(fallbackText);
        Assert(fallbackResult.Started, "Windows fallback did not start after Kokoro failed.");
        Assert(
            fallbackResult.Warning == SpeechServiceController.KokoroFallbackWarning,
            "Windows fallback did not return the Kokoro warning.");
        Assert(
            windows.LastText == fallbackText,
            "Windows fallback did not receive the original selected text.");

        controller.Stop();
        var windowsAttemptsBeforePreview = windows.SpeakAttempts;
        var previewResult = await controller.TryPreviewAsync("preview");
        Assert(!previewResult.Started, "A failed Kokoro preview was reported as started.");
        Assert(
            windows.SpeakAttempts == windowsAttemptsBeforePreview,
            "A failed Kokoro preview misleadingly fell back to Windows speech.");
    }

    private static async Task ControllerRoutesPauseAndResumeAsync()
    {
        using var kokoro = new FakeSpeechService(
            _ => Task.FromResult(SpeechStartResult.Success()));
        using var windows = new FakeSpeechService(
            _ => Task.FromResult(SpeechStartResult.Success()));
        using var controller = new SpeechServiceController(
            PulseReader.Configuration.SpeechBackend.Kokoro,
            KokoroSpeechSettings.Default,
            WindowsSpeechSettings.Default,
            windows,
            kokoro);

        var result = await controller.TrySpeakAsync("pause me");
        Assert(result.Started, "Speech did not start before the pause check.");
        Assert(controller.TryPause(), "Pause was not routed to the active speech backend.");
        Assert(
            controller.State == SpeechPlaybackState.Paused,
            "The controller did not expose the paused state.");
        Assert(controller.TryResume(), "Resume was not routed to the active speech backend.");
        Assert(
            controller.State == SpeechPlaybackState.Speaking,
            "The controller did not expose the resumed speaking state.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeSpeechService(
        Func<string, Task<SpeechStartResult>> speak)
        : IConfigurableKokoroSpeechService, IConfigurableWindowsSpeechService
    {
        private SpeechPlaybackState _state;

        public SpeechPlaybackState State => _state;

        public bool IsSpeaking => _state != SpeechPlaybackState.Idle;

        public int SpeakAttempts { get; private set; }

        public int StopCalls { get; private set; }

        public string? LastText { get; private set; }
        public SpeechRequest? LastRequest { get; private set; }

        public Task<SpeechStartResult> TrySpeakAsync(SpeechRequest request)
        {
            LastRequest = request;
            return TrySpeakAsync(request.DisplayText);
        }

        public KokoroSpeechSettings? Settings { get; private set; }

        public ContextualPronunciationAvailability ContextualPronunciationAvailability { get; } =
            new(false, string.Empty, "Not installed for this fake service.");

        public WindowsSpeechSettings? WindowsSettings { get; private set; }

        public IReadOnlyList<WindowsVoiceDescriptor> Voices { get; } =
        [
            new("Test Windows Voice", "Test Windows Voice", "English (United Kingdom)", "en-GB", true)
        ];

        public event EventHandler? SpeakingStateChanged;

        public async Task<SpeechStartResult> TrySpeakAsync(string text)
        {
            SpeakAttempts++;
            LastText = text;
            var result = await speak(text);

            if (result.Started)
            {
                _state = SpeechPlaybackState.Speaking;
                SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            }

            return result;
        }

        public void Stop()
        {
            StopCalls++;

            if (_state != SpeechPlaybackState.Idle)
            {
                _state = SpeechPlaybackState.Idle;
                SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool TryPause()
        {
            if (_state != SpeechPlaybackState.Speaking)
            {
                return false;
            }

            _state = SpeechPlaybackState.Paused;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public bool TryResume()
        {
            if (_state != SpeechPlaybackState.Paused)
            {
                return false;
            }

            _state = SpeechPlaybackState.Speaking;
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void UpdateSettings(KokoroSpeechSettings settings)
        {
            Settings = settings;
        }

        public void UpdateSettings(WindowsSpeechSettings settings)
        {
            WindowsSettings = settings;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
