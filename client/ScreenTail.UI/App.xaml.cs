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
        Window window;
        if (Array.IndexOf(args, "--gallery") >= 0)
        {
            var screenshotIndex = Array.IndexOf(args, "--screenshot");
            var directory = screenshotIndex >= 0 && screenshotIndex + 1 < args.Length ? args[screenshotIndex + 1] : null;
            window = new GalleryWindow(directory);
        }
        else
        {
            window = new ShellWindow();
        }

        MainWindow = window;
        window.Show();
    }
}
