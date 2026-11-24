using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace PulseReader.Settings;

internal sealed class SettingsScrollViewport : Panel
{
    private const int ScrollBarWidth = 10;
    private const int WheelStep = 54;
    private readonly DoubleBufferedPanel _content = new();
    private readonly SettingsScrollBar _scrollBar = new();
    private int _contentHeight = 1;
    private int _scrollOffset;

    public SettingsScrollViewport()
    {
        BackColor = PulseReaderTheme.Window;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        _content.BackColor = PulseReaderTheme.Window;
        Controls.Add(_content);

        _scrollBar.Width = ScrollBarWidth;
        _scrollBar.ValueChanged += (_, _) => SetScrollOffset(_scrollBar.Value, false);
        Controls.Add(_scrollBar);
        _scrollBar.BringToFront();
    }

    internal Control.ControlCollection ContentControls => _content.Controls;

    internal int ContentHeight => _contentHeight;

    internal int MaximumScrollOffset => Math.Max(0, _contentHeight - ClientSize.Height);

    internal int ScrollOffset => _scrollOffset;

    internal bool ScrollBarVisible => _scrollBar.Visible;

    internal void SetContentHeight(int contentHeight)
    {
        _contentHeight = Math.Max(1, contentHeight);
        UpdateViewport();
    }

    internal bool ScrollByWheel(int delta)
    {
        if (MaximumScrollOffset == 0 || delta == 0)
        {
            return false;
        }

        var notches = Math.Max(1, Math.Abs(delta) / SystemInformation.MouseWheelScrollDelta);
        var direction = delta > 0 ? -1 : 1;
        SetScrollOffset(_scrollOffset + direction * WheelStep * notches);
        return true;
    }

    internal void EnsureVisible(Control control)
    {
        if (MaximumScrollOffset == 0 || control.IsDisposed || control.Parent is null)
        {
            return;
        }

        var topLeft = _content.PointToClient(control.Parent.PointToScreen(control.Location));
        const int padding = 8;
        if (topLeft.Y - padding < _scrollOffset)
        {
            SetScrollOffset(topLeft.Y - padding);
        }
        else if (topLeft.Y + control.Height + padding > _scrollOffset + ClientSize.Height)
        {
            SetScrollOffset(topLeft.Y + control.Height + padding - ClientSize.Height);
        }
    }

    internal bool IsControlFullyVisible(Control control)
    {
        if (control.IsDisposed || control.Parent is null)
        {
            return false;
        }

        var topLeft = _content.PointToClient(control.Parent.PointToScreen(control.Location));
        return topLeft.Y >= _scrollOffset
            && topLeft.Y + control.Height <= _scrollOffset + ClientSize.Height;
    }

    internal void RegisterFocusScrolling(Control root)
    {
        if (root.TabStop)
        {
            root.Enter += (_, _) => EnsureVisible(root);
        }

        foreach (Control child in root.Controls)
        {
            RegisterFocusScrolling(child);
        }
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        UpdateViewport();
    }

    private void SetScrollOffset(int scrollOffset, bool updateScrollBar = true)
    {
        var nextOffset = Math.Clamp(scrollOffset, 0, MaximumScrollOffset);
        if (_scrollOffset == nextOffset)
        {
            return;
        }

        _scrollOffset = nextOffset;
        _content.Top = -_scrollOffset;
        if (updateScrollBar)
        {
            _scrollBar.Value = _scrollOffset;
        }
    }

    private void UpdateViewport()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var maximumOffset = MaximumScrollOffset;
        var hasScrollBar = maximumOffset > 0;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maximumOffset);

        _scrollBar.Visible = hasScrollBar;
        _scrollBar.Bounds = new Rectangle(
            Math.Max(0, ClientSize.Width - ScrollBarWidth),
            0,
            ScrollBarWidth,
            ClientSize.Height);
        _scrollBar.SetMetrics(maximumOffset, ClientSize.Height, _contentHeight, _scrollOffset);
        _scrollBar.BringToFront();

        _content.Bounds = new Rectangle(
            0,
            -_scrollOffset,
            Math.Max(1, ClientSize.Width - (hasScrollBar ? ScrollBarWidth : 0)),
            _contentHeight);
    }

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
                true);
        }
    }
}

internal sealed class SettingsScrollBar : Control
{
    private int _maximum;
    private int _viewportSize = 1;
    private int _contentSize = 1;
    private int _value;
    private int _dragOffset;
    private bool _dragging;
    private bool _hovered;

    public SettingsScrollBar()
    {
        BackColor = PulseReaderTheme.DisabledSurface;
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Value
    {
        get => _value;
        set
        {
            var nextValue = Math.Clamp(value, 0, _maximum);
            if (_value == nextValue)
            {
                return;
            }

            _value = nextValue;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal event EventHandler? ValueChanged;

    internal void SetMetrics(
        int maximum,
        int viewportSize,
        int contentSize,
        int value)
    {
        _maximum = Math.Max(0, maximum);
        _viewportSize = Math.Max(1, viewportSize);
        _contentSize = Math.Max(_viewportSize, contentSize);
        _value = Math.Clamp(value, 0, _maximum);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var separator = new Pen(PulseReaderTheme.Border);
        eventArgs.Graphics.DrawLine(separator, 0, 0, 0, Height);

        var thumb = ThumbBounds();
        using var thumbBrush = new SolidBrush(
            _dragging || _hovered ? PulseReaderTheme.Accent : PulseReaderTheme.Border);
        eventArgs.Graphics.FillRectangle(thumbBrush, thumb);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            var thumb = ThumbBounds();
            _dragging = true;
            Capture = true;
            _dragOffset = thumb.Contains(eventArgs.Location)
                ? eventArgs.Y - thumb.Top
                : thumb.Height / 2;
            Value = ValueFromThumbTop(eventArgs.Y - _dragOffset);
        }

        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (_dragging)
        {
            Value = ValueFromThumbTop(eventArgs.Y - _dragOffset);
        }

        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            _dragging = false;
            Capture = false;
            Invalidate();
        }

        base.OnMouseUp(eventArgs);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        if (!_dragging)
        {
            _hovered = false;
            Invalidate();
        }

        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseWheel(MouseEventArgs eventArgs)
    {
        var notches = Math.Max(1, Math.Abs(eventArgs.Delta) / SystemInformation.MouseWheelScrollDelta);
        var direction = eventArgs.Delta > 0 ? -1 : 1;
        Value += direction * 54 * notches;
        base.OnMouseWheel(eventArgs);
    }

    private Rectangle ThumbBounds()
    {
        const int verticalInset = 2;
        var trackHeight = Math.Max(1, Height - verticalInset * 2);
        var thumbHeight = Math.Clamp(
            (int)Math.Round(trackHeight * (_viewportSize / (double)_contentSize)),
            Math.Min(28, trackHeight),
            trackHeight);
        var travel = Math.Max(0, trackHeight - thumbHeight);
        var top = verticalInset + (_maximum == 0
            ? 0
            : (int)Math.Round(travel * (_value / (double)_maximum)));
        return new Rectangle(4, top, Math.Max(2, Width - 7), thumbHeight);
    }

    private int ValueFromThumbTop(int thumbTop)
    {
        var thumb = ThumbBounds();
        var travel = Math.Max(1, Height - 4 - thumb.Height);
        var fraction = Math.Clamp((thumbTop - 2) / (double)travel, 0d, 1d);
        return (int)Math.Round(_maximum * fraction);
    }
}
