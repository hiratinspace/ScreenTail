using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Detection;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Detection;

namespace ScreenTail.Tests.Windows.Detection;

/// <summary>
/// ST-022 against real windows. These create actual top-level windows, put them in front, and time how long
/// the watcher takes to notice — the only way to know whether the hook is delivering.
///
/// They need an interactive desktop, so they skip on a hosted runner (session 0) and run for real on the
/// spare laptop. Skipping is the honest outcome there: a foreground test on a machine with no foreground
/// proves nothing, and passing it vacuously would be worse than not running it.
/// </summary>
[Collection("foreground")]
public sealed class ForegroundWatcherTests
{
    private static bool HasDesktop =>
        new WindowsCapabilityProbe().Probe()[Capability.DesktopSession].State == CapabilityState.Ok;

    /// <summary>
    /// Whether a timing measured here means anything. A GitHub-hosted runner is a shared cloud VM whose
    /// numbers the test loop has never counted (docs/dev/windows-test-loop.md) — it measured 322 ms against
    /// the 100 ms budget on a build that the laptop passed. Correctness is still checked everywhere; only
    /// the budget is held back for hardware we can reason about.
    /// </summary>
    private static bool PerformanceCounts => !string.Equals(
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"),
        "github-hosted",
        StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task AFocusChangeIsReportedWithinTheBudget()
    {
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
        var title = $"ScreenTail probe {Guid.NewGuid():N}";
        await using var watcher = new WindowsForegroundWatcher();
        var seen = new TaskCompletionSource<ForegroundWindowInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += info =>
        {
            if (info.Title == title)
            {
                seen.TrySetResult(info);
            }
        };
        await watcher.StartAsync(TestContext.Current.CancellationToken);

        using var window = SyntheticWindow.Create(title);
        var clock = Stopwatch.StartNew();
        Assert.SkipUnless(window.BringToFront(), "Windows refused to change the foreground window on this desktop.");
        var reported = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.Equal(title, reported.Title);
        Assert.Equal(Environment.ProcessId, reported.ProcessId);
        Assert.Equal(Path.GetFileNameWithoutExtension(Environment.ProcessPath), reported.ProcessName);
        Assert.False(reported.IsElevated);
        Record($"Focus change reported in **{clock.ElapsedMilliseconds} ms** (budget 100 ms, enforced: {PerformanceCounts})");
        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count; the laptop enforces this.");
        Assert.True(clock.ElapsedMilliseconds < 100, $"took {clock.ElapsedMilliseconds} ms, budget is 100 ms");
    }

    [Fact]
    public async Task RetitlingTheWindowInFrontIsReported()
    {
        // What a browser does when the technician switches tabs: same window, new title.
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
        var first = $"ScreenTail before {Guid.NewGuid():N}";
        var second = $"ScreenTail after {Guid.NewGuid():N}";
        await using var watcher = new WindowsForegroundWatcher();
        var renamed = new TaskCompletionSource<ForegroundWindowInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += info =>
        {
            if (info.Title == second)
            {
                renamed.TrySetResult(info);
            }
        };
        await watcher.StartAsync(TestContext.Current.CancellationToken);

        using var window = SyntheticWindow.Create(first);
        Assert.SkipUnless(window.BringToFront(), "Windows refused to change the foreground window on this desktop.");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        window.Retitle(second);

        var reported = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(second, reported.Title);
    }

    [Fact]
    public async Task TheWatcherCostsAlmostNothingWhileNothingHappens()
    {
        // AC3: idle CPU < 0.5%. The hook is event-driven; the timer is the only thing ticking.
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
        await using var watcher = new WindowsForegroundWatcher();
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // The watcher's own pump thread, not the whole process: the first version of this measured the test
        // host with other tests running in parallel and reported 2.1%, which said nothing about the watcher.
        var before = PumpThreadTime(watcher.PumpThreadId);
        Assert.SkipWhen(before is null, "Could not find the watcher's pump thread.");
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        clock.Stop();
        var used = PumpThreadTime(watcher.PumpThreadId)!.Value - before!.Value;

        var percent = used.TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
        Record($"Watcher used **{percent:F3}%** of a core while idle (budget 0.5%, enforced: {PerformanceCounts})");
        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count; the laptop enforces this.");
        Assert.True(percent < 0.5, $"the watcher used {percent:F3}% of a core while idle, budget is 0.5%");
    }

    [Fact]
    public async Task StartingAndStoppingLeavesNoThreadBehind()
    {
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
        var pumps = new List<uint>();

        for (var i = 0; i < 3; i++)
        {
            var watcher = new WindowsForegroundWatcher();
            await watcher.StartAsync(TestContext.Current.CancellationToken);
            pumps.Add(watcher.PumpThreadId);
            await watcher.DisposeAsync();
        }

        // Each watcher's own pump thread must be gone. Counting the process's threads instead looked right
        // and wasn't: the thread pool grows and shrinks by itself, which failed this on a busy runner.
        Assert.Equal(3, pumps.Distinct().Count());
        using var self = Process.GetCurrentProcess();
        var alive = self.Threads.Cast<ProcessThread>().Select(t => (uint)t.Id).ToHashSet();
        Assert.All(pumps, pump => Assert.DoesNotContain(pump, alive));
    }

    [Fact]
    public async Task TheWatcherReportsWhatIsAlreadyInFrontWhenItStarts()
    {
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
        await using var watcher = new WindowsForegroundWatcher();

        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Whatever it is, it must be described rather than left blank.
        Assert.NotNull(watcher.Current);
        if (!watcher.Current.IsNone)
        {
            Assert.NotEqual(0, watcher.Current.ProcessId);
        }
    }

    /// <summary>
    /// Puts a measurement where it can be seen. xUnit keeps a passing test's console output to itself, so
    /// the earlier Console.WriteLine only ever appeared on failure — which is precisely when the number is
    /// least interesting. On a runner this writes to the job summary instead, so each run records how much
    /// headroom is left against the budget.
    /// </summary>
    private static void Record(string measurement)
    {
        Console.WriteLine(measurement);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            File.AppendAllText(summary, $"- {measurement}{Environment.NewLine}");
        }
    }

    /// <summary>CPU time used by one OS thread, or null when it has gone.</summary>
    private static TimeSpan? PumpThreadTime(uint threadId)
    {
        using var self = Process.GetCurrentProcess();
        foreach (ProcessThread thread in self.Threads)
        {
            if (thread.Id == threadId)
            {
                return thread.TotalProcessorTime;
            }
        }

        return null;
    }

    /// <summary>A real top-level window on its own message-pumping thread, so it behaves like any other app's.</summary>
    private sealed class SyntheticWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<IntPtr> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _closing;

        private SyntheticWindow(string title)
        {
            _thread = new Thread(() => Run(title)) { IsBackground = true, Name = "synthetic window" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Handle = _created.Task.GetAwaiter().GetResult();
        }

        public IntPtr Handle { get; }

        public static SyntheticWindow Create(string title) => new(title);

        /// <summary>
        /// Windows won't let a process that hasn't received input steal the foreground, which is exactly the
        /// situation on an unattended runner: the first attempt at this skipped on the laptop. Attaching to
        /// the current foreground thread's input queue lifts that restriction for the moment it takes to
        /// activate, and injects nothing — this workflow must stay safe to run on a machine in use.
        /// </summary>
        public bool BringToFront()
        {
            _ = ShowWindow(Handle, SW_SHOW);

            var ours = GetCurrentThreadId();
            var theirs = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
            try
            {
                _ = BringWindowToTop(Handle);
                _ = SetForegroundWindow(Handle);
                if (GetForegroundWindow() != Handle)
                {
                    // The shell's own way of activating a window, which the foreground lock doesn't refuse.
                    SwitchToThisWindow(Handle, true);
                }
            }
            finally
            {
                if (attached)
                {
                    _ = AttachThreadInput(ours, theirs, false);
                }
            }

            for (var i = 0; i < 40 && GetForegroundWindow() != Handle; i++)
            {
                Thread.Sleep(25);
            }

            return GetForegroundWindow() == Handle;
        }

        public void Retitle(string title) => SetWindowText(Handle, title);

        public void Dispose()
        {
            _closing = true;
            _ = PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private void Run(string title)
        {
            // "STATIC" is a class Windows already registers, so there's no class to register or clean up.
            var window = CreateWindowEx(
                0,
                "STATIC",
                title,
                WS_OVERLAPPEDWINDOW,
                40,
                40,
                420,
                220,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            _created.SetResult(window);

            while (!_closing && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }

            _ = DestroyWindow(window);
        }

        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const int SW_SHOW = 5;
        private const uint WM_CLOSE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int PointX;
            public int PointY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowText(IntPtr hWnd, string text);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref Msg lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg lpMsg);
    }
}
