using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Service.Detection;
using ScreenTail.Service.Privacy;
using ScreenTail.Tests.Windows.Detection;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>
/// ST-031: the two WinEvent hooks the capture service keeps between sessions hear only what can matter to
/// it, so its idle cost does not follow whatever else is open on the desktop.
///
/// The laptop's idle measurement passed at 17 to 20 wakeups a second and failed at 47 to 52 on the same
/// code a few minutes apart, with one thread taking the difference. These count the watchers' own pump
/// threads, from the kernel's context-switch counts, while a test window raises the events the old hooks
/// took from every thread on the desktop. A pump thread's count is the watcher's own and nobody else's, so
/// unlike a whole-process figure it means the same on a hosted runner as on the laptop, and these enforce
/// everywhere.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class IdleHookTests
{
    /// <summary>
    /// What a pump may wake for while the noise below runs: nothing it hears, and the odd stray (a timer, a
    /// window of the test's own coming and going, the window in front retitling itself). The noise is about
    /// 70 events a second, and the hooks as they were woke the foreground pump 215 times a second for it
    /// (hosted run 41344523601); fixed, the hosted runner measured 0 to 3. Ten leaves a desktop busier than
    /// a cloud VM's its strays and still fails the old subscription twenty times over.
    /// </summary>
    internal const double PumpCeiling = 10;

    private static TimeSpan Window => TimeSpan.FromSeconds(3);

    [Fact]
    public async Task TheForegroundWatcherSleepsThroughRenamesBehindTheWindowInFront()
    {
        Hardware.RequireDesktop();
        var ct = TestContext.Current.CancellationToken;
        using var noisy = DesktopWindow.CreateOrdinary($"ScreenTail renaming {Guid.NewGuid():N}");
        noisy.ShowBehind();
        using var front = DesktopWindow.CreateOrdinary($"ScreenTail front {Guid.NewGuid():N}");
        front.RequireForeground();

        // The control: the renames are delivered to a hook over every thread, as the watcher's used to be.
        using var witness = NameChangeWitness.Start();

        // The poll out of reach, so the pump has no timer of its own to wake for.
        await using var watcher = new WindowsForegroundWatcher(pollInterval: TimeSpan.FromMinutes(10));
        await watcher.StartAsync(ct);
        Assert.True(watcher.UsingHook, "the foreground watcher's hooks were not installed");
        await Task.Delay(300, ct);

        // The hook follows the window in front: its thread, not the renaming window's and not every thread.
        Assert.Equal(ThreadOf(front.Handle), watcher.NameHookThread);

        await using (var noise = Noise.Renaming(noisy))
        {
            var pump = await PumpRateAsync(watcher.PumpThreadId, ct);
            var delivered = witness.Count(noisy.Handle, NameChangeWitness.WindowObject);
            Measurements.Record(string.Create(
                CultureInfo.InvariantCulture,
                $"Foreground watcher at idle, a window behind renaming itself {noise.PerSecond:F0} times a second ({delivered} renames reached a hook over every thread): its pump woke **{pump:F1} times a second** (ceiling {PumpCeiling:F0}, enforced everywhere)"));
            Assert.True(delivered > 0, "no background rename reached the test's own hook, so the watcher's quiet proves nothing");
            Assert.True(pump < PumpCeiling, $"the foreground watcher's pump woke {pump:F1} times a second for renames behind the window in front; ceiling {PumpCeiling:F0}");
        }

        // And it still moves with the foreground: the renaming window brought to the front takes the hook.
        Assert.True(noisy.TakeForeground(), "the renaming window could not be brought to the front");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (watcher.NameHookThread != ThreadOf(noisy.Handle) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct);
        }

        Assert.Equal(ThreadOf(noisy.Handle), watcher.NameHookThread);
    }

    [Fact]
    public async Task TheFocusWatcherHearsNothingUntilASessionWantsIt()
    {
        Hardware.RequireDesktop();
        var ct = TestContext.Current.CancellationToken;
        using var window = DesktopWindow.CreateOrdinary($"ScreenTail fields {Guid.NewGuid():N}");
        window.AddFields();
        window.RequireForeground();

        await using var watcher = new WindowsFocusWatcher();
        var moves = 0;
        watcher.FocusMoved += () => Interlocked.Increment(ref moves);
        await watcher.StartAsync(ct);
        Assert.True(watcher.UsingHook, "the focus hook could not be installed");

        // No session: the service tells the watcher to stop listening (FocusInterest).
        watcher.Listen(false);
        await Task.Delay(200, ct);
        Assert.False(watcher.Hooked, "the focus hook was still in after Listen(false)");

        double quiet;
        int heardQuiet;
        await using (var noise = Noise.Focusing(window))
        {
            _ = Interlocked.Exchange(ref moves, 0);
            quiet = await PumpRateAsync(watcher.PumpThreadId, ct);
            heardQuiet = Volatile.Read(ref moves);
        }

        // The control: the same churn with the hook in is heard, so the silence above was the hook's doing.
        watcher.Listen(true);
        await Task.Delay(200, ct);
        Assert.True(watcher.Hooked, "the focus hook did not come back after Listen(true)");
        int heardListening;
        double listening;
        await using (var noise = Noise.Focusing(window))
        {
            _ = Interlocked.Exchange(ref moves, 0);
            listening = await PumpRateAsync(watcher.PumpThreadId, ct);
            heardListening = Volatile.Read(ref moves);
        }

        Measurements.Record(string.Create(
            CultureInfo.InvariantCulture,
            $"Focus watcher, focus moving between two fields 100 times a second: not listening, its pump woke **{quiet:F1} times a second** and heard {heardQuiet} moves (ceiling {PumpCeiling:F0}, enforced everywhere); listening, {listening:F1} a second and {heardListening} moves"));
        Assert.True(heardListening > 0, "with the hook in, no focus move was heard, so the quiet without it proves nothing");
        Assert.Equal(0, heardQuiet);
        Assert.True(quiet < PumpCeiling, $"the focus watcher's pump woke {quiet:F1} times a second with no one listening; ceiling {PumpCeiling:F0}");
    }

    /// <summary>A pump thread's wakeups a second over <see cref="Window"/>, from the kernel's context-switch counts.</summary>
    internal static async Task<double> PumpRateAsync(uint thread, CancellationToken ct)
    {
        var before = ThreadSwitches.Read(Environment.ProcessId);
        var start = Stopwatch.GetTimestamp();
        await Task.Delay(Window, ct);
        var after = ThreadSwitches.Read(Environment.ProcessId);
        var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        Assert.True(after.ContainsKey(thread), $"the pump thread {thread} was not found in this process");
        return unchecked(after[thread] - before.GetValueOrDefault(thread)) / seconds;
    }

    internal static uint ThreadOf(IntPtr window) => GetWindowThreadProcessId(window, out _);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}

/// <summary>
/// A test window raising, a hundred times a second, one of the events the idle service's hooks used to
/// take from every thread on the desktop. Runs until disposed.
///
/// On a thread of its own, sleeping between events, and never on the pool or a timer: a whole-process
/// figure can then leave out exactly the noise's threads (<see cref="Threads"/>) and keep the pool and timer
/// threads the service's own loops run on. Run on the pool, the noise alone came to thousands of the
/// process's wakeups a second and drowned the figure it was there to test.
/// </summary>
internal sealed class Noise : IAsyncDisposable
{
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TaskCompletionSource<uint> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stop;
    private int _count;

    private Noise(DesktopWindow window, Action<int> step)
    {
        _thread = new Thread(() =>
        {
            _started.TrySetResult(GetCurrentThreadId());
            for (var i = 0; !_stop; i++)
            {
                step(i);
                _ = Interlocked.Increment(ref _count);
                Thread.Sleep(10);
            }
        })
        {
            IsBackground = true,
            Name = "ScreenTail test noise",
        };
        _thread.Start();
        Threads = [_started.Task.GetAwaiter().GetResult(), IdleHookTests.ThreadOf(window.Handle)];
    }

    /// <summary>The noise's own thread and the window's, which every event wakes by design.</summary>
    public IReadOnlyList<long> Threads { get; }

    /// <summary>How many events a second it has actually raised: a ten-millisecond sleep is a floor, not a promise.</summary>
    public double PerSecond => Volatile.Read(ref _count) / Math.Max(0.001, _clock.Elapsed.TotalSeconds);

    /// <summary><c>EVENT_OBJECT_NAMECHANGE</c> for the window itself: a background window retitling.</summary>
    public static Noise Renaming(DesktopWindow window) =>
        new(window, i => window.Retitle(string.Create(CultureInfo.InvariantCulture, $"ScreenTail renaming {i}")));

    /// <summary><c>EVENT_OBJECT_FOCUS</c>: focus moving between the window's two fields.</summary>
    public static Noise Focusing(DesktopWindow window) =>
        new(window, i => window.Focus(i % 2 == 0 ? window.PlainField : window.PasswordField));

    public ValueTask DisposeAsync()
    {
        _stop = true;
        _ = _thread.Join(TimeSpan.FromSeconds(5));
        return ValueTask.CompletedTask;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
