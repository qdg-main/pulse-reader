using System.Speech.Synthesis;

namespace PulseReader.Speech;

internal sealed class WindowsSpeechService : IConfigurableWindowsSpeechService
{
    private readonly Lock _stateLock = new();
    private readonly SpeechSynthesizer _synthesizer;
    private WindowsSpeechSettings _settings;
    private Prompt? _currentPrompt;
    private SpeechPlaybackState _state;
    private bool _disposed;

    public WindowsSpeechService(WindowsSpeechSettings? settings = null)
    {
        _synthesizer = new SpeechSynthesizer();
        _synthesizer.SetOutputToDefaultAudioDevice();
        _synthesizer.SpeakCompleted += OnSpeakCompleted;
        var defaultVoiceName = _synthesizer.Voice.Name;
        Voices = _synthesizer.GetInstalledVoices()
            .Where(installedVoice => installedVoice.Enabled)
            .Select(installedVoice => installedVoice.VoiceInfo)
            .Select(voice => new WindowsVoiceDescriptor(
                voice.Name,
                voice.Name,
                voice.Culture.DisplayName,
                voice.Culture.Name,
                string.Equals(voice.Name, defaultVoiceName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(voice => voice.Language, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(voice => voice.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _settings = settings ?? WindowsSpeechSettings.Default;
    }

    public IReadOnlyList<WindowsVoiceDescriptor> Voices { get; }

    public SpeechPlaybackState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool IsSpeaking => State != SpeechPlaybackState.Idle;

    public event EventHandler? SpeakingStateChanged;

    public Task<SpeechStartResult> TrySpeakAsync(string text)
    {
        if (_disposed)
        {
            return Task.FromResult(
                SpeechStartResult.Failure("Windows speech synthesis is unavailable."));
        }

        lock (_stateLock)
        {
            if (_state != SpeechPlaybackState.Idle)
            {
                return Task.FromResult(
                    SpeechStartResult.Failure("Windows speech synthesis is already active."));
            }

            try
            {
                ApplySettings();
                _state = SpeechPlaybackState.Speaking;
                _currentPrompt = _synthesizer.SpeakAsync(text);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                              or ArgumentException
                                              or PlatformNotSupportedException)
            {
                _state = SpeechPlaybackState.Idle;
                _currentPrompt = null;
                return Task.FromResult(
                    SpeechStartResult.Failure("Windows could not start speech synthesis."));
            }
        }

        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(SpeechStartResult.Success());
    }

    public void UpdateSettings(WindowsSpeechSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!WindowsSpeechSettings.IsValid(settings.VoiceName, settings.Rate, settings.Volume))
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _settings = settings;
        }
    }

    private void ApplySettings()
    {
        var voiceName = Voices.FirstOrDefault(
            voice => string.Equals(voice.Id, _settings.VoiceName, StringComparison.OrdinalIgnoreCase))?.Id
            ?? Voices.FirstOrDefault(voice => voice.IsDefault)?.Id;
        if (voiceName is not null)
        {
            _synthesizer.SelectVoice(voiceName);
        }

        _synthesizer.Rate = _settings.Rate;
        _synthesizer.Volume = _settings.Volume;
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        var stateChanged = false;

        lock (_stateLock)
        {
            if (_state != SpeechPlaybackState.Idle)
            {
                _state = SpeechPlaybackState.Idle;
                _currentPrompt = null;
                stateChanged = true;
            }

            try
            {
                _synthesizer.SpeakAsyncCancelAll();
            }
            catch (InvalidOperationException)
            {
                // The synthesizer may already be stopping during Windows shutdown.
            }
        }

        if (stateChanged)
        {
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool TryPause()
    {
        lock (_stateLock)
        {
            if (_disposed || _state != SpeechPlaybackState.Speaking)
            {
                return false;
            }

            try
            {
                _synthesizer.Pause();
                _state = SpeechPlaybackState.Paused;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryResume()
    {
        lock (_stateLock)
        {
            if (_disposed || _state != SpeechPlaybackState.Paused)
            {
                return false;
            }

            try
            {
                _synthesizer.Resume();
                _state = SpeechPlaybackState.Speaking;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs eventArgs)
    {
        var stateChanged = false;

        lock (_stateLock)
        {
            if (ReferenceEquals(eventArgs.Prompt, _currentPrompt))
            {
                _currentPrompt = null;
                _state = SpeechPlaybackState.Idle;
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _synthesizer.SpeakCompleted -= OnSpeakCompleted;
        _synthesizer.Dispose();
        _disposed = true;
    }
}
