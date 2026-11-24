using PulseReader.Configuration;

namespace PulseReader.Speech;

internal sealed class SpeechServiceController : ISpeechService
{
    internal const string KokoroFallbackWarning =
        "Kokoro was unavailable, so Pulse Reader used the Windows speech voice for this selection.";

    private readonly Lock _stateLock = new();
    private readonly IConfigurableKokoroSpeechService? _kokoroSpeech;
    private readonly ISpeechService _windowsSpeech;
    private SpeechBackend _backend;
    private bool _disposed;

    public SpeechServiceController(
        SpeechBackend backend,
        KokoroSpeechSettings kokoroSettings,
        WindowsSpeechSettings windowsSettings,
        ISpeechService windowsSpeech,
        IConfigurableKokoroSpeechService? kokoroSpeech = null)
    {
        _backend = backend;
        _windowsSpeech = windowsSpeech;
        _kokoroSpeech = kokoroSpeech;
        _kokoroSpeech?.UpdateSettings(kokoroSettings);
        (_windowsSpeech as IConfigurableWindowsSpeechService)?.UpdateSettings(windowsSettings);

        _windowsSpeech.SpeakingStateChanged += OnSpeakingStateChanged;
        if (_kokoroSpeech is not null)
        {
            _kokoroSpeech.SpeakingStateChanged += OnSpeakingStateChanged;
        }
    }

    public SpeechPlaybackState State
    {
        get
        {
            var kokoroState = _kokoroSpeech?.State ?? SpeechPlaybackState.Idle;
            return kokoroState != SpeechPlaybackState.Idle
                ? kokoroState
                : _windowsSpeech.State;
        }
    }

    public bool IsSpeaking => State != SpeechPlaybackState.Idle;

    public SpeechBackend Backend
    {
        get
        {
            lock (_stateLock)
            {
                return _backend;
            }
        }
    }

    public event EventHandler? SpeakingStateChanged;

    public IReadOnlyList<WindowsVoiceDescriptor> WindowsVoices =>
        (_windowsSpeech as IConfigurableWindowsSpeechService)?.Voices
        ?? Array.Empty<WindowsVoiceDescriptor>();

    public ContextualPronunciationAvailability ContextualPronunciationAvailability =>
        _kokoroSpeech?.ContextualPronunciationAvailability
        ?? new ContextualPronunciationAvailability(
            false,
            string.Empty,
            "The optional contextual-pronunciation service is unavailable.");

    public void UpdateSettings(
        SpeechBackend backend,
        KokoroSpeechSettings kokoroSettings,
        WindowsSpeechSettings windowsSettings)
    {
        ArgumentNullException.ThrowIfNull(kokoroSettings);
        ArgumentNullException.ThrowIfNull(windowsSettings);

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _backend = backend;
            _kokoroSpeech?.UpdateSettings(kokoroSettings);
            (_windowsSpeech as IConfigurableWindowsSpeechService)?.UpdateSettings(windowsSettings);
        }
    }

    public Task<SpeechStartResult> TrySpeakAsync(string text) => TrySpeakAsync(new SpeechRequest(text));

    public async Task<SpeechStartResult> TrySpeakAsync(SpeechRequest request)
    {
        SpeechBackend backend;

        lock (_stateLock)
        {
            if (_disposed)
            {
                return SpeechStartResult.Failure("Speech synthesis is unavailable.");
            }

            backend = _backend;
        }

        if (backend == SpeechBackend.Windows)
        {
            return await _windowsSpeech.TrySpeakAsync(request);
        }

        if (_kokoroSpeech is null)
        {
            return await TryWindowsFallbackAsync(request.DisplayText);
        }

        var kokoroResult = await _kokoroSpeech.TrySpeakAsync(request);
        if (kokoroResult.Started || kokoroResult.Canceled)
        {
            return kokoroResult;
        }

        return await TryWindowsFallbackAsync(request.DisplayText);
    }

    public Task<SpeechStartResult> TryPreviewAsync(string text)
    {
        SpeechBackend backend;

        lock (_stateLock)
        {
            if (_disposed)
            {
                return Task.FromResult(SpeechStartResult.Failure("Speech synthesis is unavailable."));
            }

            backend = _backend;
        }

        return backend == SpeechBackend.Kokoro
            ? _kokoroSpeech?.TrySpeakAsync(text)
                ?? Task.FromResult(SpeechStartResult.Failure("Kokoro speech synthesis is unavailable."))
            : _windowsSpeech.TrySpeakAsync(text);
    }

    public void Stop()
    {
        _kokoroSpeech?.Stop();
        _windowsSpeech.Stop();
    }

    public bool TryPause() =>
        (_kokoroSpeech?.TryPause() ?? false) || _windowsSpeech.TryPause();

    public bool TryResume() =>
        (_kokoroSpeech?.TryResume() ?? false) || _windowsSpeech.TryResume();

    public PlaybackNavigationState NavigationState =>
        _kokoroSpeech?.State != SpeechPlaybackState.Idle
            && _kokoroSpeech is IPlaybackNavigationSpeechService navigation
                ? navigation.NavigationState
                : PlaybackNavigationState.Unavailable;

    public bool TryRestartCurrentUnit() =>
        _kokoroSpeech?.State != SpeechPlaybackState.Idle
        && _kokoroSpeech is IPlaybackNavigationSpeechService navigation
        && navigation.TryRestartCurrentUnit();

    public bool TryRestartFromBeginning() =>
        _kokoroSpeech?.State != SpeechPlaybackState.Idle
        && _kokoroSpeech is IPlaybackNavigationSpeechService navigation
        && navigation.TryRestartFromBeginning();

    public bool TryMoveToNextUnit() =>
        _kokoroSpeech?.State != SpeechPlaybackState.Idle
        && _kokoroSpeech is IPlaybackNavigationSpeechService navigation
        && navigation.TryMoveToNextUnit();

    private async Task<SpeechStartResult> TryWindowsFallbackAsync(string text)
    {
        var windowsResult = await _windowsSpeech.TrySpeakAsync(text);
        return windowsResult.Started
            ? SpeechStartResult.Success(KokoroFallbackWarning)
            : windowsResult;
    }

    private void OnSpeakingStateChanged(object? sender, EventArgs eventArgs) =>
        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _windowsSpeech.SpeakingStateChanged -= OnSpeakingStateChanged;
        if (_kokoroSpeech is not null)
        {
            _kokoroSpeech.SpeakingStateChanged -= OnSpeakingStateChanged;
        }

        Stop();
        _kokoroSpeech?.Dispose();
        _windowsSpeech.Dispose();
    }
}
