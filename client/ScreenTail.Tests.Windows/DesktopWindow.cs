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
    private volatile bool _closing;

    private DesktopWindow(string title)
    {
        _thread = new Thread(() => Run(title)) { IsBackground = true, Name = "test window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Handle = _created.Task.GetAwaiter().GetResult();
    }

    public IntPtr Handle { get; }

    public static DesktopWindow Create(string title) => new(title);

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
    public bool TakeForeground(TimeSpan? within = null)
    {
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

    public void Dispose()
    {
        _closing = true;
        _ = PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run(string title)
    {
        // "STATIC" is a class Windows already registers, so there is none to register or clean up.
        var window = CreateWindowEx(0, "STATIC", title, WS_OVERLAPPEDWINDOW, 60, 60, 420, 220, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _created.SetResult(window);

        while (!_closing && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }

        _ = DestroyWindow(window);
    }

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_SHOWNORMAL = 1;
    private const uint WM_CLOSE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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
