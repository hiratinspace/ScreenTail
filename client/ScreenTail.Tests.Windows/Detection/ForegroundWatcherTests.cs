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

        using var self = Process.GetCurrentProcess();
        var before = self.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        clock.Stop();
        self.Refresh();
        var used = self.TotalProcessorTime - before;

        var percent = used.TotalMilliseconds / (clock.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
        Assert.True(percent < 0.5, $"used {percent:F3}% of CPU while idle, budget is 0.5%");
    }

    [Fact]
    public async Task StartingAndStoppingLeavesNoThreadBehind()
    {
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
        using var self = Process.GetCurrentProcess();
        var before = self.Threads.Count;

        for (var i = 0; i < 3; i++)
        {
            var watcher = new WindowsForegroundWatcher();
            await watcher.StartAsync(TestContext.Current.CancellationToken);
            await watcher.DisposeAsync();
        }

        self.Refresh();
        // The pump thread must actually exit: three cycles would otherwise leave three behind.
        Assert.True(self.Threads.Count <= before + 1, $"thread count went from {before} to {self.Threads.Count}");
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

        public bool BringToFront()
        {
            _ = ShowWindow(Handle, SW_SHOW);
            _ = SetForegroundWindow(Handle);
            for (var i = 0; i < 20 && GetForegroundWindow() != Handle; i++)
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
