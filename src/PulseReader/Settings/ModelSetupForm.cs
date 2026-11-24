using PulseReader.Setup;

namespace PulseReader.Settings;

internal sealed class ModelSetupForm : Form
{
    private readonly Func<IProgress<SetupProgress>, CancellationToken, Task> _install;
    private readonly CancellationTokenSource _cancellation;
    private readonly Label _status = new() { AutoSize = false };
    private readonly ProgressBar _progress = new();
    private readonly GlowButton _retry = new() { Text = "Retry", Visible = false };
    private readonly GlowButton _cancel = new();
    private bool _running;
    private bool _closeRequested;
    private bool _setupDisposed;
    private Task _operation = Task.CompletedTask;

    internal Task Completion => _operation;

    public ModelSetupForm(bool optional,
        Func<IProgress<SetupProgress>, CancellationToken, Task> install,
        CancellationToken shutdown = default)
    {
        _install = install;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        Text = optional ? "Optional pronunciation model" : "Welcome to Pulse Reader";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(470, 242);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PulseReaderTheme.Window;
        ForeColor = PulseReaderTheme.Text;
        Font = new Font("Segoe UI", 10f);
        var description = new Label
        {
            Text = optional
                ? "Downloading the optional local pronunciation model and its runtime (about 2.5 GB). Text stays on your computer."
                : "Downloading Kokoro (about 320 MB) for local speech. After setup, it works offline. You can use a Windows voice instead.",
            Location = new Point(20, 20), Size = new Size(430, 65)
        };
        _status.SetBounds(20, 96, 430, 38);
        _status.ForeColor = PulseReaderTheme.MutedText;
        _progress.SetBounds(20, 144, 430, 14);
        _retry.SetBounds(210, 181, 110, 38);
        _cancel.SetBounds(330, 181, 120, 38);
        _cancel.Text = optional ? "Cancel" : "Windows voice";
        _retry.Click += (_, _) => _operation = RunAsync();
        _cancel.Click += (_, _) => RequestClose();
        Controls.AddRange([description, _status, _progress, _retry, _cancel]);
        Shown += (_, _) => _operation = RunAsync();
    }

    private async Task RunAsync()
    {
        if (_running || _closeRequested) return;
        _running = true;
        _retry.Visible = false;
        _status.ForeColor = PulseReaderTheme.MutedText;
        _status.Text = "Preparing download...";
        _progress.Style = ProgressBarStyle.Marquee;
        var progress = new Progress<SetupProgress>(value =>
        {
            if (IsDisposed || !_running) return;
            var transferred = value.BytesReceived / (1024d * 1024);
            _status.Text = value.TotalBytes is > 0
                ? $"{value.Stage}: {value.AssetName}\n{transferred:0.0} / {value.TotalBytes.Value / (1024d * 1024):0.0} MB"
                : $"{value.Stage}: {value.AssetName}";
            _progress.Style = value.TotalBytes is > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (value.TotalBytes is > 0)
                _progress.Value = (int)Math.Clamp(value.BytesReceived * 100d / value.TotalBytes.Value, 0, 100);
        });
        try
        {
            await _install(progress, _cancellation.Token);
            _running = false;
            if (!_closeRequested && !_cancellation.IsCancellationRequested)
                DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _running = false;
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (Exception exception)
        {
            _running = false;
            if (IsDisposed) return;
            if (_closeRequested) { Close(); return; }
            _status.Text = "Setup did not finish: " + exception.Message;
            _status.ForeColor = PulseReaderTheme.Error;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
            _retry.Visible = true;
        }
    }

    private void RequestClose()
    {
        _closeRequested = true;
        _cancel.Enabled = false;
        _cancellation.Cancel();
        if (!_running) { DialogResult = DialogResult.Cancel; Close(); }
        else _status.Text = "Canceling setup...";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            RequestClose();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_setupDisposed)
        {
            _setupDisposed = true;
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
        base.Dispose(disposing);
    }
}
