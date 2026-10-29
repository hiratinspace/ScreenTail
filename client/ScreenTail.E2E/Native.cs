using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenTail.E2E;

/// <summary>The few Windows calls the driver needs: the foreground, the UI's windows, clicks, and Ctrl+C.</summary>
internal static partial class Native
{
    /// <summary>
    /// Brings a window to the front and waits for Windows to agree, the way the Windows suite's
    /// <c>DesktopWindow</c> does: the foreground lock is lifted for this user (an unattended runner has had
    /// no input, so Windows would refuse silently), and the call is made attached to the foreground thread.
    /// No key or click is injected to get there.
    /// </summary>
    public static bool TakeForeground(IntPtr window, TimeSpan within)
    {
        _ = SystemParametersInfo(SpiSetForegroundLockTimeout, 0, IntPtr.Zero, SpifSendChange);
        var deadline = DateTime.UtcNow + within;
        do
        {
            _ = ShowWindow(window, SwShowNormal);
            var ours = GetCurrentThreadId();
            var theirs = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
            try
            {
                _ = BringWindowToTop(window);
                _ = SetForegroundWindow(window);
                if (GetForegroundWindow() != window)
                {
                    SwitchToThisWindow(window, true);
                }
            }
            finally
            {
                if (attached)
                {
                    _ = AttachThreadInput(ours, theirs, false);
                }
            }

            for (var i = 0; i < 20 && GetForegroundWindow() != window; i++)
            {
                Thread.Sleep(25);
            }
        }
        while (GetForegroundWindow() != window && DateTime.UtcNow < deadline);

        return GetForegroundWindow() == window;
    }

    /// <summary>What holds the foreground, by process and window class. Never the title (INV-10).</summary>
    public static string Foreground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return "nothing holds the foreground";
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        return $"{ProcessName(processId)} (class {ClassOf(window)})";
    }

    /// <summary>A visible top-level window of a process: its class and where it is. Never its title.</summary>
    public sealed record WindowRow(IntPtr Handle, string Class, int Left, int Top, int Width, int Height, bool OnAMonitor, bool Topmost);

    /// <summary>The visible top-level windows of every process with this name.</summary>
    public static IReadOnlyList<WindowRow> VisibleWindowsOf(string processName)
    {
        var ids = Process.GetProcessesByName(processName).Select(p =>
        {
            using (p)
            {
                return (uint)p.Id;
            }
        }).ToHashSet();
        var rows = new List<WindowRow>();
        _ = EnumWindows(
            (window, parameter) =>
            {
                _ = GetWindowThreadProcessId(window, out var owner);
                if (ids.Contains(owner) && IsWindowVisible(window) && GetWindowRect(window, out var rect))
                {
                    var middle = new NativePoint { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
                    var style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
                    rows.Add(new WindowRow(
                        window,
                        ClassOf(window),
                        rect.Left,
                        rect.Top,
                        rect.Right - rect.Left,
                        rect.Bottom - rect.Top,
                        MonitorFromPoint(middle, MonitorDefaultToNull) != IntPtr.Zero,
                        (style & WsExTopmost) != 0));
                }

                return true;
            },
            IntPtr.Zero);
        return rows;
    }

    /// <summary>Real left clicks at a point on the screen, with SendInput. Only on a machine whose job allows it.</summary>
    public static void Click(int x, int y)
    {
        _ = SetCursorPos(x, y);
        Thread.Sleep(60);
        var inputs = new[]
        {
            new Input { Type = InputMouse, Mouse = new MouseInput { Flags = MouseEventLeftDown } },
            new Input { Type = InputMouse, Mouse = new MouseInput { Flags = MouseEventLeftUp } },
        };
        Send(inputs);
    }

    /// <summary>
    /// A real key chord such as Ctrl+Alt+R, pressed and released with SendInput, which is how the service's
    /// registered hotkeys hear a technician. Modifiers are Ctrl, Alt, Shift and Win; the key is a letter
    /// or a digit.
    /// </summary>
    public static void Chord(string chord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chord);
        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var keys = new List<ushort>();
        foreach (var part in parts)
        {
            keys.Add(part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 0x11,
                "ALT" => 0x12,
                "SHIFT" => 0x10,
                "WIN" => 0x5B,
                { Length: 1 } key when char.IsAsciiLetterOrDigit(key[0]) => key[0],
                _ => throw new ArgumentException($"A chord this driver cannot press: {chord}", nameof(chord)),
            });
        }

        var inputs = keys.Select(k => new Input { Type = InputKeyboard, Key = new KeyboardInput { Vk = k } })
            .Concat(Enumerable.Reverse(keys).Select(k => new Input { Type = InputKeyboard, Key = new KeyboardInput { Vk = k, Flags = KeyEventKeyUp } }))
            .ToArray();
        Send(inputs);
    }

    private static void Send(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Length} events (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>The middle of a window's rectangle, in screen pixels.</summary>
    public static (int X, int Y) Middle(IntPtr window)
    {
        _ = GetWindowRect(window, out var rect);
        return ((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
    }

    /// <summary>
    /// Sends Ctrl+Break to the console a process is attached to, which is how a console program is asked
    /// to stop cleanly: the .NET host turns it into a graceful shutdown, as Ctrl+C in its window does.
    /// Ctrl+Break rather than Ctrl+C because a process can inherit "ignore Ctrl+C" from whatever started
    /// it, and no process can ignore Ctrl+Break. This process lets go of its own console to do it and
    /// ignores the event itself.
    /// </summary>
    /// <returns>
    /// 0 when the event was sent; 3 when the process's console could not be attached to; 4 when the console
    /// is shared with other processes, which the event would reach too; 5 when the event could not be sent.
    /// The console is let go of first, so the answer is the exit code, not a message.
    /// </returns>
    public static int SendStop(int processId)
    {
        _ = FreeConsole();
        if (!AttachConsole((uint)processId))
        {
            return 3;
        }

        try
        {
            // The event goes to every process on that console. One the runner shares would take the
            // runner's own shell with it, so anything there but the target and this process is a refusal.
            var attached = new uint[16];
            var count = GetConsoleProcessList(attached, (uint)attached.Length);
            var others = attached.Take((int)Math.Min(count, (uint)attached.Length)).Count(id => id != (uint)processId && id != (uint)Environment.ProcessId);
            if (count == 0 || count > attached.Length || others > 0)
            {
                return 4;
            }

            _ = SetConsoleCtrlHandler(Ignore, true);
            return GenerateConsoleCtrlEvent(CtrlBreakEvent, 0) ? 0 : 5;
        }
        finally
        {
            // Long enough for the event to be delivered before this process leaves the console.
            Thread.Sleep(500);
            _ = FreeConsole();
        }
    }

    private static string ProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "(gone)";
        }
    }

    private static string ClassOf(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "(unknown)";
    }

    private const uint SpiSetForegroundLockTimeout = 0x2001;
    private const uint SpifSendChange = 0x02;
    private const int SwShowNormal = 1;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const uint MonitorDefaultToNull = 0;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint CtrlBreakEvent = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // INPUT on x64: the type, then the union at offset 8, forty bytes in all, which is the size SendInput
    // checks. The runners are x64.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public MouseInput Mouse;

        [FieldOffset(8)]
        public KeyboardInput Key;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr pvParam, uint winIni);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [Out] char[] buffer, int length);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    /// <summary>This process's answer to the event it sends: handled, so it is not ended by it. Kept in a static so it is never collected.</summary>
    private static readonly ConsoleCtrlHandler Ignore = _ => true;

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ConsoleCtrlHandler(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
