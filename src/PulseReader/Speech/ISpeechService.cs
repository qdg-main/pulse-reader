namespace PulseReader.Speech;

internal interface ISpeechService : IDisposable
{
    SpeechPlaybackState State { get; }

    bool IsSpeaking => State != SpeechPlaybackState.Idle;

    event EventHandler? SpeakingStateChanged;

    Task<SpeechStartResult> TrySpeakAsync(string text);

    Task<SpeechStartResult> TrySpeakAsync(SpeechRequest request) => TrySpeakAsync(request.DisplayText);

    bool TryPause();

    bool TryResume();

    void Stop();
}

internal enum SpeechPlaybackState
{
    Idle,
    Preparing,
    Speaking,
    Paused
}

internal interface IConfigurableKokoroSpeechService : ISpeechService
{
    ContextualPronunciationAvailability ContextualPronunciationAvailability { get; }

    void UpdateSettings(KokoroSpeechSettings settings);
}

internal interface IPlaybackNavigationSpeechService
{
    PlaybackNavigationState NavigationState { get; }

    bool TryRestartCurrentUnit();

    bool TryRestartFromBeginning();

    bool TryMoveToNextUnit();
}

internal readonly record struct PlaybackNavigationState(
    bool IsAvailable,
    int CurrentUnitIndex,
    int UnitCount)
{
    public static PlaybackNavigationState Unavailable { get; } = new(false, 0, 0);

    public bool CanRestartCurrent => IsAvailable && UnitCount > 0;

    public bool CanRestartFromBeginning => IsAvailable && CurrentUnitIndex > 0;

    public bool CanMoveNext => IsAvailable && CurrentUnitIndex + 1 < UnitCount;
}

internal interface IConfigurableWindowsSpeechService : ISpeechService
{
    IReadOnlyList<WindowsVoiceDescriptor> Voices { get; }

    void UpdateSettings(WindowsSpeechSettings settings);
}

internal sealed record SpeechStartResult(bool Started, bool Canceled, string? Warning, string? Error)
{
    public static SpeechStartResult Success(string? warning = null) =>
        new(true, false, warning, null);

    public static SpeechStartResult CanceledResult { get; } =
        new(false, true, null, null);

    public static SpeechStartResult Failure(string error) =>
        new(false, false, null, error);
}
