using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace PulseReader.Settings;

internal sealed class GlowCheckBox : Control
{
    private bool _checked;
    private bool _hovered;

    public GlowCheckBox()
    {
        Height = 28;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.UserPaint,
            true);
        BackColor = Color.Transparent;
        ForeColor = PulseReaderTheme.Text;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleDefaultActionDescription = "Select option";
    }

    [DefaultValue(false)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            AccessibleDefaultActionDescription = value ? "Clear option" : "Select option";
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var active = Enabled && (_hovered || Focused);
        var box = new Rectangle(1, (Height - 17) / 2, 17, 17);
        using var background = new SolidBrush(
            Enabled
                ? Checked ? Color.FromArgb(32, 82, 117) : PulseReaderTheme.Surface
                : PulseReaderTheme.DisabledSurface);
        using var border = new Pen(
            Enabled
                ? active || Checked ? PulseReaderTheme.Accent : PulseReaderTheme.Border
                : PulseReaderTheme.DisabledBorder,
            active ? 1.5f : 1f);
        eventArgs.Graphics.FillRectangle(background, box);
        eventArgs.Graphics.DrawRectangle(border, box);

        if (Checked)
        {
            using var check = new Pen(
                Enabled ? PulseReaderTheme.AccentBright : PulseReaderTheme.DisabledText,
                2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            eventArgs.Graphics.DrawLines(
                check,
                [new Point(5, box.Top + 9), new Point(8, box.Top + 12), new Point(14, box.Top + 5)]);
        }

        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            new Rectangle(27, 0, Math.Max(1, Width - 27), Height),
            Enabled ? PulseReaderTheme.Text : PulseReaderTheme.DisabledText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        if (Enabled)
        {
            Checked = !Checked;
        }

        base.OnClick(eventArgs);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnGotFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnGotFocus(eventArgs);
    }

    protected override void OnLostFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnLostFocus(eventArgs);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (Enabled && eventArgs.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }

        base.OnKeyDown(eventArgs);
    }
}
