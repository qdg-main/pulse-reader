namespace PulseReader.Tray;

internal static class TrayMenuPlacement
{
    private const int EdgeMargin = 8;
    private const int CursorOverlap = 16;

    public static Rectangle Calculate(
        Size menuSize,
        Point cursor,
        Rectangle workingArea)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(menuSize.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(menuSize.Height);

        var usableWidth = Math.Max(1, workingArea.Width - EdgeMargin * 2);
        var usableHeight = Math.Max(1, workingArea.Height - EdgeMargin * 2);
        var width = Math.Min(menuSize.Width, usableWidth);
        var height = Math.Min(menuSize.Height, usableHeight);
        var left = cursor.X - width + CursorOverlap;
        var top = cursor.Y - height - EdgeMargin;

        left = Math.Clamp(
            left,
            workingArea.Left + EdgeMargin,
            workingArea.Right - width - EdgeMargin);
        top = Math.Clamp(
            top,
            workingArea.Top + EdgeMargin,
            workingArea.Bottom - height - EdgeMargin);

        return new Rectangle(left, top, width, height);
    }
}
