using System.Windows;
using ScreenTail.Shared.Logging;
using ScreenTail.UI.Gallery;
using ScreenTail.UI.Shell;
using ScreenTail.UI.Theme;

namespace ScreenTail.UI;

/// <summary>
/// Entry point for both of the UI's lives: the CI screenshot harnesses, and the application.
///
/// Disposable because the application owns the live shell, which owns the pipe, the tray icon and the
/// pill. WPF calls <see cref="OnExit"/> on a clean shutdown; <see cref="Dispose"/> is what makes that
/// promise good when something else tears the process down first.
/// </summary>
public partial class App : Application, IDisposable
{
    private Shell.LiveShell? _live;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ST-011: a crash leaves a stack-only report in the local queue when the technician opted in,
        // and otherwise nothing. Neither handler marks the exception handled: the process still dies,
        // as it should, but it says where.
        DispatcherUnhandledException += (_, args) => Report(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception crash)
            {
                Report(crash);
            }
        };

        // Before a window, a tray icon or a pipe. The service verifies the file this process started
        // from, and .NET will load somebody else's code into a genuine signed process if the environment
        // asks — so an attacker needs no forged binary, only our real one launched with a startup hook
        // set. The process being hijacked is ours, so it is ours to refuse (ST-012, 2026-09-19 review).
        //
        // A development build says so and carries on: this is how a profiler is attached.
        if (Core.Ipc.RunningHonestly.WhyNotToStart(Platform.Ipc.Signing.IsDevelopmentBuild) is { } refusal)
        {
            _ = MessageBox.Show(refusal, "ScreenTail", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(4);
            return;
        }

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
        else if (directory is not null)
        {
            // The shell renders itself the same way the gallery does when asked. RenderTargetBitmap draws
            // offscreen, so this works on a hosted runner with no interactive desktop — which is what makes
            // a XAML shell checkable at all without a machine to look at. It catches what writing XAML
            // blind actually gets wrong: a missing resource key, a style on the wrong target type, a
            // binding to a property that is not there.
            window = new ShellWindow(directory);
        }
        else
        {
            // The application (ST-085). Everything above this line is a screenshot harness; this is the
            // only path that connects to the capture service, and until it existed the UI process was a
            // set of windows rendering literals with INV-4 enforced by nothing (weaknesses P0-2).
            //
            // It starts in the tray rather than opening a window. A technician's first session of the day
            // begins by focusing a remote-support tool, not by opening us, and the pill and the tray icon
            // are the whole interface until there is a draft to review.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _live = new Shell.LiveShell();
            _ = _live.StartAsync();
            return;
        }

        MainWindow = window;
        window.Show();
    }

    private static void Report(Exception crash) =>
        _ = CrashReport.Write(
            CrashReport.DefaultDirectory,
            crash,
            "ScreenTail.UI",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            CrashReport.Enabled(name => Environment.GetEnvironmentVariable(name)));

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        // Synchronous on purpose: this runs on the way out, and an async void here would let the process
        // exit with the tray icon still in the notification area — the ghost icon that only disappears
        // when the mouse passes over it.
        _live?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _live = null;
        GC.SuppressFinalize(this);
    }
}
