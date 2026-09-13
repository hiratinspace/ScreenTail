using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Capture;

namespace ScreenTail.Service.Capture;

/// <summary>
/// Captures the window in front (ST-025).
///
/// The grab is a GDI <c>BitBlt</c> with <c>CAPTUREBLT</c>, which is what ST-001 proved works and, crucially,
/// honours display affinity: the HUD sets <c>WDA_EXCLUDEFROMCAPTURE</c> on itself, so it is absent from
/// these frames without this code knowing the HUD exists. <c>Graphics.CopyFromScreen</c> cannot be used —
/// it rejects <c>SRCCOPY | CAPTUREBLT</c>, which the spike found the hard way.
///
/// Each stage is timed separately. ADR-0001 measured 178 ms p95 for this path against a 120 ms budget but
/// recorded only the total, and the total does not say what to fix. With the split, a slow grab argues for
/// Windows.Graphics.Capture and a slow encode argues for WIC; without it, either change is a guess.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ScreenshotCapturer(int jpegQuality = 82) : IScreenshotCapturer, IDisposable
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    // Held between captures. The laptop measured the grab at 18.7 ms for a 420x220 window, which is far too
    // much for 92,000 pixels: almost all of it is creating and destroying these, not copying pixels. They
    // are cheap to keep and expensive to make, so they are made once and kept until the size changes.
    private readonly Lock _gate = new();
    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private bool _disposed;

    public CapturedFrame? CaptureForegroundWindow(int maxEdge = Downscale.MaxEdge)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_disposed || !Prepare(width, height))
            {
                return null;
            }

            var grabStart = Stopwatch.GetTimestamp();
            var previous = SelectObject(_memoryDc, _bitmap);
            var copied = BitBlt(_memoryDc, 0, 0, width, height, _screenDc, rect.Left, rect.Top, SRCCOPY | CAPTUREBLT);
            DrawCursor(_memoryDc, rect);
            _ = SelectObject(_memoryDc, previous);
            var grab = Stopwatch.GetElapsedTime(grabStart);
            if (!copied)
            {
                return null;
            }

            var convertStart = Stopwatch.GetTimestamp();
            using var captured = Image.FromHbitmap(_bitmap);
            var convert = Stopwatch.GetElapsedTime(convertStart);

            var plan = Downscale.For(width, height, maxEdge);
            var resizeStart = Stopwatch.GetTimestamp();
            using var stored = plan.Resamples ? Resize(captured, plan) : captured;
            var resize = Stopwatch.GetElapsedTime(resizeStart);

            var encodeStart = Stopwatch.GetTimestamp();
            var bytes = Encode(stored);
            var encode = Stopwatch.GetElapsedTime(encodeStart);

            return new CapturedFrame(bytes, stored.Width, stored.Height, width, height, new CaptureTiming(grab, convert, resize, encode));
        }
    }

    /// <summary>
    /// Makes sure the device contexts and the bitmap exist and are the right size, reusing them when they
    /// are. CAPTUREBLT, used in the copy, includes layered windows — which is what makes the HUD's
    /// exclusion meaningful: without it the HUD would be missing for the wrong reason (ADR-0001 finding 3).
    /// </summary>
    private bool Prepare(int width, int height)
    {
        if (_screenDc == IntPtr.Zero)
        {
            _screenDc = GetDC(IntPtr.Zero);
            if (_screenDc == IntPtr.Zero)
            {
                return false;
            }
        }

        if (_memoryDc == IntPtr.Zero)
        {
            _memoryDc = CreateCompatibleDC(_screenDc);
            if (_memoryDc == IntPtr.Zero)
            {
                return false;
            }
        }

        if (_bitmap != IntPtr.Zero && _bitmapWidth == width && _bitmapHeight == height)
        {
            return true;
        }

        if (_bitmap != IntPtr.Zero)
        {
            _ = DeleteObject(_bitmap);
        }

        _bitmap = CreateCompatibleBitmap(_screenDc, width, height);
        _bitmapWidth = width;
        _bitmapHeight = height;
        return _bitmap != IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(_bitmap);
                _bitmap = IntPtr.Zero;
            }

            if (_memoryDc != IntPtr.Zero)
            {
                _ = DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
            }

            if (_screenDc != IntPtr.Zero)
            {
                _ = ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Draws the cursor where it was. A screenshot of a menu is ambiguous without it — "they clicked
    /// something here" is the whole point of capturing on click.
    /// </summary>
    private static void DrawCursor(IntPtr target, Rect rect)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || info.Flags != CURSOR_SHOWING || info.Cursor == IntPtr.Zero)
        {
            return;
        }

        _ = DrawIconEx(target, info.X - rect.Left, info.Y - rect.Top, info.Cursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    private static Bitmap Resize(Bitmap source, DownscalePlan plan)
    {
        var resized = new Bitmap(plan.Width, plan.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(resized);

        // Bilinear rather than bicubic: on the text a technician reads back the difference is not visible,
        // and this is on the measured path. If the laptop says resize is the expensive stage, this is the
        // first thing to revisit.
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(source, 0, 0, plan.Width, plan.Height);
        return resized;
    }

    private byte[] Encode(Bitmap bitmap)
    {
        using var parameters = new EncoderParameters(1);
        using var quality = new EncoderParameter(Encoder.Quality, (long)jpegQuality);
        parameters.Param[0] = quality;

        using var buffer = new MemoryStream(128 * 1024);
        bitmap.Save(buffer, JpegCodec, parameters);
        return buffer.ToArray();
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo pci);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
}
