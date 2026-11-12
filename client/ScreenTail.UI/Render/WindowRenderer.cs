using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
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
        var bitmap = Render(window, null, 1);
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
            // Near-opaque, then un-premultiplied. Pbgra32 scales each channel by the alpha, so a straight
            // colour cannot be compared against it directly — and requiring alpha 255 was worse still:
            // every antialiased glyph pixel on this pill comes back at 254, so the check skipped precisely
            // the pixels it was looking for and failed a correctly painted amber ‖.
            var alpha = pixels[i + 3];
            if (alpha < 200)
            {
                continue;
            }

            double pr = Straight(pixels[i + 2], alpha) - over.R;
            double pg = Straight(pixels[i + 1], alpha) - over.G;
            double pb = Straight(pixels[i], alpha) - over.B;

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

    private static double Straight(byte channel, byte alpha) => Math.Min(255.0, channel * 255.0 / alpha);

    public static void Save(Window window, string path, double? height = null, double scale = 1)
    {
        Write(Render(window, height, scale), path);
    }

    /// <summary>
    /// Lays the window's content out at the size it is drawn at, and returns that size.
    ///
    /// The window itself is not what is drawn. The hosted runner's screen is 1024x768, so Windows shrinks
    /// a 1280x800 shell to fit it, and the first version of this drew the window: a shrunk layout on a
    /// canvas the size of the window's frame, which left a transparent strip down the right and along the
    /// bottom of every shell render. What is drawn now is the window's client area, the first visual in
    /// it, laid out at the client size the window asks for (its Width and Height less its frame), so a
    /// render shows the layout a technician's larger screen gets. A window sized to its content is drawn
    /// as it is. <paramref name="height"/> asks for a taller layout than the window's own, for a render of
    /// a page that scrolls.
    /// </summary>
    public static Size Lay(Window window, double? height = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.UpdateLayout();
        if (Client(window) is not { } root)
        {
            return new Size(window.ActualWidth, window.ActualHeight);
        }

        var size = Natural(window, root);
        if (height is { } tall)
        {
            size.Height = tall;
        }

        if (!Near(root.RenderSize, size))
        {
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
        }

        return size;
    }

    private static RenderTargetBitmap Render(Window window, double? height, double scale)
    {
        var size = Lay(window, height);
        Visual target = Client(window) is { } root ? root : window;

        // 150% is drawn as 150% rather than as a 100% picture made bigger: the window is told its DPI is
        // 144, so layout rounding and text are worked out for that many pixels, as they are on a real
        // 150% display. The bitmap's own DPI does the scaling. Put back afterwards, so the next render is
        // at the window's real DPI.
        var original = VisualTreeHelper.GetDpi(window);
        var dpi = scale == 1 ? original : new DpiScale(scale, scale);
        var told = false;
        if (scale != 1)
        {
            try
            {
                VisualTreeHelper.SetRootDpi(window, dpi);
                told = true;
                _ = Lay(window, height);
            }
            catch (InvalidOperationException error)
            {
                // Still a 150% picture, only scaled rather than laid out at 150%: said, not hidden.
                Console.WriteLine($"  the window would not take {dpi.PixelsPerInchX} DPI ({error.Message}); scaling instead");
            }
        }

        try
        {
            var width = (int)Math.Ceiling(size.Width * dpi.DpiScaleX);
            var pixelHeight = (int)Math.Ceiling(size.Height * dpi.DpiScaleY);
            var bitmap = new RenderTargetBitmap(width, pixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(target);
            return bitmap;
        }
        finally
        {
            if (told)
            {
                VisualTreeHelper.SetRootDpi(window, original);
            }
        }
    }

    /// <summary>The window's client area: the root of its template, which is what a technician sees.</summary>
    private static FrameworkElement? Client(Window window) =>
        VisualTreeHelper.GetChildrenCount(window) > 0 ? VisualTreeHelper.GetChild(window, 0) as FrameworkElement : null;

    /// <summary>
    /// The client size the window asks for. Its frame is measured the first time, before anything here has
    /// laid the content out at another size, and remembered, because afterwards the content's size says
    /// what was last drawn rather than what the frame takes.
    /// </summary>
    private static Size Natural(Window window, FrameworkElement root)
    {
        var frame = Frames.GetValue(
            window,
            w => new StrongBox<Size>(new Size(Math.Max(0, w.ActualWidth - root.ActualWidth), Math.Max(0, w.ActualHeight - root.ActualHeight)))).Value;

        var sizesWidth = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
        var sizesHeight = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
        var width = sizesWidth || double.IsNaN(window.Width) ? root.ActualWidth : window.Width - frame.Width;
        var height = sizesHeight || double.IsNaN(window.Height) ? root.ActualHeight : window.Height - frame.Height;
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    private static readonly ConditionalWeakTable<Window, StrongBox<Size>> Frames = [];

    private static bool Near(Size a, Size b) => Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

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
