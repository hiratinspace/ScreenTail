using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using ScreenTail.UI.Theme;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// The one WPF <see cref="Application"/> this process may have, with the app's styles, on a thread of its
/// own that pumps for as long as the collection that owns it runs (<see cref="WpfApplicationOwner"/>).
///
/// A plain Application, as App.xaml merges its dictionaries, so nothing of the app's start-up runs (no
/// pipe, no tray icon). Each test hands its body to <see cref="RunAsync{T}(Func{Task{T}})"/>, which runs it
/// on that thread; between bodies the thread is idle in its message loop, which is what lets UI Automation
/// (and the Axe.Windows scan) read the windows from the test's own thread without deadlocking on them.
///
/// Shut down when the collection ends, on its own thread, which clears <c>Application.Current</c> (the
/// reason <see cref="ApplicationOwnerRunsLast"/> exists: no XAML loads in a process after that).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TheApplication : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Application? _app;
    private string? _hung;
    private readonly System.Diagnostics.Stopwatch _busy = new();
    private int _bodies;

    public TheApplication()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "wpf application" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = _started.Task.GetAwaiter().GetResult();
    }

    public Dispatcher Dispatcher { get; }

    /// <summary>The application's resources, where <see cref="ThemeManager.Apply"/> swaps the palette.</summary>
    public ResourceDictionary Resources => _app!.Resources;

    /// <summary>How long a body may take. Two minutes; a test of the timeout itself shortens it.</summary>
    internal TimeSpan Limit { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs <paramref name="body"/> on the application's thread and waits for it, at most <see cref="Limit"/>.
    ///
    /// A body that hangs keeps the one UI thread busy, and every later test would queue behind it and time
    /// out in turn, two minutes each. So a timeout is remembered and later calls fail at once, naming the
    /// test that overran, until that body does finish: one that was only slow frees the thread, and the
    /// tests after it run again.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Volatile.Read(ref _hung) is { } earlier)
        {
            Assert.Fail($"Not run: {earlier} did not finish, and the UI thread is still busy with it.");
        }

        _bodies++;
        _busy.Start();
        var task = Dispatcher.InvokeAsync(body).Task.Unwrap();
        var finished = await Task.WhenAny(task, Task.Delay(Limit)).ConfigureAwait(false);
        _busy.Stop();
        if (finished != task)
        {
            var overran = TestContext.Current.Test?.TestDisplayName ?? "an earlier test";
            _hung = overran;
            _ = task.ContinueWith(_ => Interlocked.CompareExchange(ref _hung, null, overran), TaskScheduler.Default);
            Assert.Fail(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The UI thread did not finish within {Limit.TotalSeconds:0.#} s."));
        }

        return await task.ConfigureAwait(false);
    }

    public Task RunAsync(Func<Task> body) => RunAsync(async () =>
    {
        await body().ConfigureAwait(true);
        return true;
    });

    public Task RunAsync(Action body) => RunAsync(() =>
    {
        body();
        return Task.FromResult(true);
    });

    public void Dispose()
    {
        // What the collection cost, for the job log: the laptop's job has a time limit (ST-084 review).
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Accessibility suite: {_bodies} runs on the UI thread, {_busy.Elapsed.TotalSeconds:0.0} s; {AxeScan.Scans} Accessibility Insights scans, {AxeScan.Spent.TotalSeconds:0.0} s"));
        AxeScan.Stop();
        if (_app is { } app)
        {
            // Queued rather than waited for: a thread that hung would never run it.
            _ = Dispatcher.BeginInvoke(() => app.Shutdown());
            Dispatcher.InvokeShutdown();
            _ = _thread.Join(TimeSpan.FromSeconds(30));
            _app = null;
        }
    }

    private void Pump()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var file in new[] { "Tokens.Common.g.xaml", "Components.xaml" })
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/ScreenTail.UI;component/Theme/{file}") });
            }

            ThemeManager.Apply(AppTheme.Dark, app.Resources);
            _app = app;
            _started.SetResult(Dispatcher.CurrentDispatcher);
        }
        catch (Exception failure)
        {
            _started.SetException(ExceptionDispatchInfo.Capture(failure).SourceException);
            return;
        }

        Dispatcher.Run();
    }
}
