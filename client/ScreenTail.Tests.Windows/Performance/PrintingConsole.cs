using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>Which program hosts a test console's window.</summary>
public enum ConsoleHost
{
    /// <summary>Whatever Windows opens a new console in: Windows Terminal where it is the default terminal.</summary>
    Default,

    /// <summary>The classic console host, started by name, whose window is a <c>ConsoleWindowClass</c>.</summary>
    Classic,
}

/// <summary>
/// A console window of its own process printing a line every 20 ms, as the runner's console does while a
/// job writes its log (ST-031). It can retitle itself every half second too, the way a shell does as
/// commands run.
///
/// Paced rather than flat out on purpose. A console printing as fast as it can keeps a core or two busy
/// drawing, and on a four-core runner that alone multiplied this process's .NET pool threads' context
/// switches by five (the probe on 2026-11-01: 24 a second at idle, 127 with the console printing), every
/// one of them a preemption of a thread that was already running rather than a wakeup anything asked for.
/// That measures the machine, not the service.
///
/// The shell runs while a file of the test's own exists and ends by itself, cleanly, when the file goes:
/// a terminal closes the tab of a shell that exited cleanly, and the window with it when it was the last.
/// Nothing is ever asked to close by message. A Windows Terminal window can hold the runner's own console
/// beside the test's, and closing the window tells every console in it to close: the runner took that as
/// Ctrl+C and cancelled the job on both runners (PR #252, runs 41350911964 and 41350911872). The shell is
/// killed only if it has not ended five seconds after being told, and only its own process tree.
/// Its window is found by its class, never its title.
/// </summary>
internal sealed class PrintingConsole : IDisposable
{
    private readonly Process _process;
    private readonly string _sentinel;

    private PrintingConsole(Process process, string sentinel, IntPtr window, ConsoleHost host)
    {
        _process = process;
        _sentinel = sentinel;
        Window = window;
        Host = host;
    }

    public IntPtr Window { get; }

    /// <summary>The host that actually opened the window, which is the classic one when the default terminal gave none.</summary>
    public ConsoleHost Host { get; }

    /// <summary>The window class, which says which console host opened it.</summary>
    public string ClassName => ClassOf(Window);

    public bool IsForeground => GetForegroundWindow() == Window;

    /// <summary>The prefix of every title the console gives itself when asked to retitle.</summary>
    public const string TitlePrefix = "ScreenTail console title";

    /// <summary>
    /// Starts the console and waits for its window. A default terminal that opens no new window (Windows
    /// Terminal set to add a tab to one already open) is told to stop, and the classic host is used instead,
    /// so the test runs either way. Fails the test when neither opens a window.
    /// </summary>
    public static async Task<PrintingConsole> StartAsync(ConsoleHost host, bool retitle, CancellationToken ct)
    {
        var first = await TryStartAsync(host, retitle, ct);
        if (first is not null || host == ConsoleHost.Classic)
        {
            return first ?? throw Failed(host);
        }

        Measurements.Record("The default terminal opened no new window for the test console (a tab, perhaps); the classic console host was used instead.");
        return await TryStartAsync(ConsoleHost.Classic, retitle, ct) ?? throw Failed(ConsoleHost.Classic);
    }

    private static InvalidOperationException Failed(ConsoleHost host)
    {
        Assert.Fail($"The test console's window ({host}) did not appear within 15 seconds.");
        return new InvalidOperationException("unreachable");
    }

    private static async Task<PrintingConsole?> TryStartAsync(ConsoleHost host, bool retitle, CancellationToken ct)
    {
        var sentinel = Path.Combine(Path.GetTempPath(), "screentail-tests", $"console-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        await File.WriteAllTextAsync(sentinel, string.Empty, ct);

        var retitling = retitle
            ? string.Create(CultureInfo.InvariantCulture, $"if ($i % 25 -eq 0) {{ $Host.UI.RawUI.WindowTitle = '{TitlePrefix} ' + $i }}; ")
            : string.Empty;
        var command = string.Create(
            CultureInfo.InvariantCulture,
            $"-NoProfile -NonInteractive -Command \"& {{ $i = 0; while (Test-Path -LiteralPath '{sentinel.Replace("'", "''", StringComparison.Ordinal)}') {{ $i++; 'ScreenTail console line ' + $i; {retitling}Start-Sleep -Milliseconds 20 }} }}\"");
        var start = host == ConsoleHost.Classic
            ? new ProcessStartInfo("conhost.exe", "powershell.exe " + command)
            : new ProcessStartInfo("powershell.exe", command);
        start.UseShellExecute = true;
        start.WindowStyle = ProcessWindowStyle.Normal;

        var before = Consoles();
        var process = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} did not start.");
        var wanted = host == ConsoleHost.Classic ? "ConsoleWindowClass" : null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var window = Consoles().FirstOrDefault(candidate => !before.Contains(candidate) && (wanted is null || ClassOf(candidate) == wanted));
            if (window != IntPtr.Zero)
            {
                return new PrintingConsole(process, sentinel, window, host);
            }

            await Task.Delay(100, ct);
        }

        Stop(process, sentinel);
        return null;
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

    public void Dispose() => Stop(_process, _sentinel);

    /// <summary>The shell told to end by itself, and made to if it has not. No window is sent anything.</summary>
    private static void Stop(Process process, string sentinel)
    {
        try
        {
            File.Delete(sentinel);
        }
        catch (IOException)
        {
            // The shell is killed below instead.
        }

        try
        {
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Gone while being asked.
        }

        process.Dispose();
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
}
