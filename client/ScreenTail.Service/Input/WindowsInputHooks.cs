using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Input;

namespace ScreenTail.Service.Input;

/// <summary>
/// Low-level mouse and keyboard hooks (ST-024).
///
/// These callbacks sit on the path every input event in the system travels down. Windows gives them a few
/// milliseconds each (<c>LowLevelHooksTimeout</c>, 300 ms by default but effectively much tighter under
/// load) and silently removes a hook that overruns — so the callbacks here classify, write one struct to a
/// lock-free buffer, and return. No allocation, no lock, no syscall, no logging. Everything else happens on
/// the consumer's thread.
///
/// **The keyboard callback cannot record a keystroke.** It reads the virtual-key code, decides which of
/// three categories the press falls into, and the code goes out of scope. <see cref="InputSignal"/> has
/// nowhere to put a key even if a later change tried (INV-2).
///
/// Hooks are re-installed on session lock/unlock and on resume from sleep, which is when Windows is most
/// likely to have dropped them.
///
/// **Started and stopped once per session for the life of the service** (ST-031, T-33). Each start is a
/// fresh run of <see cref="RestartablePump"/> with its own stop signal and its own window class, and it
/// succeeds only once both hooks are in, so the caller never says "installed" about a loop that ended.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsInputHooks : IAsyncDisposable
{
    private static int _classSerial;
    private readonly InputRingBuffer _buffer;
    private readonly HookProc _mouseCallback;
    private readonly HookProc _keyboardCallback;
    private readonly WndProc _windowCallback;
    private readonly RestartablePump _pump;
    private uint _pumpThreadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private IntPtr _window;
    private long _installs;
    private long _worstCallbackTicks;
    private bool _warmed;

    public WindowsInputHooks(InputRingBuffer? buffer = null)
    {
        _buffer = buffer ?? new InputRingBuffer();

        // Held in fields for the life of the object: a delegate passed to SetWindowsHookEx that the GC
        // collects leaves Windows calling into freed memory.
        _mouseCallback = OnMouse;
        _keyboardCallback = OnKeyboard;
        _windowCallback = OnWindowMessage;
        _pump = new RestartablePump("ScreenTail input hooks", Pump, thread => thread.SetApartmentState(ApartmentState.STA));
    }

    public InputRingBuffer Buffer => _buffer;

    /// <summary>How many times the hooks have been installed. Goes up on every re-install after a lock or a resume.</summary>
    public long Installs => Interlocked.Read(ref _installs);

    public bool Installed => _mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero;

    internal uint PumpThreadId => _pumpThreadId;

    /// <summary>
    /// The longest a callback has held the input path, in microseconds. ST-024 budgets 1 ms; the spike
    /// measured 0.445 ms on this hardware. Measuring costs two timestamps and a compare — about fifty
    /// nanoseconds against that budget — and it is the only number that says whether the machine still
    /// feels right to the person using it.
    /// </summary>
    public double WorstCallbackMicroseconds =>
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _worstCallbackTicks)).TotalMicroseconds;

    public void ResetWorstCallback() => Interlocked.Exchange(ref _worstCallbackTicks, 0);

    /// <summary>Converts a hook timestamp into milliseconds since <paramref name="sessionStart"/>.</summary>
    public static long ToSessionMs(long timestamp, long sessionStart) =>
        (long)Stopwatch.GetElapsedTime(sessionStart, timestamp).TotalMilliseconds;

    public static TimeSpan Elapsed(long from, long to) => Stopwatch.GetElapsedTime(from, to);

    /// <summary>
    /// Installs the hooks on a fresh pump thread and returns once both are in. Throws when Windows would not
    /// install them, so the caller counts a failure instead of announcing hooks that are not there.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default) => _pump.StartAsync(ct);

    /// <summary>Takes the hooks out and waits for the pump thread to end. Safe to call again, or first.</summary>
    public Task StopAsync(CancellationToken ct = default) => _pump.StopAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    // ---- the hot path ----------------------------------------------------------------------------------

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        var entered = Stopwatch.GetTimestamp();
        if (code == HC_ACTION)
        {
            var message = (uint)wParam;
            if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                var button = message switch
                {
                    WM_RBUTTONDOWN => MouseButtonKind.Right,
                    WM_MBUTTONDOWN => MouseButtonKind.Middle,
                    _ => MouseButtonKind.Left,
                };

                // Read the two ints we need straight out of MSLLHOOKSTRUCT rather than marshalling the whole
                // struct. Memory-safe and allocation-free; a raw pointer read would save a few nanoseconds
                // against a budget measured in milliseconds, which is not a trade worth making here.
                _ = _buffer.Write(new InputSignal(
                    InputKind.Click,
                    Stopwatch.GetTimestamp(),
                    Marshal.ReadInt32(lParam, 0),
                    Marshal.ReadInt32(lParam, 4),
                    button));
            }
        }

        RecordCost(entered);
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        var entered = Stopwatch.GetTimestamp();
        if (code == HC_ACTION && ((uint)wParam is WM_KEYDOWN or WM_SYSKEYDOWN))
        {
            // The only moment a key's identity exists. It is classified and dropped before this returns;
            // nothing downstream can recover it, because InputSignal has no field able to carry it.
            var key = Marshal.ReadInt32(lParam, 0);
            var kind = Classify(key);
            if (kind is { } observed)
            {
                _ = _buffer.Write(new InputSignal(observed, Stopwatch.GetTimestamp()));
            }
        }

        RecordCost(entered);
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>Keeps the worst callback seen so far. One compare, no lock, no allocation.</summary>
    private void RecordCost(long entered)
    {
        var cost = Stopwatch.GetTimestamp() - entered;
        var worst = Interlocked.Read(ref _worstCallbackTicks);
        while (cost > worst)
        {
            var seen = Interlocked.CompareExchange(ref _worstCallbackTicks, cost, worst);
            if (seen == worst)
            {
                break;
            }

            worst = seen;
        }
    }

    /// <summary>
    /// Which of the three permitted categories a key press falls into, or null for keys that say nothing
    /// worth recording (modifiers on their own, navigation, function keys without a modifier).
    /// </summary>
    private static InputKind? Classify(int virtualKey)
    {
        if (virtualKey is VK_RETURN)
        {
            return InputKind.Enter;
        }

        if (IsModifier(virtualKey))
        {
            return null;
        }

        // A modifier held down means this is a command, not typing. Which command is not recorded: the
        // schema's shortcut event carries a timestamp and nothing else.
        if (ModifierHeld())
        {
            return InputKind.Shortcut;
        }

        return IsPrintable(virtualKey) ? InputKind.PrintableKey : null;
    }

    private static bool IsModifier(int key) =>
        key is VK_SHIFT or VK_LSHIFT or VK_RSHIFT or VK_CONTROL or VK_LCONTROL or VK_RCONTROL
            or VK_MENU or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN or VK_CAPITAL;

    private static bool ModifierHeld() =>
        (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
        || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
        || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
        || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    /// <summary>
    /// Whether this key would put a character on screen. Decided by range rather than by translating the
    /// key, because translating it would mean knowing which character — and then having it.
    /// </summary>
    private static bool IsPrintable(int key) =>
        key is (>= 0x30 and <= 0x39)      // 0-9
            or (>= 0x41 and <= 0x5A)      // A-Z
            or (>= 0x60 and <= 0x69)      // numpad digits
            or (>= 0xBA and <= 0xC0)      // punctuation
            or (>= 0xDB and <= 0xDF)      // brackets, quote, backslash
            or VK_SPACE or VK_MULTIPLY or VK_ADD or VK_SUBTRACT or VK_DECIMAL or VK_DIVIDE or VK_OEM_102;

    // ---- pump, install, re-install ---------------------------------------------------------------------

    /// <summary>
    /// One run, on its own thread: a window for session and power notifications, the hooks, and the message
    /// loop the hooks are delivered through. Everything it makes it takes down again before it returns, so
    /// the next session's run starts from nothing.
    /// </summary>
    private void Pump(PumpRun run)
    {
        var threadId = GetCurrentThreadId();
        _pumpThreadId = threadId;

        // The thread's message queue exists before anything can post to it, so the stop's WM_QUIT is never lost.
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

        // A class of this run's own: the last session's was unregistered when its run ended, and a name
        // still taken would cost this run its window and with it every lock, unlock and resume re-install.
        var className = $"ScreenTailInput{Environment.ProcessId}.{Interlocked.Increment(ref _classSerial)}";
        var instance = GetModuleHandle(null);
        var registered = false;
        try
        {
            registered = RegisterMessageClass(className, instance);
            _window = registered ? CreateMessageWindow(className, instance) : IntPtr.Zero;
            if (_window != IntPtr.Zero)
            {
                // Lock, unlock and resume are when Windows is most likely to have dropped a hook.
                _ = WTSRegisterSessionNotification(_window, NOTIFY_FOR_THIS_SESSION);
            }

            Warm();
            if (!Install(out var error))
            {
                // Not ready: the start fails with this, and the caller counts it rather than saying installed.
                throw new Win32Exception(error);
            }

            // Registered before Ready, and run at once if the stop came first.
            using var wake = run.Stopping.Register(() => _ = PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero));
            run.Ready();

            while (!run.Stopping.IsCancellationRequested && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        finally
        {
            Uninstall();
            if (_window != IntPtr.Zero)
            {
                _ = WTSUnRegisterSessionNotification(_window);
                _ = DestroyWindow(_window);
                _window = IntPtr.Zero;
            }

            if (registered)
            {
                _ = UnregisterClass(className, instance);
            }

            _pumpThreadId = 0;
        }
    }

    private IntPtr OnWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        var sessionChanged = message == WM_WTSSESSION_CHANGE && (uint)wParam is WTS_SESSION_UNLOCK or WTS_SESSION_LOGON;
        var resumed = message == WM_POWERBROADCAST && (uint)wParam is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC;
        if (sessionChanged || resumed)
        {
            Reinstall();
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>
    /// Runs the callbacks' code once before the hooks go in, so the first real key press or click does not
    /// pay for compiling it. On the laptop on 2026-10-19 the first line of typing cost one callback
    /// 1,479 µs against ST-024's 1 ms budget, and every later line 116 µs at worst. Code below zero is the
    /// pass-through branch, which records nothing; the classification and the buffer write are run against
    /// a buffer of their own, so nothing reaches the session. Once per object: later sessions' runs find it
    /// compiled. The warm-up's own cost is not a callback, so the worst callback starts again from zero.
    /// </summary>
    private void Warm()
    {
        if (_warmed)
        {
            return;
        }

        _ = OnKeyboard(-1, IntPtr.Zero, IntPtr.Zero);
        _ = OnMouse(-1, IntPtr.Zero, IntPtr.Zero);

        var scratch = new InputRingBuffer(4);
        foreach (var key in (ReadOnlySpan<int>)[VK_RETURN, VK_SHIFT, 0x41])
        {
            if (Classify(key) is { } kind)
            {
                _ = scratch.Write(new InputSignal(kind, Stopwatch.GetTimestamp()));
            }
        }

        var block = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(block, 0);
            _ = scratch.Write(new InputSignal(InputKind.Click, Stopwatch.GetTimestamp(), Marshal.ReadInt32(block, 0), Marshal.ReadInt32(block, 4), MouseButtonKind.Left));
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }

        ResetWorstCallback();
        _warmed = true;
    }

    /// <summary>Takes the hooks out and puts them back. Called on lock/unlock and resume, and by tests.</summary>
    internal void Reinstall()
    {
        Uninstall();
        _ = Install(out _);
    }

    /// <summary>
    /// Puts both hooks in. Counts an install only when both went in; otherwise takes out whichever did and
    /// returns the Windows error, because one hook reported as two is worse than none reported honestly.
    /// </summary>
    private bool Install(out int error)
    {
        // idHook needs no module handle for a low-level hook in the installing process.
        error = 0;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseCallback, IntPtr.Zero, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            error = Marshal.GetLastPInvokeError();
        }

        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardCallback, IntPtr.Zero, 0);
        if (_keyboardHook == IntPtr.Zero && error == 0)
        {
            error = Marshal.GetLastPInvokeError();
        }

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            Uninstall();
            return false;
        }

        _ = Interlocked.Increment(ref _installs);
        return true;
    }

    private void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private bool RegisterMessageClass(string className, IntPtr instance)
    {
        var wndClass = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = _windowCallback,
            Instance = instance,
            ClassName = className,
        };

        return RegisterClassEx(ref wndClass) != 0;
    }

    /// <summary>A message-only window: it never draws, and exists to receive session and power notifications.</summary>
    private static IntPtr CreateMessageWindow(string className, IntPtr instance) =>
        CreateWindowEx(0, className, className, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const uint WTS_SESSION_UNLOCK = 0x8;
    private const uint WTS_SESSION_LOGON = 0x5;
    private const uint PBT_APMRESUMESUSPEND = 0x7;
    private const uint PBT_APMRESUMEAUTOMATIC = 0x12;
    private const int NOTIFY_FOR_THIS_SESSION = 0;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const int VK_RETURN = 0x0D;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_CAPITAL = 0x14;
    private const int VK_SPACE = 0x20;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_MULTIPLY = 0x6A;
    private const int VK_ADD = 0x6B;
    private const int VK_SUBTRACT = 0x6D;
    private const int VK_DECIMAL = 0x6E;
    private const int VK_DIVIDE = 0x6F;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_OEM_102 = 0xE2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProc WndProc;
        public int ExtraClassBytes;
        public int ExtraWindowBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);
}
