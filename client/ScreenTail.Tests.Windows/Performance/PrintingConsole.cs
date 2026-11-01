using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>
/// A console window of its own process printing a line every 20 ms, as the runner's console does while a
/// job writes its log, brought to the front (ST-031).
///
/// Paced rather than flat out on purpose. A console printing as fast as it can keeps a core or two busy
/// drawing, and on a four-core runner that alone multiplied this process's .NET pool threads' context
/// switches by five (the probe on 2026-11-01: 24 a second at idle, 127 with the console printing), every
/// one of them a preemption of a thread that was already running rather than a wakeup anything asked for.
/// That measures the machine, not the service.
///
/// The window is found by its class, never its title: a console window opened by Windows' default
/// terminal (<c>CASCADIA_HOSTING_WINDOW_CLASS</c>) or by the classic host (<c>ConsoleWindowClass</c>).
/// </summary>
internal sealed class PrintingConsole : IDisposable
{
    private readonly Process _process;
    private readonly HashSet<IntPtr> _before;

    private PrintingConsole(Process process, HashSet<IntPtr> before, IntPtr window)
    {
        _process = process;
        _before = before;
        Window = window;
    }

    public IntPtr Window { get; }

    /// <summary>The window class, which says which console host opened it.</summary>
    public string ClassName => ClassOf(Window);

    public bool IsForeground => GetForegroundWindow() == Window;

    /// <summary>Starts the console and waits for its window, failing the test when none appears.</summary>
    public static async Task<PrintingConsole> StartAsync(CancellationToken ct)
    {
        var before = Consoles();
        var process = Process.Start(new ProcessStartInfo(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"& { $i = 0; while ($true) { $i++; 'ScreenTail console line ' + $i; Start-Sleep -Milliseconds 20 } }\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        }) ?? throw new InvalidOperationException("powershell.exe did not start.");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var window = Consoles().FirstOrDefault(candidate => !before.Contains(candidate));
            if (window != IntPtr.Zero)
            {
                return new PrintingConsole(process, before, window);
            }

            await Task.Delay(100, ct);
        }

        Kill(process);
        process.Dispose();
        Assert.Fail("The printing console's window did not appear within 15 seconds.");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Brings the console to the front, the way <see cref="DesktopWindow.TakeForeground"/> does for a window of the test's own.</summary>
    public bool TakeForeground()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (GetForegroundWindow() != Window && DateTime.UtcNow < deadline)
        {
            var ours = GetCurrentThreadId();
            var theirs = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
            try
            {
                _ = ShowWindow(Window, SW_RESTORE);
                _ = BringWindowToTop(Window);
                _ = SetForegroundWindow(Window);
            }
            finally
            {
                if (attached)
                {
                    _ = AttachThreadInput(ours, theirs, false);
                }
            }

            Thread.Sleep(100);
        }

        return GetForegroundWindow() == Window;
    }

    public void Dispose()
    {
        Kill(_process);
        _process.Dispose();

        // A default-terminal window can outlive the shell it was opened for; ask any left to close.
        foreach (var window in Consoles().Where(candidate => !_before.Contains(candidate)))
        {
            _ = PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Gone while being asked.
        }
    }

    private static HashSet<IntPtr> Consoles()
    {
        var found = new HashSet<IntPtr>();
        _ = EnumWindows(
            (window, unused) =>
            {
                if (IsWindowVisible(window) && ClassOf(window) is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS")
                {
                    _ = found.Add(window);
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }

    private static string ClassOf(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "(unknown)";
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
