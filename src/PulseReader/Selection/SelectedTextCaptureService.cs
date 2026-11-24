using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PulseReader.Selection;

internal sealed class SelectedTextCaptureService
{
    private readonly ISelectedTextCapturePlatform _platform;
    private readonly ISelectedTextAutomationFallback _automationFallback;
    private readonly SelectedTextCaptureTiming _timing;

    public SelectedTextCaptureService()
        : this(
            new WindowsSelectedTextCapturePlatform(),
            new UiAutomationSelectedTextFallback(),
            SelectedTextCaptureTiming.Default)
    {
    }

    internal SelectedTextCaptureService(
        ISelectedTextCapturePlatform platform,
        ISelectedTextAutomationFallback automationFallback,
        SelectedTextCaptureTiming timing)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(automationFallback);
        ArgumentNullException.ThrowIfNull(timing);
        timing.Validate();
        _platform = platform;
        _automationFallback = automationFallback;
        _timing = timing;
    }

    public Task<SelectedTextCaptureResult> CaptureAsync(CancellationToken cancellationToken) =>
        CaptureFromWindowAsync(_platform.GetForegroundWindowHandle(), cancellationToken);

    public async Task<SelectedTextCaptureResult> CaptureFromWindowAsync(
        IntPtr selectedApplicationWindow,
        CancellationToken cancellationToken)
    {
        if (selectedApplicationWindow == IntPtr.Zero
            || _platform.GetForegroundWindowHandle() != selectedApplicationWindow)
        {
            return SelectedTextCaptureResult.Failed(
                "The original application is no longer active. Try selecting the text again.");
        }

        var selectedControlWindow = _platform.GetCaretWindowHandle(selectedApplicationWindow);

        SnapshotCaptureAttempt captureAttempt;
        try
        {
            captureAttempt = await TryCaptureSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return SelectedTextCaptureResult.Failed("Selection capture was cancelled.");
        }

        if (captureAttempt.Snapshot is null)
        {
            var captureError = captureAttempt.ClipboardChanged
                ? "The clipboard kept changing before Pulse Reader could copy the selection. Try again."
                : "The clipboard is busy. Pulse Reader left it unchanged.";
            return captureAttempt.ClipboardChanged
                ? SelectedTextCaptureResult.Failed(captureError)
                : await TryAutomationFallbackAsync(
                    selectedApplicationWindow,
                    selectedControlWindow,
                    ClipboardRestorationStatus.NotRequired,
                    captureError,
                    cancellationToken);
        }

        using var snapshot = captureAttempt.Snapshot;
        var clipboardChanged = false;
        var expectedSequence = captureAttempt.SequenceNumber;
        var restorationStatus = ClipboardRestorationStatus.NotRequired;
        var automationAllowed = true;
        string? selectedText = null;
        string? error = null;

        try
        {
            var clearResult = await TryClearClipboardAsync(
                expectedSequence,
                cancellationToken);
            if (clearResult.Status == ClipboardMutationStatus.Superseded)
            {
                automationAllowed = false;
                error = "The clipboard changed before Pulse Reader could copy the selection. Try again.";
            }
            else if (clearResult.Status == ClipboardMutationStatus.Busy)
            {
                error = "The clipboard is busy. Pulse Reader left it unchanged.";
            }
            else
            {
                clipboardChanged = true;
                expectedSequence = clearResult.SequenceNumber;

                var postedCopySent = _platform.PostCopyMessage(selectedControlWindow);
                var postedObservation = CopiedTextObservation.Unchanged(expectedSequence);
                if (postedCopySent)
                {
                    postedObservation = await WaitForCopiedTextAsync(
                        expectedSequence,
                        selectedApplicationWindow,
                        cancellationToken,
                        _timing.PostedCopyTimeout);
                    if (postedObservation.ChangedInAnotherWindow)
                    {
                        automationAllowed = false;
                        error = ClipboardChangedInAnotherWindowError;
                    }
                    else if (postedObservation.ClipboardChanged)
                    {
                        expectedSequence = postedObservation.SequenceNumber;
                        selectedText = postedObservation.Text;
                    }
                }

                if (selectedText is null
                    && (!postedCopySent || !postedObservation.ClipboardChanged))
                {
                    if (_platform.GetClipboardSequenceNumber() != expectedSequence)
                    {
                        automationAllowed = false;
                        error = "The clipboard changed before Pulse Reader could send the copy command. Try again.";
                    }
                    else
                    {
                        _platform.RestoreForegroundWindow(selectedApplicationWindow);
                        if (!_platform.SendCopyShortcut())
                        {
                            error = "Windows could not send the copy command.";
                        }
                        else
                        {
                            var fallbackObservation = await WaitForCopiedTextAsync(
                                expectedSequence,
                                selectedApplicationWindow,
                                cancellationToken,
                                _timing.CopyTimeout);
                            if (fallbackObservation.ChangedInAnotherWindow)
                            {
                                automationAllowed = false;
                                error = ClipboardChangedInAnotherWindowError;
                            }
                            else if (fallbackObservation.ClipboardChanged)
                            {
                                expectedSequence = fallbackObservation.SequenceNumber;
                                selectedText = fallbackObservation.Text;
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            automationAllowed = false;
            error = "Selection capture was cancelled.";
        }
        finally
        {
            if (clipboardChanged)
            {
                restorationStatus = await RestoreSnapshotAsync(snapshot, expectedSequence);
            }
        }

        if (selectedText is not null
            || !automationAllowed
            || restorationStatus == ClipboardRestorationStatus.Superseded)
        {
            return new SelectedTextCaptureResult(selectedText, restorationStatus, error);
        }

        return await TryAutomationFallbackAsync(
            selectedApplicationWindow,
            selectedControlWindow,
            restorationStatus,
            error,
            cancellationToken);
    }

    private async Task<SelectedTextCaptureResult> TryAutomationFallbackAsync(
        IntPtr selectedApplicationWindow,
        IntPtr selectedControlWindow,
        ClipboardRestorationStatus restorationStatus,
        string? clipboardError,
        CancellationToken cancellationToken)
    {
        if (selectedApplicationWindow == IntPtr.Zero
            || _platform.GetForegroundWindowHandle() != selectedApplicationWindow)
        {
            return new SelectedTextCaptureResult(null, restorationStatus, clipboardError);
        }

        try
        {
            var selectedText = await _automationFallback.TryCaptureAsync(
                new SelectedTextAutomationTarget(
                    selectedApplicationWindow,
                    selectedControlWindow),
                cancellationToken);
            return string.IsNullOrWhiteSpace(selectedText)
                ? new SelectedTextCaptureResult(null, restorationStatus, clipboardError)
                : new SelectedTextCaptureResult(selectedText, restorationStatus, null);
        }
        catch (OperationCanceledException)
        {
            return new SelectedTextCaptureResult(
                null,
                restorationStatus,
                "Selection capture was cancelled.");
        }
    }

    private async Task<SnapshotCaptureAttempt> TryCaptureSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var clipboardChanged = false;
        for (var attempt = 0; attempt < _timing.ClipboardRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequenceBefore = _platform.GetClipboardSequenceNumber();

            try
            {
                if (!_platform.TryCaptureSnapshot(out var snapshot) || snapshot is null)
                {
                    return new SnapshotCaptureAttempt(null, sequenceBefore, clipboardChanged);
                }

                var sequenceAfter = _platform.GetClipboardSequenceNumber();
                if (sequenceBefore == sequenceAfter)
                {
                    return new SnapshotCaptureAttempt(snapshot, sequenceAfter, clipboardChanged);
                }

                clipboardChanged = true;
                snapshot.Dispose();
            }
            catch (Exception exception) when (IsClipboardException(exception))
            {
                // The clipboard owner may still be rendering a format. Retry briefly.
            }

            await DelayBeforeRetryAsync(attempt, cancellationToken);
        }

        return new SnapshotCaptureAttempt(null, 0, clipboardChanged);
    }

    private async Task<ClipboardMutationResult> TryClearClipboardAsync(
        uint expectedSequence,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _timing.ClipboardRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_platform.GetClipboardSequenceNumber() != expectedSequence)
            {
                return new ClipboardMutationResult(
                    ClipboardMutationStatus.Superseded,
                    expectedSequence);
            }

            try
            {
                _platform.ClearClipboard();
                return new ClipboardMutationResult(
                    ClipboardMutationStatus.Succeeded,
                    _platform.GetClipboardSequenceNumber());
            }
            catch (Exception exception) when (IsClipboardException(exception))
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
        }

        return new ClipboardMutationResult(
            ClipboardMutationStatus.Busy,
            expectedSequence);
    }

    private async Task<CopiedTextObservation> WaitForCopiedTextAsync(
        uint clearedSequenceNumber,
        IntPtr selectedApplicationWindow,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        var latestSequence = clearedSequenceNumber;
        var clipboardChanged = false;

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequenceBeforeRead = _platform.GetClipboardSequenceNumber();
            if (sequenceBeforeRead != clearedSequenceNumber)
            {
                clipboardChanged = true;
                latestSequence = sequenceBeforeRead;
                if (selectedApplicationWindow != IntPtr.Zero
                    && _platform.GetForegroundWindowHandle() != selectedApplicationWindow)
                {
                    return new CopiedTextObservation(
                        null,
                        sequenceBeforeRead,
                        true,
                        true);
                }

                try
                {
                    var text = _platform.ReadClipboardText();
                    var sequenceAfterRead = _platform.GetClipboardSequenceNumber();
                    if (sequenceBeforeRead == sequenceAfterRead)
                    {
                        text = text?.Trim();
                        return new CopiedTextObservation(
                            string.IsNullOrWhiteSpace(text) ? null : text,
                            sequenceAfterRead,
                            true,
                            false);
                    }

                    latestSequence = sequenceAfterRead;
                }
                catch (Exception exception) when (IsClipboardException(exception))
                {
                    // The source application may still own or render the copied data.
                }
            }

            await Task.Delay(_timing.CopyPollDelay, cancellationToken);
        }

        return new CopiedTextObservation(null, latestSequence, clipboardChanged, false);
    }

    private async Task<ClipboardRestorationStatus> RestoreSnapshotAsync(
        IClipboardCaptureSnapshot snapshot,
        uint expectedSequence)
    {
        for (var attempt = 0; attempt < _timing.ClipboardRetryCount * 2; attempt++)
        {
            if (_platform.GetClipboardSequenceNumber() != expectedSequence)
            {
                return ClipboardRestorationStatus.Superseded;
            }

            try
            {
                snapshot.Restore();
                return snapshot.IsComplete
                    ? ClipboardRestorationStatus.Restored
                    : ClipboardRestorationStatus.RestoredPartially;
            }
            catch (Exception exception) when (IsClipboardException(exception))
            {
                await Task.Delay(_timing.ClipboardRetryDelay);
            }
        }

        return _platform.GetClipboardSequenceNumber() == expectedSequence
            ? ClipboardRestorationStatus.Failed
            : ClipboardRestorationStatus.Superseded;
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        if (attempt + 1 < _timing.ClipboardRetryCount)
        {
            await Task.Delay(_timing.ClipboardRetryDelay, cancellationToken);
        }
    }

    private static bool IsClipboardException(Exception exception) =>
        exception is ExternalException or InvalidOperationException or IOException;

    private const string ClipboardChangedInAnotherWindowError =
        "The clipboard changed in another window before Pulse Reader could read the selection. Try again.";

    private sealed record SnapshotCaptureAttempt(
        IClipboardCaptureSnapshot? Snapshot,
        uint SequenceNumber,
        bool ClipboardChanged);

    private readonly record struct ClipboardMutationResult(
        ClipboardMutationStatus Status,
        uint SequenceNumber);

    private enum ClipboardMutationStatus
    {
        Succeeded,
        Busy,
        Superseded
    }

    private readonly record struct CopiedTextObservation(
        string? Text,
        uint SequenceNumber,
        bool ClipboardChanged,
        bool ChangedInAnotherWindow)
    {
        public static CopiedTextObservation Unchanged(uint sequenceNumber) =>
            new(null, sequenceNumber, false, false);
    }
}

internal enum ClipboardRestorationStatus
{
    NotRequired,
    Restored,
    RestoredPartially,
    Superseded,
    Failed
}

internal sealed record SelectedTextCaptureResult(
    string? Text,
    ClipboardRestorationStatus ClipboardRestoration,
    string? Error)
{
    public string? ClipboardWarning => ClipboardRestoration switch
    {
        ClipboardRestorationStatus.RestoredPartially =>
            "Pulse Reader restored the standard clipboard contents, but one or more application-specific formats could not be preserved.",
        ClipboardRestorationStatus.Superseded =>
            "The clipboard changed during capture, so Pulse Reader left the newer contents in place.",
        ClipboardRestorationStatus.Failed =>
            "Pulse Reader could not restore the previous clipboard contents.",
        _ => null
    };

    public static SelectedTextCaptureResult Failed(string error) =>
        new(null, ClipboardRestorationStatus.NotRequired, error);
}
