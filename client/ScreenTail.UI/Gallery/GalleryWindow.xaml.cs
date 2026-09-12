using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTail.UI.Theme;

namespace ScreenTail.UI.Gallery;

/// <summary>
/// ST-016 gallery. <c>ScreenTail.UI.exe --gallery</c> opens it; <c>--gallery --screenshot &lt;dir&gt;</c>
/// renders every theme to PNG and exits, which is how CI produces the screenshots the ticket asks for.
/// </summary>
public partial class GalleryWindow : Window
{
    private readonly string? _screenshotDirectory;

    public GalleryWindow(string? screenshotDirectory = null)
    {
        _screenshotDirectory = screenshotDirectory;
        InitializeComponent();
        SampleTable.ItemsSource = new[]
        {
            new SessionRow("Draft", "14 min ago", "12 min", "ScreenConnect", "—"),
            new SessionRow("Published", "3 h ago", "21 min", "ScreenConnect", "Acme Dental · #48190"),
            new SessionRow("Partial", "Yesterday", "6 min", "Remote Desktop", "—"),
        };
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_screenshotDirectory is null)
        {
            return;
        }

        Directory.CreateDirectory(_screenshotDirectory);
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.HighContrast })
        {
            ThemeManager.Apply(theme, Application.Current.Resources);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            UpdateLayout();
            Save(Path.Combine(_screenshotDirectory, $"gallery-{theme.ToString().ToLowerInvariant()}.png"));
        }

        Application.Current.Shutdown();
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<AppTheme>(name, out var theme))
        {
            ThemeManager.Apply(theme, Application.Current.Resources);
        }
    }

    private void Save(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {path} ({width}x{height})"));
    }

    private sealed record SessionRow(string Status, string Started, string Duration, string Tool, string Client);
}
