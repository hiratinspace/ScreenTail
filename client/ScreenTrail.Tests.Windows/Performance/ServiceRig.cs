using System.Net.Http;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTrail.Core.Capture;
using ScreenTrail.Core.Detection;
using ScreenTrail.Core.Detection.Registry;
using ScreenTrail.Core.Input;
using ScreenTrail.Core.Net;
using ScreenTrail.Core.Perf;
using ScreenTrail.Core.Privacy;
using ScreenTrail.Core.Sessions;
using ScreenTrail.Core.Speech;
using ScreenTrail.Core.Store;
using ScreenTrail.Service.Capture;
using ScreenTrail.Service.Detection;
using ScreenTrail.Service.Input;
using ScreenTrail.Service.Privacy;
using ScreenTrail.Service.Speech;
using ScreenTrail.Service.Store;

namespace ScreenTrail.Tests.Windows.Performance;

/// <summary>
/// The capture service's running parts, put together the way <c>CaptureHost</c> puts them together, over
/// a store in the temp folder (ST-031).
///
/// What runs here is everything that runs in the service with no session, and everything a session adds
/// short of speech recognition: the foreground and focus watchers, the scope coordinator, the indicator,
/// sensitive-context and password-field guards, the hook lifetime, the click drain, the processor throttle,
/// the scene sampler, the redaction worker with the real OCR engine, and the narration recorder with the
/// real microphone. The
/// speech model is never loaded, because the service does not load it until a session wants it and a CI run
/// has nobody talking; ADR-0001 and the unload test measure it on its own.
///
/// Left out, each because it costs nothing between events or would reach outside the test: the pipe
/// server (no client), the global hotkeys (they would take the chords from a real ScreenTrail), the outbox
/// and retention timers (thirty seconds and an hour), and the device and policy loops (no backend).
///
/// <b>When CaptureHost gains a loop, add it here,</b> or the laptop's idle figure stops describing the
/// service.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class ServiceRig : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentrail-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _running = [];
    private readonly List<IAsyncDisposable> _owned = [];
    private readonly List<IDisposable> _ownedSync = [];

    private ServiceRig()
    {
    }

    public SessionMachine Machine { get; private set; } = null!;

    public SqliteSessionStore Store { get; private set; } = null!;

    public RedactionWorker Redaction { get; private set; } = null!;

    public SceneSampleLoop Scenes { get; private set; } = null!;

    /// <summary>
    /// ST-031's throttle, reading this process. The test host's own work counts towards it, so a recording
    /// measurement can report the level it ended at.
    /// </summary>
    public CaptureThrottle Throttle { get; private set; } = null!;

    /// <summary>
    /// Every move of the throttle's level so far, oldest first. A throttled service records less and so costs
    /// less, which would hide a processor regression behind a figure that looks inside budget; a measurement
    /// takes the count before and after its window and looks only at the moves in between.
    /// </summary>
    public IReadOnlyList<CaptureLevelChange> ThrottleMoves
    {
        get
        {
            lock (_throttleMoves)
            {
                return [.. _throttleMoves];
            }
        }
    }

    private readonly List<CaptureLevelChange> _throttleMoves = [];

    public HookLifetime Hooks { get; private set; } = null!;

    /// <summary>The foreground watcher's pump thread, so a measurement can say what it alone cost.</summary>
    public uint ForegroundPumpThread { get; private set; }

    /// <summary>The thread the foreground watcher's name hook listens to (0: every thread, or none).</summary>
    public uint NameHookThread => _foreground?.NameHookThread ?? 0;

    private WindowsForegroundWatcher? _foreground;

    /// <summary>Whether the focus hook is in right now. Out with no session (ST-031, FocusInterest).</summary>
    public bool FocusHooked => _focus?.Hooked ?? false;

    private WindowsFocusWatcher? _focus;

    /// <summary>The focus watcher's pump thread, likewise.</summary>
    public uint FocusPumpThread { get; private set; }

    /// <summary>Frames waiting to be read or being read, so a test can let the worker finish.</summary>
    public PendingFrames Pending { get; private set; } = null!;

    /// <summary>The store's files, for the disk figure.</summary>
    public string StorePath => Path.Combine(_dir, "store.db");

    /// <param name="scope">
    /// Where the capture loops may look. The coordinator's own decision when null, which on the laptop is
    /// whatever the runner has in front and almost never in scope. A recording measurement passes a
    /// decision for a window the test owns, so frames are taken of nothing but that.
    /// </param>
    public static Task<ServiceRig> StartAsync(Func<ScopeDecision?>? scope, CancellationToken ct) =>
        StartAsync(scope, new ServiceRigOptions(), ct);

    /// <param name="options">What differs from the service as composed for the idle and recording budgets.</param>
    public static async Task<ServiceRig> StartAsync(Func<ScopeDecision?>? scope, ServiceRigOptions options, CancellationToken ct)
    {
        var rig = new ServiceRig();
        await rig.ComposeAsync(scope, options, ct);
        return rig;
    }

    /// <summary>The narration recorder, for its counts.</summary>
    public NarrationRecorder Narration { get; private set; } = null!;

    /// <summary>The recogniser the recorder calls, timed; null unless the rig was asked to load the model.</summary>
    public TimedRecogniser? Speech { get; private set; }

    private async Task ComposeAsync(Func<ScopeDecision?>? scopeOverride, ServiceRigOptions options, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var stopping = _stopping.Token;

        Store = await SqliteSessionStore.OpenAsync(StorePath, new DpapiKeyProvider(Path.Combine(_dir, "store.key")), ct: ct);
        _owned.Add(Store);

        var engine = new RedactionEngine();
        var pending = new PendingFrames();
        Pending = pending;
        Machine = new SessionMachine(
            Store,
            new NoCaptureSources(),
            new UnavailableDrafter(),
            options: new SessionMachineOptions { Scrubber = engine, Pending = pending });

        var registry = RemoteToolRegistry.Load(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json"), ct));
        var exclusions = ExclusionList.Load(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Registry", "exclusions-default.json"), ct));
        var policy = new ScopePolicy(registry, new ScopeOptions { Exclusions = exclusions });

        // Auto-start off: the laptop's foreground is the runner's, and a session it started on its own
        // would be a recording measurement in the middle of an idle one. The setting is a real one (ST-080).
        var coordinator = new AutoSessionCoordinator(
            Machine,
            policy,
            new SessionTrigger(policy),
            () => true,
            NullLogger.Instance,
            () => (true, null),
            clipboard: null,
            autoStart: () => false);
        var scope = scopeOverride ?? (() => coordinator.CurrentScope);

        var foreground = new WindowsForegroundWatcher();
        _owned.Add(foreground);
        foreground.Changed += coordinator.Observe;
        await foreground.StartAsync(ct);
        ForegroundPumpThread = foreground.PumpThreadId;
        _foreground = foreground;
        _running.Add(coordinator.RunAsync(stopping));

        var hooks = new WindowsInputHooks();
        _owned.Add(hooks);
        Hooks = new HookLifetime(Machine, hooks.StartAsync, hooks.StopAsync);
        _ownedSync.Add(Hooks);
        Hooks.Follow(stopping);

        var capturer = new ScreenshotCapturer();
        _ownedSync.Add(capturer);
        var clicks = new ClickCaptureLoop(Machine, new WindowsInputHooksAccessor(hooks), capturer, scope, NullLogger.Instance);
        _running.Add(clicks.RunAsync(stopping));
        Throttle = new CaptureThrottle(new ThisProcessTime());
        Throttle.Changed += change =>
        {
            lock (_throttleMoves)
            {
                _throttleMoves.Add(change);
            }
        };
        _running.Add(Throttle.RunAsync(Machine, null, stopping));

        // The throttle always reads the process, so a measurement can say where it would have gone; it acts
        // on sampling and OCR only when asked, so a breakdown measures the work a full session does.
        var acting = options.ApplyThrottle ? Throttle : null;
        Scenes = new SceneSampleLoop(Machine, capturer, scope, NullLogger.Instance, new SceneSampler(), throttle: acting);
        _running.Add(Scenes.RunAsync(stopping));

        Redaction = new RedactionWorker(Store, new WindowsOcrRecogniser(), new WindowsFrameMasker(), engine, pending, throttle: acting);
        Redaction.BacklogChanged += Machine.ReportPendingRedactions;
        var sensitive = new SensitiveContextGuard(Machine);
        Redaction.SensitiveContextSeen += sensitive.Seen;
        var indicator = new IndicatorGuard(Machine, () => true);
        _running.Add(indicator.RunAsync(stopping));
        _running.Add(sensitive.RunAsync(stopping));

        var focus = new WindowsFocusWatcher();
        _owned.Add(focus);
        var focusedField = new WindowsFocusedFieldProbe();
        _ownedSync.Add(focusedField);
        var password = new PasswordFieldGuard(Machine, focusedField);
        _ownedSync.Add(password);
        focus.FocusMoved += password.FocusMoved;
        await focus.StartAsync(ct);
        FocusPumpThread = focus.PumpThreadId;
        _focus = focus;
        _ownedSync.Add(new FocusInterest(Machine, focus.Listen));
        _running.Add(password.RunAsync(stopping));

        _running.Add(Redaction.RunAsync(stopping));

        // The real microphone, opened only while a session records, as in the service, unless a test brings
        // its own. Without speech the model is pointed at an empty folder and never prepared, so speech
        // heard is dropped and counted; with it, the tests' own copy of the model is loaded as the service
        // loads its own at the first session.
        var microphone = options.Microphone ?? new WindowsMicrophone();
        _owned.Add(microphone);
        var whisper = options.Speech
            ? new WhisperRecogniser(
                new ModelDownload(new HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null))))),
                SpeechModels.BaseEnglish,
                SpeechUnloadTests.ModelCache(),
                sizeContext: options.SizeContext)
            : new WhisperRecogniser(
                new ModelDownload(new HttpClient(new EgressGuard(new EgressPolicy()))),
                SpeechModels.BaseEnglish,
                Path.Combine(_dir, "models"));
        _owned.Add(whisper);
        ISpeechRecogniser recogniser = whisper;
        if (options.Speech)
        {
            if (!await whisper.PrepareAsync(ct))
            {
                throw new InvalidOperationException($"The speech model could not be had: {whisper.Unavailable}");
            }

            Speech = new TimedRecogniser(whisper);
            recogniser = Speech;
        }

        var narration = new NarrationRecorder(
            microphone,
            recogniser,
            (heardIn, segment, token) => Machine.TryAppendTranscriptAsync(heardIn, segment, token),
            recording: () => Machine.Hearing,
            hearing: () => scope()?.MayHearNarration == true,
            backlog: Machine.Narration);
        _ownedSync.Add(narration);
        Narration = narration;
        Machine.StateChanged += _ => narration.Nudge();
        _running.Add(narration.RunAsync(stopping));
    }

    /// <summary>The store's files on disk, the write-ahead log included.</summary>
    public long StoreBytes() =>
        new[] { string.Empty, "-wal" }
            .Select(suffix => new FileInfo(StorePath + suffix))
            .Where(file => file.Exists)
            .Sum(file => file.Length);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await Task.WhenAll(_running).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            // The loops end on the cancellation they were handed.
        }

        if (Machine is not null)
        {
            await Machine.DisposeAsync();
        }

        foreach (var owned in Enumerable.Reverse(_ownedSync))
        {
            owned.Dispose();
        }

        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        _stopping.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A file still held a moment after its owner closed it is the temp folder's to clean.
        }
    }
}

/// <param name="Microphone">The microphone the narration recorder listens to; the real one when null.</param>
/// <param name="Speech">Whether the speech model is loaded, so what the microphone hears is transcribed.</param>
/// <param name="ApplyThrottle">
/// Whether the processor throttle acts on scene sampling and OCR, as in the service. It reads the process
/// either way, so its moves are reported; a breakdown turns it off to measure a full session's work.
/// </param>
/// <param name="SizeContext">Whether the model encodes only as much context as each segment fills (AudioContext); off, as in the service.</param>
internal sealed record ServiceRigOptions(IMicrophone? Microphone = null, bool Speech = false, bool ApplyThrottle = true, bool SizeContext = false);

/// <summary>
/// The recogniser the narration recorder calls, with the time each call took. Counts and durations only:
/// what was said is passed through and never kept here (INV-10).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TimedRecogniser(ISpeechRecogniser inner) : ISpeechRecogniser
{
    private readonly Lock _gate = new();
    private readonly List<(double AudioSeconds, double WallSeconds)> _calls = [];

    public bool Ready => inner.Ready;

    /// <summary>Every call so far: the audio it was given and the time it took, oldest first.</summary>
    public IReadOnlyList<(double AudioSeconds, double WallSeconds)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public async Task<Transcribed?> TranscribeAsync(SpeechSegment segment, ReadOnlyMemory<short> audio, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return await inner.TranscribeAsync(segment, audio, ct);
        }
        finally
        {
            var took = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            lock (_gate)
            {
                _calls.Add((audio.Length / 16_000.0, took));
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
