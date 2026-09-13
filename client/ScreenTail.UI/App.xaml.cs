using System.Windows;
using ScreenTail.UI.Gallery;
using ScreenTail.UI.Shell;
using ScreenTail.UI.Theme;

namespace ScreenTail.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Dark by default (Scope §6.2); ST-070 persists the user's choice. High contrast always wins.
        ThemeManager.Apply(ThemeManager.Effective(AppTheme.Dark), Resources);

        var args = e.Args;
        var screenshotIndex = Array.IndexOf(args, "--screenshot");
        var directory = screenshotIndex >= 0 && screenshotIndex + 1 < args.Length ? args[screenshotIndex + 1] : null;

        Window window;
        if (Array.IndexOf(args, "--gallery") >= 0)
        {
            window = new GalleryWindow(directory);
        }
        else
        {
            // The shell renders itself the same way the gallery does when asked. RenderTargetBitmap draws
            // offscreen, so this works on a hosted runner with no interactive desktop — which is what makes
            // a XAML shell checkable at all without a machine to look at. It catches what writing XAML
            // blind actually gets wrong: a missing resource key, a style on the wrong target type, a
            // binding to a property that is not there.
            window = new ShellWindow(directory);
        }

        MainWindow = window;
        window.Show();
    }
}
