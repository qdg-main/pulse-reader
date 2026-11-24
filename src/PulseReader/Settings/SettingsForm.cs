using PulseReader.Configuration;
using PulseReader.Hotkeys;
using PulseReader.Speech;

namespace PulseReader.Settings;

internal sealed class SettingsForm : Form
{
    private const int CollapsedClientHeight = 915;
    private const int ExpandedClientHeight = 1150;
    private const int CollapsedVoicePanelHeight = 417;
    private const int ExpandedVoicePanelHeight = 652;
    private const int ContentTop = 72;
    private const int WindowEdgeMargin = 12;

    private readonly Func<AppConfiguration, string?> _applyConfiguration;
    private readonly Func<HotkeyDefinition, string?> _applyHotkey;
    private readonly Func<string, Task<SpeechStartResult>> _previewSpeech;
    private readonly Action _stopSpeech;
    private readonly Func<string?> _suspendHotkeyCapture;
    private readonly Func<string?> _resumeHotkeyCapture;
    private readonly Func<bool, string?> _applyStartupRegistration;
    private readonly IReadOnlyList<WindowsVoiceDescriptor> _windowsVoices;
    private ContextualPronunciationAvailability _contextualPronunciationAvailability;
    private readonly Func<Task<bool>>? _installContextualPronunciation;
    private readonly Func<ContextualPronunciationAvailability>? _getContextualPronunciationAvailability;
    private readonly ThemedComboBox _backendBox = new();
    private readonly ThemedComboBox _languageBox = new();
    private readonly ThemedComboBox _voiceBox = new();
    private readonly BlendSectionHeader _blendSectionHeader = new();
    private readonly GlowCheckBox _blendVoicesCheckBox = new();
    private readonly GlowCheckBox _showPlaybackControlsCheckBox = new();
    private readonly GlowCheckBox _contextualPronunciationCheckBox = new();
    private readonly GlowCheckBox _startWithWindowsCheckBox = new();
    private readonly GlowCheckBox _automaticUpdatesCheckBox = new();
    private readonly ThemedComboBox _secondaryLanguageBox = new();
    private readonly ThemedComboBox _secondaryVoiceBox = new();
    private readonly GlowSlider _blendBalanceSlider = new();
    private readonly GlowSlider _speedSlider = new();
    private readonly GlowSlider _volumeSlider = new();
    private readonly Label _languageLabel = FieldLabel("LANGUAGE");
    private readonly Label _voiceLabel = FieldLabel("VOICE");
    private readonly Label _secondaryLanguageLabel = FieldLabel("LANGUAGE 2");
    private readonly Label _secondaryVoiceLabel = FieldLabel("VOICE 2");
    private readonly Label _blendBalanceLabel = FieldLabel("VOICE BALANCE");
    private readonly Label _speedLabel = FieldLabel("SPEED");
    private readonly Label _volumeLabel = FieldLabel("VOLUME");
    private readonly Label _speedValue = ValueLabel();
    private readonly Label _volumeValue = ValueLabel();
    private readonly Label _blendBalanceValue = ValueLabel();
    private readonly Label _statusLabel = new();
    private readonly HotkeyCaptureBox _hotkeyBox = new();
    private readonly GlowButton _applyHotkeyButton = new();
    private readonly GlowButton _previewButton = new();
    private readonly GlowButton _resetSpeedButton = ResetButton();
    private readonly GlowButton _resetVolumeButton = ResetButton();
    private readonly Panel _blendBottomSeparator = Separator();
    private readonly SettingsScrollViewport _contentViewport = new();
    private readonly ThemePanel _voicePanel = new();
    private readonly ThemePanel _contextualPronunciationPanel = new();
    private readonly ThemePanel _hotkeyPanel = new();
    private readonly ThemePanel _playbackPanel = new();
    private readonly ThemePanel _startupPanel = new();
    private readonly ThemePanel _updatesPanel = new();
    private readonly SettingsWindowChrome _windowChrome;
    private readonly Bitmap _logo;
    private AppConfiguration _configuration;
    private HotkeyDefinition _registeredHotkey;
    private int _previewRequestVersion;
    private int _desiredClientHeight = CollapsedClientHeight;
    private bool _previewRequested;
    private bool _initializing = true;
    private bool _allowClose;
    private bool _layeredChromeActive;
    private bool _focusBackendOnNextShow;
    private bool _adjustingWindowBounds;
    private bool _updatingSpeechControls;
    private bool _updatingStartupControl;
    private bool _updatingPlaybackControl;
    private bool _updatingAutomaticUpdatesControl;
    private bool _updatingContextualPronunciationControl;
    private Rectangle _workingArea;

    public SettingsForm(
        AppConfiguration configuration,
        HotkeyDefinition hotkey,
        Func<AppConfiguration, string?> applyConfiguration,
        Func<HotkeyDefinition, string?> applyHotkey,
        Func<string, Task<SpeechStartResult>> previewSpeech,
        Action stopSpeech,
        Func<string?> suspendHotkeyCapture,
        Func<string?> resumeHotkeyCapture,
        IReadOnlyList<WindowsVoiceDescriptor>? windowsVoices = null,
        bool startWithWindows = false,
        Func<bool, string?>? applyStartupRegistration = null,
        ContextualPronunciationAvailability? contextualPronunciationAvailability = null,
        Func<Task<bool>>? installContextualPronunciation = null,
        Func<ContextualPronunciationAvailability>? getContextualPronunciationAvailability = null)
    {
        _configuration = configuration;
        _registeredHotkey = hotkey;
        _applyConfiguration = applyConfiguration;
        _applyHotkey = applyHotkey;
        _previewSpeech = previewSpeech;
        _stopSpeech = stopSpeech;
        _suspendHotkeyCapture = suspendHotkeyCapture;
        _resumeHotkeyCapture = resumeHotkeyCapture;
        _windowsVoices = windowsVoices ?? Array.Empty<WindowsVoiceDescriptor>();
        _installContextualPronunciation = installContextualPronunciation;
        _getContextualPronunciationAvailability = getContextualPronunciationAvailability;
        _contextualPronunciationAvailability = contextualPronunciationAvailability
            ?? new ContextualPronunciationAvailability(
                false,
                string.Empty,
                "Optional model not installed. See the configuration guide.");
        _applyStartupRegistration = applyStartupRegistration ?? (_ => null);
        _startWithWindowsCheckBox.Checked = startWithWindows;

        using var logoSource = TrayIconFrameRenderer.LoadSpeakingSource(0);
        _logo = TrayIconFrameRenderer.Render(logoSource, 48);
        _windowChrome = new SettingsWindowChrome();

        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(416, CollapsedClientHeight);
        MinimumSize = Size;
        MaximumSize = Size;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = PulseReaderTheme.Window;
        ForeColor = PulseReaderTheme.Text;
        Font = new Font("Segoe UI", 9f);
        Text = "Pulse Reader Settings";
        KeyPreview = true;
        DoubleBuffered = true;

        BuildInterface();
        PopulateFromConfiguration();
        _initializing = false;

        LocationChanged += (_, _) =>
        {
            if (!_adjustingWindowBounds && Visible)
            {
                var workingArea = Screen.FromControl(this).WorkingArea;
                if (workingArea != _workingArea)
                {
                    ApplyDesiredClientHeight(workingArea, false);
                }
            }

            SyncWindowChrome();
        };
        Resize += (_, _) =>
        {
            UpdateWindowRegion();
            SyncWindowChrome();
        };
        DpiChanged += (_, _) =>
            ApplyDesiredClientHeight(Screen.FromControl(this).WorkingArea, false);
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };
        FormClosing += OnFormClosing;
        VisibleChanged += (_, _) =>
        {
            if (!Visible)
            {
                _focusBackendOnNextShow = _hotkeyBox.IsCapturing || _hotkeyBox.Focused;
                _hotkeyBox.CancelCapture();
                DiscardPendingHotkey();
                ResumeHotkeyCapture();
            }
            else if (_focusBackendOnNextShow)
            {
                _focusBackendOnNextShow = false;
                BeginInvoke(() =>
                {
                    if (Visible && !_backendBox.IsDisposed)
                    {
                        _backendBox.Focus();
                    }
                });
            }
        };
        UpdateWindowRegion();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && !_windowChrome.IsDisposed)
        {
            _layeredChromeActive = _windowChrome.TryShowChrome(Bounds);
            Owner = _layeredChromeActive ? _windowChrome : null;
            UpdateWindowRegion();
        }

        base.SetVisibleCore(value);
        if (!value && !_windowChrome.IsDisposed)
        {
            _windowChrome.Hide();
        }
    }

    public void ShowNearTray()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        ApplyDesiredClientHeight(workingArea, false);
        Location = new Point(
            Math.Max(workingArea.Left + WindowEdgeMargin, workingArea.Right - Width - WindowEdgeMargin),
            Math.Max(workingArea.Top + WindowEdgeMargin, workingArea.Bottom - Height - WindowEdgeMargin));

        if (!Visible)
        {
            Show();
        }

        WindowState = FormWindowState.Normal;
        Activate();
    }

    public void CloseForApplicationExit()
    {
        _allowClose = true;
        Close();
    }

    public void UpdateSpeakingState(bool isSpeaking)
    {
        if (_previewRequested && !isSpeaking)
        {
            _previewRequested = false;
            UpdatePreviewButton();
        }
    }

    private void BuildInterface()
    {
        var logoBox = new PictureBox
        {
            Image = _logo,
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(18, 13),
            Size = new Size(48, 48),
            BackColor = Color.Transparent
        };
        var title = Label("Pulse Reader", 18f, FontStyle.Bold);
        title.Location = new Point(74, 13);
        title.AutoSize = true;
        var subtitle = Label("VOICE, PLAYBACK & APP SETTINGS", 8f, FontStyle.Bold, PulseReaderTheme.MutedText);
        subtitle.Location = new Point(76, 43);
        subtitle.AutoSize = true;

        var closeButton = new CloseButton
        {
            Location = new Point(372, 14),
            Size = new Size(30, 30),
            TabStop = false
        };
        closeButton.Click += (_, _) => Hide();

        Controls.Add(logoBox);
        Controls.Add(title);
        Controls.Add(subtitle);
        Controls.Add(closeButton);
        EnableWindowDrag(logoBox, title, subtitle);

        _contentViewport.Location = new Point(0, ContentTop);
        _contentViewport.Size = new Size(ClientSize.Width, CollapsedClientHeight - ContentTop);
        _contentViewport.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_contentViewport);

        _voicePanel.Location = new Point(14, 0);
        _voicePanel.Size = new Size(388, CollapsedVoicePanelHeight);
        _contentViewport.ContentControls.Add(_voicePanel);

        AddFieldLabel(_voicePanel, "SPEECH BACKEND", 16, 14);
        _backendBox.Location = new Point(16, 34);
        _backendBox.Size = new Size(356, 33);
        _backendBox.Items.AddRange([SpeechBackend.Kokoro, SpeechBackend.Windows]);
        _backendBox.SelectionChangeCommitted += (_, _) =>
        {
            StopPreview();
            PopulatePrimaryControlsForBackend();
            ApplySpeechSettings();
        };
        _voicePanel.Controls.Add(_backendBox);

        _languageLabel.Location = new Point(16, 78);
        _voicePanel.Controls.Add(_languageLabel);
        _languageBox.Location = new Point(16, 98);
        _languageBox.Size = new Size(356, 33);
        _languageBox.SelectionChangeCommitted += (_, _) =>
        {
            PopulatePrimaryVoices(_languageBox.SelectedItem as string, null);
            ApplySpeechSettings();
            _ = PreviewSelectedVoiceAsync();
        };
        _voicePanel.Controls.Add(_languageBox);

        _voiceLabel.Location = new Point(16, 142);
        _voicePanel.Controls.Add(_voiceLabel);
        _voiceBox.Location = new Point(16, 162);
        _voiceBox.Size = new Size(356, 33);
        _voiceBox.DisplayMember = nameof(KokoroVoiceDescriptor.DisplayName);
        _voiceBox.SelectionChangeCommitted += (_, _) =>
        {
            ApplySpeechSettings();
            _ = PreviewSelectedVoiceAsync();
        };
        _voicePanel.Controls.Add(_voiceBox);

        _blendSectionHeader.Location = new Point(16, 203);
        _blendSectionHeader.Size = new Size(356, 25);
        _blendSectionHeader.ExpandedChanged += (_, _) => ApplyBlendLayout();
        _voicePanel.Controls.Add(_blendSectionHeader);

        _blendVoicesCheckBox.Text = "Blend a second voice";
        _blendVoicesCheckBox.Font = new Font("Segoe UI Semibold", 9f);
        _blendVoicesCheckBox.Location = new Point(16, 235);
        _blendVoicesCheckBox.Size = new Size(356, 29);
        _blendVoicesCheckBox.CheckedChanged += (_, _) =>
        {
            _blendSectionHeader.BlendEnabled = _blendVoicesCheckBox.Checked;
            UpdateBackendControlState();
            if (!_initializing)
            {
                StopPreview();
                ApplySpeechSettings();
                if (_backendBox.SelectedItem is SpeechBackend.Kokoro)
                {
                    _ = PreviewSelectedVoiceAsync();
                }
            }
        };
        _voicePanel.Controls.Add(_blendVoicesCheckBox);

        _secondaryLanguageLabel.Location = new Point(16, 275);
        _voicePanel.Controls.Add(_secondaryLanguageLabel);
        _secondaryLanguageBox.Location = new Point(16, 295);
        _secondaryLanguageBox.Size = new Size(356, 33);
        _secondaryLanguageBox.Items.AddRange(KokoroVoiceCatalog.Languages.Cast<object>().ToArray());
        _secondaryLanguageBox.SelectionChangeCommitted += (_, _) =>
        {
            PopulateSecondaryVoices(_secondaryLanguageBox.SelectedItem as string, null);
            ApplySpeechSettings();
            _ = PreviewSelectedVoiceAsync();
        };
        _voicePanel.Controls.Add(_secondaryLanguageBox);

        _secondaryVoiceLabel.Location = new Point(16, 339);
        _voicePanel.Controls.Add(_secondaryVoiceLabel);
        _secondaryVoiceBox.Location = new Point(16, 359);
        _secondaryVoiceBox.Size = new Size(356, 33);
        _secondaryVoiceBox.DisplayMember = nameof(KokoroVoiceDescriptor.DisplayName);
        _secondaryVoiceBox.SelectionChangeCommitted += (_, _) =>
        {
            ApplySpeechSettings();
            _ = PreviewSelectedVoiceAsync();
        };
        _voicePanel.Controls.Add(_secondaryVoiceBox);

        _blendBalanceLabel.Location = new Point(16, 403);
        _voicePanel.Controls.Add(_blendBalanceLabel);
        _blendBalanceValue.Location = new Point(172, 399);
        _blendBalanceValue.Size = new Size(200, 20);
        _voicePanel.Controls.Add(_blendBalanceValue);
        _blendBalanceSlider.Location = new Point(16, 422);
        _blendBalanceSlider.Size = new Size(356, 25);
        _blendBalanceSlider.Minimum = 0;
        _blendBalanceSlider.Maximum = 100;
        _blendBalanceSlider.ValueChanged += (_, _) =>
        {
            UpdateBlendBalanceText();
            ApplySpeechSettings();
        };
        _voicePanel.Controls.Add(_blendBalanceSlider);

        _blendBottomSeparator.Location = new Point(16, 458);
        _blendBottomSeparator.Size = new Size(356, 1);
        _voicePanel.Controls.Add(_blendBottomSeparator);

        _speedLabel.Location = new Point(16, 239);
        _voicePanel.Controls.Add(_speedLabel);
        _resetSpeedButton.Location = new Point(270, 235);
        _resetSpeedButton.Click += (_, _) => ResetSpeed();
        _voicePanel.Controls.Add(_resetSpeedButton);
        _speedValue.Location = new Point(328, 235);
        _voicePanel.Controls.Add(_speedValue);
        _speedSlider.Location = new Point(16, 258);
        _speedSlider.Size = new Size(356, 25);
        _speedSlider.Minimum = 75;
        _speedSlider.Maximum = 125;
        _speedSlider.ValueChanged += (_, _) =>
        {
            UpdateSpeedText();
            ApplySpeechSettings();
        };
        _voicePanel.Controls.Add(_speedSlider);

        _volumeLabel.Location = new Point(16, 294);
        _voicePanel.Controls.Add(_volumeLabel);
        _resetVolumeButton.Location = new Point(270, 290);
        _resetVolumeButton.Click += (_, _) => _volumeSlider.Value = 100;
        _voicePanel.Controls.Add(_resetVolumeButton);
        _volumeValue.Location = new Point(328, 290);
        _voicePanel.Controls.Add(_volumeValue);
        _volumeSlider.Location = new Point(16, 313);
        _volumeSlider.Size = new Size(356, 25);
        _volumeSlider.Minimum = 0;
        _volumeSlider.Maximum = 100;
        _volumeSlider.ValueChanged += (_, _) =>
        {
            _volumeValue.Text = $"{_volumeSlider.Value}%";
            ApplySpeechSettings();
        };
        _voicePanel.Controls.Add(_volumeSlider);

        _previewButton.Text = "▶  Preview voice";
        _previewButton.Location = new Point(16, 359);
        _previewButton.Size = new Size(356, 42);
        _previewButton.Click += async (_, _) => await TogglePreviewAsync();
        _voicePanel.Controls.Add(_previewButton);

        _contextualPronunciationPanel.Location = new Point(14, _voicePanel.Bottom + 10);
        _contextualPronunciationPanel.Size = new Size(388, 64);
        _contentViewport.ContentControls.Add(_contextualPronunciationPanel);
        AddFieldLabel(_contextualPronunciationPanel, "CONTEXTUAL PRONUNCIATION", 16, 9);
        _contextualPronunciationCheckBox.Text = _contextualPronunciationAvailability.IsInstalled
            || _installContextualPronunciation is not null
            ? "Use local model for ambiguous words"
            : "Optional local model is not installed";
        _contextualPronunciationCheckBox.Font = new Font("Segoe UI Semibold", 9f);
        _contextualPronunciationCheckBox.Location = new Point(16, 27);
        _contextualPronunciationCheckBox.Size = new Size(356, 29);
        _contextualPronunciationCheckBox.CheckedChanged += async (_, _) =>
            await ApplyContextualPronunciationSettingAsync();
        _contextualPronunciationPanel.Controls.Add(_contextualPronunciationCheckBox);
        _hotkeyPanel.Location = new Point(14, _contextualPronunciationPanel.Bottom + 10);
        _hotkeyPanel.Size = new Size(388, 82);
        _contentViewport.ContentControls.Add(_hotkeyPanel);
        AddFieldLabel(_hotkeyPanel, "GLOBAL HOTKEY", 16, 11);
        _hotkeyBox.Location = new Point(16, 33);
        _hotkeyBox.Size = new Size(224, 30);
        _hotkeyBox.HotkeyChanged += (_, _) =>
        {
            _applyHotkeyButton.Enabled = _hotkeyBox.Hotkey != _registeredHotkey;
            SetStatus("Press Apply to register this shortcut.");
        };
        _hotkeyBox.CaptureStarted += (_, _) => SuspendHotkeyCapture();
        _hotkeyBox.CaptureEnded += (_, _) => ResumeHotkeyCapture();
        _hotkeyPanel.Controls.Add(_hotkeyBox);

        _applyHotkeyButton.Text = "Apply";
        _applyHotkeyButton.Location = new Point(251, 32);
        _applyHotkeyButton.Size = new Size(121, 32);
        _applyHotkeyButton.Click += (_, _) => ApplyHotkey();
        _hotkeyPanel.Controls.Add(_applyHotkeyButton);

        _playbackPanel.Location = new Point(14, _hotkeyPanel.Bottom + 10);
        _playbackPanel.Size = new Size(388, 64);
        _contentViewport.ContentControls.Add(_playbackPanel);
        AddFieldLabel(_playbackPanel, "FLOATING CONTROLS", 16, 9);
        _showPlaybackControlsCheckBox.Text = "Show playback and monitoring controls";
        _showPlaybackControlsCheckBox.Font = new Font("Segoe UI Semibold", 9f);
        _showPlaybackControlsCheckBox.Location = new Point(16, 28);
        _showPlaybackControlsCheckBox.Size = new Size(356, 29);
        _showPlaybackControlsCheckBox.CheckedChanged += (_, _) => ApplyPlaybackControlSetting();
        _playbackPanel.Controls.Add(_showPlaybackControlsCheckBox);

        _startupPanel.Location = new Point(14, _playbackPanel.Bottom + 10);
        _startupPanel.Size = new Size(388, 64);
        _contentViewport.ContentControls.Add(_startupPanel);
        AddFieldLabel(_startupPanel, "WINDOWS STARTUP", 16, 9);
        _startWithWindowsCheckBox.Text = "Start Pulse Reader when I sign in";
        _startWithWindowsCheckBox.Font = new Font("Segoe UI Semibold", 9f);
        _startWithWindowsCheckBox.Location = new Point(16, 28);
        _startWithWindowsCheckBox.Size = new Size(356, 29);
        _startWithWindowsCheckBox.CheckedChanged += (_, _) => ApplyStartupRegistration();
        _startupPanel.Controls.Add(_startWithWindowsCheckBox);

        _updatesPanel.Location = new Point(14, _startupPanel.Bottom + 10);
        _updatesPanel.Size = new Size(388, 64);
        _contentViewport.ContentControls.Add(_updatesPanel);
        AddFieldLabel(_updatesPanel, "APP UPDATES", 16, 9);
        _automaticUpdatesCheckBox.Text = "Check for updates automatically";
        _automaticUpdatesCheckBox.AccessibleName = _automaticUpdatesCheckBox.Text;
        _automaticUpdatesCheckBox.Font = new Font("Segoe UI Semibold", 9f);
        _automaticUpdatesCheckBox.Location = new Point(16, 28);
        _automaticUpdatesCheckBox.Size = new Size(356, 29);
        _automaticUpdatesCheckBox.CheckedChanged += (_, _) => ApplyAutomaticUpdatesSetting();
        _updatesPanel.Controls.Add(_automaticUpdatesCheckBox);

        _statusLabel.Location = new Point(18, _updatesPanel.Bottom + 8);
        _statusLabel.Size = new Size(380, 20);
        _statusLabel.ForeColor = PulseReaderTheme.MutedText;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _contentViewport.ContentControls.Add(_statusLabel);

        _contentViewport.RegisterFocusScrolling(_voicePanel);
        _contentViewport.RegisterFocusScrolling(_contextualPronunciationPanel);
        _contentViewport.RegisterFocusScrolling(_hotkeyPanel);
        _contentViewport.RegisterFocusScrolling(_playbackPanel);
        _contentViewport.RegisterFocusScrolling(_startupPanel);
        _contentViewport.RegisterFocusScrolling(_updatesPanel);
    }

    private void PopulateFromConfiguration()
    {
        _backendBox.SelectedItem = _configuration.SpeechBackend;
        var secondaryVoice = KokoroVoiceCatalog.Get(_configuration.KokoroSecondaryVoice);
        _secondaryLanguageBox.SelectedItem = secondaryVoice.Language;
        PopulateSecondaryVoices(secondaryVoice.Language, secondaryVoice.Id);
        _blendBalanceSlider.Value = _configuration.KokoroPrimaryVoiceWeight;
        _blendVoicesCheckBox.Checked = _configuration.KokoroVoiceBlendEnabled;
        _blendSectionHeader.Expanded = _configuration.KokoroVoiceBlendEnabled;
        _contextualPronunciationCheckBox.Checked =
            _configuration.ContextualPronunciationEnabled;
        _showPlaybackControlsCheckBox.Checked = _configuration.ShowPlaybackControls;
        _automaticUpdatesCheckBox.Checked = _configuration.CheckForUpdatesAutomatically;
        PopulatePrimaryControlsForBackend();
        UpdateBlendBalanceText();
        _hotkeyBox.SetHotkey(_registeredHotkey);
        _applyHotkeyButton.Enabled = false;
        ApplyBlendLayout();
        UpdateBackendControlState();
        UpdatePreviewButton();
        SetStatus(DefaultStatusText());
    }

    private SpeechBackend SelectedBackend =>
        _backendBox.SelectedItem is SpeechBackend backend
            ? backend
            : SpeechBackend.Kokoro;

    private void PopulatePrimaryControlsForBackend()
    {
        _updatingSpeechControls = true;
        try
        {
            if (SelectedBackend == SpeechBackend.Kokoro)
            {
                var voice = KokoroVoiceCatalog.Get(_configuration.KokoroVoice);
                PopulateLanguageBox(KokoroVoiceCatalog.Languages, voice.Language);
                PopulatePrimaryVoices(voice.Language, voice.Id);
                ConfigureSpeedSlider(75, 125, (int)Math.Round(_configuration.SpeechSpeed * 100));
                _volumeSlider.Value = _configuration.SpeechVolume;
            }
            else
            {
                var voice = _windowsVoices.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, _configuration.WindowsVoice, StringComparison.OrdinalIgnoreCase))
                    ?? _windowsVoices.FirstOrDefault(candidate => candidate.IsDefault)
                    ?? _windowsVoices.FirstOrDefault();
                var languages = _windowsVoices
                    .Select(candidate => candidate.Language)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                PopulateLanguageBox(languages, voice?.Language);
                PopulatePrimaryVoices(voice?.Language, voice?.Id);
                ConfigureSpeedSlider(
                    WindowsSpeechSettings.MinimumRate,
                    WindowsSpeechSettings.MaximumRate,
                    _configuration.WindowsSpeechRate);
                _volumeSlider.Value = _configuration.WindowsSpeechVolume;
            }

            UpdateSpeedText();
            _volumeValue.Text = $"{_volumeSlider.Value}%";
        }
        finally
        {
            _updatingSpeechControls = false;
        }

        UpdateBackendControlState();
    }

    private void ConfigureSpeedSlider(int minimum, int maximum, int value)
    {
        if (_speedSlider.Maximum < minimum)
        {
            _speedSlider.Maximum = maximum;
        }

        _speedSlider.Minimum = minimum;
        _speedSlider.Maximum = maximum;
        _speedSlider.Value = value;
    }

    private void PopulateLanguageBox(IEnumerable<string> languages, string? selectedLanguage)
    {
        var items = languages.Cast<object>().ToArray();
        _languageBox.BeginUpdate();
        _languageBox.Items.Clear();
        _languageBox.Items.AddRange(items);
        _languageBox.SelectedItem = items.FirstOrDefault(item =>
            string.Equals((string)item, selectedLanguage, StringComparison.CurrentCultureIgnoreCase))
            ?? items.FirstOrDefault();
        _languageBox.EndUpdate();
    }

    private void PopulatePrimaryVoices(string? language, string? selectedVoiceId)
    {
        if (SelectedBackend == SpeechBackend.Windows)
        {
            var voices = _windowsVoices
                .Where(voice => string.Equals(
                    voice.Language,
                    language,
                    StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
            PopulateVoiceBox(_voiceBox, voices, selectedVoiceId);
            return;
        }

        var kokoroVoices = KokoroVoiceCatalog.GetByLanguage(
            language ?? KokoroVoiceCatalog.Get(KokoroSpeechSettings.DefaultVoice).Language);
        PopulateVoiceBox(_voiceBox, kokoroVoices, selectedVoiceId);
    }

    private void PopulateSecondaryVoices(string? language, string? selectedVoiceId)
    {
        var voices = KokoroVoiceCatalog.GetByLanguage(
            language ?? KokoroVoiceCatalog.Get(KokoroSpeechSettings.DefaultSecondaryVoice).Language);
        PopulateVoiceBox(_secondaryVoiceBox, voices, selectedVoiceId);
    }

    private static void PopulateVoiceBox(
        ThemedComboBox voiceBox,
        IReadOnlyList<KokoroVoiceDescriptor> voices,
        string? selectedVoiceId)
    {
        voiceBox.BeginUpdate();
        voiceBox.Items.Clear();
        voiceBox.Items.AddRange(voices.Cast<object>().ToArray());
        voiceBox.SelectedItem = voices.FirstOrDefault(voice => voice.Id == selectedVoiceId) ?? voices.First();
        voiceBox.EndUpdate();
    }

    private static void PopulateVoiceBox(
        ThemedComboBox voiceBox,
        IReadOnlyList<WindowsVoiceDescriptor> voices,
        string? selectedVoiceId)
    {
        voiceBox.BeginUpdate();
        voiceBox.Items.Clear();
        voiceBox.Items.AddRange(voices.Cast<object>().ToArray());
        voiceBox.SelectedItem = voices.FirstOrDefault(voice =>
                string.Equals(voice.Id, selectedVoiceId, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault();
        voiceBox.EndUpdate();
    }

    private void ApplySpeechSettings()
    {
        if (_initializing || _updatingSpeechControls)
        {
            return;
        }

        AppConfiguration next;
        if (SelectedBackend == SpeechBackend.Kokoro)
        {
            if (_voiceBox.SelectedItem is not KokoroVoiceDescriptor voice
                || _secondaryVoiceBox.SelectedItem is not KokoroVoiceDescriptor secondaryVoice)
            {
                return;
            }

            next = _configuration with
            {
                SpeechBackend = SpeechBackend.Kokoro,
                KokoroVoice = voice.Id,
                KokoroVoiceBlendEnabled = _blendVoicesCheckBox.Checked,
                KokoroSecondaryVoice = secondaryVoice.Id,
                KokoroPrimaryVoiceWeight = _blendBalanceSlider.Value,
                SpeechSpeed = _speedSlider.Value / 100f,
                SpeechVolume = _volumeSlider.Value
            };
        }
        else
        {
            next = _configuration with
            {
                SpeechBackend = SpeechBackend.Windows,
                WindowsVoice = (_voiceBox.SelectedItem as WindowsVoiceDescriptor)?.Id,
                WindowsSpeechRate = _speedSlider.Value,
                WindowsSpeechVolume = _volumeSlider.Value
            };
        }

        var error = _applyConfiguration(next);
        if (error is null)
        {
            _configuration = next;
            SetStatus(
                SelectedBackend == SpeechBackend.Windows && _windowsVoices.Count == 0
                    ? DefaultStatusText()
                    : "Voice settings saved.",
                SelectedBackend == SpeechBackend.Windows && _windowsVoices.Count == 0);
        }
        else
        {
            // A canceled/failed model install did not change the active backend.
            // Restore its controls before another edit or preview can use stale UI state.
            ReloadConfiguration(_configuration);
            SetStatus(error, true);
        }

        UpdateBackendControlState();
        UpdatePreviewButton();
    }

    private void ApplyHotkey()
    {
        if (_hotkeyBox.Hotkey is not { } hotkey)
        {
            SetStatus("Press the shortcut you want to use first.", true);
            return;
        }

        var error = _applyHotkey(hotkey);
        if (error is null)
        {
            _registeredHotkey = hotkey;
            _configuration = _configuration with { Hotkey = hotkey.ToConfiguration() };
            _applyHotkeyButton.Enabled = false;
            SetStatus($"Hotkey changed to {hotkey.DisplayName}.");
        }
        else
        {
            _hotkeyBox.SetHotkey(_registeredHotkey);
            _applyHotkeyButton.Enabled = false;
            SetStatus(error, true);
        }
    }

    private void ApplyPlaybackControlSetting()
    {
        if (_initializing || _updatingPlaybackControl)
        {
            return;
        }

        var next = _configuration with
        {
            ShowPlaybackControls = _showPlaybackControlsCheckBox.Checked
        };
        var error = _applyConfiguration(next);
        if (error is null)
        {
            _configuration = next;
            SetStatus("Playback control setting saved.");
        }
        else
        {
            _updatingPlaybackControl = true;
            try
            {
                _showPlaybackControlsCheckBox.Checked = _configuration.ShowPlaybackControls;
            }
            finally
            {
                _updatingPlaybackControl = false;
            }

            SetStatus(error, true);
        }
    }

    private void ApplyAutomaticUpdatesSetting()
    {
        if (_initializing || _updatingAutomaticUpdatesControl)
        {
            return;
        }

        var next = _configuration with
        {
            CheckForUpdatesAutomatically = _automaticUpdatesCheckBox.Checked
        };
        var error = _applyConfiguration(next);
        if (error is null)
        {
            _configuration = next;
            SetStatus("Automatic update setting saved.");
            return;
        }

        _updatingAutomaticUpdatesControl = true;
        try
        {
            _automaticUpdatesCheckBox.Checked = _configuration.CheckForUpdatesAutomatically;
        }
        finally
        {
            _updatingAutomaticUpdatesControl = false;
        }

        SetStatus(error, true);
    }

    private async Task ApplyContextualPronunciationSettingAsync()
    {
        if (_initializing || _updatingContextualPronunciationControl)
        {
            return;
        }

        if (_contextualPronunciationCheckBox.Checked
            && !_contextualPronunciationAvailability.IsInstalled
            && _installContextualPronunciation is not null)
        {
            _updatingContextualPronunciationControl = true;
            _contextualPronunciationCheckBox.Enabled = false;
            try
            {
                var installed = await _installContextualPronunciation();
                if (IsDisposed || Disposing) return;
                if (_getContextualPronunciationAvailability is not null)
                    _contextualPronunciationAvailability = _getContextualPronunciationAvailability();
                if (!installed || !_contextualPronunciationAvailability.IsInstalled)
                {
                    _contextualPronunciationCheckBox.Checked = false;
                    SetStatus("Optional model setup was canceled or did not finish.", true);
                    return;
                }
            }
            catch (Exception exception)
            {
                if (IsDisposed || Disposing) return;
                _contextualPronunciationCheckBox.Checked = false;
                SetStatus("Optional model setup failed: " + exception.Message, true);
                return;
            }
            finally
            {
                _updatingContextualPronunciationControl = false;
                if (!IsDisposed && !Disposing) UpdateBackendControlState();
            }
        }

        var next = _configuration with
        {
            ContextualPronunciationEnabled = _contextualPronunciationCheckBox.Checked
        };
        var error = _applyConfiguration(next);
        if (error is null)
        {
            _configuration = next;
            SetStatus(
                next.ContextualPronunciationEnabled
                    ? "Contextual pronunciation enabled."
                    : "Contextual pronunciation disabled.");
        }
        else
        {
            _updatingContextualPronunciationControl = true;
            try
            {
                _contextualPronunciationCheckBox.Checked =
                    _configuration.ContextualPronunciationEnabled;
            }
            finally
            {
                _updatingContextualPronunciationControl = false;
            }

            SetStatus(error, true);
        }
    }

    private void ApplyStartupRegistration()
    {
        if (_initializing || _updatingStartupControl)
        {
            return;
        }

        var enabled = _startWithWindowsCheckBox.Checked;
        var error = _applyStartupRegistration(enabled);
        if (error is null)
        {
            SetStatus(enabled
                ? "Pulse Reader will start when you sign in to Windows."
                : "Pulse Reader will no longer start when you sign in.");
            return;
        }

        _updatingStartupControl = true;
        try
        {
            _startWithWindowsCheckBox.Checked = !enabled;
        }
        finally
        {
            _updatingStartupControl = false;
        }

        SetStatus(error, true);
    }

    private async Task TogglePreviewAsync()
    {
        if (_previewRequested)
        {
            StopPreview();
            return;
        }

        await PreviewSelectedVoiceAsync();
    }

    private async Task PreviewSelectedVoiceAsync()
    {
        var backend = SelectedBackend;
        var kokoroVoice = _voiceBox.SelectedItem as KokoroVoiceDescriptor;
        var secondaryVoice = _secondaryVoiceBox.SelectedItem as KokoroVoiceDescriptor;
        var windowsVoice = _voiceBox.SelectedItem as WindowsVoiceDescriptor;
        if ((backend == SpeechBackend.Kokoro && (kokoroVoice is null || secondaryVoice is null))
            || (backend == SpeechBackend.Windows && windowsVoice is null))
        {
            return;
        }

        var requestVersion = ++_previewRequestVersion;
        _stopSpeech();
        _previewRequested = true;
        UpdatePreviewButton();

        SetStatus(backend == SpeechBackend.Kokoro
            ? "Preparing voice preview…"
            : "Starting Windows voice preview…");

        var previewText = backend == SpeechBackend.Windows
            ? SettingsPresentation.PreviewText(windowsVoice!)
            : SettingsPresentation.PreviewText(
                backend,
                kokoroVoice!,
                _configuration.KokoroVoiceBlendEnabled,
                secondaryVoice,
                _configuration.KokoroPrimaryVoiceWeight);
        var result = await _previewSpeech(previewText);
        if (IsDisposed || requestVersion != _previewRequestVersion)
        {
            return;
        }

        if (result.Started)
        {
            SetStatus(backend == SpeechBackend.Windows
                ? SettingsPresentation.PlayingStatus(windowsVoice!)
                : SettingsPresentation.PlayingStatus(
                    backend,
                    kokoroVoice!,
                    _configuration.KokoroVoiceBlendEnabled,
                    secondaryVoice,
                    _configuration.KokoroPrimaryVoiceWeight));
        }
        else if (!result.Canceled)
        {
            _previewRequested = false;
            UpdatePreviewButton();
            SetStatus(result.Error ?? "The voice preview could not start.", true);
        }
        else
        {
            _previewRequested = false;
            UpdatePreviewButton();
        }
    }

    private void StopPreview()
    {
        _previewRequestVersion++;
        _stopSpeech();
        _previewRequested = false;
        UpdatePreviewButton();
        SetStatus("Preview stopped.");
    }

    private void UpdatePreviewButton()
    {
        var backend = _backendBox.SelectedItem is SpeechBackend selectedBackend
            ? selectedBackend
            : SpeechBackend.Kokoro;
        _previewButton.Text = SettingsPresentation.PreviewButtonText(backend, _previewRequested);
    }

    internal void ReloadConfiguration(AppConfiguration configuration)
    {
        _initializing = true;
        try
        {
            _configuration = configuration;
            PopulateFromConfiguration();
        }
        finally { _initializing = false; }
    }

    private void UpdateBackendControlState()
    {
        var kokoroEnabled = SelectedBackend == SpeechBackend.Kokoro;
        var primaryEnabled = kokoroEnabled || _windowsVoices.Count > 0;
        var blendEnabled = kokoroEnabled && _blendVoicesCheckBox.Checked;
        _languageBox.Enabled = primaryEnabled;
        _voiceBox.Enabled = primaryEnabled;
        _blendSectionHeader.Enabled = kokoroEnabled;
        _blendSectionHeader.BlendEnabled = _blendVoicesCheckBox.Checked;
        _blendVoicesCheckBox.Enabled = kokoroEnabled;
        _secondaryLanguageBox.Enabled = blendEnabled;
        _secondaryVoiceBox.Enabled = blendEnabled;
        _blendBalanceSlider.Enabled = blendEnabled;
        _speedSlider.Enabled = primaryEnabled;
        _volumeSlider.Enabled = primaryEnabled;
        _resetSpeedButton.Enabled = primaryEnabled;
        _resetVolumeButton.Enabled = primaryEnabled;
        _previewButton.Enabled = primaryEnabled;
        var contextualEnabled = kokoroEnabled
            && (_contextualPronunciationAvailability.IsInstalled
                || _installContextualPronunciation is not null
                || _configuration.ContextualPronunciationEnabled);
        _contextualPronunciationCheckBox.Enabled = contextualEnabled;

        var labelColor = primaryEnabled ? PulseReaderTheme.MutedText : PulseReaderTheme.DisabledText;
        var valueColor = primaryEnabled ? PulseReaderTheme.AccentBright : PulseReaderTheme.DisabledText;
        _languageLabel.ForeColor = labelColor;
        _voiceLabel.ForeColor = labelColor;
        _secondaryLanguageLabel.ForeColor = blendEnabled
            ? PulseReaderTheme.MutedText
            : PulseReaderTheme.DisabledText;
        _secondaryVoiceLabel.ForeColor = blendEnabled
            ? PulseReaderTheme.MutedText
            : PulseReaderTheme.DisabledText;
        _blendBalanceLabel.ForeColor = blendEnabled
            ? PulseReaderTheme.MutedText
            : PulseReaderTheme.DisabledText;
        _speedLabel.ForeColor = labelColor;
        _volumeLabel.ForeColor = labelColor;
        _speedValue.ForeColor = valueColor;
        _volumeValue.ForeColor = valueColor;
        _blendBalanceValue.ForeColor = blendEnabled
            ? PulseReaderTheme.AccentBright
            : PulseReaderTheme.DisabledText;
    }

    internal static int ConstrainClientHeight(int desiredClientHeight, int workingAreaHeight) =>
        Math.Min(
            Math.Max(1, desiredClientHeight),
            Math.Max(1, workingAreaHeight - WindowEdgeMargin * 2));

    internal void ApplyWorkingAreaForTest(Rectangle workingArea) =>
        ApplyDesiredClientHeight(workingArea, false);

    protected override void OnMouseWheel(MouseEventArgs eventArgs)
    {
        var pointer = PointToClient(MousePosition);
        if (_contentViewport.Bounds.Contains(pointer)
            && _contentViewport.ScrollByWheel(eventArgs.Delta))
        {
            return;
        }

        base.OnMouseWheel(eventArgs);
    }

    private void UpdateBlendBalanceText()
    {
        _blendBalanceValue.Text =
            $"Voice 1 {_blendBalanceSlider.Value}%  ·  Voice 2 {100 - _blendBalanceSlider.Value}%";
    }

    private void ResetSpeed() =>
        _speedSlider.Value = SelectedBackend == SpeechBackend.Windows
            ? WindowsSpeechSettings.DefaultRate
            : (int)Math.Round(KokoroSpeechSettings.DefaultSpeed * 100);

    private void UpdateSpeedText()
    {
        _speedValue.Text = SelectedBackend == SpeechBackend.Windows
            ? _speedSlider.Value switch
            {
                > 0 => $"+{_speedSlider.Value}",
                _ => _speedSlider.Value.ToString()
            }
            : $"{_speedSlider.Value / 100f:0.00}×";
    }

    private void ApplyBlendLayout()
    {
        var expanded = _blendSectionHeader.Expanded;
        _blendVoicesCheckBox.Visible = expanded;
        _secondaryLanguageLabel.Visible = expanded;
        _secondaryLanguageBox.Visible = expanded;
        _secondaryVoiceLabel.Visible = expanded;
        _secondaryVoiceBox.Visible = expanded;
        _blendBalanceLabel.Visible = expanded;
        _blendBalanceValue.Visible = expanded;
        _blendBalanceSlider.Visible = expanded;
        _blendBottomSeparator.Visible = expanded;

        var shift = expanded ? 235 : 0;
        _speedLabel.Top = 239 + shift;
        _resetSpeedButton.Top = 235 + shift;
        _speedValue.Top = 235 + shift;
        _speedSlider.Top = 258 + shift;
        _volumeLabel.Top = 294 + shift;
        _resetVolumeButton.Top = 290 + shift;
        _volumeValue.Top = 290 + shift;
        _volumeSlider.Top = 313 + shift;
        _previewButton.Top = 359 + shift;

        _voicePanel.Height = expanded ? ExpandedVoicePanelHeight : CollapsedVoicePanelHeight;
        _contextualPronunciationPanel.Top = _voicePanel.Bottom + 10;
        _hotkeyPanel.Top = _contextualPronunciationPanel.Bottom + 10;
        _playbackPanel.Top = _hotkeyPanel.Bottom + 10;
        _startupPanel.Top = _playbackPanel.Bottom + 10;
        _updatesPanel.Top = _startupPanel.Bottom + 10;
        _statusLabel.Top = _updatesPanel.Bottom + 8;
        _desiredClientHeight = expanded ? ExpandedClientHeight : CollapsedClientHeight;
        _contentViewport.SetContentHeight(_desiredClientHeight - ContentTop);
        var workingArea = Visible
            ? Screen.FromControl(this).WorkingArea
            : Screen.FromPoint(Cursor.Position).WorkingArea;
        ApplyDesiredClientHeight(workingArea, true);
        UpdateBackendControlState();
    }

    private void ApplyDesiredClientHeight(Rectangle workingArea, bool preserveBottom)
    {
        _workingArea = workingArea;
        var clientHeight = ConstrainClientHeight(_desiredClientHeight, workingArea.Height);
        var previousBottom = Bottom;
        _adjustingWindowBounds = true;
        try
        {
            if (ClientSize.Height != clientHeight)
            {
                MinimumSize = Size.Empty;
                MaximumSize = Size.Empty;
                ClientSize = new Size(ClientSize.Width, clientHeight);
                MinimumSize = Size;
                MaximumSize = Size;
            }

            _contentViewport.Height = Math.Max(1, ClientSize.Height - ContentTop);
            if (Visible)
            {
                var minimumTop = workingArea.Top + WindowEdgeMargin;
                var maximumTop = Math.Max(
                    minimumTop,
                    workingArea.Bottom - Height - WindowEdgeMargin);
                Top = Math.Clamp(
                    preserveBottom ? previousBottom - Height : Top,
                    minimumTop,
                    maximumTop);
            }
        }
        finally
        {
            _adjustingWindowBounds = false;
        }
    }

    private void SuspendHotkeyCapture()
    {
        var error = _suspendHotkeyCapture();
        if (error is not null)
        {
            SetStatus(error, true);
        }
    }

    private void ResumeHotkeyCapture()
    {
        var error = _resumeHotkeyCapture();
        if (error is not null)
        {
            SetStatus(error, true);
        }
    }

    private void DiscardPendingHotkey()
    {
        if (_hotkeyBox.Hotkey == _registeredHotkey)
        {
            return;
        }

        _hotkeyBox.SetHotkey(_registeredHotkey);
        _applyHotkeyButton.Enabled = false;
        SetStatus(DefaultStatusText());
    }

    private string DefaultStatusText() =>
        _configuration.SpeechBackend == SpeechBackend.Windows
            ? _windowsVoices.Count > 0
                ? "Windows voice settings are saved automatically."
                : "No enabled Windows speech voices were found."
            : "Changes to voice settings are saved automatically.";

    private void SetStatus(string text, bool error = false)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = error ? PulseReaderTheme.Error : PulseReaderTheme.MutedText;
    }

    private void EnableWindowDrag(params Control[] controls)
    {
        MouseEventHandler beginDrag = (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
            }
        };

        MouseDown += beginDrag;
        foreach (var control in controls)
        {
            control.MouseDown += beginDrag;
        }
    }

    private void SyncWindowChrome()
    {
        if (!_layeredChromeActive || _windowChrome.IsDisposed)
        {
            return;
        }

        if (_windowChrome.TryUpdateChrome(Bounds))
        {
            return;
        }

        _layeredChromeActive = false;
        Owner = null;
        UpdateWindowRegion();
        Invalidate();
    }

    private void UpdateWindowRegion()
    {
        var bounds = _layeredChromeActive
            ? Rectangle.Inflate(ClientRectangle, -SettingsWindowChrome.ContentInset, -SettingsWindowChrome.ContentInset)
            : ClientRectangle;
        var radius = _layeredChromeActive
            ? SettingsWindowChrome.CornerRadius - SettingsWindowChrome.ContentInset
            : SettingsWindowChrome.CornerRadius;
        using var path = PulseReaderTheme.RoundedRectangle(bounds, radius);
        Region?.Dispose();
        Region = new Region(path);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (!_allowClose && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Owner = null;
            _windowChrome.Hide();
            _windowChrome.Dispose();
            _logo.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void AddFieldLabel(Control parent, string text, int x, int y)
    {
        var label = FieldLabel(text);
        label.Location = new Point(x, y);
        parent.Controls.Add(label);
    }

    private static Label FieldLabel(string text)
    {
        var label = Label(text, 8f, FontStyle.Bold, PulseReaderTheme.MutedText);
        label.AutoSize = true;
        return label;
    }

    private static Label Label(
        string text,
        float size,
        FontStyle style = FontStyle.Regular,
        Color? color = null) =>
        new()
        {
            Text = text,
            UseMnemonic = false,
            Font = new Font("Segoe UI", size, style),
            ForeColor = color ?? PulseReaderTheme.Text,
            BackColor = Color.Transparent
        };

    private static Label ValueLabel() =>
        new()
        {
            AutoSize = false,
            Size = new Size(44, 20),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = PulseReaderTheme.AccentBright,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 8.5f)
        };

    private static GlowButton ResetButton() =>
        new()
        {
            Text = "Reset",
            Size = new Size(52, 21),
            Font = new Font("Segoe UI Semibold", 7.5f),
            TabStop = false
        };

    private static Panel Separator() =>
        new()
        {
            BackColor = PulseReaderTheme.Separator,
            Enabled = false,
            TabStop = false
        };

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(
            IntPtr windowHandle,
            uint message,
            IntPtr wordParameter,
            IntPtr longParameter);
    }
}
