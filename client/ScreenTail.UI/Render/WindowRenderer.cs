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
    /// <summary>
    /// Renders and saves, and fails when <paramref name="mustContain"/> is not on the result.
    ///
    /// The distinct-colour check says something was drawn; it cannot say the right thing was drawn. A
    /// recording pill whose state glyph came out the TextBlock default black counted exactly as many
    /// colours as a correct one, and passed — which is how three of the HUD's four tones shipped with no
    /// colour at all. A named token either appears on the pixels or it does not.
    /// </summary>
    public static void SaveExpecting(Window window, string path, (byte R, byte G, byte B) mustContain, string what)
    {
        var bitmap = Render(window);
        Write(bitmap, path);

        if (!Contains(bitmap, mustContain))
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} has no {what} pixels (#{mustContain.R:X2}{mustContain.G:X2}{mustContain.B:X2}) on it.");
        }
    }

    /// <summary>Whether any pixel is within a hair of the colour, allowing for antialiasing on the edges.</summary>
    private static bool Contains(RenderTargetBitmap bitmap, (byte R, byte G, byte B) colour)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            // Pbgra32, and premultiplied — so only fully opaque pixels can be compared directly, which
            // the glyph's own body is.
            if (pixels[i + 3] > 200
                && Math.Abs(pixels[i + 2] - colour.R) <= 24
                && Math.Abs(pixels[i + 1] - colour.G) <= 24
                && Math.Abs(pixels[i] - colour.B) <= 24)
            {
                return true;
            }
        }

        return false;
    }

    public static void Save(Window window, string path)
    {
        Write(Render(window), path);
    }

    private static RenderTargetBitmap Render(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

    private static void Write(RenderTargetBitmap bitmap, string path)
    {
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
            $"wrote {path} ({bitmap.PixelWidth}x{bitmap.PixelHeight}, {DistinctColours(bitmap)} distinct colours)"));
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
