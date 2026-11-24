using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace PulseReader.Settings;

internal sealed class BlendSectionHeader : Control
{
    private bool _expanded;
    private bool _blendEnabled;
    private bool _hovered;

    public BlendSectionHeader()
    {
        Text = "Blend a second voice";
        Height = 25;
        Font = new Font("Segoe UI Semibold", 8.5f);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.UserPaint,
            true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = Text;
        UpdateAccessibilityDescription();
    }

    [DefaultValue(false)]
    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
            {
                return;
            }

            _expanded = value;
            UpdateAccessibilityDescription();
            Invalidate();
            ExpandedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [DefaultValue(false)]
    public bool BlendEnabled
    {
        get => _blendEnabled;
        set
        {
            if (_blendEnabled == value)
            {
                return;
            }

            _blendEnabled = value;
            Invalidate();
        }
    }

    public event EventHandler? ExpandedChanged;

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var textColor = !Enabled
            ? PulseReaderTheme.DisabledText
            : _hovered || Focused || BlendEnabled
                ? PulseReaderTheme.AccentBright
                : PulseReaderTheme.MutedText;
        var lineColor = !Enabled
            ? PulseReaderTheme.DisabledBorder
            : _hovered || Focused
                ? PulseReaderTheme.Border
                : PulseReaderTheme.Separator;
        var textSize = TextRenderer.MeasureText(
            eventArgs.Graphics,
            Text,
            Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        const int arrowWidth = 9;
        const int textArrowGap = 7;
        const int lineGap = 10;
        var contentWidth = textSize.Width + textArrowGap + arrowWidth;
        var contentLeft = Math.Max(0, (Width - contentWidth) / 2);
        var lineY = Height / 2;

        using var line = new Pen(lineColor);
        if (contentLeft - lineGap > 0)
        {
            eventArgs.Graphics.DrawLine(line, 0, lineY, contentLeft - lineGap, lineY);
        }

        var contentRight = contentLeft + contentWidth;
        if (contentRight + lineGap < Width)
        {
            eventArgs.Graphics.DrawLine(line, contentRight + lineGap, lineY, Width - 1, lineY);
        }

        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            new Rectangle(contentLeft, 0, textSize.Width, Height),
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var arrowLeft = contentLeft + textSize.Width + textArrowGap;
        var arrowCenterY = lineY;
        using var arrow = new Pen(textColor, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        if (Expanded)
        {
            eventArgs.Graphics.DrawLine(arrow, arrowLeft, arrowCenterY + 2, arrowLeft + 4, arrowCenterY - 2);
            eventArgs.Graphics.DrawLine(arrow, arrowLeft + 4, arrowCenterY - 2, arrowLeft + 8, arrowCenterY + 2);
        }
        else
        {
            eventArgs.Graphics.DrawLine(arrow, arrowLeft, arrowCenterY - 2, arrowLeft + 4, arrowCenterY + 2);
            eventArgs.Graphics.DrawLine(arrow, arrowLeft + 4, arrowCenterY + 2, arrowLeft + 8, arrowCenterY - 2);
        }
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        if (Enabled)
        {
            Expanded = !Expanded;
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

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        Invalidate();
        base.OnEnabledChanged(eventArgs);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (Enabled && eventArgs.KeyCode is Keys.Space or Keys.Enter)
        {
            Expanded = !Expanded;
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }

        base.OnKeyDown(eventArgs);
    }

    private void UpdateAccessibilityDescription()
    {
        AccessibleDefaultActionDescription = Expanded
            ? "Collapse second voice settings"
            : "Expand second voice settings";
    }
}
