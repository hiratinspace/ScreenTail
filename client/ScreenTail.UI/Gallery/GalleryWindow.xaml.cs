using System.IO;
using System.Windows;
using System.Windows.Controls;
using ScreenTail.UI.Render;
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
        _ = WindowRenderer.Intend(this);
        SampleTable.ItemsSource = new[]
        {
            new SessionRow("Draft", "14 min ago", "12 min", "ScreenConnect", "Not published"),
            new SessionRow("Published", "3 h ago", "21 min", "ScreenConnect", "Acme Dental · #48190"),
            new SessionRow("Partial", "Yesterday", "6 min", "Remote Desktop", "Not published"),
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
        ContrastThemeFiles.Report();
        foreach (var theme in RenderThemes.All)
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            UpdateLayout();
            WindowRenderer.Save(this, Path.Combine(_screenshotDirectory, RenderThemes.FileName("gallery", theme)));
        }

        BrandIconSheet.Save(_screenshotDirectory);
        RenderIndex.Write(_screenshotDirectory);
        Application.Current.Shutdown();
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<AppTheme>(name, out var theme))
        {
            ThemeManager.Apply(theme, Application.Current.Resources);
        }
    }

    private sealed record SessionRow(string Status, string Started, string Duration, string Tool, string Client);
}
