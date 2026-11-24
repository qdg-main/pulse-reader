using System.Drawing.Drawing2D;
using PulseReader.Settings;

namespace PulseReader.Tray;

internal enum TrayMenuGlyph
{
    Stop,
    Settings,
    Update,
    Exit
}

internal sealed class TrayMenuItemControl : Control
{
    private readonly TrayMenuGlyph _glyph;
    private bool _hovered;
    private bool _keyboardFocusVisible;
    private bool _pressed;

    public TrayMenuItemControl(string text, TrayMenuGlyph glyph)
    {
        Text = text;
        _glyph = glyph;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.MenuItem;
        BackColor = PulseReaderTheme.Window;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Semibold", 9.25f);
        TabStop = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
    }

    internal void PerformClick()
    {
        if (Enabled)
        {
            OnClick(EventArgs.Empty);
        }
    }

    internal void SetKeyboardFocusVisible(bool visible)
    {
        if (_keyboardFocusVisible == visible)
        {
            return;
        }

        _keyboardFocusVisible = visible;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Space
        || base.IsInputKey(keyData);

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(eventArgs);
    }

    protected override void OnEnter(EventArgs eventArgs)
    {
        Invalidate();
        base.OnEnter(eventArgs);
    }

    protected override void OnLeave(EventArgs eventArgs)
    {
        _pressed = false;
        Invalidate();
        base.OnLeave(eventArgs);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        _keyboardFocusVisible = false;
        if (Enabled)
        {
            Focus();
        }

        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (Enabled && eventArgs.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        var invoke = _pressed
                     && Enabled
                     && eventArgs.Button == MouseButtons.Left
                     && ClientRectangle.Contains(eventArgs.Location);
        _pressed = false;
        Invalidate();
        base.OnMouseUp(eventArgs);
        if (invoke)
        {
            PerformClick();
        }
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (Enabled && eventArgs.KeyCode is Keys.Enter or Keys.Space)
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            PerformClick();
        }

        base.OnKeyDown(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var itemBackground = PulseReaderTheme.Window;
        if (Enabled && (_hovered || _keyboardFocusVisible))
        {
            itemBackground = _pressed
                ? Color.FromArgb(31, 63, 101)
                : PulseReaderTheme.SurfaceHover;
            using var fill = new SolidBrush(itemBackground);
            using var path = PulseReaderTheme.RoundedRectangle(
                new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f),
                7f);
            graphics.FillPath(fill, path);

            using var accent = new Pen(
                _pressed ? PulseReaderTheme.AccentBright : PulseReaderTheme.Accent,
                2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(accent, 4f, 11f, 4f, Height - 11f);
        }

        var color = Enabled ? PulseReaderTheme.Text : PulseReaderTheme.DisabledText;
        DrawGlyph(graphics, color, itemBackground);
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            new Rectangle(42, 0, Width - 52, Height),
            color,
            TextFormatFlags.Left
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix);
    }

    private void DrawGlyph(Graphics graphics, Color color, Color backgroundColor)
    {
        var center = new PointF(23f, Height / 2f);
        using var pen = new Pen(color, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        switch (_glyph)
        {
            case TrayMenuGlyph.Stop:
                using (var fill = new SolidBrush(Color.FromArgb(50, color)))
                {
                    graphics.FillRectangle(fill, center.X - 5f, center.Y - 5f, 10f, 10f);
                }

                graphics.DrawRectangle(pen, center.X - 5f, center.Y - 5f, 10f, 10f);
                break;

            case TrayMenuGlyph.Settings:
                using (var gearPen = new Pen(color, 1.15f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                })
                using (var background = new SolidBrush(backgroundColor))
                {
                    DrawGear(
                        graphics,
                        gearPen,
                        background,
                        new PointF(center.X - 2f, center.Y - 2f),
                        4.2f,
                        7f,
                        8);
                    DrawGear(
                        graphics,
                        gearPen,
                        background,
                        new PointF(center.X + 5f, center.Y + 5f),
                        2.8f,
                        4.8f,
                        7);
                }

                break;

            case TrayMenuGlyph.Exit:
                graphics.DrawLine(pen, center.X - 6f, center.Y - 7f, center.X - 6f, center.Y + 7f);
                graphics.DrawLine(pen, center.X - 6f, center.Y - 7f, center.X + 1f, center.Y - 7f);
                graphics.DrawLine(pen, center.X - 6f, center.Y + 7f, center.X + 1f, center.Y + 7f);
                graphics.DrawLine(pen, center.X - 1f, center.Y, center.X + 7f, center.Y);
                graphics.DrawLine(pen, center.X + 4f, center.Y - 3f, center.X + 7f, center.Y);
                graphics.DrawLine(pen, center.X + 4f, center.Y + 3f, center.X + 7f, center.Y);
                break;

            case TrayMenuGlyph.Update:
                DrawUpdateArrow(graphics, pen, center, 205f);
                DrawUpdateArrow(graphics, pen, center, 25f);
                break;
        }
    }

    private static void DrawUpdateArrow(Graphics graphics, Pen pen, PointF center, float startAngle)
    {
        const float radius = 6f;
        const float sweep = 140f;
        graphics.DrawArc(pen, center.X - radius, center.Y - radius, radius * 2f, radius * 2f,
            startAngle, sweep);
        var angle = (startAngle + sweep) * MathF.PI / 180f;
        var end = new PointF(center.X + MathF.Cos(angle) * radius, center.Y + MathF.Sin(angle) * radius);
        var tangent = new PointF(-MathF.Sin(angle), MathF.Cos(angle));
        var normal = new PointF(MathF.Cos(angle), MathF.Sin(angle));
        graphics.DrawLines(pen,
        [
            new(end.X - tangent.X * 3f + normal.X * 2.5f,
                end.Y - tangent.Y * 3f + normal.Y * 2.5f),
            end,
            new(end.X - tangent.X * 3f - normal.X * 2.5f,
                end.Y - tangent.Y * 3f - normal.Y * 2.5f)
        ]);
    }

    private static void DrawGear(
        Graphics graphics,
        Pen pen,
        Brush background,
        PointF center,
        float rootRadius,
        float outerRadius,
        int teeth)
    {
        using var path = new GraphicsPath();
        var points = new PointF[teeth * 4];
        var angleStep = MathF.Tau / points.Length;
        for (var index = 0; index < points.Length; index++)
        {
            var radius = index % 4 is 1 or 2 ? outerRadius : rootRadius;
            var angle = -MathF.PI / 2f + index * angleStep;
            points[index] = new PointF(
                center.X + MathF.Cos(angle) * radius,
                center.Y + MathF.Sin(angle) * radius);
        }

        path.AddPolygon(points);
        using var fill = new SolidBrush(Color.FromArgb(22, pen.Color));
        graphics.FillPath(fill, path);
        graphics.DrawPath(pen, path);

        var holeRadius = Math.Max(1.1f, rootRadius * 0.42f);
        graphics.FillEllipse(
            background,
            center.X - holeRadius,
            center.Y - holeRadius,
            holeRadius * 2f,
            holeRadius * 2f);
        graphics.DrawEllipse(
            pen,
            center.X - holeRadius,
            center.Y - holeRadius,
            holeRadius * 2f,
            holeRadius * 2f);
    }
}
