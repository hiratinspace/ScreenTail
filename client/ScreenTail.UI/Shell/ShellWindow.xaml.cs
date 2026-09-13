using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTail.Core.Shell;
using ScreenTail.UI.Theme;
using AppTheme = ScreenTail.UI.Theme.AppTheme;

namespace ScreenTail.UI.Shell;

public partial class ShellWindow : Window
{
    private readonly string? _screenshotDirectory;

    public ShellWindow()
        : this(null)
    {
    }

    public ShellWindow(string? screenshotDirectory)
    {
        InitializeComponent();
        _screenshotDirectory = screenshotDirectory;

        // A state store with something in it, so a screenshot shows the shell doing its job rather than an
        // empty frame that would pass the check while proving nothing.
        var state = new ShellState();
        state.Connected(new Shared.Ipc.CaptureStateSnapshot
        {
            State = "recording",
            SessionId = "preview",
            RemoteTool = "screenconnect",
            ElapsedMs = 12 * 60 * 1000,
        });

        DataContext = new ShellViewModel(state);
        if (_screenshotDirectory is not null)
        {
            ContentRendered += async (_, _) => await CaptureAsync(state);
        }
    }

    private async Task CaptureAsync(ShellState state)
    {
        Directory.CreateDirectory(_screenshotDirectory!);

        // Both states worth looking at: working normally, and telling the technician the service is gone.
        foreach (var (name, arrange) in new (string, Action)[]
        {
            ("recording", () => { }),
            ("service-down", state.Lost),
        })
        {
            arrange();
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.HighContrast })
            {
                ThemeManager.Apply(theme, Application.Current.Resources);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                UpdateLayout();
                Save(Path.Combine(_screenshotDirectory!, $"shell-{name}-{theme.ToString().ToLowerInvariant()}.png"));
            }
        }

        // ST-071's panel renders in the same pass. It is a separate window, so it needs its own render
        // rather than appearing inside the shell's.
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.HighContrast })
        {
            ThemeManager.Apply(theme, Application.Current.Resources);
            var panel = new Diagnostics.DiagnosticsWindow { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
            panel.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            panel.UpdateLayout();
            Save(panel, Path.Combine(_screenshotDirectory!, $"panel-{theme.ToString().ToLowerInvariant()}.png"));
            panel.Close();
        }

        Application.Current.Shutdown();
    }

    private void Save(string path) => Save(this, path);

    private static void Save(Window window, string path)
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
