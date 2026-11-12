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

    /// <param name="prepare">Runs once the content is laid out at the size it is drawn at, just before
    /// the picture is taken: a scroll to a section, or a focus ring. Whatever it returns is disposed after.</param>
    public static void Save(Window window, string path, double? height = null, double scale = 1, Func<IDisposable?>? prepare = null)
    {
        Write(Render(window, height, scale, prepare), path);
    }

    /// <summary>
    /// The size the window's client area is drawn at: the size its XAML asks for less its frame, or as
    /// tall as <paramref name="height"/> for a render of a page that scrolls. A window that sizes itself to
    /// its content, or that <see cref="Intend"/> was not told about, is drawn as it is.
    ///
    /// The hosted runner's screen is about 1024x768, and the window does not get that size there. What was
    /// tried, so nobody tries it again: setting Width and Height (Windows caps the window at the screen:
    /// the 1280x800 shell came out 1044x788 outside, 1028x749 inside); answering WM_GETMINMAXINFO with no
    /// cap (no change: WPF answers it first); SetWindowPos with SWP_NOSENDCHANGING, which skips the message
    /// the cap is applied in (no change either). Laying the client area out bigger by hand while it was
    /// still in the window did not hold, because the window's next layout pass put it back. So when the
    /// window is smaller than asked, <see cref="Render"/> lifts its content out into a host that belongs to
    /// no window and draws that instead.
    /// </summary>
    public static Size Lay(Window window, double? height = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.UpdateLayout();
        if (Client(window) is not { } root)
        {
            return new Size(window.ActualWidth, window.ActualHeight);
        }

        var got = new Size(root.ActualWidth, root.ActualHeight);
        if (window.SizeToContent != SizeToContent.Manual || !Intended.TryGetValue(window, out var box))
        {
            return got;
        }

        var frame = new Size(Math.Max(0, window.ActualWidth - root.ActualWidth), Math.Max(0, window.ActualHeight - root.ActualHeight));
        var asked = box.Value;
        return new Size(
            double.IsNaN(asked.Width) ? got.Width : Math.Max(1, asked.Width - frame.Width),
            height ?? (double.IsNaN(asked.Height) ? got.Height : Math.Max(1, asked.Height - frame.Height)));
    }

    private static RenderTargetBitmap Render(Window window, double? height, double scale, Func<IDisposable?>? prepare = null)
    {
        var size = Lay(window, height);
        var root = Client(window);
        var fits = root is null || (Math.Abs(root.ActualWidth - size.Width) < 0.5 && Math.Abs(root.ActualHeight - size.Height) < 0.5);
        return fits || window.Content is not UIElement content
            ? RenderInWindow(window, root, size, height, scale, prepare)
            : RenderDetached(window, content, size, scale, prepare);
    }

    private static RenderTargetBitmap RenderInWindow(Window window, FrameworkElement? root, Size size, double? height, double scale, Func<IDisposable?>? prepare)
    {
        Visual target = root is not null ? root : window;

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

        using var prepared = prepare?.Invoke();
        window.UpdateLayout();
        try
        {
            return Draw(target, size, dpi);
        }
        finally
        {
            if (told)
            {
                VisualTreeHelper.SetRootDpi(window, original);
            }
        }
    }

    /// <summary>
    /// Draws the window's content at <paramref name="size"/> in a host that belongs to no window, and puts
    /// it back. The host is what the window's default template is (a Border with the window's background
    /// and border, round an AdornerDecorator) and inherits what the window would have handed down: its
    /// resources, data context, text and layout settings. So the picture is the window's, bigger.
    ///
    /// Taken out and put back with nothing in between that runs the dispatcher, so the Unloaded and Loaded
    /// the move schedules cancel out and no view sees it leave.
    /// </summary>
    private static RenderTargetBitmap RenderDetached(Window window, UIElement content, Size size, double scale, Func<IDisposable?>? prepare)
    {
        var decorator = new System.Windows.Documents.AdornerDecorator();
        var host = new System.Windows.Controls.Border
        {
            Background = window.Background,
            BorderBrush = window.BorderBrush,
            BorderThickness = window.BorderThickness,
            Padding = window.Padding,
            Child = decorator,
            Resources = window.Resources,
            DataContext = window.DataContext,
            FlowDirection = window.FlowDirection,
            Language = window.Language,
            UseLayoutRounding = window.UseLayoutRounding,
            SnapsToDevicePixels = window.SnapsToDevicePixels,
        };
        foreach (var property in Inherited)
        {
            host.SetValue(property, window.GetValue(property));
        }

        var dpi = scale == 1 ? VisualTreeHelper.GetDpi(window) : new DpiScale(scale, scale);

        // Pinned for the move. Inherited, it would be null for the moment between window and host, and
        // a ContentControl bound to it (the shell's content area) would throw its pane away and build a
        // new one: a different scroller from the one the caller scrolls, and a strip whose model the old
        // view disposes on its way out.
        var pinned = content is FrameworkElement element && element.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue
            ? element
            : null;
        pinned?.SetValue(FrameworkElement.DataContextProperty, window.DataContext);
        window.Content = null;
        try
        {
            decorator.Child = content;
            VisualTreeHelper.SetRootDpi(host, dpi);
            host.Measure(size);
            host.Arrange(new Rect(size));
            host.UpdateLayout();
            using var prepared = prepare?.Invoke();
            host.UpdateLayout();
            return Draw(host, size, dpi);
        }
        finally
        {
            decorator.Child = null;
            window.Content = content;
            pinned?.ClearValue(FrameworkElement.DataContextProperty);
            window.UpdateLayout();
        }
    }

    /// <summary>What the window's style sets and its content inherits: the text settings.</summary>
    private static readonly DependencyProperty[] Inherited =
    [
        System.Windows.Documents.TextElement.ForegroundProperty,
        System.Windows.Documents.TextElement.FontFamilyProperty,
        System.Windows.Documents.TextElement.FontSizeProperty,
        System.Windows.Documents.TextElement.FontWeightProperty,
        System.Windows.Documents.TextElement.FontStyleProperty,
        System.Windows.Documents.TextElement.FontStretchProperty,
        TextOptions.TextFormattingModeProperty,
        TextOptions.TextRenderingModeProperty,
        TextOptions.TextHintingModeProperty,
    ];

    private static RenderTargetBitmap Draw(Visual target, Size size, DpiScale dpi)
    {
        var width = (int)Math.Ceiling(size.Width * dpi.DpiScaleX);
        var pixelHeight = (int)Math.Ceiling(size.Height * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, pixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(target);
        return bitmap;
    }

    /// <summary>The window's client area: the root of its template, which is what a technician sees.</summary>
    private static FrameworkElement? Client(Window window) =>
        VisualTreeHelper.GetChildrenCount(window) > 0 ? VisualTreeHelper.GetChild(window, 0) as FrameworkElement : null;

    /// <summary>
    /// Remembers the size the window's XAML asks for, so <see cref="Lay"/> can give the window that size.
    /// Called before the window is shown: showing it on the runner rewrites
    /// <see cref="FrameworkElement.Width"/> to what fitted. Only harness windows are ever passed here.
    /// </summary>
    public static T Intend<T>(T window)
        where T : Window
    {
        ArgumentNullException.ThrowIfNull(window);
        Intended.AddOrUpdate(window, new StrongBox<Size>(new Size(window.Width, window.Height)));
        return window;
    }

    private static readonly ConditionalWeakTable<Window, StrongBox<Size>> Intended = [];

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
