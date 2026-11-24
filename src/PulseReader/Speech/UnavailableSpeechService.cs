namespace PulseReader.Speech;

internal sealed class UnavailableSpeechService : ISpeechService
{
    public SpeechPlaybackState State => SpeechPlaybackState.Idle;

    public bool IsSpeaking => false;

    public event EventHandler? SpeakingStateChanged
    {
        add { }
        remove { }
    }

    public Task<SpeechStartResult> TrySpeakAsync(string text) =>
        Task.FromResult(
            SpeechStartResult.Failure(
                "Pulse Reader could not find an available Windows speech voice."));

    public bool TryPause() => false;

    public bool TryResume() => false;

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
