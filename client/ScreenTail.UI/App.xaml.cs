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

        // Dark by default (Scope §6.2); ST-070 persists the user's choice. High contrast always wins —
        // Apply substitutes it, so every caller gets that rather than only this one, and FollowSystem
        // keeps it true if Windows switches while the app is running.
        ThemeManager.Apply(AppTheme.Dark, Resources);
        ThemeManager.FollowSystem(Resources);

        var args = e.Args;
        var screenshotIndex = Array.IndexOf(args, "--screenshot");
        var directory = screenshotIndex >= 0 && screenshotIndex + 1 < args.Length ? args[screenshotIndex + 1] : null;

        Window window;
        if (Array.IndexOf(args, "--gallery") >= 0)
        {
            window = new GalleryWindow(directory);
        }
        else if (Array.IndexOf(args, "--hud") >= 0 && directory is not null)
        {
            // ST-072's variants. The pill is its own window per variant, so it renders from a plain task
            // rather than from a host window's ContentRendered.
            window = new Window { Width = 1, Height = 1, ShowInTaskbar = false, Left = -4000, Top = -4000 };
            window.ContentRendered += async (_, _) =>
            {
                await Hud.HudPreview.CaptureAsync(directory);
                Shutdown();
            };
            MainWindow = window;
            window.Show();
            return;
        }
        else if (Array.IndexOf(args, "--note") >= 0)
        {
            // ST-074's pane, rendered against a fixture in every state and theme. Same trick as the shell.
            window = new Review.NotePreviewWindow(directory);
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
