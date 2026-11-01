using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenTail.Service.Privacy;

/// <summary>
/// Says when keyboard focus moves, so the password-field guard knows to look again (ST-040).
///
/// A WinEvent hook only delivers to a thread that pumps messages, so this owns one — the same shape as
/// ST-022's foreground watcher, and deliberately not the same thread. That watcher's hook spans
/// <c>EVENT_SYSTEM_FOREGROUND</c> to <c>EVENT_OBJECT_NAMECHANGE</c>, a range that does include
/// <c>EVENT_OBJECT_FOCUS</c>, so this could have ridden on it. It does not, because that would put a second
/// consumer on a thread whose idle cost is measured against a budget in ST-031 and whose behaviour is
/// pinned by ST-022's acceptance tests. A pump with no timer costs nothing while it waits in GetMessage.
///
/// The callback does nothing but raise the event, which does nothing but release a semaphore. Asking UI
/// Automation what has focus is a cross-process call into the application that just took focus, and a
/// message pump is the last place to make one.
///
/// <b>Hooked only while someone listens (ST-031).</b> The hook covers every control in every application,
/// so with no session it woke this thread, and the guard behind it, for each focus change on the machine to
/// decide nothing. <see cref="Listen"/> takes the hook out and puts it back on this thread, and the service
/// follows <see cref="Core.Privacy.FocusInterest"/>: hooked while a session is under way or a hold is kept.
/// A new watcher listens, so one constructed on its own (a test, a tool) behaves as it always did.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFocusWatcher : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly WinEventProc _callback;
    private Thread? _pump;
    private uint _pumpThreadId;
    private volatile bool _hookInstalled;
    private volatile bool _wanted = true;
    private volatile bool _hooked;

    public WindowsFocusWatcher() => _callback = OnWinEvent;

    /// <summary>Keyboard focus moved somewhere. Raised on the pump thread, so handlers must not block.</summary>
    public event Action? FocusMoved;

    /// <summary>False when the hook could not be installed and only the guard's backstop poll is running.</summary>
    public bool UsingHook => _hookInstalled;

    /// <summary>OS id of the pump thread, so a test can count what this watcher costs (ST-031).</summary>
    internal uint PumpThreadId => _pumpThreadId;

    /// <summary>Whether the hook is in right now, as opposed to <see cref="UsingHook"/>, which says it can be.</summary>
    internal bool Hooked => _hooked;

    /// <summary>
    /// Puts the hook in (true) or takes it out (false). Safe from any thread and never blocks: the change is
    /// posted to the pump, because a WinEvent hook belongs to the thread that set it. Before the pump runs,
    /// the last answer given is the one it starts with.
    /// </summary>
    public void Listen(bool wanted)
    {
        _wanted = wanted;
        var pump = _pumpThreadId;
        if (pump != 0)
        {
            _ = PostThreadMessage(pump, WM_LISTEN, IntPtr.Zero, IntPtr.Zero);
        }
    }

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
            Name = "ScreenTail focus watcher",
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
        try
        {
            // The queue exists before the id is published, so a Listen posted the moment StartAsync returns
            // is never refused for want of one.
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
            _pumpThreadId = GetCurrentThreadId();

            // Installed once to learn whether it can be, whatever the first answer, so UsingHook and the
            // service's "event hook" log line mean what they always meant.
            hook = Install();
            _hookInstalled = hook != IntPtr.Zero;
            hook = Follow(hook);
            ready.TrySetResult();

            while (!_stopping.IsCancellationRequested && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WM_LISTEN)
                {
                    hook = Follow(hook);
                    continue;
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
            if (hook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(hook);
            }

            _hooked = false;
            ready.TrySetResult();
        }
    }

    /// <summary>One event, not a range. Focus changes are frequent enough on their own.</summary>
    private IntPtr Install() =>
        SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);

    /// <summary>On the pump: brings the hook into line with the latest answer, and returns it.</summary>
    private IntPtr Follow(IntPtr hook)
    {
        if (_wanted && hook == IntPtr.Zero)
        {
            hook = Install();
        }
        else if (!_wanted && hook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(hook);
            hook = IntPtr.Zero;
        }

        _hooked = hook != IntPtr.Zero;
        return hook;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        // Deliberately not filtered by window or object id. A WPF PasswordBox raises focus against its
        // parent window with a child id, and a browser field raises it against nothing recognisable — the
        // guard asks UI Automation what has focus rather than trying to read it out of these arguments.
        FocusMoved?.Invoke();
    }

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WM_QUIT = 0x0012;

    /// <summary>A thread message of this watcher's own: bring the hook into line with <see cref="_wanted"/>.</summary>
    private const uint WM_LISTEN = 0x8000 + 0x31;
    private const uint PM_NOREMOVE = 0x0000;

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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);
}
