namespace PulseReader.Speech;

internal sealed class FallbackSpeechService : ISpeechService
{
    private readonly Lock _stateLock = new();
    private readonly ISpeechService _primary;
    private readonly ISpeechService _fallback;
    private readonly string _fallbackWarning;
    private ISpeechService? _active;
    private bool _disposed;

    public FallbackSpeechService(
        ISpeechService primary,
        ISpeechService fallback,
        string fallbackWarning)
    {
        _primary = primary;
        _fallback = fallback;
        _fallbackWarning = fallbackWarning;
        _primary.SpeakingStateChanged += OnBackendSpeakingStateChanged;
        _fallback.SpeakingStateChanged += OnBackendSpeakingStateChanged;
    }

    public SpeechPlaybackState State
    {
        get
        {
            lock (_stateLock)
            {
                return _active?.State ?? SpeechPlaybackState.Idle;
            }
        }
    }

    public bool IsSpeaking => State != SpeechPlaybackState.Idle;

    public event EventHandler? SpeakingStateChanged;

    public async Task<SpeechStartResult> TrySpeakAsync(string text)
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return SpeechStartResult.Failure("Speech synthesis is unavailable.");
            }

            if (_active?.IsSpeaking == true)
            {
                return SpeechStartResult.Failure("Speech synthesis is already active.");
            }

            _active = _primary;
        }

        var primaryResult = await TryBackendAsync(_primary, text);
        if (primaryResult.Started || primaryResult.Canceled)
        {
            return primaryResult;
        }

        lock (_stateLock)
        {
            _active = _fallback;
        }

        var fallbackResult = await TryBackendAsync(_fallback, text);
        if (fallbackResult.Started)
        {
            return SpeechStartResult.Success(_fallbackWarning);
        }

        return fallbackResult.Canceled
            ? fallbackResult
            : SpeechStartResult.Failure(
                "Neither Kokoro nor Windows speech synthesis could speak the selected text.");
    }

    private static async Task<SpeechStartResult> TryBackendAsync(
        ISpeechService backend,
        string text)
    {
        try
        {
            return await backend.TrySpeakAsync(text);
        }
        catch (OperationCanceledException)
        {
            return SpeechStartResult.CanceledResult;
        }
        catch (Exception)
        {
            return SpeechStartResult.Failure("The speech backend failed unexpectedly.");
        }
    }

    public void Stop()
    {
        ISpeechService? active;

        lock (_stateLock)
        {
            active = _active;
        }

        active?.Stop();
    }

    public bool TryPause()
    {
        ISpeechService? active;
        lock (_stateLock)
        {
            active = _active;
        }

        return active?.TryPause() == true;
    }

    public bool TryResume()
    {
        ISpeechService? active;
        lock (_stateLock)
        {
            active = _active;
        }

        return active?.TryResume() == true;
    }

    private void OnBackendSpeakingStateChanged(object? sender, EventArgs eventArgs)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(sender, _active))
            {
                return;
            }
        }

        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _primary.SpeakingStateChanged -= OnBackendSpeakingStateChanged;
        _fallback.SpeakingStateChanged -= OnBackendSpeakingStateChanged;
        _primary.Dispose();

        if (!ReferenceEquals(_primary, _fallback))
        {
            _fallback.Dispose();
        }
    }
}
