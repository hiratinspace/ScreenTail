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
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsInputHooks : IAsyncDisposable
{
    private readonly InputRingBuffer _buffer;
    private readonly HookProc _mouseCallback;
    private readonly HookProc _keyboardCallback;
    private readonly WndProc _windowCallback;
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _pump;
    private uint _pumpThreadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private IntPtr _window;
    private long _installs;

    public WindowsInputHooks(InputRingBuffer? buffer = null)
    {
        _buffer = buffer ?? new InputRingBuffer();

        // Held in fields for the life of the object: a delegate passed to SetWindowsHookEx that the GC
        // collects leaves Windows calling into freed memory.
        _mouseCallback = OnMouse;
        _keyboardCallback = OnKeyboard;
        _windowCallback = OnWindowMessage;
    }

    public InputRingBuffer Buffer => _buffer;

    /// <summary>How many times the hooks have been installed. Goes up on every re-install after a lock or a resume.</summary>
    public long Installs => Interlocked.Read(ref _installs);

    public bool Installed => _mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero;

    internal uint PumpThreadId => _pumpThreadId;

    /// <summary>Converts a hook timestamp into milliseconds since <paramref name="sessionStart"/>.</summary>
    public static long ToSessionMs(long timestamp, long sessionStart) =>
        (long)Stopwatch.GetElapsedTime(sessionStart, timestamp).TotalMilliseconds;

    public static TimeSpan Elapsed(long from, long to) => Stopwatch.GetElapsedTime(from, to);

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_pump is not null)
        {
            return Task.CompletedTask;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pump = new Thread(() => Pump(ready)) { IsBackground = true, Name = "ScreenTail input hooks" };
        _pump.SetApartmentState(ApartmentState.STA);
        _pump.Start();
        return ready.Task.WaitAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_pump is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_pumpThreadId != 0)
        {
            _ = PostThreadMessage(_pumpThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        var pump = _pump;
        _pump = null;
        await Task.Run(() => pump.Join(TimeSpan.FromSeconds(5)), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    // ---- the hot path ----------------------------------------------------------------------------------

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
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

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
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

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
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

    private void Pump(TaskCompletionSource ready)
    {
        try
        {
            _pumpThreadId = GetCurrentThreadId();
            _window = CreateMessageWindow();
            if (_window != IntPtr.Zero)
            {
                // Lock, unlock and resume are when Windows is most likely to have dropped a hook.
                _ = WTSRegisterSessionNotification(_window, NOTIFY_FOR_THIS_SESSION);
            }

            Install();
            ready.TrySetResult();

            while (!_stopping.IsCancellationRequested && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
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

            ready.TrySetResult();
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

    /// <summary>Takes the hooks out and puts them back. Called on lock/unlock and resume, and by tests.</summary>
    internal void Reinstall()
    {
        Uninstall();
        Install();
    }

    private void Install()
    {
        // idHook needs no module handle for a low-level hook in the installing process.
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseCallback, IntPtr.Zero, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardCallback, IntPtr.Zero, 0);
        Interlocked.Increment(ref _installs);
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

    /// <summary>A message-only window: it never draws, and exists to receive session and power notifications.</summary>
    private IntPtr CreateMessageWindow()
    {
        var className = $"ScreenTailInput{Environment.ProcessId}";
        var wndClass = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = _windowCallback,
            Instance = GetModuleHandle(null),
            ClassName = className,
        };

        return RegisterClassEx(ref wndClass) == 0
            ? IntPtr.Zero
            : CreateWindowEx(0, className, className, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wndClass.Instance, IntPtr.Zero);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const uint WM_QUIT = 0x0012;
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
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);
}
