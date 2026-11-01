using System.Runtime.InteropServices;

namespace ScreenTail.Tests.Windows.Detection;

/// <summary>
/// A name-change hook of the test's own, installed the way the foreground watcher installs its own (out of
/// context, every process, on a thread that pumps messages), which keeps every event it is given.
///
/// It is the control for the rename filter. Silence from the watcher means something only when the same
/// renames are shown to have been delivered to a hook like the watcher's. They were not, at first: the
/// test's windows were STATIC controls, which handle WM_SETTEXT themselves and raise no
/// <c>EVENT_OBJECT_NAMECHANGE</c>, so the watcher's silence was guaranteed and proved nothing. This counts
/// the deliveries, so a window that stops announcing its renames fails the test instead of passing it.
/// </summary>
internal sealed class NameChangeWitness : IDisposable
{
    /// <summary>The object id a window's own title change is raised with.</summary>
    public const int WindowObject = 0;

    private readonly Thread _thread;
    private readonly WinEventProc _callback;
    private readonly TaskCompletionSource<bool> _installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(IntPtr Window, int ObjectId)> _seen = [];
    private readonly HashSet<string> _titles = new(StringComparer.Ordinal);
    private IntPtr _titlesOf;
    private uint _threadId;

    private NameChangeWitness()
    {
        _callback = OnWinEvent;
        _thread = new Thread(Run) { IsBackground = true, Name = "name change witness" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Starts listening, and fails the test when Windows will not install the hook.</summary>
    public static NameChangeWitness Start()
    {
        var witness = new NameChangeWitness();
        if (!witness._installed.Task.Wait(TimeSpan.FromSeconds(5)) || !witness._installed.Task.Result)
        {
            witness.Dispose();
            Assert.Fail("The test's own name-change hook could not be installed.");
        }

        return witness;
    }

    /// <summary>The OS id of the thread the hook delivers to, so a measurement can leave it out.</summary>
    public uint ThreadId => _threadId;

    /// <summary>
    /// Reads the window's own title at every rename it raises from now on, so a test can say which titles
    /// the window actually announced. Kept in memory for the test's assertions and never written anywhere.
    /// One window at a time: reading a title is a message to the window's thread.
    /// </summary>
    public void TrackTitles(IntPtr window)
    {
        lock (_seen)
        {
            _titlesOf = window;
            _titles.Clear();
        }
    }

    /// <summary>The distinct titles the tracked window announced with a rename that start with a prefix.</summary>
    public IReadOnlyList<string> TitlesStartingWith(string prefix)
    {
        lock (_seen)
        {
            return [.. _titles.Where(title => title.StartsWith(prefix, StringComparison.Ordinal))];
        }
    }

    /// <summary>How many name changes this window has raised with the given object id.</summary>
    public int Count(IntPtr window, int objectId)
    {
        lock (_seen)
        {
            return _seen.Count(e => e.Window == window && e.ObjectId == objectId);
        }
    }

    /// <summary>Every object id this window has raised a name change with, for a message.</summary>
    public string ObjectIds(IntPtr window)
    {
        lock (_seen)
        {
            var ids = _seen.Where(e => e.Window == window).Select(e => e.ObjectId).Distinct().ToArray();
            return ids.Length == 0 ? "none" : string.Join(", ", ids);
        }
    }

    /// <summary>Waits until this window has raised a name change with the given object id.</summary>
    public async Task<bool> WaitForAsync(IntPtr window, int objectId, TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (Count(window, objectId) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct);
        }

        return Count(window, objectId) > 0;
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            _ = PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var hook = SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        _installed.TrySetResult(hook != IntPtr.Zero);
        if (hook == IntPtr.Zero)
        {
            return;
        }

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        finally
        {
            _ = UnhookWinEvent(hook);
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        IntPtr tracked;
        lock (_seen)
        {
            _seen.Add((window, objectId));
            tracked = _titlesOf;
        }

        if (tracked != IntPtr.Zero && window == tracked && objectId == WindowObject)
        {
            var buffer = new char[512];
            var length = GetWindowText(window, buffer, buffer.Length);
            if (length > 0)
            {
                lock (_seen)
                {
                    _ = _titles.Add(new string(buffer, 0, length));
                }
            }
        }
    }

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WM_QUIT = 0x0012;

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

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
}
