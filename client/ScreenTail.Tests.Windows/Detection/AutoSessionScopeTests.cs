using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Detection;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Detection;

/// <summary>
/// Which window the capture loops are pointed at, and when a session starts and stops on its own
/// (ST-023; found 2026-09-21 while reviewing for efficiency, extended for the 2026-10-12 review).
///
/// <c>CurrentScope</c> is what the screenshot capturer asks before taking a frame, and it carries the
/// window handle the frame is expected to belong to. It was only refreshed when the scope or the tool
/// changed — so a technician moving between two windows of the same remote tool, which is what a second
/// customer session looks like, left the old handle in place.
///
/// <c>CaptureForegroundWindow(expected:)</c> then finds the foreground window is not the one it was told
/// to expect and returns nothing. No error, no interval on the timeline: the frames are simply missing
/// from the half of the session that happened in the second window.
///
/// The fixture can say no: the indicator can be off and auto-start can be off, because the only fixture
/// used to pass <c>() => true</c> and the INV-4 refusal was never exercised (T-26). Nothing here needs a
/// desktop; the coordinator is driven directly and the machine writes to a database in the temp folder.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AutoSessionScopeTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly RemoteToolRegistry Shipped = RemoteToolRegistry.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));

    [Fact]
    public async Task TheSecondWindowOfTheSameToolIsTheOneCaptureFollows()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        await coordinator.HandleAsync(Window(handle: 222), ct);

        Assert.Equal(222, coordinator.CurrentScope?.Window);
    }

    [Fact]
    public async Task TheFirstWindowIsStillWhatItSays()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);

        await coordinator.HandleAsync(Window(handle: 111), ct);

        Assert.Equal(CaptureScope.RemoteTool, coordinator.CurrentScope?.Scope);
        Assert.Equal(111, coordinator.CurrentScope?.Window);
    }

    [Fact]
    public async Task ATicketInTheTitleIsRememberedAndTheClipboardIsLeftAlone()
    {
        // ST-077 AC1: "#48213" in the window → the session carries 48213 for Review to pre-select. The
        // title said enough, so the clipboard was never asked.
        var ct = TestContext.Current.CancellationToken;
        var reads = 0;
        var coordinator = await CoordinatorAsync(ct, clipboard: () => { reads++; return "99999"; });

        await coordinator.HandleAsync(Window(handle: 111, title: "Ticket #48213 - Printer offline - Remote Desktop"), ct);

        var session = (await _store!.LoadSessionAsync(_machine!.SessionId!, ct))!;
        Assert.Equal("48213", session.SuggestedTicket);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task WithNothingInTheTitleTheClipboardIsReadOnceAtStart()
    {
        // AC3: once, at start. A second foreground change in the same session reads nothing. The clipboard
        // introduces its number as a ticket: a bare number copied whole is a one-time code or a PIN more
        // often than a ticket, and does not count (TicketHint, 2026-10-12 review S-45).
        var ct = TestContext.Current.CancellationToken;
        var reads = 0;
        var coordinator = await CoordinatorAsync(ct, clipboard: () => { reads++; return "Ticket #50011"; });

        await coordinator.HandleAsync(Window(handle: 111), ct);
        await coordinator.HandleAsync(Window(handle: 222), ct);

        var session = (await _store!.LoadSessionAsync(_machine!.SessionId!, ct))!;
        Assert.Equal("50011", session.SuggestedTicket);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task NoTicketAnywhereMeansNoSuggestion()
    {
        // AC2: no match → the picker is as before. Nothing invented from a clipboard full of other things.
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, clipboard: () => "call 0161 496 0123");

        await coordinator.HandleAsync(Window(handle: 111), ct);

        Assert.Null((await _store!.LoadSessionAsync(_machine!.SessionId!, ct))!.SuggestedTicket);
    }

    // ---- INV-4 and the auto-start switch (T-26) -------------------------------------------------------

    [Fact]
    public async Task NoSessionStartsWhileNothingShowsThatCaptureIsOn()
    {
        // INV-4: a session that starts with no indicator is a recording nobody was told about. The tool
        // takes focus, nothing is on screen to say so, and nothing begins.
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, indicated: () => false);

        await coordinator.HandleAsync(Window(handle: 111), ct);

        Assert.Equal(SessionState.Idle, _machine!.State);
        Assert.Null(_machine.SessionId);

        // The refusal leaves the trigger disarmed for nothing: it does not believe a session is running.
        Assert.False(_trigger!.SessionRunning);
    }

    [Fact]
    public async Task AutoStartOffWaitsForTheChord()
    {
        // Settings → Capture "Auto-start" off (ST-080): the tool is in scope, and the session waits for
        // Ctrl+Alt+R. The chord then starts it, and the trigger follows the start it did not make.
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, autoStart: () => false);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        Assert.Equal(SessionState.Idle, _machine!.State);

        Assert.True(await coordinator.StartFromForegroundAsync(ct));

        Assert.Equal(SessionState.Recording, _machine.State);
        Assert.True(_trigger!.SessionRunning);
    }

    [Fact]
    public async Task TheChordIsRefusedTooWhileNothingShowsThatCaptureIsOn()
    {
        // The manual path has the same gate as the automatic one.
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, indicated: () => false);

        Assert.False(await coordinator.StartFromForegroundAsync(ct));
        Assert.Equal(SessionState.Idle, _machine!.State);
    }

    // ---- an update being installed (ST-113) ---------------------------------------------------------

    [Fact]
    public async Task NoSessionStartsOnItsOwnWhileAnUpdateIsBeingInstalled()
    {
        // Windows Installer ends the service to replace it, so the tool taking focus starts nothing while
        // the update holds starts back, and starts a session once the hold is released.
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);
        var hold = await _machine!.HoldStartsAsync(ct);
        Assert.NotNull(hold);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        Assert.Equal(SessionState.Idle, _machine.State);
        Assert.Null(_machine.SessionId);

        hold!.Dispose();
        await coordinator.HandleAsync(Window(handle: 222), ct);
        Assert.Equal(SessionState.Recording, _machine.State);
    }

    [Fact]
    public async Task WhenTheHoldIsReleasedTheWindowInFrontIsDecidedAgain()
    {
        // 2026-10-29 review: the tool took focus while Windows Installer ran and was refused. The
        // technician cancels at the prompt, the hold goes, and the tool still in front starts its session
        // without the technician having to click away and back.
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);
        var hold = await _machine!.HoldStartsAsync(ct);
        await coordinator.HandleAsync(Window(handle: 111), ct);
        Assert.Equal(SessionState.Idle, _machine.State);

        hold!.Dispose();
        Assert.True(await coordinator.RetryAsync(Window(handle: 111), _ => true, ct));

        Assert.Equal(SessionState.Recording, _machine.State);
    }

    [Fact]
    public async Task ARetryReadsTheWindowInFrontNowAndSkipsOneThatIsGone()
    {
        // Second review: the window the hold refused may have closed by the time the hold goes. The retry
        // asks for the window in front now, and does nothing when there is none or it no longer exists,
        // rather than replaying the window last decided.
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);
        var hold = await _machine!.HoldStartsAsync(ct);
        await coordinator.HandleAsync(Window(handle: 111), ct);
        hold!.Dispose();

        Assert.False(await coordinator.RetryAsync(Window(handle: 111), _ => false, ct));
        Assert.False(await coordinator.RetryAsync(ForegroundWindowInfo.None(At), _ => true, ct));
        Assert.Equal(SessionState.Idle, _machine.State);

        Assert.True(await coordinator.RetryAsync(Window(handle: 333), handle => handle == 333, ct));
        Assert.Equal(SessionState.Recording, _machine.State);
    }

    [Fact]
    public async Task TheChordStartsNothingWhileAnUpdateIsBeingInstalled()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, autoStart: () => false);
        using var hold = await _machine!.HoldStartsAsync(ct);
        Assert.NotNull(hold);

        Assert.False(await coordinator.StartFromForegroundAsync(ct));
        Assert.Equal(SessionState.Idle, _machine.State);
    }

    // ---- the trigger follows the machine (S-6, T-22) ------------------------------------------------

    [Fact]
    public async Task ARefusedStartIsRetriedWhenTheToolTakesFocusAgain()
    {
        // The trigger used to mark the session running the moment it decided to start one, whether or
        // not the machine then did. A refused start left it believing a session was under way, so the
        // next remote-tool focus was "no change" and nothing started until a full grace had passed away
        // from the tool (2026-10-12 review, T-22).
        var ct = TestContext.Current.CancellationToken;
        var showing = false;
        var coordinator = await CoordinatorAsync(ct, indicated: () => showing);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        Assert.Equal(SessionState.Idle, _machine!.State);

        showing = true;
        await coordinator.HandleAsync(Window(handle: 222), ct);

        Assert.Equal(SessionState.Recording, _machine.State);
    }

    [Fact]
    public async Task AManualStopDoesNotDisarmAutoStart()
    {
        // The technician stops the session from the tray while still in the remote tool. The trigger has to
        // hear of it, or it keeps believing a session is running and the next remote-tool focus starts
        // nothing (2026-10-12 review, S-6).
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        Assert.Equal(SessionState.Recording, _machine!.State);

        Assert.True(await _machine.StopAsync(ct));
        Assert.False(_trigger!.SessionRunning);

        await coordinator.HandleAsync(Window(handle: 222), ct);

        Assert.Equal(SessionState.Recording, _machine.State);
    }

    [Fact]
    public async Task AManualStartOutsideAnyToolKeepsRunningWhileItsWindowIsInFront()
    {
        // Ctrl+Alt+R over Outlook starts a session as "other". Once the trigger followed manual starts, the
        // grace ran from the start and ended the session while the technician was still working in the
        // window they started it in; main kept it running (c8 blocker). That window counts as the
        // session's tool for as long as it is in front.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTime(At);
        var coordinator = await CoordinatorAsync(ct, time: clock);

        await coordinator.HandleAsync(Window(handle: 333, process: "outlook", title: "Inbox"), ct);
        Assert.True(await coordinator.StartFromForegroundAsync(ct));
        Assert.Equal(SessionState.Recording, _machine!.State);
        Assert.True(_trigger!.SessionRunning);

        await TickForAsync(coordinator, clock, TimeSpan.FromSeconds(300), ct);

        Assert.Equal(SessionState.Recording, _machine.State);
        Assert.True(_trigger.SessionRunning);
    }

    [Fact]
    public async Task AManualStartOutsideAnyToolStopsOnceItsWindowIsLeftForTheGrace()
    {
        // The control: the grace still ends a session started by hand (S-6), counted from when the
        // technician leaves the window it was started in, so the microphone is not left open for good.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTime(At);
        var coordinator = await CoordinatorAsync(ct, time: clock);

        await coordinator.HandleAsync(Window(handle: 333, process: "outlook", title: "Inbox"), ct);
        Assert.True(await coordinator.StartFromForegroundAsync(ct));
        await TickForAsync(coordinator, clock, TimeSpan.FromSeconds(300), ct);

        await coordinator.HandleAsync(Window(handle: 444, process: "notepad", title: "Untitled"), ct);
        await TickForAsync(coordinator, clock, TimeSpan.FromSeconds(85), ct);
        Assert.Equal(SessionState.Recording, _machine!.State);

        await TickForAsync(coordinator, clock, TimeSpan.FromSeconds(10), ct);

        Assert.NotEqual(SessionState.Recording, _machine.State);
        Assert.False(_trigger!.SessionRunning);
    }

    [Fact]
    public async Task ASessionInTheToolPastTheGraceKeepsRunning()
    {
        // The technician works in one remote tool window for five minutes. Nothing moves in the foreground,
        // and the auto-stop timer ended the session ninety seconds in, because the grace ran from when the
        // tool took focus rather than from when it lost it (c8 blocker). Each tick with the tool in front
        // starts the grace over.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTime(At);
        var coordinator = await CoordinatorAsync(ct, time: clock);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        Assert.Equal(SessionState.Recording, _machine!.State);

        await TickForAsync(coordinator, clock, TimeSpan.FromSeconds(300), ct);

        Assert.Equal(SessionState.Recording, _machine.State);
        Assert.True(_trigger!.SessionRunning);
    }

    // ---- auto-stop (T-28) ------------------------------------------------------------------------------

    [Fact]
    public async Task TheSessionStopsOnceTheGraceRunsOutAwayFromTheTool()
    {
        // The whole stop path, through the coordinator and into the machine: the tool took focus, the
        // technician moved to Outlook and stayed there past the grace, and the next foreground change
        // after that ends the session.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTime(At);
        var coordinator = await CoordinatorAsync(ct, time: clock);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        Assert.Equal(SessionState.Recording, _machine!.State);

        clock.Advance(TimeSpan.FromSeconds(30));
        await coordinator.HandleAsync(Window(handle: 333, process: "outlook", title: "Inbox"), ct);
        Assert.Equal(SessionState.Recording, _machine.State);

        clock.Advance(TimeSpan.FromSeconds(61));
        await coordinator.HandleAsync(Window(handle: 444, process: "outlook", title: "Ticket"), ct);

        Assert.NotEqual(SessionState.Recording, _machine.State);
        Assert.False(_trigger!.SessionRunning);
    }

    [Fact]
    public async Task AnIdleDesktopStillEndsTheSessionThroughTheCoordinator()
    {
        // The technician leaves the tool for Outlook and then walks away: no foreground change follows, so
        // the coordinator's own tick has to carry the stop. Driven by the injected clock rather than by
        // waiting for the timer.
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTime(At);
        var coordinator = await CoordinatorAsync(ct, time: clock);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        await coordinator.HandleAsync(Window(handle: 333, process: "outlook", title: "Inbox"), ct);

        clock.Advance(TimeSpan.FromSeconds(89));
        await coordinator.TickOnceAsync(ct);
        Assert.Equal(SessionState.Recording, _machine!.State);

        clock.Advance(TimeSpan.FromSeconds(2));
        await coordinator.TickOnceAsync(ct);

        Assert.NotEqual(SessionState.Recording, _machine.State);
        Assert.False(_trigger!.SessionRunning);
    }

    // ---- a policy change re-decides the window in front (T-30) ----------------------------------------

    [Fact]
    public async Task TurningAllWindowsOffStopsCapturingTheWindowAlreadyInFront()
    {
        // "All windows" is on and Outlook is being photographed. The technician (or the tenant's policy)
        // turns it off. Nothing about the foreground changes, so until now nothing re-decided Outlook, and
        // it stayed in scope until the next focus change (INV-5, INV-11; 2026-10-12 review, T-30).
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTime(At);
        var coordinator = await CoordinatorAsync(ct, time: clock, options: new ScopeOptions { CaptureAllWindows = true });

        await coordinator.HandleAsync(Window(handle: 111), ct);
        await coordinator.HandleAsync(Window(handle: 333, process: "outlook", title: "Inbox"), ct);
        Assert.True(coordinator.CurrentScope?.MayCaptureFrames);
        Assert.Equal(333, coordinator.CurrentScope?.Window);

        _policy!.Apply(new ScopeOptions { CaptureAllWindows = false });

        // At once, for the next click or scene sample: no frame of Outlook is taken under the old decision.
        Assert.False(coordinator.CurrentScope?.MayCaptureFrames);
        Assert.Equal(CaptureScope.OutOfScope, coordinator.CurrentScope?.Scope);

        // And on the next tick the change is written down, so Review shows where capture stopped.
        await coordinator.TickOnceAsync(ct);
        var session = (await _store!.LoadSessionAsync(_machine!.SessionId!, ct))!;
        var focus = session.Events.OfType<FocusEvent>().ToList();
        Assert.Contains(focus, e => e.Process == "outlook" && e.Scope == CaptureScope.AdminTool);
        Assert.Contains(focus, e => e.Process == "outlook" && e.Scope == CaptureScope.OutOfScope);
    }

    [Fact]
    public async Task APolicyThatChangesNothingAboutTheWindowInFrontWritesNothing()
    {
        // The control: a re-decision with the same answer is not a timeline entry.
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        _policy!.Apply(new ScopeOptions { CaptureAllWindows = true });
        await coordinator.TickOnceAsync(ct);

        var session = (await _store!.LoadSessionAsync(_machine!.SessionId!, ct))!;
        Assert.Single(session.Events.OfType<FocusEvent>());
        Assert.Equal(111, coordinator.CurrentScope?.Window);
    }

    /// <summary>Runs the auto-stop timer as production does, a tick every five seconds, for this long.</summary>
    private static async Task TickForAsync(AutoSessionCoordinator coordinator, ManualTime clock, TimeSpan span, CancellationToken ct)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromSeconds(5))
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await coordinator.TickOnceAsync(ct);
        }
    }

    private async Task<AutoSessionCoordinator> CoordinatorAsync(
        CancellationToken ct,
        Func<string?>? clipboard = null,
        Func<bool>? indicated = null,
        Func<bool>? autoStart = null,
        TimeProvider? time = null,
        ScopeOptions? options = null)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)), ct: ct);
        _machine = new SessionMachine(_store, new NoCaptureSources(), new UnavailableDrafter());
        _policy = new ScopePolicy(Shipped, options);
        _trigger = new SessionTrigger(_policy, time, TimeSpan.FromSeconds(90));
        return new AutoSessionCoordinator(
            _machine,
            _policy,
            _trigger,
            indicated ?? (() => true),
            NullLogger.Instance,
            () => (false, "default"),
            clipboard,
            autoStart: autoStart,
            time: time);
    }

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"st-scope-{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;
    private SessionMachine? _machine;
    private ScopePolicy? _policy;
    private SessionTrigger? _trigger;

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_machine is not null)
        {
            await _machine.DisposeAsync();
        }

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        File.Delete(_path);
    }

    /// <summary>A window of one remote tool by default: same process, same tool id, different handle.</summary>
    private static ForegroundWindowInfo Window(nint handle, string title = "Remote Desktop", string process = "mstsc") =>
        new(handle, 100, process, title, "Window", null, false, At);
}
