using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Input;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Input;

namespace ScreenTail.Tests.Windows.Input;

/// <summary>
/// ST-024 against real hooks. These inject keystrokes and clicks with SendInput, so they **take over the
/// keyboard and mouse** of whatever machine runs them, and skip unless
/// <c>SCREENTAIL_ALLOW_INPUT_INJECTION=1</c> says that's allowed. Only the dedicated laptop job sets it —
/// the capability job stays safe to run on a machine someone is using.
///
/// Input is aimed at a window this test owns and focuses first, so the keystrokes land nowhere else.
/// </summary>
public sealed class InputHookTests
{
    private static bool CanInject =>
        Environment.GetEnvironmentVariable("SCREENTAIL_ALLOW_INPUT_INJECTION") == "1"
        && new WindowsCapabilityProbe().Probe()[Capability.DesktopSession].State == CapabilityState.Ok;

    private static bool PerformanceCounts => !string.Equals(
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task HooksInstallAndComeBackOutCleanly()
    {
        Assert.SkipUnless(
            new WindowsCapabilityProbe().Probe()[Capability.InputHooks].State == CapabilityState.Ok,
            "Windows won't allow input hooks on this machine.");

        await using var hooks = new WindowsInputHooks();
        await hooks.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(hooks.Installed);
        Assert.Equal(1, hooks.Installs);
    }

    [Fact]
    public async Task ReinstallingReplacesBothHooks()
    {
        // AC3 in the form a test can reach: lock/unlock and resume both land here, and this is the path they
        // take. The events themselves need a human locking the machine.
        Assert.SkipUnless(
            new WindowsCapabilityProbe().Probe()[Capability.InputHooks].State == CapabilityState.Ok,
            "Windows won't allow input hooks on this machine.");

        await using var hooks = new WindowsInputHooks();
        await hooks.StartAsync(TestContext.Current.CancellationToken);

        hooks.Reinstall();
        hooks.Reinstall();

        Assert.True(hooks.Installed);
        Assert.Equal(3, hooks.Installs);
    }

    [Fact]
    public async Task TypingAPasswordRecordsOnlyHowManyKeys()
    {
        // The criterion that matters most: a real password goes through a real keyboard hook, and what comes
        // out the other side is a number.
        Assert.SkipUnless(CanInject, "Input injection not permitted on this runner.");
        const string Password = "Winter2026!";
        var buffer = new InputRingBuffer(256);
        await using var hooks = new WindowsInputHooks(buffer);
        await hooks.StartAsync(TestContext.Current.CancellationToken);

        using var window = FocusedWindow.Create("ScreenTail input target");
        Assert.SkipUnless(window.Focused, "Could not take the foreground; another window owns input.");
        Synthetic.Type(Password);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var signals = new InputSignal[256];
        var count = buffer.Drain(signals);
        var kinds = signals.AsSpan(0, count).ToArray().Select(s => s.Kind).ToList();

        Assert.Contains(InputKind.PrintableKey, kinds);
        Assert.DoesNotContain(InputKind.Enter, kinds);
        // Every signal is a category and a time. There is no field holding a key, so nothing here can be
        // turned back into what was typed.
        Assert.All(signals.AsSpan(0, count).ToArray(), s => Assert.True(s.Kind is InputKind.PrintableKey or InputKind.Shortcut));
    }

    [Fact]
    public async Task EnterIsItsOwnCategory()
    {
        Assert.SkipUnless(CanInject, "Input injection not permitted on this runner.");
        var buffer = new InputRingBuffer(64);
        await using var hooks = new WindowsInputHooks(buffer);
        await hooks.StartAsync(TestContext.Current.CancellationToken);

        using var window = FocusedWindow.Create("ScreenTail enter target");
        Assert.SkipUnless(window.Focused, "Could not take the foreground; another window owns input.");
        Synthetic.PressEnter();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var signals = new InputSignal[64];
        var count = buffer.Drain(signals);
        Assert.Contains(InputKind.Enter, signals.AsSpan(0, count).ToArray().Select(s => s.Kind));
    }

    [Fact]
    public async Task AClickIsBufferedQuicklyWithWhereAndWhich()
    {
        // AC1: buffered within 2 ms of the click, and the callback itself under 1 ms.
        Assert.SkipUnless(CanInject, "Input injection not permitted on this runner.");
        var buffer = new InputRingBuffer(64);
        await using var hooks = new WindowsInputHooks(buffer);
        await hooks.StartAsync(TestContext.Current.CancellationToken);

        using var window = FocusedWindow.Create("ScreenTail click target");
        Assert.SkipUnless(window.Focused, "Could not take the foreground; another window owns input.");

        var sent = Stopwatch.GetTimestamp();
        Synthetic.ClickLeft();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var signals = new InputSignal[64];
        var count = buffer.Drain(signals);
        var click = signals.AsSpan(0, count).ToArray().FirstOrDefault(s => s.Kind == InputKind.Click);
        Assert.Equal(InputKind.Click, click.Kind);
        Assert.Equal(MouseButtonKind.Left, click.Button);

        var latency = Stopwatch.GetElapsedTime(sent, click.Timestamp);
        Record($"Click buffered **{latency.TotalMilliseconds:F3} ms** after it was sent (budget 2 ms, enforced: {PerformanceCounts})");
        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count.");
        Assert.True(latency.TotalMilliseconds < 2, $"buffered after {latency.TotalMilliseconds:F3} ms, budget is 2 ms");
    }

    [Fact]
    public async Task TheCallbackStaysOutOfTheWayUnderSustainedTyping()
    {
        // The number that matters for the machine's feel: how long each callback holds the input path. The
        // spike measured 0.445 ms worst case against a 5 ms budget; ST-024's own budget is 1 ms.
        Assert.SkipUnless(CanInject, "Input injection not permitted on this runner.");
        var buffer = new InputRingBuffer(8192);
        await using var hooks = new WindowsInputHooks(buffer);
        await hooks.StartAsync(TestContext.Current.CancellationToken);

        using var window = FocusedWindow.Create("ScreenTail load target");
        Assert.SkipUnless(window.Focused, "Could not take the foreground; another window owns input.");

        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 20; i++)
        {
            Synthetic.Type("the quick brown fox jumps over the lazy dog");
        }

        await Task.Delay(500, TestContext.Current.CancellationToken);
        var wall = Stopwatch.GetElapsedTime(start);

        var signals = new InputSignal[8192];
        var count = buffer.Drain(signals);
        Assert.True(count > 600, $"only {count} of ~860 keystrokes reached the buffer");
        Assert.Equal(0, buffer.Dropped);

        // Per-callback cost is not directly observable from here; what is observable is that the hook kept
        // up with sustained input without Windows removing it, which is what a slow callback causes.
        Assert.True(hooks.Installed, "Windows removed the hook, which is what it does to a slow callback");
        Record($"Handled **{count}** keystrokes in {wall.TotalMilliseconds:F0} ms with {buffer.Dropped} dropped");
    }

    private static void Record(string measurement)
    {
        Console.WriteLine(measurement);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            File.AppendAllText(summary, $"- {measurement}{Environment.NewLine}");
        }
    }

    /// <summary>Synthetic input, aimed at whatever currently has focus — which the caller has just taken.</summary>
    private static class Synthetic
    {
        public static void Type(string text)
        {
            foreach (var character in text)
            {
                var vk = VkKeyScan(character);
                if (vk == -1)
                {
                    continue;
                }

                var shift = (vk & 0x0100) != 0;
                Key((ushort)(vk & 0xFF), shift);
            }
        }

        public static void PressEnter() => Key(0x0D, false);

        public static void ClickLeft()
        {
            Send(new Input { Type = INPUT_MOUSE, Data = { Mouse = new MouseInput { Flags = MOUSEEVENTF_LEFTDOWN } } });
            Send(new Input { Type = INPUT_MOUSE, Data = { Mouse = new MouseInput { Flags = MOUSEEVENTF_LEFTUP } } });
        }

        private static void Key(ushort virtualKey, bool shift)
        {
            if (shift)
            {
                Send(Keyboard(0x10, false));
            }

            Send(Keyboard(virtualKey, false));
            Send(Keyboard(virtualKey, true));
            if (shift)
            {
                Send(Keyboard(0x10, true));
            }
        }

        private static Input Keyboard(ushort virtualKey, bool up) => new()
        {
            Type = INPUT_KEYBOARD,
            Data = { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = up ? KEYEVENTF_KEYUP : 0 } },
        };

        private static void Send(Input input)
        {
            var one = new[] { input };
            _ = SendInput(1, one, Marshal.SizeOf<Input>());
        }

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;
            [FieldOffset(0)]
            public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint Data;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScan(char ch);
    }

    /// <summary>A window of ours, brought to the front so injected input lands on it and nowhere else.</summary>
    private sealed class FocusedWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<IntPtr> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _closing;

        private FocusedWindow(string title)
        {
            _thread = new Thread(() => Run(title)) { IsBackground = true, Name = "input target" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Handle = _created.Task.GetAwaiter().GetResult();
            Focused = TakeForeground();
        }

        public IntPtr Handle { get; }

        public bool Focused { get; }

        public static FocusedWindow Create(string title) => new(title);

        public void Dispose()
        {
            _closing = true;
            _ = PostMessage(Handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private bool TakeForeground()
        {
            _ = ShowWindow(Handle, 5);
            var ours = GetCurrentThreadId();
            var theirs = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
            try
            {
                _ = SetForegroundWindow(Handle);
                if (GetForegroundWindow() != Handle)
                {
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

        private void Run(string title)
        {
            var window = CreateWindowEx(0, "STATIC", title, 0x00CF0000, 60, 60, 380, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            _created.SetResult(window);
            while (!_closing && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }

            _ = DestroyWindow(window);
        }

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
        private static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

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
