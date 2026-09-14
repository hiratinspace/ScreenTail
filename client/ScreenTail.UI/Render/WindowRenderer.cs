using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenTail.UI.Render;

/// <summary>
/// Renders a window to a PNG offscreen.
///
/// <see cref="RenderTargetBitmap"/> does not need an interactive desktop, which is what makes a WPF
/// screen checkable on a hosted runner with nobody looking at it — and checkable at all while the
/// development machine is a Mac. It catches what writing XAML blind actually gets wrong: a missing
/// resource key, a style on the wrong target type, a binding to a property that is not there.
/// </summary>
internal static class WindowRenderer
{
    public static void Save(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        // Counted here rather than measured as a file size in CI. The first version of this check used
        // bytes, and then failed on a shell that had just been *improved*: flattening the background and
        // dropping a drop-shadow made the PNG compress smaller. Bytes measure how compressible a picture
        // is; what the check means to ask is whether anything was drawn.
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"wrote {path} ({width}x{height}, {DistinctColours(bitmap)} distinct colours)"));
    }

    /// <summary>How many different colours are on the rendered window, sampled on a grid.</summary>
    private static int DistinctColours(RenderTargetBitmap bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var seen = new HashSet<uint>();
        for (var y = 0; y < bitmap.PixelHeight; y += 4)
        {
            for (var x = 0; x < bitmap.PixelWidth; x += 4)
            {
                var i = (y * stride) + (x * 4);
                seen.Add(BitConverter.ToUInt32(pixels, i));
            }
        }

        return seen.Count;
    }
}
