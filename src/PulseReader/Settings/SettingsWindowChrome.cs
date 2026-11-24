using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PulseReader.Settings;

internal sealed class SettingsWindowChrome : Form
{
    internal const int CornerRadius = 19;
    internal const int ContentInset = 6;
    internal const float OuterBorderRadius = 15.5f;
    internal const float InnerBorderRadius = 13.5f;

    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    public SettingsWindowChrome()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    public bool TryShowChrome(Rectangle bounds)
    {
        try
        {
            Bounds = bounds;
            if (!Visible)
            {
                Show();
            }

            var applied = ApplyChrome(bounds);
            if (!applied)
            {
                Hide();
            }

            return applied;
        }
        catch
        {
            Hide();
            return false;
        }
    }

    public bool TryUpdateChrome(Rectangle bounds)
    {
        if (!Visible)
        {
            return false;
        }

        try
        {
            Bounds = bounds;
            var applied = ApplyChrome(bounds);
            if (!applied)
            {
                Hide();
            }

            return applied;
        }
        catch
        {
            Hide();
            return false;
        }
    }

    internal static Bitmap RenderChrome(Size size)
    {
        var bitmap = new Bitmap(
            Math.Max(1, size.Width),
            Math.Max(1, size.Height),
            PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;

        using var windowPath = PulseReaderTheme.RoundedRectangle(
            new RectangleF(0.5f, 0.5f, bitmap.Width - 1f, bitmap.Height - 1f),
            CornerRadius);
        using var windowFill = new SolidBrush(PulseReaderTheme.Window);
        graphics.FillPath(windowFill, windowPath);

        using var outer = new Pen(PulseReaderTheme.Border, 2f);
        using var inner = new Pen(Color.FromArgb(85, PulseReaderTheme.Accent), 1f);
        using var outerPath = PulseReaderTheme.RoundedRectangle(
            new RectangleF(2.5f, 2.5f, bitmap.Width - 5f, bitmap.Height - 5f),
            OuterBorderRadius);
        using var innerPath = PulseReaderTheme.RoundedRectangle(
            new RectangleF(4.5f, 4.5f, bitmap.Width - 9f, bitmap.Height - 9f),
            InnerBorderRadius);
        graphics.DrawPath(outer, outerPath);
        graphics.DrawPath(inner, innerPath);
        return bitmap;
    }

    private bool ApplyChrome(Rectangle bounds)
    {
        using var bitmap = RenderChrome(bounds.Size);
        var screenDeviceContext = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDeviceContext == IntPtr.Zero)
        {
            return false;
        }

        var memoryDeviceContext = NativeMethods.CreateCompatibleDC(screenDeviceContext);
        if (memoryDeviceContext == IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDeviceContext);
            return false;
        }

        var bitmapHandle = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;
        try
        {
            bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
            previousBitmap = NativeMethods.SelectObject(memoryDeviceContext, bitmapHandle);
            if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
            {
                return false;
            }

            var destination = new NativePoint(bounds.Left, bounds.Top);
            var size = new NativeSize(bounds.Width, bounds.Height);
            var source = new NativePoint(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            return NativeMethods.UpdateLayeredWindow(
                Handle,
                screenDeviceContext,
                ref destination,
                ref size,
                memoryDeviceContext,
                ref source,
                0,
                ref blend,
                UlwAlpha);
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero && previousBitmap != new IntPtr(-1))
            {
                NativeMethods.SelectObject(memoryDeviceContext, previousBitmap);
            }

            if (bitmapHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }

            NativeMethods.DeleteDC(memoryDeviceContext);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDeviceContext);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteObject(IntPtr graphicsObject);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateLayeredWindow(
            IntPtr windowHandle,
            IntPtr destinationDeviceContext,
            ref NativePoint destination,
            ref NativeSize size,
            IntPtr sourceDeviceContext,
            ref NativePoint source,
            int colorKey,
            ref BlendFunction blend,
            int flags);
    }
}
