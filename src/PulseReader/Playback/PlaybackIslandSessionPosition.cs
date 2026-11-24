namespace PulseReader.Playback;

internal sealed record PlaybackIslandSessionPosition(
    PlaybackIslandPlacement Placement,
    Rectangle Bounds,
    bool ManualPositionActive,
    bool PinUpdatePending,
    PlaybackIslandPinState PinState);
