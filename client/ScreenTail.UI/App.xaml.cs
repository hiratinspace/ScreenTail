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
            // ST-072's variants. Each pill is its own window, so there is no host window to hang the
            // render off — and the first version hung a 1x1 Window's ContentRendered, which never fires
            // on a window with no Content. The job sat there for twenty minutes before CI killed it.
            //
            // Driven from the dispatcher instead, with shutdown held open explicitly because nothing is
            // shown until the first pill appears and OnLastWindowClose would end the app between them.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(
                async () =>
                {
                    try
                    {
                        await Hud.HudPreview.CaptureAsync(directory);
                    }
                    catch (Exception error)
                    {
                        // Printed and non-zero rather than hung: a render that cannot finish should say so
                        // in seconds, which is the whole difference between a failing check and a wasted
                        // twenty-minute job.
                        Console.Error.WriteLine(error);
                        Environment.ExitCode = 1;
                    }

                    Shutdown();
                },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
