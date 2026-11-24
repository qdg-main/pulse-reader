using System.Diagnostics;
using System.Drawing.Drawing2D;
using PulseReader.Settings;
using PulseReader.Updates;

namespace PulseReader.Tray;

internal enum TrayMenuCommand
{
    StopSpeaking,
    Settings,
    Update,
    Exit
}

internal sealed class TrayMenuForm : Form
{
    internal static readonly Size MenuSize = new(272, 270);
    internal const int BasePointerDismissDistance = 56;
    internal const int DeactivationToggleSuppressionMilliseconds = 350;

    private const int WsExToolWindow = 0x00000080;

    private readonly SettingsWindowChrome _windowChrome = new();
    private readonly System.Windows.Forms.Timer _pointerDismissTimer = new()
    {
        Interval = 80
    };
    private readonly Bitmap _logo;
    private readonly Label _hotkeyLabel = new();
    private readonly TrayMenuItemControl _stopSpeakingItem =
        new("Stop speaking", TrayMenuGlyph.Stop);
    private readonly TrayMenuItemControl _settingsItem =
        new("Settings", TrayMenuGlyph.Settings);
    private readonly TrayMenuItemControl _updateItem =
        new("Updates unavailable", TrayMenuGlyph.Update);
    private readonly ToolTip _updateToolTip = new();
    private readonly TrayMenuItemControl _exitItem =
        new("Exit Pulse Reader", TrayMenuGlyph.Exit);
    private readonly TrayMenuItemControl[] _items;
    private bool _layeredChromeActive;
    private bool _dismissOnDeactivate = true;
    private bool _dismissedByDeactivation;
    private bool _hidingProgrammatically;
    private bool _monitoring;
    private long _lastDeactivationTimestamp;

    public TrayMenuForm(string hotkeyDisplayName)
    {
        _items = [_stopSpeakingItem, _settingsItem, _updateItem, _exitItem];
        using var logoSource = TrayIconFrameRenderer.LoadSpeakingSource(0);
        _logo = TrayIconFrameRenderer.Render(logoSource, 42);

        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = PulseReaderTheme.Window;
        ClientSize = MenuSize;
        Font = new Font("Segoe UI", 9f);
        ForeColor = PulseReaderTheme.Text;
        FormBorderStyle = FormBorderStyle.None;
        MaximumSize = Size;
        MinimumSize = Size;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Pulse Reader menu";
        TopMost = true;
        AccessibleName = "Pulse Reader notification area menu";

        _windowChrome.TopMost = true;
        _pointerDismissTimer.Tick += (_, _) => DismissIfPointerMovedAway();
        BuildInterface();
        SetHotkey(hotkeyDisplayName);
        SetSpeakingState(false);
        SetUpdateState(UpdateMenuMode.Unsupported);

        _stopSpeakingItem.Click += (_, _) => InvokeCommand(TrayMenuCommand.StopSpeaking);
        _settingsItem.Click += (_, _) => InvokeCommand(TrayMenuCommand.Settings);
        _updateItem.Click += (_, _) => InvokeCommand(TrayMenuCommand.Update);
        _exitItem.Click += (_, _) => InvokeCommand(TrayMenuCommand.Exit);
        Deactivate += (_, _) =>
        {
            if (_dismissOnDeactivate && !_hidingProgrammatically)
            {
                _dismissedByDeactivation = true;
                _lastDeactivationTimestamp = Stopwatch.GetTimestamp();
                HideMenuCore();
            }
        };
        LocationChanged += (_, _) => SyncWindowChrome();
        Resize += (_, _) =>
        {
            UpdateWindowRegion();
            SyncWindowChrome();
        };
        UpdateWindowRegion();
    }

    public event EventHandler? StopSpeakingRequested;

    public event EventHandler? StopMonitoringRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? UpdateRequested;

    public event EventHandler? ExitRequested;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
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
            _pointerDismissTimer.Stop();
            _windowChrome.Hide();
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideMenu();
            return true;
        }

        if (keyData is Keys.Down or Keys.Up)
        {
            MoveSelection(keyData == Keys.Down ? 1 : -1);
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    public void ToggleAt(Point cursorPosition)
    {
        if (Visible)
        {
            HideMenu();
            return;
        }

        if (ConsumeRecentDeactivation())
        {
            return;
        }

        ShowAt(cursorPosition);
    }

    public void ShowAt(Point cursorPosition)
    {
        _dismissedByDeactivation = false;
        var workingArea = Screen.FromPoint(cursorPosition).WorkingArea;
        Bounds = TrayMenuPlacement.Calculate(MenuSize, cursorPosition, workingArea);
        foreach (var item in _items)
        {
            item.SetKeyboardFocusVisible(false);
        }

        Show();
        Activate();
        _pointerDismissTimer.Start();
    }

    public void HideMenu()
    {
        _dismissedByDeactivation = false;
        _hidingProgrammatically = true;
        try
        {
            HideMenuCore();
        }
        finally
        {
            _hidingProgrammatically = false;
        }
    }

    public void SetHotkey(string displayName)
    {
        _hotkeyLabel.Text = $"HOTKEY   {displayName}";
    }

    public void SetSpeakingState(bool isSpeaking)
    {
        _stopSpeakingItem.Enabled = isSpeaking || _monitoring;
    }

    public void SetMonitoringState(bool monitoring)
    {
        _monitoring = monitoring;
        _stopSpeakingItem.Text = monitoring ? "Stop monitoring" : "Stop speaking";
        _stopSpeakingItem.AccessibleName = _stopSpeakingItem.Text;
        _stopSpeakingItem.Invalidate();
    }

    public void SetUpdateState(
        UpdateMenuMode mode,
        string? availableVersion = null,
        int? downloadPercent = null)
    {
        _updateItem.Text = mode switch
        {
            UpdateMenuMode.Idle => "Check for updates",
            UpdateMenuMode.Checking => "Checking for updates…",
            UpdateMenuMode.Available => string.IsNullOrWhiteSpace(availableVersion)
                ? "Update available" : $"Update to {availableVersion}",
            UpdateMenuMode.Downloading => "Cancel download",
            UpdateMenuMode.Cancelling => "Cancelling download…",
            UpdateMenuMode.Applying => "Applying update…",
            _ => "Updates unavailable"
        };
        _updateItem.Enabled = mode is UpdateMenuMode.Idle
            or UpdateMenuMode.Available or UpdateMenuMode.Downloading;
        _updateItem.AccessibleName = _updateItem.Text;
        var progress = mode == UpdateMenuMode.Downloading && downloadPercent.HasValue
            ? $"Downloading update: {Math.Clamp(downloadPercent.Value, 0, 100)}%. Select to cancel."
            : mode == UpdateMenuMode.Downloading
                ? "Downloading update. Select to cancel."
                : _updateItem.Text;
        _updateItem.AccessibleDescription = progress;
        _updateToolTip.SetToolTip(_updateItem, progress);
        _updateItem.Invalidate();
    }

    internal bool StopSpeakingEnabledForTest => _stopSpeakingItem.Enabled;

    internal string StopSpeakingTextForTest => _stopSpeakingItem.Text;

    internal string HotkeyTextForTest => _hotkeyLabel.Text;

    internal void SetDismissOnDeactivateForTest(bool enabled) =>
        _dismissOnDeactivate = enabled;

    internal void ActivateForTest(TrayMenuCommand command) =>
        ItemFor(command).PerformClick();

    internal bool ProcessKeyForTest(Keys keyData)
    {
        var message = new Message();
        return ProcessCmdKey(ref message, keyData);
    }

    internal static bool IsPointerBeyondDismissDistance(
        Rectangle menuBounds,
        Point pointer,
        int dismissDistance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dismissDistance);
        var dismissalBounds = Rectangle.Inflate(
            menuBounds,
            dismissDistance,
            dismissDistance);
        return !dismissalBounds.Contains(pointer);
    }

    internal static bool ShouldSuppressToggleAfterDeactivation(TimeSpan elapsed) =>
        elapsed >= TimeSpan.Zero
        && elapsed <= TimeSpan.FromMilliseconds(DeactivationToggleSuppressionMilliseconds);

    private void BuildInterface()
    {
        var logoBox = new PictureBox
        {
            AccessibleName = "Pulse Reader",
            BackColor = Color.Transparent,
            Image = _logo,
            Location = new Point(17, 14),
            Size = new Size(42, 42),
            SizeMode = PictureBoxSizeMode.Zoom,
            TabStop = false
        };
        Controls.Add(logoBox);

        var titleLabel = new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 11.5f),
            ForeColor = PulseReaderTheme.Text,
            Location = new Point(70, 17),
            Text = "Pulse Reader"
        };
        Controls.Add(titleLabel);

        _hotkeyLabel.AutoSize = false;
        _hotkeyLabel.BackColor = Color.Transparent;
        _hotkeyLabel.Font = new Font("Segoe UI Semibold", 7.75f);
        _hotkeyLabel.ForeColor = PulseReaderTheme.MutedText;
        _hotkeyLabel.Location = new Point(71, 42);
        _hotkeyLabel.Size = new Size(184, 18);
        _hotkeyLabel.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_hotkeyLabel);

        Controls.Add(CreateSeparator(69));
        ConfigureItem(_stopSpeakingItem, 77);
        ConfigureItem(_settingsItem, 121);
        ConfigureItem(_updateItem, 165);
        Controls.Add(CreateSeparator(212));
        ConfigureItem(_exitItem, 220);
    }

    private void ConfigureItem(TrayMenuItemControl item, int top)
    {
        item.Location = new Point(12, top);
        item.Size = new Size(248, 40);
        Controls.Add(item);
    }

    private static Panel CreateSeparator(int top) =>
        new()
        {
            BackColor = PulseReaderTheme.Separator,
            Location = new Point(16, top),
            Size = new Size(240, 1),
            TabStop = false
        };

    private void MoveSelection(int direction)
    {
        var enabled = _items.Where(item => item.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            return;
        }

        var currentIndex = Array.FindIndex(enabled, item => item.Focused);
        var nextIndex = currentIndex < 0
            ? direction > 0 ? 0 : enabled.Length - 1
            : (currentIndex + direction + enabled.Length) % enabled.Length;
        foreach (var item in _items)
        {
            item.SetKeyboardFocusVisible(item == enabled[nextIndex]);
        }

        enabled[nextIndex].Select();
    }

    private TrayMenuItemControl ItemFor(TrayMenuCommand command) => command switch
    {
        TrayMenuCommand.StopSpeaking => _stopSpeakingItem,
        TrayMenuCommand.Settings => _settingsItem,
        TrayMenuCommand.Update => _updateItem,
        TrayMenuCommand.Exit => _exitItem,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    private void InvokeCommand(TrayMenuCommand command)
    {
        if (!ItemFor(command).Enabled)
        {
            return;
        }

        HideMenu();
        switch (command)
        {
            case TrayMenuCommand.StopSpeaking:
                if (_monitoring)
                {
                    StopMonitoringRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    StopSpeakingRequested?.Invoke(this, EventArgs.Empty);
                }
                break;
            case TrayMenuCommand.Settings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayMenuCommand.Update:
                UpdateRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayMenuCommand.Exit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void DismissIfPointerMovedAway()
    {
        if (!Visible)
        {
            _pointerDismissTimer.Stop();
            return;
        }

        var scaledDistance = Math.Max(
            BasePointerDismissDistance,
            (int)Math.Round(BasePointerDismissDistance * DeviceDpi / 96f));
        if (IsPointerBeyondDismissDistance(Bounds, Cursor.Position, scaledDistance))
        {
            HideMenu();
        }
    }

    private bool ConsumeRecentDeactivation()
    {
        if (!_dismissedByDeactivation)
        {
            return false;
        }

        _dismissedByDeactivation = false;
        return ShouldSuppressToggleAfterDeactivation(
            Stopwatch.GetElapsedTime(_lastDeactivationTimestamp));
    }

    private void HideMenuCore()
    {
        if (Visible)
        {
            Hide();
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
            ? Rectangle.Inflate(
                ClientRectangle,
                -SettingsWindowChrome.ContentInset,
                -SettingsWindowChrome.ContentInset)
            : ClientRectangle;
        var radius = _layeredChromeActive
            ? SettingsWindowChrome.CornerRadius - SettingsWindowChrome.ContentInset
            : SettingsWindowChrome.CornerRadius;
        using var path = PulseReaderTheme.RoundedRectangle(bounds, radius);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Owner = null;
            _pointerDismissTimer.Stop();
            _pointerDismissTimer.Dispose();
            _updateToolTip.Dispose();
            _windowChrome.Hide();
            _windowChrome.Dispose();
            _logo.Dispose();
        }

        base.Dispose(disposing);
    }
}
