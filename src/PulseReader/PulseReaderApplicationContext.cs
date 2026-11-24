using System.ComponentModel;
using System.Runtime.InteropServices;
using PulseReader.Configuration;
using PulseReader.Hotkeys;
using PulseReader.Monitoring;
using PulseReader.Playback;
using PulseReader.Selection;
using PulseReader.Settings;
using PulseReader.Setup;
using PulseReader.Speech;
using PulseReader.Startup;
using PulseReader.Tray;
using PulseReader.Updates;

namespace PulseReader;

internal sealed class PulseReaderApplicationContext : ApplicationContext
{
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly Control _uiDispatcher = new();
    private readonly NotifyIcon _trayIcon;
    private readonly TrayIconAnimator? _trayIconAnimator;
    private readonly TrayMenuForm _trayMenu;
    private readonly SelectedTextCaptureService _textCapture = new();
    private readonly SpeechServiceController _speech;
    private readonly GlobalHotkeyManager _hotkeyManager = new();
    private readonly HotkeyHoldGestureDetector _hotkeyGestureDetector = new();
    private readonly WindowMonitoringDiagnosticLog? _monitoringDiagnostics;
    private readonly ConfigurationService _configurationService;
    private readonly WindowsStartupRegistrationService _startupRegistration = new();
    private readonly SettingsForm _settingsForm;
    private readonly ModelSetupService _modelSetup = new();
    private ModelSetupForm? _modelSetupForm;
    private readonly PlaybackIslandForm _playbackIsland;
    private readonly System.Windows.Forms.Timer _monitoringControlsTimer = new()
    {
        Interval = 100
    };
    private AppConfiguration _configuration;
    private HotkeyDefinition _registeredHotkey;
    private Task? _captureTask;
    private Task? _hotkeyGestureTask;
    private CancellationTokenSource? _hotkeyGestureCancellation;
    private CancellationTokenSource? _manualCaptureCancellation;
    private PlaybackIslandSessionPosition? _monitoringIslandPosition;
    private WindowTextMonitoringSession? _monitoringSession;
    private MonitoringSpeechQueue? _monitoringSpeechQueue;
    private readonly List<MonitoredTextEventArgs> _pendingMonitoredText = [];
    private PlaybackIslandPlacement _playbackPlacement;
    private PlaybackIslandPlacement _monitoringPlacement;
    private bool _monitoringInitializing;
    private MonitoringThinkingEventArgs? _pendingThinkingUpdate;
    private bool _monitoringStartupPending;
    private DateTimeOffset _monitoringAcknowledgementUntil;
    private bool _monitoringQueueWarningShown;
    private bool _selectionPlaybackActive;
    private bool _optionalModelDisabledForSession;
    private bool _exiting;
    private bool _disposed;
    private readonly System.Windows.Forms.Timer _updateCheckTimer = new() { Interval = 5000 };
    private readonly UpdateClient _updateClient;
    private UpdateInstallation? _updateInstallation;
    private UpdateCheckStateStore? _updateStateStore;
    private UpdateCheckState _updateState = new();
    private UpdateOffer? _updateOffer;
    private UpdateMenuMode _updateMenuMode = UpdateMenuMode.Unsupported;
    private CancellationTokenSource? _updateCancellation;
    private Task<bool>? _updateTask;
    private bool _automaticUpdateInFlight;

    public PulseReaderApplicationContext(
        WindowMonitoringDiagnosticLog? monitoringDiagnostics = null)
        : this(new UpdateClient(), monitoringDiagnostics)
    {
    }

    internal PulseReaderApplicationContext(
        UpdateClient updateClient,
        WindowMonitoringDiagnosticLog? monitoringDiagnostics = null)
    {
        _updateClient = updateClient ?? throw new ArgumentNullException(nameof(updateClient));
        _monitoringDiagnostics = monitoringDiagnostics;
        _uiDispatcher.CreateControl();

        _configurationService = new ConfigurationService();
        var loadedConfiguration = _configurationService.LoadOrCreate();
        _configuration = loadedConfiguration.Configuration;
        _registeredHotkey = loadedConfiguration.Hotkey;

        var optionalModelStartup = OptionalModelStartupPolicy.Apply(
            _configuration,
            () => new ContextualPronunciationModelStore().GetAvailability(),
            value => _configurationService.TrySave(value, out var error) ? null : error);
        _configuration = optionalModelStartup.Configuration;
        _optionalModelDisabledForSession = _configuration.ContextualPronunciationEnabled
            && !optionalModelStartup.RuntimeContextualPronunciationEnabled;

        _speech = SpeechServiceFactory.Create(
            _configuration.SpeechBackend,
            new KokoroSpeechSettings(
                _configuration.KokoroVoice,
                _configuration.SpeechSpeed,
                _configuration.SpeechVolume,
                _configuration.KokoroVoiceBlendEnabled,
                _configuration.KokoroSecondaryVoice,
                _configuration.KokoroPrimaryVoiceWeight,
                optionalModelStartup.RuntimeContextualPronunciationEnabled),
            new WindowsSpeechSettings(
                _configuration.WindowsVoice,
                _configuration.WindowsSpeechRate,
                _configuration.WindowsSpeechVolume),
            out var speechWarning);
        _speech.SpeakingStateChanged += OnSpeakingStateChanged;

        _playbackIsland = new PlaybackIslandForm(
            new PlaybackIslandPinState(
                _configuration.PlaybackControlsPinned,
                _configuration.PlaybackControlsPinnedX,
                _configuration.PlaybackControlsPinnedY,
                _configuration.PlaybackControlsPinAnchor,
                _configuration.PlaybackControlsWindowPinnedX,
                _configuration.PlaybackControlsWindowPinnedY),
            TrySavePlaybackPinState);
        _playbackIsland.PauseRequested += (_, _) => PausePlayback();
        _playbackIsland.ResumeRequested += (_, _) => ResumePlayback();
        _playbackIsland.RestartCurrentRequested += (_, _) => _speech.TryRestartCurrentUnit();
        _playbackIsland.RestartFromBeginningRequested += (_, _) => _speech.TryRestartFromBeginning();
        _playbackIsland.NextRequested += (_, _) => _speech.TryMoveToNextUnit();
        _playbackIsland.StopRequested += (_, _) => StopPlaybackOrMonitoring();

        _trayMenu = new TrayMenuForm(_registeredHotkey.DisplayName);
        _trayMenu.StopSpeakingRequested += (_, _) => StopSpeaking();
        _trayMenu.StopMonitoringRequested += (_, _) => StopMonitoring();
        _trayMenu.SettingsRequested += (_, _) => ShowSettings();
        _trayMenu.ExitRequested += async (_, _) => await ExitApplicationAsync();

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "Pulse Reader",
            Visible = true
        };
        _trayIcon.MouseUp += OnTrayIconMouseUp;
        _trayIcon.DoubleClick += (_, _) => ShowSettings();

        try
        {
            _trayIconAnimator = new TrayIconAnimator(_trayIcon);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or ExternalException
                                          or InvalidOperationException)
        {
            // Keep the Windows information icon if the custom artwork cannot be loaded.
        }

        _hotkeyManager.Pressed += OnHotkeyPressed;
        _monitoringControlsTimer.Tick += (_, _) => UpdateMonitoringControlsVisibility();

        try
        {
            _hotkeyManager.Register(_registeredHotkey);
        }
        catch (Win32Exception)
        {
            ShowNotification(
                "Hotkey unavailable",
                $"{_registeredHotkey.DisplayName} is already in use. Open Settings to choose another shortcut.",
                ToolTipIcon.Warning);
        }

        var startupState = _startupRegistration.GetState();

        _settingsForm = new SettingsForm(
            _configuration,
            _registeredHotkey,
            ApplySpeechConfiguration,
            ApplyHotkey,
            _speech.TryPreviewAsync,
            StopSpeaking,
            SuspendHotkeyCapture,
            ResumeHotkeyCapture,
            _speech.WindowsVoices,
            startupState.IsEnabled,
            _startupRegistration.SetEnabled,
            _speech.ContextualPronunciationAvailability,
            InstallOptionalModelAsync,
            () => _speech.ContextualPronunciationAvailability);

        if (_configuration.SpeechBackend == SpeechBackend.Kokoro
            && !File.Exists(new KokoroModelStore().ModelPath))
            Application.Idle += OnFirstRunSetup;

        if (loadedConfiguration.Warning is not null)
        {
            ShowNotification("Configuration warning", loadedConfiguration.Warning, ToolTipIcon.Warning);
        }

        if (speechWarning is not null)
        {
            ShowNotification("Speech unavailable", speechWarning, ToolTipIcon.Warning);
        }

        if (optionalModelStartup.Warning is not null)
        {
            ShowNotification("Optional pronunciation model", optionalModelStartup.Warning, ToolTipIcon.Warning);
        }

        if (startupState.Warning is not null)
        {
            ShowNotification("Startup setting", startupState.Warning, ToolTipIcon.Warning);
        }

        if (_monitoringDiagnostics is not null)
        {
            ShowNotification(
                "Monitoring diagnostics enabled",
                _monitoringDiagnostics.IncludeStructure
                    ? "Accessibility structure and salted text fingerprints will be recorded for this run."
                    : "Performance metrics and salted text fingerprints will be recorded without detailed structural capture.",
                ToolTipIcon.Info);
        }
        InitializeUpdates();
    }

    private void InitializeUpdates()
    {
        var version = typeof(Program).Assembly.GetName().Version ?? new Version(1, 0, 0);
        _trayMenu.UpdateRequested += OnUpdateRequested;
        _updateCheckTimer.Tick += OnUpdateCheckTimerTick;
        if (!UpdateInstallationDiscovery.TryRead(AppContext.BaseDirectory, version,
                out _updateInstallation, out var reason))
        {
            System.Diagnostics.Trace.WriteLine(reason);
            SetUpdateMenuState(UpdateMenuMode.Unsupported);
            return;
        }
        _updateStateStore = new(Path.Combine(UpdateHandoff.OperationsRoot, "update-check.json"));
        _updateState = _updateStateStore.Load();
        SetUpdateMenuState(CachedUpdateVersion() is null ? UpdateMenuMode.Idle : UpdateMenuMode.Available);
        if (_configuration.CheckForUpdatesAutomatically) _updateCheckTimer.Start();
    }

    private Version? CachedUpdateVersion() =>
        UpdateVersionPolicy.TryParseStable(_updateState.AvailableVersion, false, out var version)
        && _updateInstallation is not null && version! > _updateInstallation.Version ? version : null;

    private void SetUpdateMenuState(UpdateMenuMode mode, int? progress = null)
    {
        if (_disposed) return;
        _updateMenuMode = mode;
        var available = _updateOffer?.Version ?? CachedUpdateVersion();
        _trayMenu.SetUpdateState(mode, available?.ToString(), progress);
        _trayIconAnimator?.SetUpdateAvailable(available is not null);
        var detail = mode switch
        {
            UpdateMenuMode.Available => "Update " + available,
            UpdateMenuMode.Checking => "Checking for updates",
            UpdateMenuMode.Downloading => progress is { } percent ? $"Downloading update {percent}%" : "Downloading update",
            UpdateMenuMode.Cancelling => "Cancelling update",
            UpdateMenuMode.Applying => "Applying update",
            _ => null
        };
        var tooltip = detail is null ? "Pulse Reader" : "Pulse Reader | " + detail;
        _trayIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    private async void OnUpdateCheckTimerTick(object? sender, EventArgs e)
    {
        _updateCheckTimer.Interval = 60000;
        if (_exiting || _disposed || !_configuration.CheckForUpdatesAutomatically
            || _updateInstallation is null || _updateTask is { IsCompleted: false }
            || !UpdateCheckStateStore.IsDue(_updateState, DateTimeOffset.UtcNow)) return;
        _updateTask = RunUpdateOperationAsync(manual: false, requestedVersion: null);
        await _updateTask;
    }

    private async void OnUpdateRequested(object? sender, EventArgs e)
    {
        if (_exiting || _disposed || _updateInstallation is null) return;
        if (_updateMenuMode == UpdateMenuMode.Downloading)
        {
            SetUpdateMenuState(UpdateMenuMode.Cancelling);
            _updateCancellation?.Cancel();
            return;
        }
        if (_updateTask is { IsCompleted: false } || _updateMenuMode == UpdateMenuMode.Applying) return;
        if (UpdateCheckStateStore.IsDeferred(_updateState, DateTimeOffset.UtcNow))
        {
            ShowNotification("Update check deferred", "GitHub has temporarily paused update checks. Please try again later.", ToolTipIcon.Warning);
            return;
        }
        var requested = _updateMenuMode == UpdateMenuMode.Available
            ? _updateOffer?.Version ?? CachedUpdateVersion() : null;
        _updateTask = RunUpdateOperationAsync(manual: true, requested);
        if (await _updateTask && !_exiting && !_disposed) await ExitApplicationAsync();
    }

    private async Task<bool> RunUpdateOperationAsync(bool manual, Version? requestedVersion)
    {
        if (_updateInstallation is null) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
        _updateCancellation = cancellation;
        _automaticUpdateInFlight = !manual;
        string? operationDirectory = null;
        string? completionTooltip = null;
        var handedOff = false;
        try
        {
            SetUpdateMenuState(UpdateMenuMode.Checking);
            _updateState = _updateState with { LastAttempt = DateTimeOffset.UtcNow };
            _updateStateStore?.TrySave(_updateState);
            var check = await _updateClient.CheckAsync(_updateInstallation, cancellation.Token);
            if (_exiting || _disposed) return false;
            if (check.RetryAfter is not null)
            {
                _updateState = _updateState with { RetryAfter = check.RetryAfter };
                _updateStateStore?.TrySave(_updateState);
                if (manual) ShowNotification("Update check deferred", "GitHub has temporarily paused update checks. Please try again later.", ToolTipIcon.Warning);
                return false;
            }
            _updateOffer = check.Offer;
            _updateState = _updateState with
            {
                LastSuccess = DateTimeOffset.UtcNow, RetryAfter = null,
                AvailableVersion = _updateOffer?.Version.ToString()
            };
            _updateStateStore?.TrySave(_updateState);
            if (_updateOffer is null)
            {
                SetUpdateMenuState(UpdateMenuMode.Idle);
                if (manual) completionTooltip = check.NoRelease ? "Pulse Reader | No published release yet" : "Pulse Reader | Up to date";
                return false;
            }
            SetUpdateMenuState(UpdateMenuMode.Available);
            // A check never silently turns into installation; a changed version
            // gets a fresh Update action before the user accepts its package.
            if (!manual || requestedVersion != _updateOffer.Version) return false;
            var offer = _updateOffer;
            var receipt = new RegistryDistributionTermsReceiptStore().ReadAcceptedSha256();
            if (!string.Equals(receipt, offer.TermsSha256, StringComparison.OrdinalIgnoreCase))
            {
                using var terms = new DistributionTermsForm(offer.TermsText, isUpdate: true);
                if (terms.ShowDialog() != DialogResult.OK) return false;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            operationDirectory = UpdateHandoff.CreateOperationDirectory();
            SetUpdateMenuState(UpdateMenuMode.Downloading);
            var progress = new Progress<SetupProgress>(value =>
            {
                if (_exiting || _disposed || cancellation.IsCancellationRequested
                    || _updateMenuMode != UpdateMenuMode.Downloading) return;
                var percent = value.TotalBytes is > 0
                    ? (int?)Math.Clamp(value.BytesReceived * 100d / value.TotalBytes.Value, 0, 100) : null;
                SetUpdateMenuState(UpdateMenuMode.Downloading, percent);
            });
            var prepared = await _updateClient.PrepareAsync(_updateInstallation, offer,
                operationDirectory, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SetUpdateMenuState(UpdateMenuMode.Applying);
            handedOff = await new UpdateHandoff().StartAsync(prepared, offer.TermsSha256, cancellation.Token);
            if (!handedOff) throw new IOException("The update helper did not accept the update. Pulse Reader is still running.");
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or HttpRequestException or TimeoutException
            or InvalidOperationException or System.Text.Json.JsonException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.WriteLine(exception);
            if (exception is UpdateRateLimitException rateLimit)
            {
                _updateState = _updateState with { RetryAfter = rateLimit.RetryAfter };
                _updateStateStore?.TrySave(_updateState);
            }
            if (manual && !_exiting && !_disposed)
                ShowNotification("Update did not finish", exception.Message
                    + (operationDirectory is null ? "" : "\nUpdate details: " + operationDirectory), ToolTipIcon.Warning);
            return false;
        }
        finally
        {
            _updateCancellation = null;
            _automaticUpdateInFlight = false;
            if (!handedOff && !_exiting && !_disposed)
            {
                SetUpdateMenuState(_updateOffer is not null || CachedUpdateVersion() is not null
                    ? UpdateMenuMode.Available : UpdateMenuMode.Idle);
                if (completionTooltip is not null) _trayIcon.Text = completionTooltip;
            }
        }
    }

    private void ShowSettings()
    {
        if (!_exiting)
        {
            _trayMenu.HideMenu();
            _settingsForm.ShowNearTray();
        }
    }

    private void OnTrayIconMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        if (!_exiting && eventArgs.Button == MouseButtons.Right)
        {
            _trayMenu.ToggleAt(Cursor.Position);
        }
    }

    private void OnFirstRunSetup(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= OnFirstRunSetup;
        if (_exiting || _disposed) return;
        using var setup = new ModelSetupForm(false, _modelSetup.EnsureKokoroAsync,
            _shutdownCancellation.Token);
        _modelSetupForm = setup;
        try
        {
            if (setup.ShowDialog() != DialogResult.OK && !_exiting && !_disposed)
            {
                var error = ApplySpeechConfiguration(_configuration with { SpeechBackend = SpeechBackend.Windows });
                if (error is null) _settingsForm.ReloadConfiguration(_configuration);
                else ShowNotification("Setup not completed", error, ToolTipIcon.Warning);
            }
        }
        finally { _modelSetupForm = null; }
    }

    private Task<bool> InstallOptionalModelAsync()
    {
        if (_exiting || _disposed || _modelSetupForm is not null) return Task.FromResult(false);
        using var setup = new ModelSetupForm(true, _modelSetup.EnsureContextualPronunciationAsync,
            _shutdownCancellation.Token);
        _modelSetupForm = setup;
        try { return Task.FromResult(setup.ShowDialog(_settingsForm) == DialogResult.OK); }
        finally { _modelSetupForm = null; }
    }

    private string? ApplySpeechConfiguration(AppConfiguration configuration)
    {
        if (_exiting || _disposed) return "Pulse Reader is closing.";
        if (configuration.SpeechBackend == SpeechBackend.Kokoro
            && _modelSetupForm is null && !File.Exists(new KokoroModelStore().ModelPath))
        {
            using var setup = new ModelSetupForm(false, _modelSetup.EnsureKokoroAsync,
                _shutdownCancellation.Token);
            _modelSetupForm = setup;
            try
            {
                if (setup.ShowDialog(_settingsForm) != DialogResult.OK)
                    return "Kokoro setup did not finish. The previous speech setting is unchanged.";
            }
            finally { _modelSetupForm = null; }
        }
        if (_exiting || _disposed) return "Pulse Reader is closing.";
        if (configuration.ContextualPronunciationEnabled
            && !_configuration.ContextualPronunciationEnabled
            && !_speech.ContextualPronunciationAvailability.IsInstalled)
        {
            return _speech.ContextualPronunciationAvailability.Message
                ?? "Install the optional local pronunciation model before enabling it.";
        }

        configuration = configuration with
        {
            PlaybackControlsPinned = _configuration.PlaybackControlsPinned,
            PlaybackControlsPinnedX = _configuration.PlaybackControlsPinnedX,
            PlaybackControlsPinnedY = _configuration.PlaybackControlsPinnedY,
            PlaybackControlsPinAnchor = _configuration.PlaybackControlsPinAnchor,
            PlaybackControlsWindowPinnedX = _configuration.PlaybackControlsWindowPinnedX,
            PlaybackControlsWindowPinnedY = _configuration.PlaybackControlsWindowPinnedY
        };
        if (!_configurationService.TrySave(configuration, out var error))
        {
            return error;
        }

        _configuration = configuration;
        if (configuration.CheckForUpdatesAutomatically && _updateInstallation is not null)
            _updateCheckTimer.Start();
        else
        {
            _updateCheckTimer.Stop();
            if (_automaticUpdateInFlight) _updateCancellation?.Cancel();
        }
        if (!configuration.ContextualPronunciationEnabled) _optionalModelDisabledForSession = false;
        _speech.UpdateSettings(
            configuration.SpeechBackend,
            new KokoroSpeechSettings(
                configuration.KokoroVoice,
                configuration.SpeechSpeed,
                configuration.SpeechVolume,
                configuration.KokoroVoiceBlendEnabled,
                configuration.KokoroSecondaryVoice,
                configuration.KokoroPrimaryVoiceWeight,
                configuration.ContextualPronunciationEnabled && !_optionalModelDisabledForSession),
            new WindowsSpeechSettings(
                configuration.WindowsVoice,
                configuration.WindowsSpeechRate,
                configuration.WindowsSpeechVolume));

        if (!configuration.ShowPlaybackControls)
        {
            _playbackIsland.HideIsland();
        }
        else if (_monitoringSession is not null)
        {
            UpdateMonitoringControlsVisibility();
        }
        else if (_selectionPlaybackActive && _speech.State != SpeechPlaybackState.Idle)
        {
            _playbackIsland.ShowFor(
                _playbackPlacement,
                _speech.State,
                _speech.NavigationState);
        }

        return null;
    }

    private bool TrySavePlaybackPinState(PlaybackIslandPinState pinState)
    {
        var nextConfiguration = _configuration with
        {
            PlaybackControlsPinned = pinState.IsPinned,
            PlaybackControlsPinnedX = pinState.NormalizedX,
            PlaybackControlsPinnedY = pinState.NormalizedY,
            PlaybackControlsPinAnchor = pinState.Anchor,
            PlaybackControlsWindowPinnedX = pinState.WindowNormalizedX,
            PlaybackControlsWindowPinnedY = pinState.WindowNormalizedY
        };
        if (!_configurationService.TrySave(nextConfiguration, out var error))
        {
            ShowNotification(
                "Playback position",
                error ?? "Pulse Reader could not save the playback control position.",
                ToolTipIcon.Warning);
            return false;
        }

        _configuration = nextConfiguration;
        return true;
    }

    private string? ApplyHotkey(HotkeyDefinition hotkey)
    {
        var previousHotkey = _registeredHotkey;
        if (!_hotkeyManager.TryReplace(hotkey, out var registrationError))
        {
            return registrationError;
        }

        var nextConfiguration = _configuration with { Hotkey = hotkey.ToConfiguration() };
        if (!_configurationService.TrySave(nextConfiguration, out var saveError))
        {
            _hotkeyManager.TryReplace(previousHotkey, out _);
            return saveError;
        }

        _registeredHotkey = hotkey;
        _configuration = nextConfiguration;
        _trayMenu.SetHotkey(hotkey.DisplayName);
        return null;
    }

    private string? SuspendHotkeyCapture() =>
        _hotkeyManager.TrySuspend(out var error) ? null : error;

    private string? ResumeHotkeyCapture() =>
        _hotkeyManager.TryResume(out var error) ? null : error;

    private void OnHotkeyPressed(object? sender, EventArgs eventArgs)
    {
        if (_exiting
            || _modelSetupForm is not null
            || _hotkeyGestureTask is { IsCompleted: false })
        {
            return;
        }

        var sourceWindow = NativeInput.GetForegroundWindowHandle();
        if (sourceWindow == IntPtr.Zero)
        {
            return;
        }

        var placement = PlaybackIslandPlacementCapture.Capture(
            sourceWindow,
            Cursor.Position);
        var stoppedExistingSpeech = false;
        if (PlaybackHotkeyPolicy.ShouldStopImmediately(
                _monitoringSession is not null,
                _manualCaptureCancellation is { IsCancellationRequested: false }
                    || _monitoringSpeechQueue?.State.HasManualPlayback == true,
                _speech.State))
        {
            StopSpeaking();
            stoppedExistingSpeech = true;
        }

        if (!stoppedExistingSpeech && _captureTask is { IsCompleted: false }
            && _manualCaptureCancellation is not { IsCancellationRequested: true }) return;

        _hotkeyGestureCancellation?.Cancel();
        _hotkeyGestureCancellation?.Dispose();
        _hotkeyGestureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCancellation.Token);
        _hotkeyGestureTask = HandleHotkeyGestureAsync(
            sourceWindow,
            placement,
            stoppedExistingSpeech,
            _hotkeyGestureCancellation);
    }

    private async Task HandleHotkeyGestureAsync(
        IntPtr sourceWindow,
        PlaybackIslandPlacement placement,
        bool stoppedExistingSpeech,
        CancellationTokenSource gestureCancellation)
    {
        try
        {
            var gesture = await _hotkeyGestureDetector.ClassifyAsync(
                _registeredHotkey,
                gestureCancellation.Token);
            if (gesture == HotkeyGesture.Hold)
            {
                await ToggleWindowMonitoringAsync(
                    sourceWindow,
                    placement,
                    gestureCancellation.Token);
                return;
            }

            if (stoppedExistingSpeech || _exiting)
            {
                return;
            }

            // Let a canceled clipboard transaction finish restoring its snapshot
            // before honoring the next capture. Do not drop that task on shutdown.
            if (_manualCaptureCancellation is { IsCancellationRequested: true }
                && _captureTask is { } previousCapture)
                await previousCapture;
            if (_exiting) return;

            if (_monitoringSession is not null)
            {
                if (_speech.IsSpeaking
                    && _monitoringSession.SourceWindow == sourceWindow)
                {
                    StopSpeaking();
                }
                else
                {
                    _captureTask = CaptureManualInterjectionAsync(sourceWindow, placement);
                }
            }
            else
            {
                _playbackPlacement = placement;
                _captureTask = CaptureAndSpeakAsync(sourceWindow);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is ExternalException
                                          or IOException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or ObjectDisposedException)
        {
            StopMonitoring();
            if (!_exiting)
            {
                ShowNotification(
                    "Monitoring unavailable",
                    "Pulse Reader could not start or update this window's text monitor.",
                    ToolTipIcon.Warning);
            }
        }
        finally
        {
            if (ReferenceEquals(_hotkeyGestureCancellation, gestureCancellation))
            {
                _hotkeyGestureCancellation = null;
            }

            gestureCancellation.Dispose();
            _hotkeyGestureTask = null;
        }
    }

    private async Task CaptureAndSpeakAsync(IntPtr sourceWindow)
    {
        try
        {
            var result = await _textCapture.CaptureFromWindowAsync(
                sourceWindow,
                _shutdownCancellation.Token);

            ShowClipboardWarning(result);

            if (result.Error is not null && !_exiting)
            {
                ShowNotification("Could not read selection", result.Error, ToolTipIcon.Info);
                return;
            }

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                if (!_exiting)
                {
                    ShowNotification(
                        "No text selected",
                        "Select copyable text, then press the Pulse Reader hotkey.",
                        ToolTipIcon.Info);
                }

                return;
            }

            if (_exiting)
            {
                return;
            }

            _selectionPlaybackActive = true;
            if (_configuration.ShowPlaybackControls)
            {
                _playbackIsland.ShowFor(
                    _playbackPlacement,
                    SpeechPlaybackState.Preparing,
                    PlaybackNavigationState.Unavailable);
            }

            var speechResult = await _speech.TrySpeakAsync(result.Text);
            ReportSpeechResult(speechResult);

            if (!speechResult.Started)
            {
                _selectionPlaybackActive = false;
                _playbackIsland.HideIsland();
            }
        }
        catch (Exception exception) when (exception is ExternalException
                                          or IOException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or ObjectDisposedException)
        {
            if (!_exiting)
            {
                ShowNotification(
                    "Could not read selection",
                    "Pulse Reader could not access the current selection.",
                    ToolTipIcon.Warning);
            }
        }
        finally
        {
            if (_selectionPlaybackActive && _speech.State == SpeechPlaybackState.Idle)
            {
                _selectionPlaybackActive = false;
                _playbackIsland.HideIsland();
            }
        }
    }

    private async Task ToggleWindowMonitoringAsync(
        IntPtr sourceWindow,
        PlaybackIslandPlacement placement,
        CancellationToken cancellationToken)
    {
        if (_monitoringSession?.SourceWindow == sourceWindow)
        {
            StopMonitoring();
            MonitoringLatchSound.Play(attached: false);
            return;
        }

        if (_monitoringSession is not null)
        {
            StopMonitoring();
        }

        if (_speech.IsSpeaking)
        {
            StopSpeaking();
        }

        var session = new WindowTextMonitoringSession(sourceWindow, _monitoringDiagnostics);
        session.TextAvailable += OnMonitoredTextAvailable;
        session.ContextChanged += OnMonitoredContextChanged;
        session.ThinkingChanged += OnMonitoredThinkingChanged;
        session.WorkerStopped += OnMonitoredWorkerStopped;
        _monitoringSession = session;
        _monitoringDiagnostics?.RecordAttached(sourceWindow);
        _monitoringPlacement = placement;
        _monitoringInitializing = true;
        _monitoringQueueWarningShown = false;
        _pendingMonitoredText.Clear();

        // Acknowledge the hold threshold immediately, before UIA startup or
        // release-dependent clipboard capture. This also keeps the gesture
        // visible when the pointer is nowhere near the control island.
        var queue = new MonitoringSpeechQueue(_speech);
        queue.StateChanged += OnMonitoringQueueStateChanged;
        queue.SpeechResultProduced += OnMonitoringSpeechResultProduced;
        queue.QueueCapacityReached += OnMonitoringQueueCapacityReached;
        _monitoringSpeechQueue = queue;
        _monitoringStartupPending = true;
        _monitoringControlsTimer.Start();
        UpdateSpeakingState();
        var startResult = await session.StartAsync(_shutdownCancellation.Token);
        // A worker can fail immediately after startup. Its queued exit callback
        // may already have detached it or a newer session may have replaced it.
        if (!ReferenceEquals(_monitoringSession, session)) return;
        if (!startResult.Started || _exiting)
        {
            StopMonitoring();
            if (!_exiting && startResult.Error is not null)
            {
                ShowNotification("Monitoring unavailable", startResult.Error, ToolTipIcon.Info);
            }

            return;
        }

        _monitoringStartupPending = false;
        _monitoringAcknowledgementUntil = DateTimeOffset.UtcNow.AddSeconds(2);
        queue.SetContextSuspended(session.IsContextSuspended);
        UpdateSpeakingState();
        MonitoringLatchSound.Play();

        await _hotkeyGestureDetector.WaitForReleaseAsync(
            _registeredHotkey,
            cancellationToken);
        if (_exiting || !ReferenceEquals(_monitoringSession, session)) return;

        var selectionResult = await _textCapture.CaptureFromWindowAsync(
            sourceWindow,
            _shutdownCancellation.Token);
        if (_exiting || !ReferenceEquals(_monitoringSession, session)) return;
        ShowClipboardWarning(selectionResult);
        if (!string.IsNullOrWhiteSpace(selectionResult.Text))
        {
            queue.EnqueueInitialSelection(selectionResult.Text);
        }

        _monitoringInitializing = false;
        _monitoringAcknowledgementUntil = DateTimeOffset.UtcNow.AddSeconds(2);
        if (_pendingThinkingUpdate is { } thinking && thinking.ContextVersion == session.ContextVersion)
            queue.UpdateThinking(thinking.Snapshot);
        _pendingThinkingUpdate = null;
        foreach (var pending in _pendingMonitoredText)
        {
            EnqueueMonitoredText(queue, pending);
        }

        _pendingMonitoredText.Clear();
        UpdateSpeakingState();
    }

    private async Task CaptureManualInterjectionAsync(IntPtr sourceWindow, PlaybackIslandPlacement placement)
    {
        var queue = _monitoringSpeechQueue;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
        _manualCaptureCancellation = cancellation;
        try
        {
            var result = await _textCapture.CaptureFromWindowAsync(
                sourceWindow,
                cancellation.Token);
            if (cancellation.IsCancellationRequested || _exiting || !ReferenceEquals(queue, _monitoringSpeechQueue)) return;
            ShowClipboardWarning(result);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                queue?.EnqueueManualSelection(result.Text, placement);
            }
            else if (_speech.IsSpeaking)
            {
                queue?.StopCurrentSpeech();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is ExternalException
                                          or IOException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or ObjectDisposedException)
        {
            if (!_exiting)
            {
                ShowNotification(
                    "Could not read selection",
                    "Pulse Reader could not access the temporary selection.",
                    ToolTipIcon.Warning);
            }
        }
        finally
        {
            if (ReferenceEquals(_manualCaptureCancellation, cancellation)) _manualCaptureCancellation = null;
        }
    }

    private void OnMonitoredTextAvailable(object? sender, MonitoredTextEventArgs eventArgs) =>
        RunOnUiThread(
            () =>
            {
                if (_monitoringSession is null || !ReferenceEquals(sender, _monitoringSession)
                    || eventArgs.ContextVersion != _monitoringSession.ContextVersion
                    || _monitoringSession.IsContextSuspended)
                {
                    return;
                }

                if (_monitoringInitializing || _monitoringSpeechQueue is null)
                {
                    _pendingMonitoredText.Add(eventArgs);
                }
                else
                {
                    EnqueueMonitoredText(_monitoringSpeechQueue, eventArgs);
                }
            });

    private void OnMonitoredContextChanged(object? sender, MonitoringContextEventArgs eventArgs) =>
        RunOnUiThread(() =>
        {
            if (!ReferenceEquals(sender, _monitoringSession)) return;
            _pendingMonitoredText.Clear();
            _pendingThinkingUpdate = null;
            _monitoringSpeechQueue?.SetContextSuspended(eventArgs.Suspended);
            UpdateSpeakingState();
        });

    private void OnMonitoredThinkingChanged(object? sender, MonitoringThinkingEventArgs eventArgs) =>
        RunOnUiThread(() =>
        {
            if (!ReferenceEquals(sender, _monitoringSession) || _monitoringSession is null
                || eventArgs.ContextVersion != _monitoringSession.ContextVersion || _monitoringSession.IsContextSuspended) return;
            if (_monitoringInitializing) _pendingThinkingUpdate = eventArgs;
            else _monitoringSpeechQueue?.UpdateThinking(eventArgs.Snapshot);
        });

    internal static void EnqueueMonitoredText(
        MonitoringSpeechQueue queue,
        MonitoredTextEventArgs eventArgs)
    {
        if (eventArgs.CompletedRevision is { } revision)
        {
            queue.ReconcileCompletedResponse(revision.DraftUnits, revision.FinalUnits, revision.PublishedDraftIndices,
                revision.DraftPrefixOffset, revision.FinalPrefixOffset, revision.EarlierOpening);
        }
        else if (eventArgs.BeginsCompletedResponse)
        {
            queue.EnqueueCompletedResponse(eventArgs.Text);
        }
        else
        {
            queue.EnqueueMonitoredText(eventArgs.Text, eventArgs.IsDiscreteMessage, eventArgs.SpeechRequest,
                eventArgs.PlaybackFinished);
        }
    }

    private void OnMonitoredWorkerStopped(object? sender, MonitoringWorkerStoppedEventArgs eventArgs) =>
        RunOnUiThread(
            () =>
            {
                if (eventArgs.WasStarted && ReferenceEquals(sender, _monitoringSession))
                {
                    StopMonitoring();
                    if (!_exiting && eventArgs.Reason != "cancelled")
                        ShowNotification(
                            "Window monitoring stopped",
                            eventArgs.Reason == "source-closed" ? "The monitored window was closed."
                                : "The text monitor stopped unexpectedly. Hold the hotkey again to reattach.",
                            eventArgs.Reason == "source-closed" ? ToolTipIcon.Info : ToolTipIcon.Warning);
                }
            });

    private void OnMonitoringQueueStateChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is MonitoringSpeechQueue queue && ReferenceEquals(queue, _monitoringSpeechQueue))
            _monitoringDiagnostics?.RecordQueueState(queue.State);
        RunOnUiThread(UpdateSpeakingState);
    }

    private void OnMonitoringSpeechResultProduced(
        object? sender,
        MonitoringSpeechResultEventArgs eventArgs)
    {
        _monitoringDiagnostics?.RecordSpeechResult(
            eventArgs.Kind,
            eventArgs.Text,
            eventArgs.Result);
        RunOnUiThread(() => ReportSpeechResult(eventArgs.Result));
    }

    private void OnMonitoringQueueCapacityReached(object? sender, EventArgs eventArgs) =>
        RunOnUiThread(
            () =>
            {
                if (_monitoringQueueWarningShown)
                {
                    return;
                }

                _monitoringQueueWarningShown = true;
                _monitoringSpeechQueue?.Pause();
                ShowNotification(
                    "Monitoring paused",
                    "The unread monitoring queue reached its safety limit. Resume after listening or detach from the window.",
                    ToolTipIcon.Warning);
            });

    private void ShowClipboardWarning(SelectedTextCaptureResult result)
    {
        if (result.ClipboardWarning is not null && !_exiting)
        {
            ShowNotification(
                "Clipboard warning",
                result.ClipboardWarning,
                ToolTipIcon.Warning);
        }
    }

    private void ReportSpeechResult(SpeechStartResult speechResult)
    {
        if (speechResult.Warning is not null && !_exiting)
        {
            ShowNotification("Speech fallback", speechResult.Warning, ToolTipIcon.Warning);
        }

        if (!speechResult.Started
            && !speechResult.Canceled
            && speechResult.Error is not null
            && !_exiting)
        {
            ShowNotification("Speech unavailable", speechResult.Error, ToolTipIcon.Warning);
        }
    }

    private void PausePlayback()
    {
        if (_monitoringSpeechQueue is not null && !_monitoringSpeechQueue.State.IsManualPlaybackActive)
        {
            _monitoringSpeechQueue.Pause();
        }
        else
        {
            _speech.TryPause();
        }
    }

    private void ResumePlayback()
    {
        if (_monitoringSpeechQueue is not null && !_monitoringSpeechQueue.State.IsManualPlaybackActive)
        {
            _monitoringSpeechQueue.Resume();
        }
        else
        {
            _speech.TryResume();
        }
    }

    private void StopPlaybackOrMonitoring()
    {
        if (_monitoringIslandPosition is not null)
        {
            _monitoringSpeechQueue?.TryStopManualPlayback();
            UpdateSpeakingState();
            return;
        }
        if (_monitoringSession is not null)
        {
            StopMonitoring();
        }
        else
        {
            StopSpeaking();
        }
    }

    private void StopMonitoring()
    {
        _manualCaptureCancellation?.Cancel();
        _monitoringIslandPosition = null;
        var session = _monitoringSession;
        var queue = _monitoringSpeechQueue;
        _monitoringSession = null;
        _monitoringSpeechQueue = null;
        _monitoringInitializing = false;
        _monitoringStartupPending = false;
        _monitoringAcknowledgementUntil = DateTimeOffset.MinValue;
        _monitoringQueueWarningShown = false;
        _pendingMonitoredText.Clear();
        _monitoringControlsTimer.Stop();
        _pendingThinkingUpdate = null;

        if (session is not null)
        {
            _monitoringDiagnostics?.RecordDetached();
            session.TextAvailable -= OnMonitoredTextAvailable;
            session.ContextChanged -= OnMonitoredContextChanged;
            session.ThinkingChanged -= OnMonitoredThinkingChanged;
            session.WorkerStopped -= OnMonitoredWorkerStopped;
            session.Dispose();
        }

        if (queue is not null)
        {
            queue.StateChanged -= OnMonitoringQueueStateChanged;
            queue.SpeechResultProduced -= OnMonitoringSpeechResultProduced;
            queue.QueueCapacityReached -= OnMonitoringQueueCapacityReached;
            queue.Dispose();
        }

        _selectionPlaybackActive = false;
        _playbackIsland.HideIsland();
        UpdateSpeakingState();
    }

    private void RunOnUiThread(Action action)
    {
        if (_disposed || _uiDispatcher.IsDisposed)
        {
            return;
        }

        if (_uiDispatcher.InvokeRequired)
        {
            try
            {
                _uiDispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        action();
    }

    private void StopSpeaking()
    {
        if (_monitoringSpeechQueue is not null)
        {
            var capturing = _manualCaptureCancellation is { IsCancellationRequested: false };
            _manualCaptureCancellation?.Cancel();
            if (!_monitoringSpeechQueue.TryStopManualPlayback() && !capturing)
                _monitoringSpeechQueue.StopCurrentSpeech();
            UpdateSpeakingState();
            return;
        }

        _selectionPlaybackActive = false;
        _playbackIsland.HideIsland();
        _speech.Stop();
        UpdateSpeakingState();
    }

    private void OnSpeakingStateChanged(object? sender, EventArgs eventArgs)
    {
        if (_disposed || _uiDispatcher.IsDisposed)
        {
            return;
        }

        if (_uiDispatcher.InvokeRequired)
        {
            try
            {
                _uiDispatcher.BeginInvoke(UpdateSpeakingState);
            }
            catch (InvalidOperationException)
            {
                // The message loop is already shutting down.
            }
        }
        else
        {
            UpdateSpeakingState();
        }
    }

    private void UpdateSpeakingState()
    {
        if (!_disposed)
        {
            var state = _speech.State;
            var isSpeaking = state != SpeechPlaybackState.Idle;
            var monitoring = _monitoringSession is not null;
            _trayMenu.SetMonitoringState(monitoring);
            _trayMenu.SetSpeakingState(isSpeaking);
            _trayIconAnimator?.SetMonitoringState(monitoring);
            _trayIconAnimator?.SetPlaybackState(state);
            _settingsForm.UpdateSpeakingState(isSpeaking);

            if (monitoring)
            {
                UpdateMonitoringControlsVisibility();
            }
            else if (_selectionPlaybackActive && _configuration.ShowPlaybackControls)
            {
                if (state == SpeechPlaybackState.Idle)
                {
                    if (_captureTask is not { IsCompleted: false })
                    {
                        _selectionPlaybackActive = false;
                        _playbackIsland.HideIsland();
                    }
                    else
                    {
                        _playbackIsland.UpdateState(
                            SpeechPlaybackState.Preparing,
                            PlaybackNavigationState.Unavailable);
                    }
                }
                else
                {
                    _playbackIsland.ShowFor(
                        _playbackPlacement,
                        state,
                        _speech.NavigationState);
                }
            }
            else if (!_configuration.ShowPlaybackControls)
            {
                _playbackIsland.HideIsland();
            }
        }
    }

    private void UpdateMonitoringControlsVisibility()
    {
        var manualPlacement = _monitoringSpeechQueue?.State.ManualPlacement;
        if (_monitoringSession is not null && manualPlacement is { } placement)
        {
            _monitoringIslandPosition ??= _playbackIsland.SuspendPresentation();
            if (_configuration.ShowPlaybackControls)
                _playbackIsland.ShowFor(placement,
                    _speech.State == SpeechPlaybackState.Idle ? SpeechPlaybackState.Preparing : _speech.State,
                    _speech.NavigationState);
            else _playbackIsland.ConcealIsland();
            return;
        }
        if (_monitoringIslandPosition is { } position)
        {
            _monitoringIslandPosition = null;
            _playbackIsland.RestorePresentation(position);
        }
        if (_monitoringSession is null
            || _monitoringSpeechQueue is null
            || !_configuration.ShowPlaybackControls)
        {
            if (_monitoringSession is not null)
            {
                _playbackIsland.ConcealIsland();
            }

            return;
        }

        if (NativeInput.GetForegroundWindowHandle() != _monitoringSession.SourceWindow)
        {
            _playbackIsland.ConcealIsland();
            return;
        }

        var queueState = _monitoringSpeechQueue.State;
        var speechState = _speech.State;
        var displayState = _monitoringStartupPending ? SpeechPlaybackState.Preparing
            : queueState.IsPaused || (queueState.IsContextSuspended && speechState == SpeechPlaybackState.Idle)
            ? SpeechPlaybackState.Paused
            : speechState != SpeechPlaybackState.Idle
                ? speechState
                : queueState.PendingItemCount > 0
                    ? SpeechPlaybackState.Preparing
                    : SpeechPlaybackState.Idle;
        var shouldShow = displayState != SpeechPlaybackState.Idle
                         || _monitoringInitializing
                         || DateTimeOffset.UtcNow < _monitoringAcknowledgementUntil
                         || _playbackIsland.IsPointerNear(Cursor.Position, 84);
        if (!shouldShow)
        {
            _playbackIsland.ConcealIsland();
            return;
        }

        _monitoringPlacement = PlaybackIslandPlacementCapture.RefreshSourceWindow(
            _monitoringPlacement);
        _playbackIsland.ShowFor(
            _monitoringPlacement,
            displayState,
            _speech.NavigationState,
            monitoring: true,
            monitoringPaused: queueState.IsPaused || queueState.IsContextSuspended);
    }

    private void ShowNotification(string title, string message, ToolTipIcon icon)
    {
        if (!_disposed)
        {
            _trayIcon.ShowBalloonTip(3000, title, message, icon);
        }
    }

    private async Task ExitApplicationAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _updateCheckTimer.Stop();
        _shutdownCancellation.Cancel();
        _hotkeyGestureCancellation?.Cancel();

        if (_updateTask is not null) await _updateTask;

        if (_modelSetupForm is { } setup)
            await setup.Completion;

        if (_captureTask is not null)
        {
            try
            {
                await _captureTask;
            }
            catch (OperationCanceledException)
            {
                // Cancellation only shortens clipboard polling during shutdown.
            }
        }

        StopMonitoring();
        _speech.Stop();
        _trayMenu.HideMenu();
        _playbackIsland.HideIsland();
        _settingsForm.CloseForApplicationExit();
        _hotkeyManager.Dispose();
        _trayIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Application.Idle -= OnFirstRunSetup;
            _shutdownCancellation.Cancel();
            _hotkeyGestureCancellation?.Cancel();
            _monitoringControlsTimer.Stop();
            _updateCheckTimer.Stop();
            var monitoringSession = _monitoringSession;
            var monitoringQueue = _monitoringSpeechQueue;
            _monitoringSession = null;
            _monitoringSpeechQueue = null;
            monitoringSession?.Dispose();
            monitoringQueue?.Dispose();
            _speech.SpeakingStateChanged -= OnSpeakingStateChanged;
            _speech.Dispose();
            _trayMenu.Dispose();
            _updateClient.Dispose();
            _updateCheckTimer.Dispose();
            _playbackIsland.Dispose();
            _settingsForm.Dispose();
            _hotkeyManager.Dispose();
            _trayIcon.Visible = false;
            _trayIconAnimator?.Dispose();
            _trayIcon.Dispose();
            _uiDispatcher.Dispose();
            _monitoringControlsTimer.Dispose();
            _hotkeyGestureCancellation?.Dispose();
            _shutdownCancellation.Dispose();
        }

        base.Dispose(disposing);
    }

}
