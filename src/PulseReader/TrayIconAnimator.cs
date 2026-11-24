using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using PulseReader.Settings;
using PulseReader.Speech;

namespace PulseReader;

internal sealed class TrayIconAnimator : IDisposable
{
    private static readonly int[] SpeakingSequence = [0, 1, 2, 1];

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Icon _idleIcon;
    private readonly Icon _updateIdleIcon;
    private readonly Icon[] _speakingFrames;
    private readonly Icon[] _updateSpeakingFrames;
    private int _frameIndex;
    private SpeechPlaybackState _state;
    private bool _monitoring;
    private bool _updateAvailable;
    private bool _disposed;

    internal static int RenderSize => NativeTraySize;

    internal static int NativeTraySize => Math.Max(
        SystemInformation.SmallIconSize.Width,
        SystemInformation.SmallIconSize.Height);

    internal TrayIconAnimator(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;

        using var idleSource = TrayIconFrameRenderer.LoadIdleSource();
        var icons = new List<Icon>();
        try
        {
            _idleIcon = CreateIcon(idleSource);
            icons.Add(_idleIcon);
            _updateIdleIcon = CreateIcon(idleSource, true);
            icons.Add(_updateIdleIcon);
            var speakingFrames = new List<Icon>();
            var updateSpeakingFrames = new List<Icon>();
            for (var frame = 0; frame < TrayIconFrameRenderer.SpeakingFrameCount; frame++)
            {
                using var speakingSource = TrayIconFrameRenderer.LoadSpeakingSource(frame);
                var speakingIcon = CreateIcon(speakingSource);
                icons.Add(speakingIcon);
                speakingFrames.Add(speakingIcon);
                var updateSpeakingIcon = CreateIcon(speakingSource, true);
                icons.Add(updateSpeakingIcon);
                updateSpeakingFrames.Add(updateSpeakingIcon);
            }
            _speakingFrames = speakingFrames.ToArray();
            _updateSpeakingFrames = updateSpeakingFrames.ToArray();
        }
        catch
        {
            foreach (var icon in icons)
            {
                icon.Dispose();
            }

            throw;
        }

        _timer = new System.Windows.Forms.Timer { Interval = 170 };
        _timer.Tick += OnAnimationTick;
        _notifyIcon.Icon = _idleIcon;
    }

    internal void SetPlaybackState(SpeechPlaybackState state)
    {
        if (_disposed || _state == state)
        {
            return;
        }

        _state = state;
        _frameIndex = 0;

        UpdateIcon();
    }

    internal void SetMonitoringState(bool monitoring)
    {
        if (_disposed || _monitoring == monitoring)
        {
            return;
        }

        _monitoring = monitoring;
        _frameIndex = 0;
        UpdateIcon();
    }

    internal void SetUpdateAvailable(bool available)
    {
        if (_disposed || _updateAvailable == available)
        {
            return;
        }

        _updateAvailable = available;
        UpdateIcon();
    }

    private Icon[] CurrentSpeakingFrames => _updateAvailable ? _updateSpeakingFrames : _speakingFrames;

    private void UpdateIcon()
    {
        if (_disposed)
        {
            return;
        }

        if (_state == SpeechPlaybackState.Speaking)
        {
            _notifyIcon.Icon = CurrentSpeakingFrames[SpeakingSequence[_frameIndex]];
            _timer.Start();
        }
        else if (_state == SpeechPlaybackState.Paused || _monitoring)
        {
            _timer.Stop();
            _notifyIcon.Icon = CurrentSpeakingFrames[SpeakingSequence[0]];
        }
        else
        {
            _timer.Stop();
            _notifyIcon.Icon = _updateAvailable ? _updateIdleIcon : _idleIcon;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnAnimationTick;
        _timer.Dispose();
        _idleIcon.Dispose();
        _updateIdleIcon.Dispose();

        foreach (var frame in _speakingFrames)
        {
            frame.Dispose();
        }

        foreach (var frame in _updateSpeakingFrames)
        {
            frame.Dispose();
        }
    }

    internal static Bitmap RenderFrame(Bitmap source, int size, bool updateAvailable)
    {
        var bitmap = TrayIconFrameRenderer.Render(source, size);
        if (updateAvailable)
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var diameter = Math.Max(4f, size * 0.28f);
            var inset = Math.Max(0.5f, size * 0.035f);
            var badge = new RectangleF(size - diameter - inset, size - diameter - inset,
                diameter, diameter);
            using var fill = new SolidBrush(Color.FromArgb(66, 160, 255));
            using var outline = new Pen(PulseReaderTheme.Window, Math.Max(1f, size * 0.055f));
            graphics.FillEllipse(fill, badge);
            graphics.DrawEllipse(outline, badge);
        }

        return bitmap;
    }

    private static Icon CreateIcon(Bitmap source, bool updateAvailable = false)
    {
        using var bitmap = RenderFrame(source, RenderSize, updateAvailable);
        var iconHandle = bitmap.GetHicon();

        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private void OnAnimationTick(object? sender, EventArgs eventArgs)
    {
        if (_disposed || _state != SpeechPlaybackState.Speaking)
        {
            return;
        }

        _frameIndex = (_frameIndex + 1) % SpeakingSequence.Length;
        _notifyIcon.Icon = CurrentSpeakingFrames[SpeakingSequence[_frameIndex]];
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
