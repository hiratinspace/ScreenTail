using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTail.Core.Shell;

namespace ScreenTail.UI.Gallery;

/// <summary>
/// One render of the shipped icons for the gallery artifact (Spec v0.6.0 §2.0): every tray state on the
/// light (#F3F3F3) and dark (#202020) taskbar at each frame the .ico carries, and the app icon's frames.
/// Each frame is drawn at three times its size with nearest-neighbour scaling, so what the owner sees is
/// the pixels Windows will show, not a smoothed enlargement. Decoded from the same embedded resources the
/// tray and the windows use, so a missing or broken icon fails the render.
/// </summary>
internal static class BrandIconSheet
{
    public const string FileName = "brand-icons.png";

    private const int Zoom = 3;

    public static void Save(string directory)
    {
        var rows = new StackPanel { Margin = new Thickness(16), Background = Brushes.White };
        rows.Children.Add(Label("App icon (app.ico): small glyph 16 to 24, mid glyph 32 to 48, full mark 64 and up", Brushes.Black));
        using (var app = Application.GetResourceStream(new Uri("pack://application:,,,/ScreenTail.UI;component/Assets/app.ico")).Stream)
        {
            rows.Children.Add(Row(Decode(app), Color.FromRgb(0xF3, 0xF3, 0xF3), 64));
        }

        foreach (var light in new[] { true, false })
        {
            var ground = light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20);
            foreach (var state in Enum.GetValues<TrayIcon>())
            {
                var name = TrayGlyph.FileName(state, light);
                rows.Children.Add(Label($"{name}: {state}, {(light ? "light" : "dark")} taskbar", Brushes.Black));
                using var stream = typeof(BrandIconSheet).Assembly.GetManifestResourceStream("ScreenTail.UI.Assets.Tray." + name)
                    ?? throw new InvalidOperationException($"The tray icon {name} is not embedded.");
                rows.Children.Add(Row(Decode(stream), ground, 48));
            }
        }

        rows.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        rows.Arrange(new Rect(rows.DesiredSize));
        rows.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(rows.ActualWidth + 32), (int)Math.Ceiling(rows.ActualHeight + 32), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(rows);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, FileName));
        encoder.Save(file);
    }

    private static List<BitmapSource> Decode(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return [.. decoder.Frames.Cast<BitmapSource>().OrderBy(f => f.PixelWidth)];
    }

    private static TextBlock Label(string text, Brush brush) =>
        new() { Text = text, Foreground = brush, FontSize = 12, Margin = new Thickness(0, 8, 0, 4) };

    /// <summary>The frames side by side; those past <paramref name="largest"/> pixels are left out, for width.</summary>
    private static WrapPanel Row(IEnumerable<BitmapSource> frames, Color ground, int largest)
    {
        var row = new WrapPanel();
        foreach (var frame in frames.Where(f => f.PixelWidth <= largest))
        {
            var image = new Image
            {
                Source = frame,
                Width = frame.PixelWidth * Zoom,
                Height = frame.PixelHeight * Zoom,
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            cell.Children.Add(new Border { Background = new SolidColorBrush(ground), Padding = new Thickness(6), Child = image });
            cell.Children.Add(Label($"{frame.PixelWidth} px", Brushes.DimGray));
            row.Children.Add(cell);
        }

        return row;
    }
}
