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
    /// <param name="over">The background the colour is drawn on, so a partly covered pixel can be
    /// recognised as the token blended with it rather than as some unrelated colour.</param>
    public static void SaveExpecting(
        Window window,
        string path,
        (byte R, byte G, byte B) mustContain,
        (byte R, byte G, byte B) over,
        string what)
    {
        var bitmap = Render(window);
        Write(bitmap, path);

        if (!Contains(bitmap, mustContain, over))
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} has no {what} pixels (#{mustContain.R:X2}{mustContain.G:X2}{mustContain.B:X2}) on it.");
        }
    }

    /// <summary>
    /// Whether the colour is genuinely painted somewhere on the image, allowing for antialiasing.
    ///
    /// Exact matching was the first version and it was wrong in a way worth recording. A filled glyph
    /// like ● has fully-covered pixels that are exactly the token colour, so it passed; a thin one like ‖
    /// or ⏸ never reaches full coverage at 15px, so every one of its pixels is a <em>blend</em> of the
    /// token and whatever is behind it, and none of them equals the token. The check failed three correct
    /// pills and sent me looking for a bug in the binding that was not there.
    ///
    /// So this asks the question that was actually meant: is there a pixel on the line between the
    /// background and the token, far enough along it to be the token rather than the background? That is
    /// what "painted in this colour" means once a rasteriser has been involved.
    /// </summary>
    private static bool Contains(RenderTargetBitmap bitmap, (byte R, byte G, byte B) colour, (byte R, byte G, byte B) over)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        double dr = colour.R - over.R;
        double dg = colour.G - over.G;
        double db = colour.B - over.B;
        var lengthSquared = (dr * dr) + (dg * dg) + (db * db);
        if (lengthSquared < 1)
        {
            // The token is the background. Nothing can be shown in it, and saying so beats passing.
            return false;
        }

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            // Fully opaque only. Pbgra32 is premultiplied, so anything less has had its channels scaled
            // by the alpha and comparing it to a straight colour compares two different things. Inside
            // the pill every pixel is opaque, which is where the glyph is.
            if (pixels[i + 3] != 255)
            {
                continue;
            }

            double pr = pixels[i + 2] - over.R;
            double pg = pixels[i + 1] - over.G;
            double pb = pixels[i] - over.B;

            // How far along background → token this pixel sits, and how far off that line it strays.
            var along = ((pr * dr) + (pg * dg) + (pb * db)) / lengthSquared;
            if (along < 0.6)
            {
                continue;
            }

            var offR = pr - (along * dr);
            var offG = pg - (along * dg);
            var offB = pb - (along * db);
            if ((offR * offR) + (offG * offG) + (offB * offB) <= 24 * 24)
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
