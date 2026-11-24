using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace PulseReader.Settings;

internal sealed class GlowSlider : Control
{
    private int _minimum;
    private int _maximum = 100;
    private int _value;
    private bool _dragging;

    public GlowSlider()
    {
        Height = 24;
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint
            | ControlStyles.Selectable,
            true);
        TabStop = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            Value = Math.Max(_value, value);
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            Value = Math.Min(_value, value);
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, Minimum, Maximum);
            if (_value == next)
            {
                return;
            }

            _value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var track = new Rectangle(8, (Height - 4) / 2, Math.Max(1, Width - 16), 4);
        using var emptyBrush = new SolidBrush(
            Enabled ? PulseReaderTheme.Border : PulseReaderTheme.DisabledBorder);
        using var filledBrush = new SolidBrush(
            Enabled ? PulseReaderTheme.Accent : PulseReaderTheme.DisabledText);
        eventArgs.Graphics.FillRectangle(emptyBrush, track);

        var fraction = Maximum == Minimum
            ? 0f
            : (Value - Minimum) / (float)(Maximum - Minimum);
        var x = track.Left + (int)Math.Round(track.Width * fraction);
        eventArgs.Graphics.FillRectangle(filledBrush, track.Left, track.Top, Math.Max(0, x - track.Left), track.Height);

        using var glowBrush = new SolidBrush(
            Enabled
                ? Color.FromArgb(55, PulseReaderTheme.Accent)
                : Color.FromArgb(28, PulseReaderTheme.DisabledText));
        eventArgs.Graphics.FillEllipse(glowBrush, x - 9, Height / 2 - 9, 18, 18);
        using var thumbBrush = new SolidBrush(
            Enabled ? PulseReaderTheme.AccentBright : PulseReaderTheme.DisabledText);
        eventArgs.Graphics.FillEllipse(thumbBrush, x - 5, Height / 2 - 5, 10, 10);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        Focus();
        _dragging = true;
        SetValueFromX(eventArgs.X);
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (_dragging)
        {
            SetValueFromX(eventArgs.X);
        }

        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        _dragging = false;
        base.OnMouseUp(eventArgs);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode is Keys.Left or Keys.Down)
        {
            Value--;
            eventArgs.Handled = true;
        }
        else if (eventArgs.KeyCode is Keys.Right or Keys.Up)
        {
            Value++;
            eventArgs.Handled = true;
        }

        base.OnKeyDown(eventArgs);
    }

    private void SetValueFromX(int x)
    {
        var fraction = Math.Clamp((x - 8f) / Math.Max(1, Width - 16), 0f, 1f);
        Value = Minimum + (int)Math.Round((Maximum - Minimum) * fraction);
    }
}
