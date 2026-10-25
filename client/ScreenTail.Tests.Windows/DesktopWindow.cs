using System.Runtime.InteropServices;

namespace ScreenTail.Tests.Windows;

/// <summary>
/// A real top-level window on its own message-pumping thread, used by any test that needs something to
/// focus or to type into. One implementation rather than one per test file: the first copy of this worked
/// and the second didn't, for reasons that took a laptop run to find.
/// </summary>
internal sealed class DesktopWindow : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<IntPtr> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<Action> _posted = new();
    private volatile bool _closing;

    private DesktopWindow(string title, bool ordinary)
    {
        // The class is resolved here, on the test's thread, so a registration failure fails the test that
        // asked for the window. Thrown on the window's own thread it would take the test host down with it.
        var className = ordinary ? OrdinaryClass.Value : "STATIC";
        var instance = ordinary ? GetModuleHandle(null) : IntPtr.Zero;
        _thread = new Thread(() => Run(title, className, instance)) { IsBackground = true, Name = "test window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Handle = _created.Task.GetAwaiter().GetResult();
    }

    public IntPtr Handle { get; }

    /// <summary>A plain text box inside the window, once <see cref="AddFields"/> has run.</summary>
    public IntPtr PlainField { get; private set; }

    /// <summary>A text box that masks what is typed into it — a real one, with ES_PASSWORD (ST-040).</summary>
    public IntPtr PasswordField { get; private set; }

    public static DesktopWindow Create(string title) => new(title, ordinary: false);

    /// <summary>
    /// A window whose class leaves everything to DefWindowProc, as an application's main window does with
    /// its title. Needed by any test that listens for a rename: <see cref="Create"/> builds on the STATIC
    /// control class, which handles WM_SETTEXT itself and raises no <c>EVENT_OBJECT_NAMECHANGE</c>, so a
    /// hook never hears of its renames. A hosted run showed a hook of the test's own receiving none of
    /// fifty.
    /// </summary>
    public static DesktopWindow CreateOrdinary(string title) => new(title, ordinary: true);

    /// <summary>
    /// Adds the two edit controls the password-field tests need. They are created on the window's own
    /// thread, because a child window belongs to the thread that created it and focus follows that.
    /// </summary>
    public void AddFields()
    {
        var done = new ManualResetEventSlim();
        Post(() =>
        {
            PlainField = CreateWindowEx(0, "EDIT", string.Empty, WS_CHILD | WS_VISIBLE | WS_TABSTOP, 20, 20, 200, 24, Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            PasswordField = CreateWindowEx(0, "EDIT", string.Empty, WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_PASSWORD, 20, 60, 200, 24, Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Gives a child control keyboard focus. SetFocus only works on the thread that owns the window, so
    /// this runs on the window's thread rather than the test's — calling it from here would silently do
    /// nothing and leave the probe reading whatever had focus before.
    /// </summary>
    public void Focus(IntPtr control)
    {
        var done = new ManualResetEventSlim();
        Post(() =>
        {
            _ = SetFocus(control);
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(5));
    }

    private void Post(Action action)
    {
        lock (_posted)
        {
            _posted.Enqueue(action);
        }

        _ = PostMessage(Handle, WM_RUN_ACTION, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Brings the window to the front and waits for Windows to agree.
    ///
    /// Windows refuses the foreground to a process that has received no input, which is every unattended
    /// runner, so this attaches to the current foreground thread's input queue for the moment it takes to
    /// activate — no keystroke is injected to work around it. It retries, because the previous test's window
    /// may still be closing and leaving the desktop briefly without a foreground at all: activating once,
    /// immediately after creating the window, is what made the ST-024 tests skip on hardware that could
    /// perfectly well have run them.
    /// </summary>
    /// <summary>
    /// Takes the foreground or skips the test, saying what was in the way.
    ///
    /// Three runs were spent guessing at this from the outside — a locked screen, then a service in
    /// session 0 — while the capability probe insisted the machine was fine and another test in the same
    /// run took the foreground without trouble. A skip that reports only its own existence cannot settle
    /// that. These facts can.
    /// </summary>
    public void RequireForeground()
    {
        if (TakeForeground())
        {
            return;
        }

        var why = Why();
        Measurements.Record($"{NoDesktopMarker}: {why}");
        Assert.Skip($"Could not take the foreground. {why}");
    }

    /// <summary>
    /// What Windows says is in the way, for a run nobody is sitting in front of.
    ///
    /// The process and the window class, never the title. This line goes to public logs, job summaries
    /// and uploaded artifacts, and it used to quote whatever the laptop's foreground window was showing
    /// (2026-10-12 review, S-77). The class says what kind of thing is in the way, which is all a run
    /// needs to know, and INV-10 holds for the suite's own output as it does for the product's.
    /// </summary>
    public static string Why()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return "Nothing holds the foreground on this desktop. With no window to attach to, Windows' "
                + "foreground lock refuses a process that has had no input, which is every unattended "
                + "runner — a shell (explorer.exe) or any persistent window would give it one.";
        }

        var thread = GetWindowThreadProcessId(foreground, out var processId);
        var buffer = new char[256];
        var length = GetClassName(foreground, buffer, buffer.Length);
        var className = length > 0 ? new string(buffer, 0, length) : "(unknown)";

        string owner;
        try
        {
            owner = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            owner = "(gone)";
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"The foreground is held by {owner} (pid {processId}, thread {thread}), a window of class {className}, and it did not give it up within five seconds.");
    }

    /// <summary>
    /// The marker a run leaves when no test could get a window in front. The workflow fails on it, because
    /// the alternative is a green tick over a run that verified nothing on the hardware it exists for.
    /// </summary>
    public const string NoDesktopMarker = "NO-DESKTOP";

    /// <summary>
    /// Turns off the foreground lock for this user, once per run.
    ///
    /// Windows refuses SetForegroundWindow to a process that has received no input — which is every
    /// unattended runner — and refuses it silently, so the call returns and nothing happens. The
    /// diagnostic named the holder as the runner's own console (run.cmd), and minimising that turned out
    /// not to release anything: the obstacle is the lock, not the window.
    ///
    /// SPI_SETFOREGROUNDLOCKTIMEOUT is the documented way to lift it, and it is what UI automation uses.
    /// A per-user setting on a laptop kept for testing, where CI already screenshots the desktop; it is
    /// not something the product ever does.
    /// </summary>
    private static void UnlockForeground()
    {
        if (Interlocked.Exchange(ref _unlocked, 1) == 1)
        {
            return;
        }

        _ = SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);
    }

    private static int _unlocked;

    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr pvParam, uint winIni);

    public bool TakeForeground(TimeSpan? within = null)
    {
        UnlockForeground();
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));
        do
        {
            _ = ShowWindow(Handle, SW_SHOWNORMAL);
            _ = SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            var ours = GetCurrentThreadId();
            var theirs = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
            try
            {
                _ = BringWindowToTop(Handle);
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

            for (var i = 0; i < 20 && GetForegroundWindow() != Handle; i++)
            {
                Thread.Sleep(25);
            }
        }
        while (GetForegroundWindow() != Handle && DateTime.UtcNow < deadline);

        return GetForegroundWindow() == Handle;
    }

    public void Retitle(string title) => SetWindowText(Handle, title);

    /// <summary>Whether Windows says this window is in front right now.</summary>
    public bool IsForeground => GetForegroundWindow() == Handle;

    public void Resize(int width, int height) =>
        _ = SetWindowPos(Handle, HWND_TOP, 60, 60, width, height, SWP_SHOWWINDOW);

    /// <summary>Where the window is on the screen, as Windows reports it.</summary>
    public (int Left, int Top, int Right, int Bottom) Bounds
    {
        get
        {
            _ = GetWindowRect(Handle, out var rect);
            return (rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
    }

    /// <summary>Moves and resizes the window without activating it or changing its place in the z-order.</summary>
    public void MoveTo(int x, int y, int width, int height) =>
        _ = SetWindowPos(Handle, HWND_TOP, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

    /// <summary>
    /// Shows the window on top of everything, without taking the foreground: what a toast, a call
    /// notification or an always-on-top tool looks like to a capture of the window underneath.
    /// </summary>
    public void ShowTopmostAt(int x, int y, int width, int height) =>
        _ = SetWindowPos(Handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    /// <summary>
    /// Paints the client area in two halves, light first and dark second, so a capture of the window has
    /// something to be right or wrong about. Calling it again with the other layout repaints in place,
    /// which is what a window changing looks like to the scene sampler (ST-026).
    /// </summary>
    public void ShowContrast(ContrastLayout layout)
    {
        var done = new ManualResetEventSlim();
        Post(() =>
        {
            _ = GetClientRect(Handle, out var client);
            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;
            if (_light == IntPtr.Zero)
            {
                // Children of the two registered classes: each is erased with its class brush and nothing
                // else is ever drawn on it, so the pixels are exactly the brush.
                _light = CreateWindowEx(0, OrdinaryClass.Value, string.Empty, WS_CHILD | WS_VISIBLE, 0, 0, 1, 1, Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                _dark = CreateWindowEx(0, DarkClass.Value, string.Empty, WS_CHILD | WS_VISIBLE, 0, 0, 1, 1, Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            }

            if (layout == ContrastLayout.SideBySide)
            {
                _ = MoveWindow(_light, 0, 0, width / 2, height, true);
                _ = MoveWindow(_dark, width / 2, 0, width - (width / 2), height, true);
            }
            else
            {
                _ = MoveWindow(_light, 0, 0, width, height / 2, true);
                _ = MoveWindow(_dark, 0, height / 2, width, height - (height / 2), true);
            }

            _ = UpdateWindow(_light);
            _ = UpdateWindow(_dark);
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(5));
    }

    private IntPtr _light;
    private IntPtr _dark;

    /// <summary>
    /// Moves and resizes the window without touching the z-order or the foreground, which is what a
    /// window redrawing in the background does. Each call fires <c>EVENT_OBJECT_LOCATIONCHANGE</c> — one
    /// of the 32,776 event types the watcher used to subscribe to by accident (ST-048, weaknesses P0-5).
    /// </summary>
    public void Jiggle(int width, int height) =>
        _ = SetWindowPos(Handle, HWND_TOP, 60, 60, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>
    /// Shows the window without activating it, so it is on the desktop and behind whatever is in front.
    /// What a background application looks like to the foreground watcher.
    /// </summary>
    public void ShowBehind() => _ = ShowWindow(Handle, SW_SHOWNOACTIVATE);

    /// <summary>Fills the screen, so a capture measurement has a realistically large window to work on.</summary>
    public void Maximize() => _ = ShowWindow(Handle, SW_MAXIMIZE);

    public void Dispose()
    {
        _closing = true;
        _ = PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run(string title, string className, IntPtr instance)
    {
        // "STATIC" is a class Windows already registers, so there is none to register or clean up. The
        // ordinary class is registered once for the process and lives as long as it does. A window that
        // cannot be created is handed back to the constructor as an exception, so the test fails instead of
        // waiting on a handle that will never come.
        IntPtr window;
        try
        {
            window = CreateWindowEx(0, className, title, WS_OVERLAPPEDWINDOW, 60, 60, 420, 220, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _created.TrySetException(ex);
            return;
        }

        if (window == IntPtr.Zero)
        {
            _created.TrySetException(new InvalidOperationException($"CreateWindowEx for a \"{className}\" window failed with error {Marshal.GetLastPInvokeError()}."));
            return;
        }

        _created.TrySetResult(window);

        while (!_closing && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message == WM_RUN_ACTION)
            {
                Action? action;
                lock (_posted)
                {
                    action = _posted.Count > 0 ? _posted.Dequeue() : null;
                }

                action?.Invoke();
            }

            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }

        _ = DestroyWindow(window);
    }

    /// <summary>
    /// A window class whose procedure is DefWindowProcW itself, so no managed callback has to be kept alive
    /// and a rename takes the path every application window's rename takes.
    /// </summary>
    private static readonly Lazy<string> OrdinaryClass = new(() => Register("ScreenTail test window", COLOR_WINDOW + 1));

    /// <summary>The same class with a black background, for the dark half of <see cref="ShowContrast"/>.</summary>
    private static readonly Lazy<string> DarkClass = new(() => Register("ScreenTail dark test window", GetStockObject(BLACK_BRUSH)));

    private static string Register(string name, IntPtr background)
    {
        var user32 = NativeLibrary.Load("user32.dll");
        var definition = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = NativeLibrary.GetExport(user32, "DefWindowProcW"),
            Instance = GetModuleHandle(null),
            Background = background,
            ClassName = name,
        };

        if (RegisterClassEx(ref definition) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed with error {Marshal.GetLastPInvokeError()}.");
        }

        return name;
    }

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int COLOR_WINDOW = 5;
    private const int BLACK_BRUSH = 4;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_TABSTOP = 0x00010000;
    private const uint ES_PASSWORD = 0x0020;

    /// <summary>WM_APP + 1: a message Windows will never send, so it can only be one of ours.</summary>
    private const uint WM_RUN_ACTION = 0x8001;

    private const int SW_SHOWNORMAL = 1;
    private const int SW_MAXIMIZE = 3;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_CLOSE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx definition);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

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

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr hWnd, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] className, int count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

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

/// <summary>How <see cref="DesktopWindow.ShowContrast"/> splits the client area: light first, dark second.</summary>
internal enum ContrastLayout
{
    /// <summary>Light on the left, dark on the right.</summary>
    SideBySide,

    /// <summary>Light on top, dark below.</summary>
    Stacked,
}
