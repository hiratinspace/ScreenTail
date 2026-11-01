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
///
/// Title changes come from <c>EVENT_OBJECT_NAMECHANGE</c>, hooked for the thread that owns the window in
/// front and moved with the foreground (<see cref="ForegroundNameHook"/>, ST-031). Hooked for every thread,
/// every rename on the desktop woke this thread to be thrown away, and the idle service's cost followed
/// whatever else was open.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsForegroundWatcher : IForegroundWatcher
{
    private readonly TimeProvider _time;
    private readonly ForegroundChangeFilter _filter;
    private readonly TimeSpan? _pollInterval;
    private readonly CancellationTokenSource _stopping = new();
    private readonly WinEventProc _callback;
    private Thread? _pump;
    private uint _pumpThreadId;
    private ForegroundNameHook? _names;
    private IntPtr _settleTimer;
    private (uint ProcessId, string? Name, bool Elevated) _lastProcess;
    private volatile bool _hookInstalled;
    private ForegroundWindowInfo _current;

    public WindowsForegroundWatcher(TimeProvider? time = null, TimeSpan? pollInterval = null)
    {
        _time = time ?? TimeProvider.System;
        _filter = new ForegroundChangeFilter(_time);
        _pollInterval = pollInterval;
        _callback = OnWinEvent;
        _current = ForegroundWindowInfo.None(_time.GetUtcNow());
    }

    public event Action<ForegroundWindowInfo>? Changed;

    public ForegroundWindowInfo Current => _current;

    /// <summary>Whether a window handle still names a window (ST-113: a retry skips one that closed).</summary>
    public static bool Exists(nint window) => window != 0 && IsWindow(window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    /// <summary>False when the hook couldn't be installed and polling is carrying the load.</summary>
    public bool UsingHook => _hookInstalled;

    /// <summary>OS id of the pump thread, so a test can measure what this watcher actually costs.</summary>
    internal uint PumpThreadId => _pumpThreadId;

    /// <summary>The thread the name-change hook listens to: the foreground window's, or 0 for every thread or none.</summary>
    internal uint NameHookThread => _names is { Installed: true } names ? names.Thread : 0;

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
        var foregroundHook = IntPtr.Zero;
        var timer = IntPtr.Zero;
        try
        {
            _pumpThreadId = GetCurrentThreadId();

            // Two hooks, each asking for one event. SetWinEventHook's first two arguments are eventMin and
            // eventMax and the range is inclusive, so passing the two constants below to a single call
            // subscribed to all 32,778 event types between them — every window move, focus change, menu
            // and console caret on the desktop, each delivered with its own window handle and published as
            // the foreground window (ST-048, weaknesses P0-5).
            //
            // The foreground hook hears every thread: the foreground moving is rare and is the news. The
            // name hook hears one thread, the foreground window's, and follows it (ST-031).
            foregroundHook = Hook(ForegroundEvents.SystemForeground, ForegroundNameHook.EveryThread);
            _names = new ForegroundNameHook(
                thread => Hook(ForegroundEvents.ObjectNameChange, thread),
                hook => _ = UnhookWinEvent(hook));
            var front = GetForegroundWindow();
            var frontThread = ThreadOf(front);
            _ = _names.Follow(frontThread);
            _hookInstalled = foregroundHook != IntPtr.Zero && (_names.Installed || frontThread == 0);

            // Report where we start, so a session that begins mid-task knows what was on screen.
            Publish(_filter.Offer(Read(front)));

            // With the hooks installed, EVENT_OBJECT_NAMECHANGE already delivers title changes, so the timer is
            // only a safety net and can be slow. Without it, the timer is the whole mechanism and has to be
            // quick. Polling at 250 ms regardless cost 0.520% of a core against a 0.5% budget.
            var interval = _pollInterval ?? (_hookInstalled ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(250));
            timer = SetTimer(IntPtr.Zero, IntPtr.Zero, (uint)interval.TotalMilliseconds, IntPtr.Zero);
            ready.TrySetResult();

            while (!_stopping.IsCancellationRequested && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WM_TIMER && _settleTimer != IntPtr.Zero && message.WParam == _settleTimer)
                {
                    // The one-shot settle timer: a retitle the filter held is offered again now, rather
                    // than waiting for the poll.
                    _ = KillTimer(IntPtr.Zero, _settleTimer);
                    _settleTimer = IntPtr.Zero;
                    Publish(_filter.Flush(Read(GetForegroundWindow())));
                }
                else if (message.Message == WM_TIMER)
                {
                    // Catches title changes and anything the hook missed; also the whole mechanism when the
                    // hook couldn't be installed. A foreground change the hook missed moves the name hook too.
                    var window = GetForegroundWindow();
                    _ = _names.Follow(ThreadOf(window));
                    Publish(_filter.Flush(Read(window)));
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
            if (_settleTimer != IntPtr.Zero)
            {
                _ = KillTimer(IntPtr.Zero, _settleTimer);
                _settleTimer = IntPtr.Zero;
            }

            if (timer != IntPtr.Zero)
            {
                _ = KillTimer(IntPtr.Zero, timer);
            }

            _names?.Dispose();

            if (foregroundHook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(foregroundHook);
            }

            ready.TrySetResult();
        }
    }

    /// <summary>One hook for one event type, out of context, for one thread or (0) every thread on the desktop.</summary>
    private IntPtr Hook(uint eventType, uint thread) =>
        SetWinEventHook(eventType, eventType, IntPtr.Zero, _callback, 0, thread, WINEVENT_OUTOFCONTEXT);

    /// <summary>The thread that owns a window, or 0 for none.</summary>
    private static uint ThreadOf(IntPtr window) => window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, out _);

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (window == IntPtr.Zero || _stopping.IsCancellationRequested)
        {
            return;
        }

        // Belt and braces over the two hooks above: only these event types may say what is in front, so a
        // future change to the subscription cannot quietly start feeding scope decisions the wrong window.
        if (!ForegroundEvents.Interesting(eventType))
        {
            return;
        }

        // A name change matters only for the window already in front; every other window renaming itself is
        // noise, and there is a lot of it.
        if (eventType == ForegroundEvents.ObjectNameChange && (objectId != OBJID_WINDOW || window != GetForegroundWindow()))
        {
            return;
        }

        // The name hook moves to the new foreground window's thread before the window is read, so a
        // retitle straight after the switch reaches the new hook instead of falling between the two.
        if (eventType == ForegroundEvents.SystemForeground)
        {
            _ = _names?.Follow(ThreadOf(window));
        }

        var observed = Read(window);
        var offered = _filter.Offer(observed);
        if (offered is null && !observed.SameAs(_filter.Reported))
        {
            // Held: the same window, retitled inside the settle time. Only the poll used to offer it again,
            // two seconds with the hook, and for those two seconds the old title's scope decision stood
            // for a window whose title may now name something excluded. A one-shot timer on this thread
            // offers it once the settle time is up. Armed once per hold, so a title rewriting itself many
            // times a second still costs one timer, and nothing runs when nothing is held.
            ArmSettleTimer();
        }

        Publish(offered);
    }

    private void ArmSettleTimer()
    {
        if (_settleTimer != IntPtr.Zero)
        {
            return;
        }

        // A thread timer: with no window, SetTimer ignores the id passed and returns its own, which is the
        // WM_TIMER wParam the pump compares against.
        var settle = (uint)Math.Max(1, Math.Ceiling(_filter.TitleSettle.TotalMilliseconds));
        _settleTimer = SetTimer(IntPtr.Zero, IntPtr.Zero, settle, IntPtr.Zero);
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

        // Opening the process and resolving its image name is by far the most expensive part of this, and
        // the poll asks the same question four times a second about the same window. Ask once per process.
        if (_lastProcess.ProcessId != processId)
        {
            var resolved = ReadProcess(processId);
            _lastProcess = (processId, resolved.Name, resolved.Elevated);
        }

        var (_, processName, elevated) = _lastProcess;

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
    /// The process's name, and whether its windows are elevated (INV-6).
    ///
    /// Elevation is read from the process's token: its mandatory integrity level, judged by
    /// <see cref="ProcessElevation"/>. It used to be inferred from OpenProcess failing, and a process the
    /// technician elevated runs as the same user and opens without complaint, so regedit, mmc or
    /// PowerShell run as administrator were photographed as admin tools. A process that cannot be opened,
    /// or whose token cannot be read, is still treated as elevated: blind is the closed answer, and Spec §5
    /// S2 has the technician told "elevated window, screen not captured" rather than capture going quiet.
    /// </summary>
    internal static (string? Name, bool Elevated) ReadProcess(uint processId)
    {
        if (processId == 0)
        {
            return (null, false);
        }

        var isOwn = processId == (uint)Environment.ProcessId;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
        {
            return (NameWithoutOpening(processId), ProcessElevation.IsElevated(null, OwnIntegrity.Value, isOwn));
        }

        try
        {
            var elevated = ProcessElevation.IsElevated(IntegrityOf(handle), OwnIntegrity.Value, isOwn);
            var capacity = 1024;
            var buffer = new char[capacity];
            if (QueryFullProcessImageName(handle, 0, buffer, ref capacity))
            {
                return (ProcessNameFor(new string(buffer, 0, capacity)), elevated);
            }

            return (NameWithoutOpening(processId), elevated);
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>The name a process is reported under, from the path of its image. WSLg's msrdc is "wslg".</summary>
    internal static string ProcessNameFor(string imagePath) => ProcessImage.NameFor(imagePath);

    /// <summary>This process's own level, read once: it does not change while the process runs.</summary>
    private static readonly Lazy<int?> OwnIntegrity = new(() => IntegrityOf(GetCurrentProcess()));

    /// <summary>
    /// The mandatory integrity level of a process's token, or null when it cannot be read. TOKEN_QUERY on a
    /// process opened for limited information is enough, and is granted for a same-user elevated process.
    /// </summary>
    private static int? IntegrityOf(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
        {
            return null;
        }

        try
        {
            _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 1024)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                {
                    return null;
                }

                // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first field is the SID. The
                // level is the SID's last sub-authority.
                var sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero || !IsValidSid(sid))
                {
                    return null;
                }

                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(token);
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

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Deliberately no WINEVENT_SKIPOWNPROCESS. The capture service owns no windows, so it filtered nothing
    // in production — but it did hide the test's own window, which sent the acceptance test down the polling
    // path and reported 259 ms against a 100 ms budget. Seeing our own UI come to the front is wanted
    // anyway: ST-040 needs to know when the technician is in Review rather than in the remote session.
    private const int OBJID_WINDOW = 0;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_TIMER = 0x0113;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

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
