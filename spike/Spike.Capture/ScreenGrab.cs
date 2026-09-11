using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenTail.Spike.Core;

namespace ScreenTail.Spike.Capture;

internal static class ScreenGrab
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static Rectangle WindowBounds(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DwmwaExtendedFrameBounds, out var rect, Marshal.SizeOf<Native.Rect>()) != 0
            && !Native.GetWindowRect(hwnd, out rect))
        {
            return Rectangle.Empty;
        }

        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static Rectangle VirtualScreen() => new(
        Native.GetSystemMetrics(Native.SmXVirtualScreen),
        Native.GetSystemMetrics(Native.SmYVirtualScreen),
        Native.GetSystemMetrics(Native.SmCxVirtualScreen),
        Native.GetSystemMetrics(Native.SmCyVirtualScreen));

    public static Rectangle PrimaryScreen() => new(
        0,
        0,
        Native.GetSystemMetrics(Native.SmCxScreen),
        Native.GetSystemMetrics(Native.SmCyScreen));

    /// <summary>
    /// BitBlt from the screen DC with CAPTUREBLT. Without CAPTUREBLT, layered windows (like the WPF overlay)
    /// can be missing for reasons unrelated to display affinity, and the exclusion check would pass falsely.
    /// </summary>
    public static Bitmap Capture(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            bounds.Location,
            Point.Empty,
            bounds.Size,
            CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
        return bitmap;
    }

    public static Bitmap Downscale(Bitmap source, int maxLongEdge = DownscalePlan.DefaultMaxLongEdge)
    {
        var (width, height) = DownscalePlan.Fit(source.Width, source.Height, maxLongEdge);
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;

        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // avoids a dark fringe on the edges
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, width, height),
            0,
            0,
            source.Width,
            source.Height,
            GraphicsUnit.Pixel,
            attributes);
        return result;
    }

    public static byte[] EncodeJpeg(Image image, long quality)
    {
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var stream = new MemoryStream();
        image.Save(stream, JpegCodec, parameters);
        return stream.ToArray();
    }

    public static byte[] EncodePng(Image image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>Copies a region out as tightly packed BGRA bytes.</summary>
    public static byte[] ReadBgra(Bitmap bitmap, Rectangle region)
    {
        region.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (region.IsEmpty)
        {
            return [];
        }

        var data = bitmap.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = region.Width * 4;
            var buffer = new byte[rowBytes * region.Height];
            for (var y = 0; y < region.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), buffer, y * rowBytes, rowBytes);
            }

            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
