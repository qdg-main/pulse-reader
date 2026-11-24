using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PulseReader.Settings;
using PulseReader.Speech;

namespace PulseReader.Playback;

internal sealed class PlaybackIslandForm : Form
{
    internal const int CloseButtonLingerMilliseconds = 1250;
    internal const int CloseButtonFadeMilliseconds = 180;
    internal const int IslandFadeMilliseconds = 180;
    internal const int RestartFromBeginningHoldMilliseconds = 550;
    internal const int DragActivationMinimumPixels = 8;

    private const int BaseWidth = 182;
    private const int BaseHeight = 63;
    private const int BottomMargin = 22;
    private const int WorkingAreaEdgeMargin = 8;
    private const int CloseButtonFadeFrameMilliseconds = 30;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    private readonly System.Windows.Forms.Timer _closeLingerTimer = new()
    {
        Interval = CloseButtonLingerMilliseconds
    };
    private readonly System.Windows.Forms.Timer _preparingAnimationTimer = new()
    {
        Interval = 220
    };
    private readonly System.Windows.Forms.Timer _restartFromBeginningHoldTimer = new()
    {
        Interval = RestartFromBeginningHoldMilliseconds
    };
    private readonly System.Windows.Forms.Timer _closeFadeTimer = new()
    {
        Interval = CloseButtonFadeFrameMilliseconds
    };
    private readonly System.Windows.Forms.Timer _islandFadeTimer = new()
    {
        Interval = CloseButtonFadeFrameMilliseconds
    };
    private readonly Func<PlaybackIslandPinState, bool>? _savePinState;
    private SpeechPlaybackState _state = SpeechPlaybackState.Idle;
    private PlaybackNavigationState _navigationState = PlaybackNavigationState.Unavailable;
    private IslandButton _pressedButton;
    private bool _restartFromBeginningTriggered;
    private bool _closeVisible;
    private float _closeOpacity;
    private long _closeFadeStartedAt;
    private bool _closeHovered;
    private bool _pinHovered;
    private bool _playbackHovered;
    private bool _previousHovered;
    private bool _nextHovered;
    private int _preparingFrame;
    private PlaybackIslandPinState _pinState;
    private bool _pinUpdatePending;
    private bool _manualPositionActive;
    private bool _dragCandidate;
    private bool _dragging;
    private bool _monitoring;
    private bool _monitoringPaused;
    private float _islandOpacity = 1f;
    private float _islandFadeStartOpacity = 1f;
    private bool _islandFadeTargetVisible = true;
    private long _islandFadeStartedAt;
    private Point _dragStartCursor;
    private Rectangle _dragStartBounds;
    private bool _disposed;

    public PlaybackIslandForm()
        : this(PlaybackIslandPinState.Default, null)
    {
    }

    internal PlaybackIslandForm(
        PlaybackIslandPinState pinState,
        Func<PlaybackIslandPinState, bool>? savePinState)
    {
        _pinState = pinState.IsValid()
            ? pinState
            : PlaybackIslandPinState.Default;
        _savePinState = savePinState;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Pulse Reader playback controls";
        AccessibleName = "Pulse Reader playback controls";

        _closeLingerTimer.Tick += (_, _) => HideCloseButtonIfPointerLeft();
        _closeFadeTimer.Tick += (_, _) => AdvanceCloseButtonFade();
        _islandFadeTimer.Tick += (_, _) => AdvanceIslandFade();
        _restartFromBeginningHoldTimer.Tick += (_, _) => TriggerRestartFromBeginningIfHeld();
        _preparingAnimationTimer.Tick += (_, _) =>
        {
            _preparingFrame = (_preparingFrame + 1) % 3;
            ApplyLayeredBitmap();
        };
        DpiChanged += (_, _) =>
        {
            if (!_manualPositionActive)
            {
                ApplyPreferredBounds(_lastPlacement);
            }
            else
            {
                ApplyLayeredBitmap();
            }
        };
    }

    private PlaybackIslandPlacement _lastPlacement;

    public event EventHandler? PauseRequested;

    public event EventHandler? ResumeRequested;

    public event EventHandler? StopRequested;

    public event EventHandler? RestartCurrentRequested;

    public event EventHandler? RestartFromBeginningRequested;

    public event EventHandler? NextRequested;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered | WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    public void ShowFor(
        PlaybackIslandPlacement placement,
        SpeechPlaybackState state,
        PlaybackNavigationState navigationState,
        bool monitoring = false,
        bool monitoringPaused = false)
    {
        if (_disposed || state == SpeechPlaybackState.Idle && !monitoring)
        {
            return;
        }

        _lastPlacement = placement;
        _state = state;
        _navigationState = navigationState;
        _monitoring = monitoring;
        _monitoringPaused = monitoringPaused;
        if (!_manualPositionActive)
        {
            ApplyPreferredBounds(placement);
        }
        if (!Visible)
        {
            _islandOpacity = monitoring ? 0f : 1f;
            Show();
        }

        UpdatePreparingAnimation();
        if (monitoring)
        {
            StartIslandFade(visible: true);
        }
        else
        {
            _islandFadeTimer.Stop();
            _islandFadeTargetVisible = true;
            _islandOpacity = 1f;
        }
        ApplyLayeredBitmap();
    }

    public void UpdateState(
        SpeechPlaybackState state,
        PlaybackNavigationState navigationState,
        bool monitoring = false,
        bool monitoringPaused = false)
    {
        if (_disposed)
        {
            return;
        }

        _state = state;
        _navigationState = navigationState;
        _monitoring = monitoring;
        _monitoringPaused = monitoringPaused;
        UpdatePreparingAnimation();
        if (state == SpeechPlaybackState.Idle && !monitoring)
        {
            HideIsland();
            return;
        }

        ApplyLayeredBitmap();
    }

    public void HideIsland()
    {
        ResetTransientInteraction();
        _islandFadeTimer.Stop();
        _islandFadeTargetVisible = false;
        _islandOpacity = 0f;
        if (Visible)
        {
            Hide();
        }

        _monitoring = false;
        _monitoringPaused = false;
        _manualPositionActive = false;
        _pinUpdatePending = false;
    }

    // One visible island, with independent transient drag positions for the
    // monitoring session and the temporary manual reading interaction.
    internal PlaybackIslandSessionPosition SuspendPresentation()
    {
        var position = new PlaybackIslandSessionPosition(
            _lastPlacement, Bounds, _manualPositionActive, _pinUpdatePending, _pinState);
        HideIsland();
        return position;
    }

    internal void RestorePresentation(PlaybackIslandSessionPosition position)
    {
        HideIsland();
        _lastPlacement = position.Placement;
        // A pin deliberately changed during manual playback remains global.
        _manualPositionActive = position.ManualPositionActive && position.PinState == _pinState;
        _pinUpdatePending = position.PinUpdatePending && position.PinState == _pinState;
        if (_manualPositionActive) Bounds = position.Bounds;
        else ApplyPreferredBounds(position.Placement);
    }

    public void ConcealIsland()
    {
        ResetTransientInteraction();
        if (_monitoring && Visible)
        {
            StartIslandFade(visible: false);
        }
        else if (Visible)
        {
            _islandFadeTimer.Stop();
            _islandOpacity = 0f;
            Hide();
        }
    }

    private void ResetTransientInteraction()
    {
        _closeVisible = false;
        _closeOpacity = 0f;
        _closeHovered = false;
        _pinHovered = false;
        _playbackHovered = false;
        _previousHovered = false;
        _nextHovered = false;
        _pressedButton = IslandButton.None;
        _restartFromBeginningTriggered = false;
        _dragCandidate = false;
        _dragging = false;
        Capture = false;
        Cursor = Cursors.Default;
        _closeLingerTimer.Stop();
        _closeFadeTimer.Stop();
        _preparingAnimationTimer.Stop();
        _restartFromBeginningHoldTimer.Stop();
    }

    private void StartIslandFade(bool visible)
    {
        var targetOpacity = visible ? 1f : 0f;
        if (_islandFadeTimer.Enabled
            && _islandFadeTargetVisible == visible
            || Math.Abs(_islandOpacity - targetOpacity) < 0.001f)
        {
            if (!visible && _islandOpacity <= 0f && Visible)
            {
                Hide();
            }

            return;
        }

        _islandFadeTargetVisible = visible;
        _islandFadeStartOpacity = _islandOpacity;
        _islandFadeStartedAt = Stopwatch.GetTimestamp();
        _islandFadeTimer.Start();
        AdvanceIslandFade();
    }

    private void AdvanceIslandFade()
    {
        var elapsed = Stopwatch.GetElapsedTime(_islandFadeStartedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / IslandFadeMilliseconds, 0d, 1d);
        var targetOpacity = _islandFadeTargetVisible ? 1f : 0f;
        _islandOpacity = (float)(_islandFadeStartOpacity
                                 + (targetOpacity - _islandFadeStartOpacity) * progress);
        ApplyLayeredBitmap();
        if (progress < 1d)
        {
            return;
        }

        _islandFadeTimer.Stop();
        _islandOpacity = targetOpacity;
        if (!_islandFadeTargetVisible && Visible)
        {
            Hide();
        }
    }

    public bool IsPointerNear(Point screenPoint, int distance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distance);
        return !Bounds.IsEmpty
               && Rectangle.Inflate(Bounds, distance, distance).Contains(screenPoint);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (_dragCandidate && (Control.MouseButtons & MouseButtons.Left) != 0)
        {
            var cursorPosition = Cursor.Position;
            if (!_dragging
                && HasExceededDragThreshold(
                    _dragStartCursor,
                    cursorPosition,
                    SystemInformation.DragSize))
            {
                BeginDrag();
            }

            if (_dragging)
            {
                UpdateDraggedBounds(cursorPosition);
                base.OnMouseMove(eventArgs);
                return;
            }
        }

        var metrics = Metrics();
        var closeHovered = _closeVisible && CloseButtonHitBounds(metrics).Contains(eventArgs.Location);
        var pinHovered = PinVisible && PinButtonHitBounds(metrics).Contains(eventArgs.Location);
        var playbackHovered = ContainsTransportButton(
            metrics.PlaybackButton,
            TransportButtonShape.Middle,
            eventArgs.Location,
            metrics.Scale);
        var previousHovered = ContainsTransportButton(
            metrics.PreviousButton,
            TransportButtonShape.Left,
            eventArgs.Location,
            metrics.Scale);
        var nextHovered = ContainsTransportButton(
            metrics.NextButton,
            TransportButtonShape.Right,
            eventArgs.Location,
            metrics.Scale);
        if (metrics.Pill.Contains(eventArgs.Location) || closeHovered || pinHovered)
        {
            ShowCloseButton();
        }

        if (_closeHovered != closeHovered
            || _pinHovered != pinHovered
            || _playbackHovered != playbackHovered
            || _previousHovered != previousHovered
            || _nextHovered != nextHovered)
        {
            _closeHovered = closeHovered;
            _pinHovered = pinHovered;
            _playbackHovered = playbackHovered;
            _previousHovered = previousHovered;
            _nextHovered = nextHovered;
            ApplyLayeredBitmap();
        }

        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        if (_dragging)
        {
            base.OnMouseLeave(eventArgs);
            return;
        }

        _closeHovered = false;
        _pinHovered = false;
        _playbackHovered = false;
        _previousHovered = false;
        _nextHovered = false;
        _closeLingerTimer.Stop();
        _closeLingerTimer.Start();
        ApplyLayeredBitmap();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            var metrics = Metrics();
            var pinVisible = PinVisible;
            _dragCandidate = metrics.Pill.Contains(eventArgs.Location)
                             || _closeVisible && CloseButtonHitBounds(metrics).Contains(eventArgs.Location)
                             || pinVisible && PinButtonHitBounds(metrics).Contains(eventArgs.Location);
            if (_dragCandidate)
            {
                _dragStartCursor = Cursor.Position;
                _dragStartBounds = Bounds;
                Capture = true;
            }

            _pressedButton = pinVisible && PinButtonHitBounds(metrics).Contains(eventArgs.Location)
                ? IslandButton.Pin
                : _closeVisible && CloseButtonHitBounds(metrics).Contains(eventArgs.Location)
                ? IslandButton.Close
                : ContainsTransportButton(
                    metrics.PreviousButton,
                    TransportButtonShape.Left,
                    eventArgs.Location,
                    metrics.Scale)
                    && _navigationState.CanRestartCurrent
                    ? IslandButton.Previous
                : ContainsTransportButton(
                    metrics.PlaybackButton,
                    TransportButtonShape.Middle,
                    eventArgs.Location,
                    metrics.Scale)
                    && (_monitoring
                        || _state is SpeechPlaybackState.Speaking or SpeechPlaybackState.Paused)
                    ? IslandButton.Playback
                    : ContainsTransportButton(
                        metrics.NextButton,
                        TransportButtonShape.Right,
                        eventArgs.Location,
                        metrics.Scale)
                        && _navigationState.CanMoveNext
                        ? IslandButton.Next
                    : IslandButton.None;
            _restartFromBeginningTriggered = false;
            _restartFromBeginningHoldTimer.Stop();
            if (_pressedButton == IslandButton.Previous)
            {
                _restartFromBeginningHoldTimer.Start();
            }
            ApplyLayeredBitmap();
        }

        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        var pressed = _pressedButton;
        _pressedButton = IslandButton.None;
        _restartFromBeginningHoldTimer.Stop();
        var wasDragging = _dragging;
        _dragCandidate = false;
        _dragging = false;
        Capture = false;
        Cursor = Cursors.Default;
        if (wasDragging)
        {
            _manualPositionActive = true;
            if (_pinState.IsPinned)
            {
                SaveCurrentPinnedPosition();
            }
            else
            {
                _pinUpdatePending = true;
            }

            _pinHovered = false;
            ShowCloseButton();
            base.OnMouseUp(eventArgs);
            return;
        }

        var metrics = Metrics();
        if (eventArgs.Button == MouseButtons.Left)
        {
            if (pressed == IslandButton.Pin
                && PinVisible
                && PinButtonHitBounds(metrics).Contains(eventArgs.Location))
            {
                TogglePinnedPosition();
            }
            else if (pressed == IslandButton.Close
                     && CloseButtonHitBounds(metrics).Contains(eventArgs.Location))
            {
                StopRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (pressed == IslandButton.Playback
                     && ContainsTransportButton(
                         metrics.PlaybackButton,
                         TransportButtonShape.Middle,
                         eventArgs.Location,
                         metrics.Scale))
            {
                if (_monitoringPaused || _state == SpeechPlaybackState.Paused)
                {
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (_monitoring || _state == SpeechPlaybackState.Speaking)
                {
                    PauseRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (pressed == IslandButton.Previous
                     && ContainsTransportButton(
                         metrics.PreviousButton,
                         TransportButtonShape.Left,
                         eventArgs.Location,
                         metrics.Scale)
                     && !_restartFromBeginningTriggered)
            {
                RestartCurrentRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (pressed == IslandButton.Next
                     && ContainsTransportButton(
                         metrics.NextButton,
                         TransportButtonShape.Right,
                         eventArgs.Location,
                         metrics.Scale))
            {
                NextRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        ApplyLayeredBitmap();
        base.OnMouseUp(eventArgs);
    }

    private bool PinVisible => _closeVisible;

    private void BeginDrag()
    {
        _dragging = true;
        _pressedButton = IslandButton.None;
        _restartFromBeginningTriggered = false;
        _restartFromBeginningHoldTimer.Stop();
        _closeLingerTimer.Stop();
        _closeFadeTimer.Stop();
        _closeHovered = false;
        _pinHovered = false;
        _playbackHovered = false;
        _previousHovered = false;
        _nextHovered = false;
        Cursor = Cursors.SizeAll;
    }

    private void UpdateDraggedBounds(Point cursorPosition)
    {
        var workingArea = Screen.FromPoint(cursorPosition).WorkingArea;
        var target = new Rectangle(
            _dragStartBounds.Left + cursorPosition.X - _dragStartCursor.X,
            _dragStartBounds.Top + cursorPosition.Y - _dragStartCursor.Y,
            Width,
            Height);
        Bounds = ClampBounds(
            target,
            workingArea,
            Scale(WorkingAreaEdgeMargin, Math.Max(1f, DeviceDpi / 96f)));
        _manualPositionActive = true;
        ApplyLayeredBitmap();
    }

    private void TogglePinnedPosition()
    {
        if (_pinUpdatePending || !_pinState.IsPinned)
        {
            SaveCurrentPinnedPosition();
        }
        else if (_pinState.IsPinned)
        {
            var nextState = _pinState with { IsPinned = false };
            if (_savePinState?.Invoke(nextState) ?? true)
            {
                _pinState = nextState;
                _manualPositionActive = true;
            }
        }

        ApplyLayeredBitmap();
    }

    private void SaveCurrentPinnedPosition()
    {
        var refreshedPlacement = PlaybackIslandPlacementCapture.RefreshSourceWindow(
            _lastPlacement);
        var monitorWorkingArea = Screen.FromRectangle(Bounds).WorkingArea;
        var nextState = CreatePinnedState(
            Bounds,
            monitorWorkingArea,
            refreshedPlacement,
            _pinState,
            Scale(WorkingAreaEdgeMargin, Math.Max(1f, DeviceDpi / 96f)));
        if (_savePinState?.Invoke(nextState) ?? true)
        {
            _pinState = nextState;
            _pinUpdatePending = false;
        }
    }

    private void TriggerRestartFromBeginningIfHeld()
    {
        _restartFromBeginningHoldTimer.Stop();
        if (_pressedButton != IslandButton.Previous || _restartFromBeginningTriggered)
        {
            return;
        }

        _restartFromBeginningTriggered = true;
        RestartFromBeginningRequested?.Invoke(this, EventArgs.Empty);
        ApplyLayeredBitmap();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmMouseActivate)
        {
            message.Result = (IntPtr)MaNoActivate;
            return;
        }

        base.WndProc(ref message);
    }

    private void ShowCloseButton()
    {
        _closeLingerTimer.Stop();
        _closeFadeTimer.Stop();
        var needsRedraw = !_closeVisible || _closeOpacity < 0.999f;
        _closeVisible = true;
        _closeOpacity = 1f;
        if (needsRedraw)
        {
            ApplyLayeredBitmap();
        }
    }

    private void HideCloseButtonIfPointerLeft()
    {
        _closeLingerTimer.Stop();
        if (!Visible)
        {
            return;
        }

        var localPointer = PointToClient(Cursor.Position);
        var metrics = Metrics();
        if (metrics.Pill.Contains(localPointer)
            || CloseButtonHitBounds(metrics).Contains(localPointer)
            || PinVisible && PinButtonHitBounds(metrics).Contains(localPointer))
        {
            _closeLingerTimer.Start();
            return;
        }

        _closeFadeStartedAt = Stopwatch.GetTimestamp();
        _closeFadeTimer.Start();
    }

    private void AdvanceCloseButtonFade()
    {
        if (!_closeVisible)
        {
            _closeFadeTimer.Stop();
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_closeFadeStartedAt).TotalMilliseconds;
        _closeOpacity = Math.Clamp(
            1f - (float)(elapsed / CloseButtonFadeMilliseconds),
            0f,
            1f);
        if (_closeOpacity <= 0f)
        {
            _closeFadeTimer.Stop();
            _closeVisible = false;
            _closeHovered = false;
        }

        ApplyLayeredBitmap();
    }

    private void UpdatePreparingAnimation()
    {
        if (_state == SpeechPlaybackState.Preparing && Visible)
        {
            _preparingAnimationTimer.Start();
        }
        else
        {
            _preparingAnimationTimer.Stop();
            _preparingFrame = 0;
        }
    }

    private void ApplyPreferredBounds(PlaybackIslandPlacement placement)
    {
        if (placement.WorkingArea.IsEmpty)
        {
            return;
        }

        var scale = Math.Max(1f, DeviceDpi / 96f);
        var size = new Size(Scale(BaseWidth, scale), Scale(BaseHeight, scale));
        Bounds = _pinState.IsPinned
            ? CalculatePinnedBounds(
                placement,
                size,
                _pinState,
                Scale(WorkingAreaEdgeMargin, scale))
            : CalculateBounds(
                placement,
                size,
                Scale(BottomMargin, scale),
                Scale(WorkingAreaEdgeMargin, scale));
        ApplyLayeredBitmap();
    }

    internal static Rectangle CalculateBounds(
        PlaybackIslandPlacement placement,
        Size size,
        int bottomMargin,
        int edgeMargin)
    {
        var workingArea = placement.WorkingArea;
        if (workingArea.IsEmpty || size.Width <= 0 || size.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var visibleSourceBounds = Rectangle.Intersect(
            placement.SourceWindowBounds,
            workingArea);
        var anchor = visibleSourceBounds.Width > 0 && visibleSourceBounds.Height > 0
            ? visibleSourceBounds
            : workingArea;
        var preferredLeft = anchor.Left + (anchor.Width - size.Width) / 2;
        var preferredTop = anchor.Bottom - size.Height - bottomMargin;

        var minimumLeft = workingArea.Left;
        var maximumLeft = Math.Max(minimumLeft, workingArea.Right - size.Width);
        var minimumTop = workingArea.Top;
        var maximumTop = Math.Max(minimumTop, workingArea.Bottom - size.Height);
        if (workingArea.Width >= size.Width + edgeMargin * 2)
        {
            minimumLeft += edgeMargin;
            maximumLeft -= edgeMargin;
        }

        if (workingArea.Height >= size.Height + edgeMargin + bottomMargin)
        {
            minimumTop += edgeMargin;
            maximumTop -= bottomMargin;
        }

        return new Rectangle(
            Math.Clamp(preferredLeft, minimumLeft, maximumLeft),
            Math.Clamp(preferredTop, minimumTop, maximumTop),
            size.Width,
            size.Height);
    }

    internal static Rectangle CalculatePinnedBounds(
        Rectangle workingArea,
        Size size,
        float normalizedX,
        float normalizedY,
        int edgeMargin)
    {
        if (workingArea.IsEmpty
            || size.Width <= 0
            || size.Height <= 0
            || !PlaybackIslandPinState.IsValid(normalizedX, normalizedY))
        {
            return Rectangle.Empty;
        }

        var limits = MovementLimits(workingArea, size, edgeMargin);
        return new Rectangle(
            limits.MinimumX + (int)Math.Round((limits.MaximumX - limits.MinimumX) * normalizedX),
            limits.MinimumY + (int)Math.Round((limits.MaximumY - limits.MinimumY) * normalizedY),
            size.Width,
            size.Height);
    }

    internal static Rectangle CalculatePinnedBounds(
        PlaybackIslandPlacement placement,
        Size size,
        PlaybackIslandPinState pinState,
        int edgeMargin)
    {
        if (!pinState.IsPinned || !pinState.IsValid())
        {
            return Rectangle.Empty;
        }

        var visibleSourceBounds = Rectangle.Intersect(
            placement.SourceWindowBounds,
            placement.WorkingArea);
        var canUseWindowAnchor = pinState.Anchor == PlaybackIslandPinAnchor.Window
                                 && visibleSourceBounds.Width >= size.Width
                                 && visibleSourceBounds.Height >= size.Height;
        return canUseWindowAnchor
            ? CalculatePinnedBounds(
                visibleSourceBounds,
                size,
                pinState.WindowNormalizedX,
                pinState.WindowNormalizedY,
                edgeMargin: 0)
            : CalculatePinnedBounds(
                placement.WorkingArea,
                size,
                pinState.NormalizedX,
                pinState.NormalizedY,
                edgeMargin);
    }

    internal static PlaybackIslandPinState CreatePinnedState(
        Rectangle bounds,
        Rectangle workingArea,
        int edgeMargin)
    {
        var limits = MovementLimits(workingArea, bounds.Size, edgeMargin);
        var normalizedX = limits.MaximumX == limits.MinimumX
            ? 0.5f
            : (float)(Math.Clamp(bounds.Left, limits.MinimumX, limits.MaximumX) - limits.MinimumX)
              / (limits.MaximumX - limits.MinimumX);
        var normalizedY = limits.MaximumY == limits.MinimumY
            ? 0.5f
            : (float)(Math.Clamp(bounds.Top, limits.MinimumY, limits.MaximumY) - limits.MinimumY)
              / (limits.MaximumY - limits.MinimumY);
        return new PlaybackIslandPinState(true, normalizedX, normalizedY);
    }

    internal static PlaybackIslandPinState CreatePinnedState(
        Rectangle bounds,
        Rectangle monitorWorkingArea,
        PlaybackIslandPlacement sourcePlacement,
        PlaybackIslandPinState previousState,
        int edgeMargin)
    {
        var monitorState = CreatePinnedState(bounds, monitorWorkingArea, edgeMargin);
        var visibleSourceBounds = Rectangle.Intersect(
            sourcePlacement.SourceWindowBounds,
            sourcePlacement.WorkingArea);
        if (visibleSourceBounds.Width < bounds.Width
            || visibleSourceBounds.Height < bounds.Height
            || !visibleSourceBounds.Contains(bounds))
        {
            return monitorState with
            {
                Anchor = PlaybackIslandPinAnchor.Monitor,
                WindowNormalizedX = previousState.WindowNormalizedX,
                WindowNormalizedY = previousState.WindowNormalizedY
            };
        }

        var windowState = CreatePinnedState(bounds, visibleSourceBounds, edgeMargin: 0);
        return monitorState with
        {
            Anchor = PlaybackIslandPinAnchor.Window,
            WindowNormalizedX = windowState.NormalizedX,
            WindowNormalizedY = windowState.NormalizedY
        };
    }

    internal static bool HasExceededDragThreshold(
        Point start,
        Point current,
        Size dragSize) =>
        Math.Abs(current.X - start.X) >= Math.Max(DragActivationMinimumPixels, dragSize.Width)
        || Math.Abs(current.Y - start.Y) >= Math.Max(DragActivationMinimumPixels, dragSize.Height);

    private static Rectangle ClampBounds(
        Rectangle bounds,
        Rectangle workingArea,
        int edgeMargin)
    {
        var limits = MovementLimits(workingArea, bounds.Size, edgeMargin);
        return new Rectangle(
            Math.Clamp(bounds.Left, limits.MinimumX, limits.MaximumX),
            Math.Clamp(bounds.Top, limits.MinimumY, limits.MaximumY),
            bounds.Width,
            bounds.Height);
    }

    private static MovementBounds MovementLimits(
        Rectangle workingArea,
        Size size,
        int edgeMargin)
    {
        var minimumX = workingArea.Left;
        var maximumX = Math.Max(minimumX, workingArea.Right - size.Width);
        var minimumY = workingArea.Top;
        var maximumY = Math.Max(minimumY, workingArea.Bottom - size.Height);
        if (workingArea.Width >= size.Width + edgeMargin * 2)
        {
            minimumX += edgeMargin;
            maximumX -= edgeMargin;
        }

        if (workingArea.Height >= size.Height + edgeMargin * 2)
        {
            minimumY += edgeMargin;
            maximumY -= edgeMargin;
        }

        return new MovementBounds(minimumX, maximumX, minimumY, maximumY);
    }

    private IslandMetrics Metrics()
    {
        var scale = Math.Max(1f, DeviceDpi / 96f);
        return CreateMetrics(scale);
    }

    internal static Size PreviewSize => new(BaseWidth, BaseHeight);

    internal static Point PreviewPreviousButtonCenter
    {
        get
        {
            var bounds = CreateMetrics(1f).PreviousButton;
            return Point.Round(new PointF(
                bounds.Left + bounds.Width / 2f,
                bounds.Top + bounds.Height / 2f));
        }
    }

    internal static Point PreviewPlaybackButtonCenter
    {
        get
        {
            var bounds = CreateMetrics(1f).PlaybackButton;
            return Point.Round(new PointF(
                bounds.Left + bounds.Width / 2f,
                bounds.Top + bounds.Height / 2f + Scale(2.25f, 1f)));
        }
    }

    internal static RectangleF PreviewCloseButtonBounds => CreateMetrics(1f).CloseButton;

    internal static RectangleF PreviewPinButtonBounds => CreateMetrics(1f).PinButton;

    internal static RectangleF PreviewCloseButtonHitBounds =>
        CloseButtonHitBounds(CreateMetrics(1f));

    internal static RectangleF PreviewPinButtonHitBounds =>
        PinButtonHitBounds(CreateMetrics(1f));

    internal static RectangleF PreviewTransportBounds
    {
        get
        {
            var metrics = CreateMetrics(1f);
            return RectangleF.FromLTRB(
                metrics.PreviousButton.Left,
                metrics.PreviousButton.Top,
                metrics.NextButton.Right,
                metrics.NextButton.Bottom);
        }
    }

    internal static Bitmap RenderFrame(
        Size size,
        SpeechPlaybackState state,
        bool closeVisible,
        bool closeHovered,
        bool playbackHovered,
        bool playbackPressed,
        int preparingFrame,
        PlaybackNavigationState navigationState,
        bool previousHovered = false,
        bool previousPressed = false,
        bool nextHovered = false,
        bool nextPressed = false,
        float scale = 1f,
        bool closePressed = false,
        float closeOpacity = 1f,
        bool pinVisible = false,
        bool pinHovered = false,
        bool pinPressed = false,
        bool pinPinned = false,
        float pinOpacity = 1f,
        bool monitoring = false,
        bool monitoringPaused = false)
    {
        var bitmap = new Bitmap(
            Math.Max(1, size.Width),
            Math.Max(1, size.Height),
            PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;

        var metrics = CreateMetrics(scale);

        using (var shadowPath = CreatePlayerBodyPath(
                   new RectangleF(
                       metrics.Pill.X + Scale(1f, scale),
                       metrics.Pill.Y + Scale(2f, scale),
                       metrics.Pill.Width,
                       metrics.Pill.Height),
                   scale))
        using (var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
        {
            graphics.FillPath(shadow, shadowPath);
        }

        using (var pillPath = CreatePlayerBodyPath(metrics.Pill, scale))
        using (var fill = CreateVerticalGradient(
                   metrics.Pill,
                   [
                       Color.FromArgb(255, 47, 65, 84),
                       Color.FromArgb(255, 18, 35, 55),
                       Color.FromArgb(255, 7, 17, 32)
                   ],
                   [0f, 0.28f, 1f]))
        using (var innerBorder = new Pen(Color.FromArgb(205, 64, 88, 110), Scale(1f, scale)))
        {
            DrawSoftPathShadow(graphics, pillPath, scale);
            DrawCenterWeightedBodyShadow(graphics, metrics.Pill, scale);
            graphics.FillPath(fill, pillPath);
            using var innerPath = CreatePlayerBodyPath(
                RectangleF.Inflate(metrics.Pill, Scale(-1.5f, scale), Scale(-1.5f, scale)),
                scale);
            graphics.DrawPath(innerBorder, innerPath);
        }

        DrawNavigationButton(
            graphics,
            metrics.PreviousButton,
            pointsRight: false,
            navigationState.CanRestartCurrent,
            previousHovered,
            previousPressed,
            scale);
        DrawPlaybackButton(
            graphics,
            metrics.PlaybackButton,
            state,
            playbackHovered,
            playbackPressed,
            preparingFrame,
            scale,
            monitoring,
            monitoringPaused);
        DrawNavigationButton(
            graphics,
            metrics.NextButton,
            pointsRight: true,
            navigationState.CanMoveNext,
            nextHovered,
            nextPressed,
            scale);
        DrawTransportAssemblyFinish(
            graphics,
            metrics,
            previousPressed && navigationState.CanRestartCurrent,
            state == SpeechPlaybackState.Paused
                || monitoringPaused
                || playbackPressed && state == SpeechPlaybackState.Speaking,
            nextPressed && navigationState.CanMoveNext,
            scale);

        if (closeVisible)
        {
            DrawCloseButton(
                graphics,
                metrics.CloseButton,
                closeHovered,
                closePressed,
                scale,
                closeOpacity);
        }

        if (pinVisible)
        {
            // A fully transparent pixel in a layered window does not reliably
            // participate in mouse hit-testing. Keep the floating pin visually
            // background-free while giving its complete logical target a
            // practically invisible alpha surface.
            using (var pinHitSurface = new SolidBrush(
                       Color.FromArgb(pinOpacity > 0f ? 4 : 0, 0, 0, 0)))
            {
                graphics.FillRectangle(pinHitSurface, PinButtonHitBounds(metrics));
            }

            DrawPinButton(
                graphics,
                metrics.PinButton,
                pinHovered,
                pinPressed,
                pinPinned,
                scale,
                pinOpacity);
        }

        return bitmap;
    }

    private static IslandMetrics CreateMetrics(float scale) =>
        new(
            new RectangleF(Scale(13, scale), Scale(19, scale), Scale(156, scale), Scale(34, scale)),
            new RectangleF(Scale(14, scale), Scale(20, scale), Scale(52, scale), Scale(31, scale)),
            new RectangleF(Scale(66, scale), Scale(20, scale), Scale(50, scale), Scale(31, scale)),
            new RectangleF(Scale(116, scale), Scale(20, scale), Scale(52, scale), Scale(31, scale)),
            new RectangleF(Scale(5, scale), Scale(2, scale), Scale(18, scale), Scale(18, scale)),
            new RectangleF(Scale(159, scale), Scale(2, scale), Scale(18, scale), Scale(18, scale)),
            scale);

    private static RectangleF PinButtonHitBounds(IslandMetrics metrics) =>
        FloatingButtonHitBounds(metrics.PinButton, metrics.Scale);

    private static RectangleF CloseButtonHitBounds(IslandMetrics metrics) =>
        FloatingButtonHitBounds(metrics.CloseButton, metrics.Scale);

    private static RectangleF FloatingButtonHitBounds(RectangleF artworkBounds, float scale) =>
        new(
            artworkBounds.Left - Scale(5f, scale),
            artworkBounds.Top - Scale(2f, scale),
            artworkBounds.Width + Scale(10f, scale),
            artworkBounds.Height + Scale(2f, scale));

    private static void DrawNavigationButton(
        Graphics graphics,
        RectangleF bounds,
        bool pointsRight,
        bool enabled,
        bool hovered,
        bool pressed,
        float scale)
    {
        var shape = pointsRight ? TransportButtonShape.Right : TransportButtonShape.Left;
        var iconOffset = DrawTransportButton(
            graphics,
            bounds,
            shape,
            enabled,
            hovered,
            pressed,
            scale);

        var iconColor = !enabled
            ? PulseReaderTheme.DisabledText
            : hovered
                ? PulseReaderTheme.AccentBright
                : PulseReaderTheme.Text;
        using var iconFill = new SolidBrush(iconColor);
        using var iconLine = new Pen(iconColor, Scale(1.7f, scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var centerX = bounds.Left + bounds.Width / 2f;
        var centerY = bounds.Top + bounds.Height / 2f + iconOffset;
        var direction = pointsRight ? 1f : -1f;
        var iconPoints = new[]
        {
            new PointF(centerX + direction * Scale(5.2f, scale), centerY),
            new PointF(centerX - direction * Scale(2.6f, scale), centerY - Scale(5.2f, scale)),
            new PointF(centerX - direction * Scale(2.6f, scale), centerY + Scale(5.2f, scale))
        };
        if (hovered && enabled)
        {
            DrawSoftIconGlow(
                graphics,
                new PointF(centerX, centerY),
                new SizeF(Scale(26f, scale), Scale(21f, scale)));
        }

        graphics.FillPolygon(
            iconFill,
            iconPoints);
        var barX = centerX + direction * Scale(6.2f, scale);
        graphics.DrawLine(
            iconLine,
            barX,
            centerY - Scale(5.2f, scale),
            barX,
            centerY + Scale(5.2f, scale));
    }

    private static void DrawPlaybackButton(
        Graphics graphics,
        RectangleF bounds,
        SpeechPlaybackState state,
        bool hovered,
        bool pressed,
        int preparingFrame,
        float scale,
        bool monitoring,
        bool monitoringPaused)
    {
        var active = monitoring
                     || state is SpeechPlaybackState.Speaking or SpeechPlaybackState.Paused;
        var latched = monitoringPaused || state == SpeechPlaybackState.Paused;
        var iconOffset = DrawTransportButton(
            graphics,
            bounds,
            TransportButtonShape.Middle,
            active,
            hovered,
            pressed,
            scale,
            latched);

        if (state == SpeechPlaybackState.Preparing)
        {
            var dotSize = Scale(4f, scale);
            var gap = Scale(7f, scale);
            var centerX = bounds.Left + bounds.Width / 2f;
            var centerY = bounds.Top + bounds.Height / 2f + iconOffset;
            for (var index = 0; index < 3; index++)
            {
                var alpha = index == preparingFrame ? 255 : 95;
                using var dot = new SolidBrush(Color.FromArgb(alpha, PulseReaderTheme.AccentBright));
                graphics.FillEllipse(
                    dot,
                    centerX + (index - 1) * gap - dotSize / 2f,
                    centerY - dotSize / 2f,
                    dotSize,
                    dotSize);
            }

            return;
        }

        var iconColor = hovered && active ? PulseReaderTheme.AccentBright : PulseReaderTheme.Text;
        using var icon = new Pen(iconColor, Scale(2.35f, scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        var center = new PointF(
            bounds.Left + bounds.Width / 2f,
            bounds.Top + bounds.Height / 2f + iconOffset);
        if (hovered && active)
        {
            DrawSoftIconGlow(
                graphics,
                center,
                new SizeF(Scale(27f, scale), Scale(23f, scale)));
        }

        if (monitoring && state == SpeechPlaybackState.Idle && !monitoringPaused)
        {
            DrawMonitoringIcon(graphics, center, iconColor, scale);
            return;
        }

        var showPlayIcon = monitoringPaused
            || state == SpeechPlaybackState.Paused
            || pressed && state == SpeechPlaybackState.Speaking;
        if (showPlayIcon)
        {
            using var playFill = new SolidBrush(iconColor);
            graphics.FillPolygon(
                playFill,
                [
                    new PointF(center.X - Scale(3.5f, scale), center.Y - Scale(6f, scale)),
                    new PointF(center.X + Scale(6f, scale), center.Y),
                    new PointF(center.X - Scale(3.5f, scale), center.Y + Scale(6f, scale))
                ]);
        }
        else
        {
            graphics.DrawLine(
                icon,
                center.X - Scale(3.5f, scale),
                center.Y - Scale(6f, scale),
                center.X - Scale(3.5f, scale),
                center.Y + Scale(6f, scale));
            graphics.DrawLine(
                icon,
                center.X + Scale(3.5f, scale),
                center.Y - Scale(6f, scale),
                center.X + Scale(3.5f, scale),
                center.Y + Scale(6f, scale));
        }

        if (monitoring)
        {
            DrawMonitoringBadge(graphics, bounds, scale);
        }
    }

    private static void DrawMonitoringIcon(
        Graphics graphics,
        PointF center,
        Color color,
        float scale)
    {
        using var fill = new SolidBrush(color);
        using var waves = new Pen(color, Scale(1.45f, scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var dot = Scale(3.6f, scale);
        graphics.FillEllipse(fill, center.X - dot / 2f, center.Y - dot / 2f, dot, dot);
        graphics.DrawArc(
            waves,
            center.X - Scale(7f, scale),
            center.Y - Scale(7f, scale),
            Scale(14f, scale),
            Scale(14f, scale),
            -48f,
            96f);
        graphics.DrawArc(
            waves,
            center.X - Scale(11f, scale),
            center.Y - Scale(11f, scale),
            Scale(22f, scale),
            Scale(22f, scale),
            -42f,
            84f);
    }

    private static void DrawMonitoringBadge(Graphics graphics, RectangleF bounds, float scale)
    {
        var size = Scale(4.2f, scale);
        var center = new PointF(
            bounds.Right - Scale(8f, scale),
            bounds.Top + Scale(7f, scale));
        using var glow = new SolidBrush(Color.FromArgb(55, PulseReaderTheme.AccentBright));
        using var fill = new SolidBrush(PulseReaderTheme.AccentBright);
        graphics.FillEllipse(
            glow,
            center.X - size,
            center.Y - size,
            size * 2f,
            size * 2f);
        graphics.FillEllipse(
            fill,
            center.X - size / 2f,
            center.Y - size / 2f,
            size,
            size);
    }

    private static float DrawTransportButton(
        Graphics graphics,
        RectangleF bounds,
        TransportButtonShape shape,
        bool enabled,
        bool hovered,
        bool pressed,
        float scale,
        bool latched = false)
    {
        var isPressed = pressed && enabled;
        var isLatched = latched && enabled;
        var isDepressed = isPressed || isLatched;
        var pressedOffset = isPressed
            ? Scale(2.25f, scale)
            : isLatched
                ? Scale(1.45f, scale)
                : 0f;
        var face = new RectangleF(
            bounds.X,
            bounds.Y + pressedOffset,
            bounds.Width,
            bounds.Height - pressedOffset * 0.45f);
        var shadowBounds = new RectangleF(
            bounds.X,
            bounds.Y + Scale(2.4f, scale),
            bounds.Width,
            bounds.Height);

        using (var shadowPath = CreateTransportButtonPath(shadowBounds, shape, scale))
        using (var shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
        {
            graphics.FillPath(shadow, shadowPath);
        }

        var faceColors = !enabled
            ? new[]
            {
                Color.FromArgb(255, 35, 43, 52),
                Color.FromArgb(255, 19, 27, 36),
                Color.FromArgb(255, 9, 14, 21),
                Color.FromArgb(255, 14, 20, 27),
                Color.FromArgb(255, 4, 7, 12)
            }
            : isPressed
                ? new[]
                {
                    Color.FromArgb(255, 3, 7, 13),
                    Color.FromArgb(255, 7, 16, 29),
                    Color.FromArgb(255, 18, 36, 54),
                    Color.FromArgb(255, 10, 23, 38),
                    Color.FromArgb(255, 3, 7, 15)
                }
                : isLatched
                    ? new[]
                    {
                        Color.FromArgb(255, 5, 11, 19),
                        Color.FromArgb(255, 11, 24, 39),
                        Color.FromArgb(255, 27, 47, 65),
                        Color.FromArgb(255, 14, 30, 47),
                        Color.FromArgb(255, 4, 9, 18)
                    }
                    : new[]
                    {
                        Color.FromArgb(255, 57, 73, 90),
                        Color.FromArgb(255, 31, 49, 69),
                        Color.FromArgb(255, 10, 22, 38),
                        Color.FromArgb(255, 19, 37, 57),
                        Color.FromArgb(255, 4, 10, 21)
                    };
        using var facePath = CreateTransportButtonPath(face, shape, scale);
        using var faceFill = CreateVerticalGradient(
            face,
            faceColors,
            [0f, 0.12f, 0.52f, 0.72f, 1f]);
        using var border = new Pen(
            enabled
                ? Color.FromArgb(isDepressed ? 135 : 70, 74, 91, 105)
                : Color.FromArgb(90, PulseReaderTheme.DisabledBorder),
            Scale(1f, scale));
        graphics.FillPath(faceFill, facePath);
        graphics.DrawPath(border, facePath);

        if (!isDepressed)
        {
            var savedState = graphics.Save();
            graphics.SetClip(facePath, CombineMode.Intersect);
            DrawFaceTexture(graphics, face, enabled, scale);
            graphics.Restore(savedState);

            using var lowerRim = new Pen(Color.FromArgb(220, 1, 4, 8), Scale(1.2f, scale));
            graphics.DrawLine(
                lowerRim,
                face.Left + Scale(shape == TransportButtonShape.Left ? 7.5f : 0.8f, scale),
                face.Bottom - Scale(1.4f, scale),
                face.Right - Scale(shape == TransportButtonShape.Right ? 7.5f : 0.8f, scale),
                face.Bottom - Scale(1.4f, scale));
        }

        if (isDepressed)
        {
            using var upperInset = new Pen(
                Color.FromArgb(isPressed ? 230 : 195, 1, 4, 8),
                Scale(isPressed ? 1.55f : 1.3f, scale));
            graphics.DrawLine(
                upperInset,
                face.Left + Scale(shape == TransportButtonShape.Left ? 9f : 3f, scale),
                face.Top + Scale(1.5f, scale),
                face.Right - Scale(shape == TransportButtonShape.Right ? 9f : 3f, scale),
                face.Top + Scale(1.5f, scale));

            using var lowerBounce = new Pen(
                Color.FromArgb(isPressed ? 76 : 100, 131, 151, 166),
                Scale(0.9f, scale));
            graphics.DrawLine(
                lowerBounce,
                face.Left + Scale(shape == TransportButtonShape.Left ? 8f : 3f, scale),
                face.Bottom - Scale(1.5f, scale),
                face.Right - Scale(shape == TransportButtonShape.Right ? 8f : 3f, scale),
                face.Bottom - Scale(1.5f, scale));
        }

        return pressedOffset;
    }

    private static void DrawTransportAssemblyFinish(
        Graphics graphics,
        IslandMetrics metrics,
        bool previousPressed,
        bool playbackPressed,
        bool nextPressed,
        float scale)
    {
        var assemblyBounds = RectangleF.FromLTRB(
            metrics.PreviousButton.Left,
            metrics.PreviousButton.Top,
            metrics.NextButton.Right,
            metrics.NextButton.Bottom);
        using var assemblyPath = CreatePlayerBodyPath(assemblyBounds, scale);

        var savedState = graphics.Save();
        using (var raisedFaces = new GraphicsPath(FillMode.Winding))
        {
            AddRaisedFace(metrics.PreviousButton, TransportButtonShape.Left, previousPressed);
            AddRaisedFace(metrics.PlaybackButton, TransportButtonShape.Middle, playbackPressed);
            AddRaisedFace(metrics.NextButton, TransportButtonShape.Right, nextPressed);
            graphics.SetClip(raisedFaces, CombineMode.Intersect);

            var crownBounds = new RectangleF(
                assemblyBounds.X + Scale(2f, scale),
                assemblyBounds.Y - Scale(9f, scale),
                assemblyBounds.Width - Scale(4f, scale),
                Scale(25f, scale));
            using var crownPath = new GraphicsPath();
            crownPath.AddEllipse(crownBounds);
            using var crown = new PathGradientBrush(crownPath)
            {
                CenterPoint = new PointF(
                    crownBounds.Left + crownBounds.Width / 2f,
                    assemblyBounds.Top + Scale(1f, scale)),
                InterpolationColors = new ColorBlend
                {
                    Colors =
                    [
                        Color.FromArgb(0, 95, 123, 143),
                        Color.FromArgb(20, 195, 211, 222),
                        Color.FromArgb(58, 240, 245, 248)
                    ],
                    Positions = [0f, 0.58f, 1f]
                }
            };
            graphics.FillPath(crown, crownPath);

            var topLineBounds = new RectangleF(
                assemblyBounds.Left + Scale(8f, scale),
                assemblyBounds.Top + Scale(1.1f, scale),
                assemblyBounds.Width - Scale(16f, scale),
                Math.Max(1f, Scale(0.9f, scale)));
            using var topLine = new LinearGradientBrush(
                topLineBounds,
                Color.Transparent,
                Color.Transparent,
                LinearGradientMode.Horizontal)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors =
                    [
                        Color.FromArgb(0, 226, 236, 243),
                        Color.FromArgb(42, 226, 236, 243),
                        Color.FromArgb(72, 238, 244, 248),
                        Color.FromArgb(42, 226, 236, 243),
                        Color.FromArgb(0, 226, 236, 243)
                    ],
                    Positions = [0f, 0.14f, 0.5f, 0.86f, 1f]
                }
            };
            graphics.FillRectangle(topLine, topLineBounds);

            void AddRaisedFace(RectangleF bounds, TransportButtonShape shape, bool isPressed)
            {
                if (isPressed)
                {
                    return;
                }

                using var path = CreateTransportButtonPath(bounds, shape, scale);
                raisedFaces.AddPath(path, false);
            }
        }
        graphics.Restore(savedState);

        using (var outerEdge = new Pen(Color.FromArgb(215, 77, 93, 107), Scale(0.95f, scale)))
        {
            graphics.DrawPath(outerEdge, assemblyPath);
        }

        DrawSeam(metrics.PlaybackButton.Left);
        DrawSeam(metrics.PlaybackButton.Right);

        void DrawSeam(float x)
        {
            var top = assemblyBounds.Top + Scale(5.2f, scale);
            var bottom = assemblyBounds.Bottom - Scale(3.3f, scale);
            using var recess = new Pen(Color.FromArgb(225, 1, 4, 8), Scale(1.15f, scale));
            using var bevel = new Pen(Color.FromArgb(105, 104, 120, 133), Scale(0.8f, scale));
            graphics.DrawLine(recess, x, top, x, bottom);
            graphics.DrawLine(bevel, x + Scale(1f, scale), top + Scale(0.8f, scale), x + Scale(1f, scale), bottom);
        }
    }

    private static LinearGradientBrush CreateVerticalGradient(
        RectangleF bounds,
        Color[] colors,
        float[] positions)
    {
        var brush = new LinearGradientBrush(
            bounds,
            colors[0],
            colors[^1],
            LinearGradientMode.Vertical)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = colors,
                Positions = positions
            }
        };
        return brush;
    }

    private static void DrawSoftPathShadow(
        Graphics graphics,
        GraphicsPath path,
        float scale,
        float opacity = 1f)
    {
        DrawLayer(8f, 4);
        DrawLayer(5.5f, 6);
        DrawLayer(3.5f, 9);
        DrawLayer(2f, 12);

        var bounds = path.GetBounds();
        var savedState = graphics.Save();
        graphics.SetClip(
            new RectangleF(
                bounds.Left - Scale(10f, scale),
                bounds.Top + bounds.Height * 0.32f,
                bounds.Width + Scale(20f, scale),
                bounds.Height + Scale(10f, scale)),
            CombineMode.Intersect);
        DrawLayer(6.5f, 4);
        DrawLayer(4f, 7);
        DrawLayer(2.2f, 10);
        graphics.Restore(savedState);

        void DrawLayer(float width, int alpha)
        {
            using var shadow = new Pen(
                Color.FromArgb(ScaledAlpha(alpha, opacity), 0, 0, 0),
                Scale(width, scale))
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawPath(shadow, path);
        }
    }

    private static void DrawCenterWeightedBodyShadow(
        Graphics graphics,
        RectangleF bodyBounds,
        float scale)
    {
        DrawEllipticalLayer(
            new RectangleF(
                bodyBounds.Left + Scale(12f, scale),
                bodyBounds.Bottom - Scale(4f, scale),
                bodyBounds.Width - Scale(24f, scale),
                Scale(9f, scale)),
            42,
            16,
            Scale(1f, scale),
            0.18f);
        DrawEllipticalLayer(
            new RectangleF(
                bodyBounds.Left + Scale(24f, scale),
                bodyBounds.Bottom - Scale(2f, scale),
                bodyBounds.Width - Scale(48f, scale),
                Scale(11f, scale)),
            70,
            22,
            Scale(2.5f, scale),
            0.42f);

        void DrawEllipticalLayer(
            RectangleF bounds,
            int centerAlpha,
            int middleAlpha,
            float centerYOffset,
            float horizontalFocus)
        {
            using var shadowPath = new GraphicsPath();
            shadowPath.AddEllipse(bounds);
            using var shadow = new PathGradientBrush(shadowPath)
            {
                CenterPoint = new PointF(
                    bodyBounds.Left + bodyBounds.Width / 2f,
                    bodyBounds.Bottom + centerYOffset),
                FocusScales = new PointF(horizontalFocus, 0.06f),
                InterpolationColors = new ColorBlend
                {
                    Colors =
                    [
                        Color.FromArgb(0, 0, 0, 0),
                        Color.FromArgb(middleAlpha, 0, 0, 0),
                        Color.FromArgb(centerAlpha, 0, 0, 0)
                    ],
                    Positions = [0f, 0.58f, 1f]
                }
            };
            graphics.FillPath(shadow, shadowPath);
        }
    }

    private static void DrawSoftIconGlow(
        Graphics graphics,
        PointF center,
        SizeF size,
        float opacity = 1f)
    {
        var bounds = new RectangleF(
            center.X - size.Width / 2f,
            center.Y - size.Height / 2f,
            size.Width,
            size.Height);
        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var glow = new PathGradientBrush(path)
        {
            CenterPoint = center,
            InterpolationColors = new ColorBlend
            {
                Colors =
                [
                    Color.FromArgb(0, PulseReaderTheme.Accent),
                    Color.FromArgb(ScaledAlpha(10, opacity), PulseReaderTheme.Accent),
                    Color.FromArgb(ScaledAlpha(38, opacity), PulseReaderTheme.Accent),
                    Color.FromArgb(ScaledAlpha(82, opacity), PulseReaderTheme.AccentBright)
                ],
                Positions = [0f, 0.32f, 0.72f, 1f]
            }
        };
        graphics.FillPath(glow, path);
    }

    private static void DrawFaceTexture(Graphics graphics, RectangleF face, bool enabled, float scale)
    {
        var light = Color.FromArgb(enabled ? 22 : 10, 214, 229, 239);
        var dark = Color.FromArgb(enabled ? 30 : 14, 0, 0, 0);
        var pixel = Math.Max(1f, Scale(0.65f, scale));
        var points = new (float X, float Y, bool Light)[]
        {
            (0.18f, 0.33f, true),
            (0.32f, 0.60f, false),
            (0.44f, 0.28f, true),
            (0.57f, 0.73f, true),
            (0.68f, 0.43f, false),
            (0.81f, 0.64f, true)
        };
        foreach (var point in points)
        {
            using var fleck = new SolidBrush(point.Light ? light : dark);
            graphics.FillRectangle(
                fleck,
                face.Left + face.Width * point.X,
                face.Top + face.Height * point.Y,
                pixel,
                pixel);
        }
    }

    private static bool ContainsTransportButton(
        RectangleF bounds,
        TransportButtonShape shape,
        Point point,
        float scale)
    {
        using var path = CreateTransportButtonPath(bounds, shape, scale);
        return path.IsVisible(point);
    }

    private static GraphicsPath CreatePlayerBodyPath(RectangleF bounds, float scale)
    {
        var pointDepth = Math.Min(Scale(8f, scale), bounds.Width / 5f);
        var shoulder = Math.Min(Scale(4f, scale), bounds.Height / 4f);
        var noseRadius = Math.Min(Scale(1.8f, scale), bounds.Height / 8f);
        var middleY = bounds.Top + bounds.Height / 2f;
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(bounds.Left + pointDepth, bounds.Top, bounds.Right - pointDepth, bounds.Top);
        path.AddBezier(
            bounds.Right - pointDepth, bounds.Top,
            bounds.Right - shoulder, bounds.Top + shoulder,
            bounds.Right - Scale(1f, scale), middleY - shoulder,
            bounds.Right - Scale(1f, scale), middleY - noseRadius);
        path.AddBezier(
            bounds.Right - Scale(1f, scale), middleY - noseRadius,
            bounds.Right, middleY - noseRadius * 0.5f,
            bounds.Right, middleY + noseRadius * 0.5f,
            bounds.Right - Scale(1f, scale), middleY + noseRadius);
        path.AddBezier(
            bounds.Right - Scale(1f, scale), middleY + noseRadius,
            bounds.Right - shoulder, bounds.Bottom - shoulder,
            bounds.Right - pointDepth, bounds.Bottom,
            bounds.Right - pointDepth, bounds.Bottom);
        path.AddLine(bounds.Right - pointDepth, bounds.Bottom, bounds.Left + pointDepth, bounds.Bottom);
        path.AddBezier(
            bounds.Left + pointDepth, bounds.Bottom,
            bounds.Left + shoulder, bounds.Bottom - shoulder,
            bounds.Left + Scale(1f, scale), middleY + shoulder,
            bounds.Left + Scale(1f, scale), middleY + noseRadius);
        path.AddBezier(
            bounds.Left + Scale(1f, scale), middleY + noseRadius,
            bounds.Left, middleY + noseRadius * 0.5f,
            bounds.Left, middleY - noseRadius * 0.5f,
            bounds.Left + Scale(1f, scale), middleY - noseRadius);
        path.AddBezier(
            bounds.Left + Scale(1f, scale), middleY - noseRadius,
            bounds.Left + shoulder, bounds.Top + shoulder,
            bounds.Left + pointDepth, bounds.Top,
            bounds.Left + pointDepth, bounds.Top);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath CreateTransportButtonPath(
        RectangleF bounds,
        TransportButtonShape shape,
        float scale)
    {
        if (shape == TransportButtonShape.Middle)
        {
            return PulseReaderTheme.RoundedRectangle(bounds, Scale(1.5f, scale));
        }

        var pointDepth = Math.Min(Scale(8f, scale), bounds.Width / 4f);
        var shoulder = Math.Min(Scale(4f, scale), bounds.Height / 4f);
        var noseRadius = Math.Min(Scale(1.8f, scale), bounds.Height / 8f);
        var middleY = bounds.Top + bounds.Height / 2f;
        var path = new GraphicsPath();
        if (shape == TransportButtonShape.Left)
        {
            path.StartFigure();
            path.AddLine(bounds.Right, bounds.Top, bounds.Left + pointDepth, bounds.Top);
            path.AddBezier(
                bounds.Left + pointDepth, bounds.Top,
                bounds.Left + shoulder, bounds.Top + shoulder,
                bounds.Left + Scale(1f, scale), middleY - shoulder,
                bounds.Left + Scale(1f, scale), middleY - noseRadius);
            path.AddBezier(
                bounds.Left + Scale(1f, scale), middleY - noseRadius,
                bounds.Left, middleY - noseRadius * 0.5f,
                bounds.Left, middleY + noseRadius * 0.5f,
                bounds.Left + Scale(1f, scale), middleY + noseRadius);
            path.AddBezier(
                bounds.Left + Scale(1f, scale), middleY + noseRadius,
                bounds.Left + shoulder, bounds.Bottom - shoulder,
                bounds.Left + pointDepth, bounds.Bottom,
                bounds.Left + pointDepth, bounds.Bottom);
            path.AddLine(bounds.Left + pointDepth, bounds.Bottom, bounds.Right, bounds.Bottom);
        }
        else
        {
            path.StartFigure();
            path.AddLine(bounds.Left, bounds.Top, bounds.Right - pointDepth, bounds.Top);
            path.AddBezier(
                bounds.Right - pointDepth, bounds.Top,
                bounds.Right - shoulder, bounds.Top + shoulder,
                bounds.Right - Scale(1f, scale), middleY - shoulder,
                bounds.Right - Scale(1f, scale), middleY - noseRadius);
            path.AddBezier(
                bounds.Right - Scale(1f, scale), middleY - noseRadius,
                bounds.Right, middleY - noseRadius * 0.5f,
                bounds.Right, middleY + noseRadius * 0.5f,
                bounds.Right - Scale(1f, scale), middleY + noseRadius);
            path.AddBezier(
                bounds.Right - Scale(1f, scale), middleY + noseRadius,
                bounds.Right - shoulder, bounds.Bottom - shoulder,
                bounds.Right - pointDepth, bounds.Bottom,
                bounds.Right - pointDepth, bounds.Bottom);
            path.AddLine(bounds.Right - pointDepth, bounds.Bottom, bounds.Left, bounds.Bottom);
        }

        path.CloseFigure();
        return path;
    }

    private static void DrawCloseButton(
        Graphics graphics,
        RectangleF bounds,
        bool hovered,
        bool pressed,
        float scale,
        float opacity)
    {
        var center = DrawFloatingButtonFace(
            graphics,
            bounds,
            hovered,
            pressed,
            active: false,
            scale,
            opacity);
        if (opacity <= 0f)
        {
            return;
        }

        var offset = Scale(3.2f, scale);
        using var cross = new Pen(
            WithOpacity(
                hovered ? PulseReaderTheme.AccentBright : PulseReaderTheme.Text,
                opacity),
            Scale(1.3f, scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(cross, center.X - offset, center.Y - offset, center.X + offset, center.Y + offset);
        graphics.DrawLine(cross, center.X + offset, center.Y - offset, center.X - offset, center.Y + offset);
    }

    private static void DrawPinButton(
        Graphics graphics,
        RectangleF bounds,
        bool hovered,
        bool pressed,
        bool pinned,
        float scale,
        float opacity)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        if (opacity <= 0f)
        {
            return;
        }

        var seatedOffset = pinned ? Scale(1.15f, scale) : 0f;
        var pressedOffset = pressed ? Scale(0.9f, scale) : 0f;
        var center = new PointF(
            bounds.Left + bounds.Width / 2f + seatedOffset,
            bounds.Top + bounds.Height / 2f + seatedOffset + pressedOffset);
        if (hovered || pinned)
        {
            DrawSoftIconGlow(
                graphics,
                center,
                new SizeF(
                    Scale(pinned ? 15f : 12f, scale),
                    Scale(pinned ? 15f : 12f, scale)),
                opacity * (pinned ? 0.95f : 0.38f));
        }

        if (pinned)
        {
            using var contactShadow = new SolidBrush(
                Color.FromArgb(ScaledAlpha(150, opacity), 0, 2, 6));
            graphics.FillEllipse(
                contactShadow,
                center.X - Scale(4.6f, scale),
                center.Y + Scale(1.8f, scale),
                Scale(4.5f, scale),
                Scale(2.7f, scale));
        }

        var color = pinned
            ? PulseReaderTheme.AccentBright
            : hovered
                ? PulseReaderTheme.Text
                : PulseReaderTheme.MutedText;
        var savedState = graphics.Save();
        graphics.TranslateTransform(center.X, center.Y);
        graphics.RotateTransform(-42f);
        DrawGlyph(
            Color.FromArgb(ScaledAlpha(pinned ? 180 : 125, opacity), 0, 2, 7),
            Scale(1.65f, scale),
            Scale(0.75f, scale),
            Scale(0.8f, scale));
        if (pinned)
        {
            DrawGlyph(
                Color.FromArgb(ScaledAlpha(90, opacity), PulseReaderTheme.Accent),
                Scale(2.55f, scale),
                0f,
                0f);
        }

        DrawGlyph(
            WithOpacity(color, opacity),
            Scale(1.15f, scale),
            0f,
            0f);
        graphics.Restore(savedState);

        void DrawGlyph(Color strokeColor, float width, float offsetX, float offsetY)
        {
            using var pin = new Pen(strokeColor, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var top = -Scale(4f, scale) + offsetY;
            var shoulder = Scale(2.6f, scale);
            var bodyBottom = Scale(0.8f, scale) + offsetY;
            graphics.DrawLine(pin, -shoulder + offsetX, top, shoulder + offsetX, top);
            graphics.DrawLine(
                pin,
                -Scale(1.9f, scale) + offsetX,
                top,
                -Scale(1f, scale) + offsetX,
                bodyBottom);
            graphics.DrawLine(
                pin,
                Scale(1.9f, scale) + offsetX,
                top,
                Scale(1f, scale) + offsetX,
                bodyBottom);
            graphics.DrawLine(
                pin,
                -Scale(1f, scale) + offsetX,
                bodyBottom,
                Scale(1f, scale) + offsetX,
                bodyBottom);
            graphics.DrawLine(
                pin,
                offsetX,
                bodyBottom,
                offsetX,
                Scale(4.1f, scale) + offsetY);
        }
    }

    private static PointF DrawFloatingButtonFace(
        Graphics graphics,
        RectangleF bounds,
        bool hovered,
        bool pressed,
        bool active,
        float scale,
        float opacity)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        if (opacity <= 0f)
        {
            return PointF.Empty;
        }

        using var shadow = new SolidBrush(
            Color.FromArgb(ScaledAlpha(27, opacity), 0, 0, 0));
        graphics.FillEllipse(
            shadow,
            new RectangleF(
                bounds.X + Scale(1f, scale),
                bounds.Y + Scale(2f, scale),
                bounds.Width,
                bounds.Height));

        using (var shadowPath = new GraphicsPath())
        {
            shadowPath.AddEllipse(bounds);
            DrawSoftPathShadow(graphics, shadowPath, scale, opacity);
        }

        var pressedOffset = pressed ? Scale(1f, scale) : 0f;
        var face = new RectangleF(
            bounds.X,
            bounds.Y + pressedOffset,
            bounds.Width,
            bounds.Height - pressedOffset * 0.5f);
        var colors = pressed
            ? new[]
            {
                WithOpacity(Color.FromArgb(255, 5, 12, 22), opacity),
                WithOpacity(Color.FromArgb(255, 14, 31, 48), opacity),
                WithOpacity(Color.FromArgb(255, 4, 9, 17), opacity)
            }
            : hovered || active
                ? new[]
                {
                    WithOpacity(Color.FromArgb(255, 62, 86, 108), opacity),
                    WithOpacity(Color.FromArgb(255, 23, 49, 73), opacity),
                    WithOpacity(Color.FromArgb(255, 5, 14, 27), opacity)
                }
                : new[]
                {
                    WithOpacity(Color.FromArgb(255, 48, 65, 83), opacity),
                    WithOpacity(Color.FromArgb(255, 18, 37, 57), opacity),
                    WithOpacity(Color.FromArgb(255, 4, 11, 22), opacity)
                };
        using var background = CreateVerticalGradient(face, colors, [0f, 0.48f, 1f]);
        using var steelBorder = new Pen(
            active
                ? Color.FromArgb(ScaledAlpha(235, opacity), PulseReaderTheme.AccentBright)
                : Color.FromArgb(ScaledAlpha(220, opacity), 72, 96, 116),
            Scale(0.85f, scale));
        graphics.FillEllipse(background, face);
        graphics.DrawEllipse(
            steelBorder,
            RectangleF.Inflate(face, Scale(-1.2f, scale), Scale(-1.2f, scale)));

        var center = new PointF(
            face.Left + face.Width / 2f,
            face.Top + face.Height / 2f);
        if (!pressed)
        {
            using var topHighlight = new Pen(
                Color.FromArgb(ScaledAlpha(105, opacity), 226, 238, 246),
                Scale(0.8f, scale));
            using var lowerLip = new Pen(
                Color.FromArgb(ScaledAlpha(205, opacity), 1, 4, 9),
                Scale(0.95f, scale));
            var inset = RectangleF.Inflate(face, Scale(-2.2f, scale), Scale(-2.2f, scale));
            graphics.DrawArc(topHighlight, inset, 198f, 144f);
            graphics.DrawArc(lowerLip, inset, 18f, 144f);
        }

        if (hovered || active)
        {
            DrawSoftIconGlow(
                graphics,
                center,
                new SizeF(Scale(15f, scale), Scale(15f, scale)),
                opacity);
        }

        return center;
    }

    private void ApplyLayeredBitmap()
    {
        if (_disposed || !IsHandleCreated || Width <= 0 || Height <= 0)
        {
            return;
        }

        var scale = Math.Max(1f, DeviceDpi / 96f);
        using var bitmap = RenderFrame(
            Size,
            _state,
            _closeVisible,
            _closeHovered,
            _playbackHovered,
            _pressedButton == IslandButton.Playback,
            _preparingFrame,
            _navigationState,
            _previousHovered,
            _pressedButton == IslandButton.Previous,
            _nextHovered,
            _pressedButton == IslandButton.Next,
            scale,
            _pressedButton == IslandButton.Close,
            _closeOpacity,
            PinVisible,
            _pinHovered,
            _pressedButton == IslandButton.Pin,
            _pinState.IsPinned,
            _closeOpacity,
            _monitoring,
            _monitoringPaused);
        var screenDeviceContext = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDeviceContext == IntPtr.Zero)
        {
            return;
        }

        var memoryDeviceContext = NativeMethods.CreateCompatibleDC(screenDeviceContext);
        if (memoryDeviceContext == IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDeviceContext);
            return;
        }

        var bitmapHandle = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;
        try
        {
            bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
            previousBitmap = NativeMethods.SelectObject(memoryDeviceContext, bitmapHandle);
            if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
            {
                return;
            }

            var destination = new NativePoint(Left, Top);
            var windowSize = new NativeSize(Width, Height);
            var source = new NativePoint(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                SourceConstantAlpha = (byte)Math.Clamp(
                    (int)Math.Round(_islandOpacity * byte.MaxValue),
                    0,
                    byte.MaxValue),
                AlphaFormat = AcSrcAlpha
            };
            NativeMethods.UpdateLayeredWindow(
                Handle,
                screenDeviceContext,
                ref destination,
                ref windowSize,
                memoryDeviceContext,
                ref source,
                0,
                ref blend,
                UlwAlpha);
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero && previousBitmap != new IntPtr(-1))
            {
                NativeMethods.SelectObject(memoryDeviceContext, previousBitmap);
            }

            if (bitmapHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }

            NativeMethods.DeleteDC(memoryDeviceContext);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDeviceContext);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _closeLingerTimer.Dispose();
            _closeFadeTimer.Dispose();
            _islandFadeTimer.Dispose();
            _preparingAnimationTimer.Dispose();
            _restartFromBeginningHoldTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int Scale(int value, float scale) => (int)Math.Round(value * scale);

    private static new float Scale(float value, float scale) => value * scale;

    private static int ScaledAlpha(int alpha, float opacity) =>
        Math.Clamp(
            (int)Math.Round(alpha * Math.Clamp(opacity, 0f, 1f)),
            0,
            255);

    private static Color WithOpacity(Color color, float opacity) =>
        Color.FromArgb(
            ScaledAlpha(color.A, opacity),
            color.R,
            color.G,
            color.B);

    private readonly record struct IslandMetrics(
        RectangleF Pill,
        RectangleF PreviousButton,
        RectangleF PlaybackButton,
        RectangleF NextButton,
        RectangleF PinButton,
        RectangleF CloseButton,
        float Scale);

    private readonly record struct MovementBounds(
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY);

    private enum IslandButton
    {
        None,
        Previous,
        Playback,
        Next,
        Pin,
        Close
    }

    private enum TransportButtonShape
    {
        Left,
        Middle,
        Right
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteObject(IntPtr graphicsObject);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateLayeredWindow(
            IntPtr windowHandle,
            IntPtr destinationDeviceContext,
            ref NativePoint destination,
            ref NativeSize size,
            IntPtr sourceDeviceContext,
            ref NativePoint source,
            int colorKey,
            ref BlendFunction blend,
            int flags);
    }
}
