using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Lifecycle;
using ScreenTail.Core.Net;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Store;
using ScreenTail.Service.Host;
using ScreenTail.Service.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Logging;
using ScreenTail.Shared.Settings;
using ScreenTail.Tests.Windows.Performance;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// How long the real capture service takes to stop when Windows asks it to close in the middle of a
/// recording (ST-112, owed by #251): sign-out (WM_ENDSESSION with ENDSESSION_LOGOFF), Restart Manager
/// (ENDSESSION_CLOSEAPP) and WM_CLOSE.
///
/// The real <see cref="CaptureHost"/>, composed as Program.cs composes it: the generic host, the
/// session-end window with its <see cref="CloseRequests"/>, and <see cref="ServiceRun"/>. Only its paths
/// differ (<see cref="CaptureHostPaths"/>): a throwaway folder and a pipe of its own, never the
/// technician's data folder or the pipe a real ScreenTail serves. A session is started over the pipe as
/// the UI starts one, the pill reported as the UI reports it, and kept busy with what the service does
/// while recording: the input hooks are in, a synthetic window of a remote-tool class is in front with
/// text that changes (scene frames) and moments are marked (frames for OCR and redaction), and the
/// microphone is open on a machine that has one, with the speech model on the laptop. Then Windows'
/// message goes to the service's own window.
///
/// What is held, everywhere: the store is closed within <see cref="CloseRequests.DefaultBudget"/> of the
/// message, which is the design's promise rather than a performance budget (Windows ends a program five
/// seconds after WM_ENDSESSION), and WM_ENDSESSION is answered only once it has; no write-ahead log or
/// shared-memory file is left; and the next start of the real service recovers the session as partial.
///
/// Nothing real is recorded. The only window in scope is the test's own, with made-up words in it,
/// everything is written into the throwaway folder and deleted with it, and what the run reports is
/// numbers.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class StopTimeTests : IAsyncDisposable
{
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const long ENDSESSION_CLOSEAPP = 0x1;
    private const long ENDSESSION_LOGOFF = 0x80000000;

    private const string ScreenOne = "Front desk printer queue\r\nStep one: clear the stuck jobs";
    private const string ScreenTwo = "Front desk printer queue\r\nStep two: restart the spooler";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    /// <summary>How Windows asks.</summary>
    public enum Ask
    {
        /// <summary>The end of a sign-out: WM_QUERYENDSESSION, then WM_ENDSESSION, with ENDSESSION_LOGOFF.</summary>
        SignOut,

        /// <summary>Restart Manager for an installer: the same pair with ENDSESSION_CLOSEAPP.</summary>
        RestartManager,

        /// <summary>WM_CLOSE, posted, as <c>taskkill</c> without <c>/f</c> sends it.</summary>
        CloseMessage,
    }

    /// <summary>
    /// Long enough on the laptop for the speech model to load at the session's start and the scene sampler
    /// and OCR to have frames in hand; a few seconds on a hosted runner, which has no microphone.
    /// </summary>
    private static TimeSpan Recording => Hardware.PerformanceCounts ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4);

    [Theory]
    [InlineData(Ask.SignOut)]
    [InlineData(Ask.RestartManager)]
    [InlineData(Ask.CloseMessage)]
    public async Task ARecordingServiceStopsInsideTheBudgetWhenWindowsAsksAndIsRecoveredAsPartial(Ask ask)
    {
        Hardware.RequireScreenCapture();
        var ct = TestContext.Current.CancellationToken;
        var paths = await PrepareAsync(ct);

        string sessionId;
        int marks;
        int backlog;
        bool microphone;
        TimeSpan stopped;
        TimeSpan? answered = null;
        using (var window = DesktopWindow.CreateRemoteToolShaped("Stop time test session"))
        {
            window.Resize(900, 480);
            window.ShowText(ScreenOne);
            window.RequireForeground();

            await using var service = RunningService.Start(paths);
            await service.ConnectAsync(paths, ct);
            service.ReportThePill();
            sessionId = await service.StartSessionAsync(ct);

            // Busy: the screen changes twice a second, a moment is marked each time, and the window is put
            // back in front if anything took it.
            marks = 0;
            var flips = 0;
            var busy = Stopwatch.StartNew();
            while (busy.Elapsed < Recording)
            {
                if (!window.IsForeground)
                {
                    _ = window.TakeForeground(TimeSpan.FromSeconds(1));
                }

                window.ShowText(flips++ % 2 == 0 ? ScreenTwo : ScreenOne);
                if ((await service.Pipe.SendAsync(id => new MarkMomentCommand { RequestId = id }, ct)).Ok)
                {
                    marks++;
                }

                await Task.Delay(500, ct);
            }

            var before = await service.Pipe.RequestAsync<DiagnosticsReported>(id => new GetDiagnosticsCommand { RequestId = id }, ct);
            backlog = before.RedactionBacklog;
            microphone = before.Microphone is not null;
            Assert.True(service.Pipe.State.State == CaptureStates.Recording, $"the session was {service.Pipe.State.State} ({service.Pipe.State.Reason}) when Windows was to ask, not recording\n{service.Log}");
            Assert.True(before.Scope.StartsWith("Capturing", StringComparison.Ordinal), $"the test window was not in scope at the ask\n{service.Log}");
            Assert.True(marks > 0, $"no moment was marked in {Recording.TotalSeconds:F0} s\n{service.Log}");
            Assert.Contains("Input hooks installed: True", service.Log, StringComparison.Ordinal);

            // The pill's last report counts for six seconds (IndicatorReports.GoodFor), longer than any stop.
            await service.StopReportingAsync();

            var handle = service.Window.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            var watch = new Stopwatch();
            if (ask is Ask.SignOut or Ask.RestartManager)
            {
                var flags = new IntPtr(ask == Ask.SignOut ? ENDSESSION_LOGOFF : ENDSESSION_CLOSEAPP);
                Assert.Equal(new IntPtr(1), SendMessage(handle, WM_QUERYENDSESSION, IntPtr.Zero, flags));
                watch.Start();
                _ = await Task.Run(() => SendMessage(handle, WM_ENDSESSION, new IntPtr(1), flags), ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
                answered = watch.Elapsed;
                Assert.True(service.Stopped.IsCompletedSuccessfully, $"WM_ENDSESSION was answered after {answered.Value.TotalMilliseconds:F0} ms with the service still stopping\n{service.Log}");
            }
            else
            {
                watch.Start();
                Assert.True(PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero));
            }

            await service.Stopped.WaitAsync(TimeSpan.FromSeconds(15), ct);
            stopped = watch.Elapsed;

            Assert.Equal(0, await service.Run.WaitAsync(TimeSpan.FromSeconds(15), ct));
            Assert.Equal(Expected(ask), service.Requests.First?.Kind);
            Assert.True(
                stopped < CloseRequests.DefaultBudget,
                $"the service took {stopped.TotalMilliseconds:F0} ms to stop, budget {CloseRequests.DefaultBudget.TotalMilliseconds:F0} ms\n{service.Log}");
        }

        // Closed, not merely idle: nothing holds the database, and SQLite folded its write-ahead log in and
        // removed it with the shared-memory file, which a process ended mid-write leaves behind.
        var storePath = Path.Combine(paths.DataDirectory, "store.db");
        using (new FileStream(storePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        Assert.False(File.Exists(storePath + "-wal"), "the write-ahead log is left");
        Assert.False(File.Exists(storePath + "-shm"), "the shared-memory file is left");

        // The next start of the real service recovers the session before it opens its pipe.
        TimeSpan recoveredBy;
        TimeSpan idleStop;
        await using (var next = RunningService.Start(paths))
        {
            var clock = Stopwatch.StartNew();
            await next.ConnectAsync(paths, ct);
            recoveredBy = clock.Elapsed;
            Assert.Contains("Recovered 1 session(s)", next.Log, StringComparison.Ordinal);

            clock.Restart();
            Assert.True(PostMessage(next.Window.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero));
            await next.Stopped.WaitAsync(TimeSpan.FromSeconds(15), ct);
            idleStop = clock.Elapsed;
            Assert.Equal(0, await next.Run.WaitAsync(TimeSpan.FromSeconds(15), ct));
        }

        await using var store = await SqliteSessionStore.OpenAsync(storePath, new DpapiKeyProvider(paths.KeyFile), ct: ct);
        Assert.Empty(await store.ListSessionsInStatesAsync(SessionStateNames.Orphanable, ct));
        var session = await store.LoadSessionAsync(sessionId, ct);
        Assert.NotNull(session);
        Assert.True(session.PartialCapture, "the interrupted session was not recovered as partial");

        var held = answered is { } a ? string.Create(CultureInfo.InvariantCulture, $", WM_ENDSESSION answered at {a.TotalMilliseconds:F0} ms") : string.Empty;
        Measurements.Record(string.Create(
            CultureInfo.InvariantCulture,
            $"Stop time mid-recording, {Label(ask)}: **{stopped.TotalMilliseconds:F0} ms** to the store closed{held} "
            + $"(budget {CloseRequests.DefaultBudget.TotalMilliseconds:F0} ms, enforced everywhere); "
            + $"recorded {Recording.TotalSeconds:F0} s with {marks} moments marked, {backlog} frame(s) waiting for redaction at the ask, "
            + $"{session.Frames.Count} frame(s) kept and {session.FramesPurgedUnredacted} purged unredacted, microphone {(microphone ? 1 : 0)}; "
            + $"the next start had recovered it as partial when its pipe opened, {recoveredBy.TotalMilliseconds:F0} ms after it began, and stopped idle in {idleStop.TotalMilliseconds:F0} ms"));
    }

    private static CloseKind Expected(Ask ask) => ask switch
    {
        Ask.SignOut => CloseKind.SignOut,
        Ask.RestartManager => CloseKind.RestartManager,
        _ => CloseKind.CloseMessage,
    };

    private static string Label(Ask ask) => ask switch
    {
        Ask.SignOut => "sign-out (WM_ENDSESSION, ENDSESSION_LOGOFF)",
        Ask.RestartManager => "Restart Manager (WM_ENDSESSION, ENDSESSION_CLOSEAPP)",
        _ => "WM_CLOSE",
    };

    /// <summary>
    /// The throwaway folder, with capture settings that start nothing by themselves and name the speech
    /// model the laptop keeps for the tests, and that model fetched before the clock starts.
    /// </summary>
    private async Task<CaptureHostPaths> PrepareAsync(CancellationToken ct)
    {
        _ = Directory.CreateDirectory(_dir);
        var model = SpeechModels.BaseEnglish;
        Assert.True(new CaptureSettingsStore(Path.Combine(_dir, "capture.json")).Save(new CaptureSettings { AutoStart = false, SpeechModel = model.Name }));

        // The tests' own copy, the one SpeechUnloadTests keeps. A hosted runner has no microphone and
        // never loads a model, so it fetches nothing.
        var models = SpeechUnloadTests.ModelCache();
        if (Hardware.PerformanceCounts)
        {
            using var http = new HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null))));
            Assert.True(await new ModelDownload(http).EnsureAsync(model, Path.Combine(models, $"ggml-{model.Name}.bin"), ct: ct), "the speech model could not be had");
        }

        return new CaptureHostPaths(
            DataDirectory: _dir,
            KeyFile: Path.Combine(_dir, "store.key"),
            DeviceCredentials: Path.Combine(_dir, "device.cred"),
            IpcTokenFile: Path.Combine(_dir, "ipc.token"),
            PipeName: "ScreenTail.Tests.StopTime." + Guid.NewGuid().ToString("N"),
            ModelDirectory: models);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Not made (the test skipped), or a file still held a moment after its owner closed it, which
            // is the temp folder's to clean.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The capture service as Program.cs runs it, over the given paths: the generic host with
    /// <see cref="CaptureHost"/> as its one hosted service and the scrubbed log, the session-end window
    /// wired to the host's lifetime, and <see cref="ServiceRun"/>. Nothing here registers for restart:
    /// this is the test process, not the service.
    /// </summary>
    private sealed class RunningService : IAsyncDisposable
    {
        private readonly StringWriter _log = new(CultureInfo.InvariantCulture);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _reporting = new();
        private readonly ScrubbingLoggerProvider _windowLog;
        private Task _reports = Task.CompletedTask;
        private IpcClient? _pipe;

        private RunningService(CaptureHostPaths paths)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            _ = builder.Logging.ClearProviders();
            _ = builder.Logging.SetMinimumLevel(LogLevel.Information);
            _ = builder.Logging.AddProvider(new ScrubbingLoggerProvider(_log, LogLevel.Information));
            _ = builder.Services.AddHostedService(services => new CaptureHost(
                services.GetRequiredService<ILogger<CaptureHost>>(),
                services.GetRequiredService<IHostApplicationLifetime>(),
                paths));
            var host = builder.Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            Requests = new CloseRequests(lifetime.StopApplication, _stopped.Task);
            _windowLog = new ScrubbingLoggerProvider(_log, LogLevel.Information);
            Window = SessionEndWindow.Start(Requests, _windowLog.CreateLogger("ScreenTail.Service.SessionEnd"));
            Run = Task.Run(() => ServiceRun.RunAsync(host, () => lifetime.ApplicationStopping.IsCancellationRequested, _stopped));
        }

        public CloseRequests Requests { get; }

        public SessionEndWindow Window { get; }

        public Task<int> Run { get; }

        public Task Stopped => _stopped.Task;

        public IpcClient Pipe => _pipe ?? throw new InvalidOperationException("Not connected.");

        /// <summary>The service's log so far, scrubbed as the service scrubs it (INV-10). For failure messages.</summary>
        public string Log
        {
            get
            {
                lock (_log)
                {
                    return _log.ToString();
                }
            }
        }

        public static RunningService Start(CaptureHostPaths paths) => new(paths);

        /// <summary>Connects as the UI does, once the service has written this run's token and opened its pipe.</summary>
        public async Task ConnectAsync(CaptureHostPaths paths, CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            Exception? last = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                Assert.False(Run.IsCompleted, $"the service ended before its pipe opened\n{Log}");
                if (File.Exists(paths.IpcTokenFile))
                {
                    try
                    {
                        var token = IpcTokenFile.Read(paths.IpcTokenFile);
                        try
                        {
                            // No server check: the service is this process, which a test may take on trust.
                            _pipe = await IpcClient.ConnectAsync(paths.PipeName, token, "ScreenTail.Tests.Windows", serverVerifier: null, clientVersion: "stop-time", timeout: TimeSpan.FromSeconds(3), ct: ct);
                            return;
                        }
                        finally
                        {
                            Array.Clear(token);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or IpcProtocolException)
                    {
                        last = ex;
                    }
                }

                await Task.Delay(100, ct);
            }

            Assert.Fail($"no connection to the service's pipe in 60 s ({last?.GetType().Name})\n{Log}");
        }

        /// <summary>Says the pill is on screen once a second, as the UI does while it shows it (INV-4).</summary>
        public void ReportThePill()
        {
            var stop = _reporting.Token;
            _reports = Task.Run(
                async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            _ = await Pipe.SendAsync(id => new IndicatorShowingCommand { RequestId = id, X = 8, Y = 8, Width = 220, Height = 40 }, stop);
                            await Task.Delay(1000, stop);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or IpcProtocolException)
                        {
                            return;
                        }
                    }
                },
                CancellationToken.None);
        }

        public async Task StopReportingAsync()
        {
            await _reporting.CancelAsync();
            await _reports;
        }

        /// <summary>Starts a session as the UI's Start does, once the service has heard the pill, and waits for it to record.</summary>
        public async Task<string> StartSessionAsync(CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            CommandResult started;
            while (!(started = await Pipe.SendAsync(id => new StartCommand { RequestId = id }, ct)).Ok
                && started.Error == ManualStart.NotIndicated
                && clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(200, ct);
            }

            Assert.True(started.Ok, $"the start was refused: {started.Error}\n{Log}");
            while (clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (Pipe.State is { State: CaptureStates.Recording, SessionId: { Length: > 0 } id })
                {
                    return id;
                }

                await Task.Delay(100, ct);
            }

            Assert.Fail($"the start was accepted and the state is {Pipe.State.State}, not recording\n{Log}");
            return string.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            await StopReportingAsync();

            // A test that failed before Windows asked still stops the service, the ordinary way.
            if (!Stopped.IsCompleted)
            {
                Requests.Close();
            }

            try
            {
                _ = await Run.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                // Reported by the test's own assertions; the process ends with the run.
            }

            if (_pipe is not null)
            {
                await _pipe.DisposeAsync();
            }

            Window.Dispose();
            _windowLog.Dispose();
            _reporting.Dispose();
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
