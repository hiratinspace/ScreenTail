using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Detection;

namespace ScreenTail.Service.Detection;

/// <summary>
/// Watches the foreground window with <c>EVENT_SYSTEM_FOREGROUND</c> (ST-022), falling back to polling when
/// the hook can't be installed.
///
/// A WinEvent hook only delivers to a thread that pumps messages, so this owns one: a dedicated thread that
/// installs the hook and runs a message loop until asked to stop. The alternative — hooking from a thread
/// pool thread — silently delivers nothing, which is the kind of failure that looks like "capture is quiet
/// today" rather than an error.
///
/// The same thread also runs a slow timer. Titles change without a foreground event (a browser tab loading,
/// a document being saved), and the technician moving between tabs matters as much as moving between apps.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsForegroundWatcher : IForegroundWatcher
{
    private readonly TimeProvider _time;
    private readonly ForegroundChangeFilter _filter;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _stopping = new();
    private readonly WinEventProc _callback;
    private Thread? _pump;
    private uint _pumpThreadId;
    private volatile bool _hookInstalled;
    private ForegroundWindowInfo _current;

    public WindowsForegroundWatcher(TimeProvider? time = null, TimeSpan? pollInterval = null)
    {
        _time = time ?? TimeProvider.System;
        _filter = new ForegroundChangeFilter(_time);
        // Slow enough to stay well inside the 0.5% idle-CPU budget, fast enough that a tab change lands
        // before the technician has moved on.
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _callback = OnWinEvent;
        _current = ForegroundWindowInfo.None(_time.GetUtcNow());
    }

    public event Action<ForegroundWindowInfo>? Changed;

    public ForegroundWindowInfo Current => _current;

    /// <summary>False when the hook couldn't be installed and polling is carrying the load.</summary>
    public bool UsingHook => _hookInstalled;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_pump is not null)
        {
            return Task.CompletedTask;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pump = new Thread(() => Pump(ready))
        {
            IsBackground = true,
            Name = "ScreenTail foreground watcher",
        };
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

    private void Pump(TaskCompletionSource ready)
    {
        var hook = IntPtr.Zero;
        var timer = IntPtr.Zero;
        try
        {
            _pumpThreadId = GetCurrentThreadId();
            hook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero,
                _callback,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            _hookInstalled = hook != IntPtr.Zero;

            // Report where we start, so a session that begins mid-task knows what was on screen.
            Publish(_filter.Offer(Read(GetForegroundWindow())));

            timer = SetTimer(IntPtr.Zero, IntPtr.Zero, (uint)_pollInterval.TotalMilliseconds, IntPtr.Zero);
            ready.TrySetResult();

            while (!_stopping.IsCancellationRequested && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WM_TIMER)
                {
                    // Catches title changes and anything the hook missed; also the whole mechanism when the
                    // hook couldn't be installed.
                    Publish(_filter.Flush(Read(GetForegroundWindow())));
                }

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
            if (timer != IntPtr.Zero)
            {
                _ = KillTimer(IntPtr.Zero, timer);
            }

            if (hook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(hook);
            }

            ready.TrySetResult();
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (window == IntPtr.Zero || _stopping.IsCancellationRequested)
        {
            return;
        }

        // A name change matters only for the window already in front; every other window renaming itself is
        // noise, and there is a lot of it.
        if (eventType == EVENT_OBJECT_NAMECHANGE && (objectId != OBJID_WINDOW || window != GetForegroundWindow()))
        {
            return;
        }

        Publish(_filter.Offer(Read(window)));
    }

    private void Publish(ForegroundWindowInfo? info)
    {
        if (info is null)
        {
            return;
        }

        _current = info;
        Changed?.Invoke(info);
    }

    /// <summary>Everything Windows will say about a window, with each failure degrading rather than throwing.</summary>
    private ForegroundWindowInfo Read(IntPtr window)
    {
        var at = _time.GetUtcNow();
        if (window == IntPtr.Zero)
        {
            return ForegroundWindowInfo.None(at);
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        var title = ReadTitle(window);
        var className = ReadClassName(window);
        var (processName, elevated) = ReadProcess(processId);

        return new ForegroundWindowInfo(
            window,
            (int)processId,
            processName,
            title,
            className,
            BrowserTitles.ActiveTab(processName, title),
            elevated,
            at);
    }

    private static string ReadTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        var copied = GetWindowText(window, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    private static string ReadClassName(IntPtr window)
    {
        var buffer = new char[256];
        var copied = GetClassName(window, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    /// <summary>
    /// A process we can't open is an elevated one, and that's the answer rather than an error: Spec §5 S2
    /// has the technician told "elevated window — screen not captured" instead of capture going quiet.
    /// </summary>
    private static (string? Name, bool Elevated) ReadProcess(uint processId)
    {
        if (processId == 0)
        {
            return (null, false);
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
        {
            return (NameWithoutOpening(processId), true);
        }

        try
        {
            var capacity = 1024;
            var buffer = new char[capacity];
            if (QueryFullProcessImageName(handle, 0, buffer, ref capacity))
            {
                return (Path.GetFileNameWithoutExtension(new string(buffer, 0, capacity)), false);
            }

            return (NameWithoutOpening(processId), false);
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>The name of a process we couldn't open. Often available anyway, and worth having in the note.</summary>
    private static string? NameWithoutOpening(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_TIMER = 0x0113;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

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
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);
}
