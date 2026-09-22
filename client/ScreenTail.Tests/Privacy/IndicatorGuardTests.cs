using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// INV-4 where it is actually decided: capture stops when nothing on screen says it is happening
/// (2026-09-19 review).
///
/// The guard asks whether anything reports that it is showing the indicator. It used to ask how many
/// clients were connected, which is not the same question: a connection is not a pill (2026-09-20).
///
/// The tray icon and the pill live in the UI process, and the service records without them. It starts
/// sessions by itself when a remote tool takes the foreground, it owns the global hotkeys, and nothing
/// anywhere asked whether a UI was attached. A UI that crashed, was quit from its own menu, or was never
/// started at all left the service recording a customer's screen with no indicator of any kind.
///
/// That is invisible capture reached without touching capture — the review's own phrase for it — and it
/// is the thing INV-4 exists to forbid. These tests read the encrypted store afterwards, because what is
/// written is the claim.
/// </summary>
public sealed class IndicatorGuardTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 11, 0, 0, TimeSpan.Zero);
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private MachineHarness? _harness;

    [Fact]
    public async Task CaptureStopsWhenTheLastWindowCloses()
    {
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);

        showing = false;
        await GraceOutAsync(guard, clock, TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(guard.Holding);
    }

    [Fact]
    public async Task NothingIsWrittenWhileNobodyCanSeeThatItIsRecording()
    {
        // Suppression already means the session writes nothing (INV-6), which is why it is the right
        // mechanism here: the invariant holds the moment this takes effect, with no second rule to keep.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        showing = false;
        await GraceOutAsync(guard, clock, ct);

        Assert.False(await machine.TryStageFrameAsync(Frame("f-invisible"), ct));
        Assert.False(await machine.TryRecordEventAsync(new TypingBurstEvent { TsMs = 1_000, CharCount = 12 }, ct));

        var stored = (await _harness!.Store.LoadSessionAsync(machine.SessionId!, ct))!;
        Assert.Equal(0, await _harness.Store.CountPendingFramesAsync(machine.SessionId!, ct));
        Assert.Empty(stored.Events.OfType<TypingBurstEvent>());
    }

    [Fact]
    public async Task AWindowReopeningPutsCaptureBack()
    {
        // The technician's UI crashed and came back. One job, not two notes.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        showing = false;
        await GraceOutAsync(guard, clock, ct);
        Assert.Equal(SessionState.Suppressed, machine.State);

        showing = true;
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task ARestartingWindowDoesNotInterruptTheSession()
    {
        // The UI reconnects a moment after its own restart. Suppressing on every blink would fill the
        // timeline with intervals that mean nothing and cut a session nobody interrupted.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        showing = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        await guard.TickAsync(ct);
        showing = true;
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.Equal(0, guard.Holds);
    }

    [Fact]
    public async Task TheIntervalIsOnTheTimelineWithItsReason()
    {
        // A gap in a session is something a technician and a customer may both ask about, and "the
        // window was closed" is an answer. A silent gap is not.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        showing = false;
        await GraceOutAsync(guard, clock, ct);

        var stored = (await _harness!.Store.LoadSessionAsync(machine.SessionId!, ct))!;
        var suppressed = Assert.Single(
            stored.Events.OfType<CaptureStateEvent>(),
            e => e.State == CaptureState.Suppressed);

        Assert.Equal(CaptureStateReason.NoIndicator, suppressed.Reason);
    }

    [Fact]
    public async Task AnAttachedWindowIsAnIndicator()
    {
        var machine = await RecordingAsync();

        Assert.True(new IndicatorGuard(machine, () => true).Indicated);
        Assert.False(new IndicatorGuard(machine, () => false).Indicated);
    }

    [Fact]
    public async Task TheGuardDoesNotResumeASessionTheTechnicianPaused()
    {
        // Holding is this guard's belief, not the session's state. The other guards share the same
        // suppressed state, so it has to be re-derived or this one resumes something it never held.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = false;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        await GraceOutAsync(guard, clock, ct);
        Assert.True(await machine.PauseAsync(ct));

        showing = true;
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Paused, machine.State);
        Assert.False(guard.Holding);
    }

    /// <summary>
    /// Ticks once to notice the indicator is gone, waits past the grace, and ticks again.
    ///
    /// Two ticks because that is what the running loop does: the first pass records when the last window
    /// went, and only a later one can say it has been gone long enough. A test that suppressed on the
    /// first tick would be testing a guard with no grace period at all.
    /// </summary>
    private static async Task GraceOutAsync(IndicatorGuard guard, ManualTime clock, CancellationToken ct)
    {
        await guard.TickAsync(ct);
        clock.Advance(TimeSpan.FromSeconds(5));
        await guard.TickAsync(ct);
    }

    private async Task<SessionMachine> RecordingAsync()
    {
        _harness = await MachineHarness.StartAsync();
        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null));
        return _harness.Machine;
    }

    private static StagedFrame Frame(string id) => new(id, 1_000, FrameTrigger.Click, 100, 100, null, new byte[] { 0xAA });

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}
