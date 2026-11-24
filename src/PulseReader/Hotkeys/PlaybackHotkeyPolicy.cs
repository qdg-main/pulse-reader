using PulseReader.Speech;

namespace PulseReader.Hotkeys;

internal static class PlaybackHotkeyPolicy
{
    internal static bool ShouldStopImmediately(bool monitoring, bool manualActive, SpeechPlaybackState state) =>
        manualActive || (!monitoring && state != SpeechPlaybackState.Idle);
}
