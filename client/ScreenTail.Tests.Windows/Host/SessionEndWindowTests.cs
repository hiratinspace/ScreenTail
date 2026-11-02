using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTail.Core.Lifecycle;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Host;
using ScreenTail.Service.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// The capture service's session-end window, sent the messages Windows sends (ST-112): Restart Manager's
/// close (WM_QUERYENDSESSION, then WM_ENDSESSION, with ENDSESSION_CLOSEAPP), the end of a sign-out, and
/// WM_CLOSE. Behind it, the service's own pieces over a temporary folder: the encrypted store with its
/// DPAPI key and a session recording into it, stopped the way CaptureHost stops.
///
/// The installed service itself, closed by a real Restart Manager, is the installer smoke test's check.
/// No desktop is needed for a hidden window, so this runs on the hosted runner.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class SessionEndWindowTests : IAsyncDisposable
{
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const long ENDSESSION_CLOSEAPP = 0x1;
    private const long ENDSESSION_LOGOFF = 0x80000000;

    private static readonly RemoteTool ScreenConnect = new() { Kind = RemoteToolKind.Screenconnect, ClientVersion = "24.1" };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "store.db");

    [Fact]
    public void TheWindowIsTopLevelHiddenAndUntitledSoRestartManagerSeesItAndNobodyElseDoes()
    {
        using var window = SessionEndWindow.Start(new CloseRequests(() => { }, Task.CompletedTask), NullLogger.Instance);
        var handle = window.Handle;

        Assert.NotEqual(IntPtr.Zero, handle);

        // One of this process's top-level windows, which is what Restart Manager and the end-session
        // broadcast go through. A message-only window would not be in this list.
        Assert.Contains(handle, TopLevelWindowsOf(Environment.ProcessId));
        Assert.Equal(GetDesktopWindow(), GetAncestor(handle, GA_PARENT));
        Assert.False(IsWindowVisible(handle));
        Assert.Equal(0, GetWindowTextLength(handle));
        var name = new char[256];
        var length = GetClassName(handle, name, name.Length);
        Assert.Equal(SessionEndWindow.ClassName, new string(name, 0, length));
        Assert.NotEqual(0, GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW);
    }

    [Fact]
    public async Task RestartManagersCloseStopsTheServiceAndLeavesTheStoreClosedAndTheSessionForRecovery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var service = await RecordingService.StartAsync(StorePath, ct);
        var requests = new CloseRequests(service.Stop, service.Stopped);
        using var window = SessionEndWindow.Start(requests, NullLogger.Instance);

        // Restart Manager asks first, and nothing stops: the answer is yes, and the session goes on.
        Assert.Equal(new IntPtr(1), SendMessage(window.Handle, WM_QUERYENDSESSION, IntPtr.Zero, new IntPtr(ENDSESSION_CLOSEAPP)));
        Assert.Equal(0, requests.StopsAsked);
        Assert.Equal(SessionState.Recording, service.Machine.State);

        // Then it says the session ends. The message is answered only once the store is closed.
        var watch = Stopwatch.StartNew();
        _ = await Task.Run(() => SendMessage(window.Handle, WM_ENDSESSION, new IntPtr(1), new IntPtr(ENDSESSION_CLOSEAPP)), ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        watch.Stop();

        Assert.True(service.Stopped.IsCompletedSuccessfully, "WM_ENDSESSION returned before the service had stopped");
        Assert.True(watch.Elapsed < CloseRequests.DefaultBudget, $"took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(new CloseRequest(CloseKind.RestartManager, Critical: false), requests.First);
        Assert.Equal(["capture", "machine", "store"], service.Order);
        await AssertClosedAndRecoverableAsync(service.SessionId, ct);
    }

    [Fact]
    public async Task ASignOutThatAnotherProgramRefusedStopsNothingAndOneThatHappensStopsTheService()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var service = await RecordingService.StartAsync(StorePath, ct);
        var requests = new CloseRequests(service.Stop, service.Stopped);
        using var window = SessionEndWindow.Start(requests, NullLogger.Instance);

        // Somebody refused: WM_ENDSESSION with FALSE. Still recording.
        Assert.Equal(new IntPtr(1), SendMessage(window.Handle, WM_QUERYENDSESSION, IntPtr.Zero, new IntPtr(ENDSESSION_LOGOFF)));
        _ = SendMessage(window.Handle, WM_ENDSESSION, IntPtr.Zero, new IntPtr(ENDSESSION_LOGOFF));
        Assert.Equal(0, requests.StopsAsked);
        Assert.True(await service.Machine.TryRecordEventAsync(new ClickEvent { TsMs = 9_000, X = 1, Y = 1, Button = MouseButton.Left }, ct));

        // The next try goes through.
        _ = await Task.Run(() => SendMessage(window.Handle, WM_ENDSESSION, new IntPtr(1), new IntPtr(ENDSESSION_LOGOFF)), ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(service.Stopped.IsCompletedSuccessfully);
        Assert.Equal(new CloseRequest(CloseKind.SignOut, Critical: false), requests.First);
        await AssertClosedAndRecoverableAsync(service.SessionId, ct);
    }

    [Fact]
    public async Task CloseMessageStopsTheServiceWithoutDestroyingTheWindowAndDisposeEndsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var service = await RecordingService.StartAsync(StorePath, ct);
        var requests = new CloseRequests(service.Stop, service.Stopped);
        var window = SessionEndWindow.Start(requests, NullLogger.Instance);
        var handle = window.Handle;

        // As taskkill without /f sends it: posted.
        Assert.True(PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero));
        await service.Stopped.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(new CloseRequest(CloseKind.CloseMessage, Critical: false), requests.First);
        Assert.True(IsWindow(handle), "WM_CLOSE destroyed the window instead of stopping the service");
        await AssertClosedAndRecoverableAsync(service.SessionId, ct);

        window.Dispose();
        Assert.False(IsWindow(handle), "the window outlived Dispose");
    }

    [Fact]
    public async Task AStopThatDoesNotFinishHoldsWindowsForTheBudgetAndNoLonger()
    {
        var budget = TimeSpan.FromMilliseconds(500);
        using var window = SessionEndWindow.Start(new CloseRequests(() => { }, new TaskCompletionSource().Task, budget), NullLogger.Instance);

        // Watched from outside, so a wait that is not bounded fails rather than hanging the run.
        var watch = Stopwatch.StartNew();
        _ = await Task.Run(() => SendMessage(window.Handle, WM_ENDSESSION, new IntPtr(1), new IntPtr(ENDSESSION_CLOSEAPP)), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.True(watch.Elapsed >= budget - TimeSpan.FromMilliseconds(50), $"returned after {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"returned after {watch.ElapsedMilliseconds} ms");
    }

    private async Task AssertClosedAndRecoverableAsync(string sessionId, CancellationToken ct)
    {
        // Closed, not merely idle: nothing holds the database, and SQLite folded its write-ahead log in and
        // removed it with the shared-memory file, which a process ended mid-write leaves behind.
        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        Assert.False(File.Exists(StorePath + "-wal"), "the write-ahead log is left");
        Assert.False(File.Exists(StorePath + "-shm"), "the shared-memory file is left");

        // The next start finds the session where a crash would have left it, and recovers it as partial.
        await using var store = await SqliteSessionStore.OpenAsync(StorePath, new DpapiKeyProvider(Path.Combine(_dir, "store.key")), ct: ct);
        Assert.Equal([sessionId], await store.ListSessionsInStatesAsync(SessionStateNames.Orphanable, ct));
        await using var machine = new SessionMachine(store, new NoCaptureSources(), new UnavailableDrafter());
        Assert.Equal(1, await machine.RecoverAsync(ct));
        Assert.True((await store.LoadSessionAsync(sessionId, ct))!.PartialCapture);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A file still held a moment after its owner closed it is the temp folder's to clean.
        }

        return ValueTask.CompletedTask;
    }

    private static List<IntPtr> TopLevelWindowsOf(int processId)
    {
        var found = new List<IntPtr>();
        _ = EnumWindows(
            (window, unused) =>
            {
                _ = GetWindowThreadProcessId(window, out var owner);
                if (owner == processId)
                {
                    found.Add(window);
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The service's parts that hold files, put together and stopped in CaptureHost's order: a capture loop
    /// writing into a recording session until the stop, then the machine, then the store.
    /// </summary>
    private sealed class RecordingService : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _order = [];
        private SqliteSessionStore _store = null!;
        private Task _running = Task.CompletedTask;

        public SessionMachine Machine { get; private set; } = null!;

        public string SessionId { get; private set; } = string.Empty;

        public Task Stopped => _stopped.Task;

        public IReadOnlyList<string> Order
        {
            get
            {
                lock (_order)
                {
                    return [.. _order];
                }
            }
        }

        public static async Task<RecordingService> StartAsync(string storePath, CancellationToken ct)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            var service = new RecordingService
            {
                _store = await SqliteSessionStore.OpenAsync(storePath, new DpapiKeyProvider(Path.Combine(Path.GetDirectoryName(storePath)!, "store.key")), ct: ct),
            };
            service.Machine = new SessionMachine(service._store, new NoCaptureSources(), new UnavailableDrafter());
            Assert.True(await service.Machine.StartAsync(ScreenConnect, ct: ct));
            service.SessionId = service.Machine.SessionId!;
            service._running = service.RunAsync();
            return service;
        }

        public void Stop() => _stopping.Cancel();

        private async Task RunAsync()
        {
            var stopping = _stopping.Token;
            var capturing = Task.Run(
                async () =>
                {
                    var ts = 1_000L;
                    try
                    {
                        while (true)
                        {
                            _ = await Machine.TryRecordEventAsync(new ClickEvent { TsMs = ts += 100, X = 1, Y = 1, Button = MouseButton.Left }, stopping);
                            await Task.Delay(20, stopping);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Add("capture");
                    }
                },
                CancellationToken.None);

            try
            {
                await Task.Delay(Timeout.Infinite, stopping);
            }
            catch (OperationCanceledException)
            {
            }

            await capturing;
            await Machine.DisposeAsync();
            Add("machine");
            await _store.DisposeAsync();
            Add("store");
            _ = _stopped.TrySetResult();
        }

        private void Add(string step)
        {
            lock (_order)
            {
                _order.Add(step);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            await _running.WaitAsync(TimeSpan.FromSeconds(10));
            _stopping.Dispose();
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    private const uint GA_PARENT = 1;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [Out] char[] name, int max);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
