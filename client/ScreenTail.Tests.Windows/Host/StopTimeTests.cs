using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenTail.Core.Detection.Registry;
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
/// technician's data folder or the pipe a real ScreenTail serves, and no backend address or device token
/// whatever the machine's environment says. A session is started over the pipe as the UI starts one, the
/// pill reported as the UI reports it, and kept busy: the input hooks are in, a synthetic window of a
/// remote-tool class is in front with text that changes, and moments are marked. Just before Windows asks,
/// a burst of moments is marked with no pause, so frames are still waiting for OCR and redaction when the
/// message arrives; the test requires that they are. On the laptop, in the sign-out case, the microphone is
/// required to be there and the speech model to be in memory before the recording counts.
///
/// What is held, everywhere: the store is closed within <see cref="CloseRequests.DefaultBudget"/> of the
/// message, which is the design's promise rather than a performance budget (Windows ends a program five
/// seconds after WM_ENDSESSION), and WM_ENDSESSION is answered only once it has; no write-ahead log or
/// shared-memory file is left; and the next start of the real service recovers the session as partial.
///
/// Nothing real is recorded. The test refuses to run while a real remote-support program is running, the
/// only window in scope is its own, with made-up words in it, and every frame kept is checked to show
/// those words. Everything is written into the throwaway folder and deleted with it, and what the run
/// reports is numbers.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class StopTimeTests : IAsyncDisposable
{
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const long ENDSESSION_CLOSEAPP = 0x1;
    private const long ENDSESSION_LOGOFF = 0x80000000;

    /// <summary>This test's folders under the shared tests' temp folder, so a run can clear its own leftovers.</summary>
    private const string FolderPrefix = "stop-time-";

    /// <summary>A word on both screens, which every kept frame's text must contain.</summary>
    private const string OwnWord = "printer";

    private const string ScreenOne = "Front desk printer queue\r\nStep one: clear the stuck jobs";
    private const string ScreenTwo = "Front desk printer queue\r\nStep two: restart the spooler";

    /// <summary>
    /// The speech model loaded is about 150 MB of this process's private memory (SpeechUnloadTests). Less
    /// than that, so the test is not tied to the figure, and far more than anything else a session adds.
    /// </summary>
    private const long ModelLoadedBytes = 80L * 1024 * 1024;

    private static readonly string TestsRoot = Path.Combine(Path.GetTempPath(), "screentail-tests");

    private readonly string _dir = Path.Combine(TestsRoot, FolderPrefix + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = [];

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
    /// The steady part of the recording, before the burst. Short, because the laptop's capabilities job runs
    /// close to its fifteen minutes and each of the three asks pays it once.
    /// </summary>
    private static TimeSpan Recording => TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(Ask.SignOut)]
    [InlineData(Ask.RestartManager)]
    [InlineData(Ask.CloseMessage)]
    public async Task ARecordingServiceStopsInsideTheBudgetWhenWindowsAsksAndIsRecoveredAsPartial(Ask ask)
    {
        Hardware.RequireScreenCapture();
        var ct = TestContext.Current.CancellationToken;
        RefuseWhileARealRemoteToolRuns();
        WithoutTheBackend();
        var paths = await PrepareAsync(ct);

        // The speech path is checked once, on the machine that has a microphone: the model's load is the
        // slow part, and the other two asks stop the same pipeline.
        var speech = Hardware.PerformanceCounts && ask == Ask.SignOut;

        string sessionId;
        int marks;
        int backlog;
        bool microphone;
        TimeSpan stopped;
        TimeSpan? answered = null;
        string firstLog;
        using (var window = DesktopWindow.CreateRemoteToolShaped("Stop time test session"))
        {
            window.Resize(900, 480);
            window.ShowText(ScreenOne);
            window.RequireForeground();

            await using var service = RunningService.Start(paths);
            await service.ConnectAsync(paths, ct);
            await WaitForTheScopeAsync(window, service, ct);
            service.ReportThePill();

            var memoryBefore = PrivateBytes();
            sessionId = await service.StartSessionAsync(ct);
            if (speech)
            {
                await WaitForTheSpeechModelAsync(service, memoryBefore, ct);
            }

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

            var before = await DiagnosticsAsync(service, ct);
            microphone = before.Microphone is not null;
            Assert.True(service.Pipe.State.State == CaptureStates.Recording, $"the session was {service.Pipe.State.State} ({service.Pipe.State.Reason}) when Windows was to ask, not recording\n{service.Log}");
            Assert.True(before.Scope.StartsWith("Capturing", StringComparison.Ordinal), $"the test window was not in scope at the ask\n{service.Log}");
            Assert.True(marks > 0, $"no moment was marked in {Recording.TotalSeconds:F0} s\n{service.Log}");
            Assert.True(service.Says("Input hooks installed: True"), $"the input hooks were not in during the recording\n{service.Log}");
            if (speech)
            {
                Assert.False(service.Says("could not be prepared"), $"the speech model could not be prepared\n{service.Log}");
            }

            // The pill's last report counts for six seconds (IndicatorReports.GoodFor), longer than the burst
            // and the stop together.
            await service.StopReportingAsync();
            backlog = await MarkABurstAsync(window, service, ct);
            Assert.True(backlog > 0, $"OCR had read every frame before Windows asked, so nothing was in flight at the stop\n{service.Log}");

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
            firstLog = service.Log;
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
            Assert.True(next.Says("Recovered 1 session(s)"), $"the next start did not recover the session\n{next.Log}");

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

        // The capture path was really working, and on nothing but the test's own window: every frame kept
        // was read, and shows the test's words. Counted, never quoted.
        Assert.True(session.Frames.Count > 0, $"no frame was kept ({marks} moments marked)\n{firstLog}");
        var foreign = session.Frames.Count(f => f.OcrText?.Contains(OwnWord, StringComparison.OrdinalIgnoreCase) != true);
        Assert.True(foreign == 0, $"{foreign} of {session.Frames.Count} kept frame(s) do not show the test window's words");

        var held = answered is { } a ? string.Create(CultureInfo.InvariantCulture, $", WM_ENDSESSION answered at {a.TotalMilliseconds:F0} ms") : string.Empty;
        Measurements.Record(string.Create(
            CultureInfo.InvariantCulture,
            $"Stop time mid-recording, {Label(ask)}: **{stopped.TotalMilliseconds:F0} ms** to the store closed{held} "
            + $"(budget {CloseRequests.DefaultBudget.TotalMilliseconds:F0} ms, enforced everywhere); "
            + $"recorded {Recording.TotalSeconds:F0} s with {marks} moments marked, then a burst, {backlog} frame(s) waiting for redaction at the ask; "
            + $"{session.Frames.Count} frame(s) kept, microphone {(microphone ? 1 : 0)}, speech model checked in memory {(speech ? 1 : 0)}; "
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

    private static Task<DiagnosticsReported> DiagnosticsAsync(RunningService service, CancellationToken ct) =>
        service.Pipe.RequestAsync<DiagnosticsReported>(id => new GetDiagnosticsCommand { RequestId = id }, ct);

    private static long PrivateBytes()
    {
        using var self = Process.GetCurrentProcess();
        return self.PrivateMemorySize64;
    }

    /// <summary>
    /// A real remote-support program on this machine could come to the front during the recording, and the
    /// real service would photograph it as the remote tool it is. This test does not run then; it says which
    /// program, by the registry's display name, and what to do.
    /// </summary>
    private static void RefuseWhileARealRemoteToolRuns()
    {
        var registry = RemoteToolRegistry.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));
        var running = registry.Tools.Where(tool => tool.Processes.Any(IsRunning)).Select(tool => tool.DisplayName).Distinct().ToList();
        Assert.True(
            running.Count == 0,
            $"Close {string.Join(", ", running)} on this machine and run the job again. This test runs the real capture service, recording, and must not photograph a real remote-support session.");
    }

    private static bool IsRunning(string processName)
    {
        var found = Process.GetProcessesByName(processName);
        try
        {
            return found.Length > 0;
        }
        finally
        {
            foreach (var process in found)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// The service reads its backend address and a fallback device token from the environment. A token set
    /// for the whole account on the laptop must never send this test's host to a real backend, so both are
    /// cleared for this process while the test runs and put back after it.
    /// </summary>
    private void WithoutTheBackend()
    {
        foreach (var name in new[] { "SCREENTAIL_BACKEND", "SCREENTAIL_DEVICE_TOKEN" })
        {
            _environment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
            Assert.Null(Environment.GetEnvironmentVariable(name));
        }
    }

    /// <summary>The service's own scope decision for the window in front, before anything is counted.</summary>
    private static async Task WaitForTheScopeAsync(DesktopWindow window, RunningService service, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        string scope;
        while (!(scope = (await DiagnosticsAsync(service, ct)).Scope).StartsWith("Capturing", StringComparison.Ordinal)
            && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (!window.IsForeground)
            {
                _ = window.TakeForeground(TimeSpan.FromSeconds(1));
            }

            await Task.Delay(100, ct);
        }

        Assert.True(scope.StartsWith("Capturing", StringComparison.Ordinal), $"the service never took the test window for a remote tool in 10 s\n{service.Log}");
    }

    /// <summary>
    /// On the laptop: the microphone is there, and the speech model, which the service loads when the first
    /// session starts, is in memory before the recording counts. This process is the service, so its private
    /// memory is the model's evidence.
    /// </summary>
    private static async Task WaitForTheSpeechModelAsync(RunningService service, long before, CancellationToken ct)
    {
        Assert.True((await DiagnosticsAsync(service, ct)).Microphone is not null, "the laptop has no microphone the service can open. Connect or enable one and run the job again.");
        var clock = Stopwatch.StartNew();
        while (PrivateBytes() - before < ModelLoadedBytes && clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            Assert.False(service.Says("could not be prepared"), $"the speech model could not be prepared\n{service.Log}");
            await Task.Delay(200, ct);
        }

        Assert.True(PrivateBytes() - before >= ModelLoadedBytes, $"the speech model was not in memory 20 s after the session started\n{service.Log}");
    }

    /// <summary>
    /// Moments marked back to back, the screen changing between them, until frames are waiting for OCR:
    /// the stop is measured with redaction in flight, not after it has caught up.
    /// </summary>
    private static async Task<int> MarkABurstAsync(DesktopWindow window, RunningService service, CancellationToken ct)
    {
        var backlog = 0;
        for (var round = 0; round < 3 && backlog == 0; round++)
        {
            for (var i = 0; i < 8; i++)
            {
                window.ShowText(i % 2 == 0 ? ScreenTwo : ScreenOne);
                _ = await service.Pipe.SendAsync(id => new MarkMomentCommand { RequestId = id }, ct);
            }

            backlog = (await DiagnosticsAsync(service, ct)).RedactionBacklog;
        }

        return backlog;
    }

    /// <summary>
    /// The throwaway folder, with capture settings that start nothing by themselves and name the speech
    /// model the laptop keeps for the tests, and that model fetched before the clock starts. Folders an
    /// earlier run of this test left behind go first, and anything else in the tests' temp folder older
    /// than an hour.
    /// </summary>
    private async Task<CaptureHostPaths> PrepareAsync(CancellationToken ct)
    {
        if (Directory.Exists(TestsRoot))
        {
            foreach (var stale in new DirectoryInfo(TestsRoot).GetDirectories())
            {
                if (stale.Name.StartsWith(FolderPrefix, StringComparison.Ordinal) || stale.LastWriteTimeUtc < DateTime.UtcNow - TimeSpan.FromHours(1))
                {
                    _ = DeleteFolder(stale.FullName);
                }
            }
        }

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

        return new CaptureHostPaths(_dir, "ScreenTail.Tests.StopTime." + Guid.NewGuid().ToString("N"), models);
    }

    /// <summary>Deletes a folder, trying again for a moment while a file is still held. False if it stayed.</summary>
    private static bool DeleteFolder(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }

        return !Directory.Exists(path);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var (name, value) in _environment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        // A folder that could not be removed now holds an encrypted store beside its DPAPI key; the next run
        // of this test removes it first.
        _ = DeleteFolder(_dir);
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

        /// <summary>
        /// The service's own lines so far, for failure messages: ScreenTail's categories only, scrubbed as the
        /// service scrubs them and at Information, so no foreground process or window class is in them
        /// (INV-10), and none of the host's lines, which name folders.
        /// </summary>
        public string Log
        {
            get
            {
                var kept = new List<string>();
                var keeping = false;
                foreach (var line in Raw.Split(Environment.NewLine))
                {
                    if (line.StartsWith("    ", StringComparison.Ordinal))
                    {
                        if (keeping)
                        {
                            kept.Add(line);
                        }

                        continue;
                    }

                    // "HH:mm:ss.fff LEVEL Category: message": the category starts at the 20th character.
                    keeping = line.Length > 19 && line.AsSpan(19).StartsWith("ScreenTail.", StringComparison.Ordinal);
                    if (keeping)
                    {
                        kept.Add(line);
                    }
                }

                return string.Join(Environment.NewLine, kept);
            }
        }

        private string Raw
        {
            get
            {
                lock (_log)
                {
                    return _log.ToString();
                }
            }
        }

        /// <summary>
        /// Starts the service. The last run's token file goes first: the service writes this run's only once
        /// it has recovered, and a client that read the old one in between would be refused (bad_token).
        /// </summary>
        public static RunningService Start(CaptureHostPaths paths)
        {
            File.Delete(paths.IpcTokenFile);
            return new(paths);
        }

        /// <summary>Whether the service's own lines say this.</summary>
        public bool Says(string words) => Log.Contains(words, StringComparison.Ordinal);

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
                // A service that will not stop still holds the hooks, the microphone and the screen capture,
                // and would go on holding them for every test after this one. Nothing in this process can take
                // them from it, so the process ends, loudly, and the job fails with it.
                Measurements.Record("STOP-TIME-HUNG: the capture service under test did not stop within 30 s; the test process was ended so capture stops with it");
                Environment.FailFast("StopTimeTests: the capture service under test did not stop within 30 s. Ending the test process so its hooks, microphone and capture stop.");
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
