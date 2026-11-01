using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Lifecycle;

namespace ScreenTail.Service.Host;

/// <summary>
/// The capture service's one window, hidden, so that Windows can ask the service to close (ST-112).
///
/// The service is a Windows program with no window, and Windows asks a program to close through its
/// top-level windows: WM_QUERYENDSESSION and WM_ENDSESSION at sign-out and shutdown, the same pair with
/// ENDSESSION_CLOSEAPP from Restart Manager when an installer needs a file the service holds, and WM_CLOSE
/// from <c>taskkill</c> and installers' close steps. With nothing to receive them, Restart Manager could only
/// report the service as one it cannot close, sign-out ended it in the middle of whatever it was writing,
/// and every installer had to terminate it.
///
/// <b>Top-level and hidden, not message-only.</b> A message-only window (HWND_MESSAGE) receives no broadcast
/// and is not one of a process's windows to Restart Manager, so neither the end-session messages nor Restart
/// Manager's close would reach it. This one is never shown, has no title, and is a tool window, so it never
/// appears on the taskbar or in Alt+Tab.
///
/// <b>Its own thread</b>, a plain one that runs nothing but this window's messages, so nothing here waits on
/// capture and capture never waits on this. WM_ENDSESSION is held on this thread while the service stops,
/// which is the point: Windows may end the process as soon as that message returns. What each message
/// means is <see cref="CloseRequests"/>, in Core.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class SessionEndWindow : IDisposable
{
    /// <summary>The window class, so a test can find the running service's window. Not a title: it has none.</summary>
    internal const string ClassName = "ScreenTail.Service.SessionEnd";

    private readonly CloseRequests _requests;
    private readonly ILogger _logger;
    private readonly Thread _thread;

    // Held for the life of the object: a delegate handed to RegisterClassEx that the GC collected would be a
    // call into freed memory on the next message.
    private readonly WndProc _callback;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _window;
    private uint _threadId;
    private int _disposed;

    private SessionEndWindow(CloseRequests requests, ILogger logger)
    {
        _requests = requests;
        _logger = logger;
        _callback = OnMessage;
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "ScreenTail session end",
        };
    }

    /// <summary>The window's handle, for the Windows tests. Zero if it could not be made.</summary>
    internal IntPtr Handle => _window;

    /// <summary>
    /// Makes the window on its own thread and returns once it exists, or once Windows refused it. A service
    /// without it still runs; it is ended rather than asked, as before, and the log says so.
    /// </summary>
    public static SessionEndWindow Start(CloseRequests requests, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(logger);
        var window = new SessionEndWindow(requests, logger);
        window._thread.Start();

        // Bounded: making a window takes milliseconds, and a service that never started because this hung
        // would be worse than one Windows has to end.
        if (!window._ready.Task.Wait(TimeSpan.FromSeconds(5)) || !window._ready.Task.Result)
        {
            LogNoWindow(logger);
        }

        return window;
    }

    private void Pump()
    {
        var instance = GetModuleHandle(null);
        var registered = false;
        try
        {
            _threadId = GetCurrentThreadId();
            var wndClass = new WndClassEx
            {
                Size = Marshal.SizeOf<WndClassEx>(),
                WndProc = _callback,
                Instance = instance,
                ClassName = ClassName,
            };
            registered = RegisterClassEx(ref wndClass) != 0;
            _window = registered
                ? CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, string.Empty, WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero)
                : IntPtr.Zero;
            _ = _ready.TrySetResult(_window != IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                return;
            }

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        finally
        {
            // On this thread: a window can only be destroyed by the thread that made it.
            if (_window != IntPtr.Zero)
            {
                _ = DestroyWindow(_window);
            }

            if (registered)
            {
                _ = UnregisterClass(ClassName, instance);
            }

            _ = _ready.TrySetResult(false);
        }
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        // Nothing may escape into user32: an exception crossing a window procedure ends the process, which
        // is the unclean exit this window exists to prevent. The log calls are guarded too.
        try
        {
            switch (message)
            {
                case WM_QUERYENDSESSION:
                    var asked = CloseRequest.FromEndSession(lParam.ToInt64());
                    Log(() => LogAsked(_logger, asked.ForLog));
                    return CloseRequests.MayEnd(asked) ? new IntPtr(1) : IntPtr.Zero;

                case WM_ENDSESSION:
                    var request = CloseRequest.FromEndSession(lParam.ToInt64());
                    var outcome = _requests.Ended(wParam != IntPtr.Zero, request);
                    Log(() => LogEnded(_logger, request.ForLog, outcome));
                    return IntPtr.Zero;

                case WM_CLOSE:
                    // Not DefWindowProc, which would destroy the window and leave the service running.
                    Log(() => LogCloseMessage(_logger));
                    _requests.Close();
                    return IntPtr.Zero;

                default:
                    return DefWindowProc(window, message, wParam, lParam);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var error = ex.GetType().Name;
            Log(() => LogFailed(_logger, message, error));
            return message == WM_QUERYENDSESSION ? new IntPtr(1) : IntPtr.Zero;
        }
    }

    private static void Log(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Standard error closed under us, at the end of a session: the stop goes on without the line.
        }
    }

    /// <summary>Ends the window's thread, bounded. Called once the service has stopped, after any WM_ENDSESSION wait has seen it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_threadId != 0)
        {
            _ = PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        // A WM_ENDSESSION still being answered finishes first: its wait ends when the service has stopped,
        // which it has by now. Bounded either way; the thread is a background one and cannot hold the process.
        _ = _thread.Join(TimeSpan.FromSeconds(2));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The capture service's session-end window could not be made; Windows will end the service rather than ask it to close")]
    private static partial void LogNoWindow(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows asked whether the capture service may close ({Reason}): yes")]
    private static partial void LogAsked(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows ended the session for the capture service ({Reason}): {Outcome}")]
    private static partial void LogEnded(ILogger logger, string reason, CloseOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "The capture service was asked to close (close-message)")]
    private static partial void LogCloseMessage(ILogger logger);

    // The message number and the error's type only (INV-10).
    [LoggerMessage(Level = LogLevel.Warning, Message = "The session-end window hit {Error} answering message 0x{Message:X4}")]
    private static partial void LogFailed(ILogger logger, uint message, string error);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

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
