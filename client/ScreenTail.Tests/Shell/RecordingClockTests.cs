using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// ST-072: the recording clock moves between the service's snapshots.
///
/// The service sends a snapshot when its state changes and not every second, so the pill, the tray and
/// the shell's status line all showed the time recording started at for the whole session (owner's
/// report, 2026-11-05). Every earlier test fed successive snapshots by hand, which is why none of them
/// noticed. These feed one snapshot and move only the clock.
/// </summary>
public sealed class RecordingClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheClockAdvancesWithTimeAndNoNewSnapshot()
    {
        var (state, time, clock) = Make();
        using var _ = clock;
        state.Connected(Recording(5_000));

        Assert.Equal("00:05", Pill(state, clock));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("00:06", Pill(state, clock));
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("01:07", Pill(state, clock));

        // Only the number moved. The state is the service's, and recording is what it said.
        Assert.Equal(CaptureStates.Recording, clock.Show(state.Snapshot.KnownCapture)!.State);
    }

    [Fact]
    public void ItTicksOnceASecondWhileRecordingAndTurnsOverOnTheSecond()
    {
        var (state, time, clock) = Make();
        using var _ = clock;
        var ticks = 0;
        var seen = new List<string>();
        clock.Ticked += () =>
        {
            ticks++;
            seen.Add(Pill(state, clock));
        };

        state.Connected(Recording(5_400));
        Assert.True(clock.Running);

        // The first tick waits for just past the next whole second (600 ms, and 50 more to be clear of
        // it), not a full second from arrival.
        time.Advance(TimeSpan.FromMilliseconds(649));
        Assert.Equal(0, ticks);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, ticks);

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(4, ticks);
        Assert.Equal(["00:06", "00:07", "00:08", "00:09"], seen);
    }

    [Fact]
    public void PausedStopsTheTimerAndShowsTheFrozenActiveTime()
    {
        var (state, time, clock) = Make();
        using var _ = clock;
        var ticks = 0;
        clock.Ticked += () => ticks++;
        state.Connected(Recording(5_000));
        time.Advance(TimeSpan.FromSeconds(3));
        var before = ticks;

        state.Observe(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "s1", Reason = CaptureReasons.User, ElapsedMs = 8_000 });
        Assert.False(clock.Running);
        Assert.Equal(0, time.Armed);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(before, ticks);
        Assert.Equal(8_000, clock.Show(state.Snapshot.KnownCapture)!.ElapsedMs);
        Assert.Equal("Paused (0:08)", ShellStatus.Describe(clock.Show(state.Snapshot)));
    }

    [Theory]
    [InlineData(CaptureStates.Idle)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    [InlineData(CaptureStates.DraftReady)]
    [InlineData(CaptureStates.DraftPending)]
    public void NothingButRecordingWakesTheTimer(string resting)
    {
        // ST-031: a UI with nothing to count does not wake to count it.
        var (state, time, clock) = Make();
        using var _ = clock;
        var ticks = 0;
        clock.Ticked += () => ticks++;

        state.Connected(new CaptureStateSnapshot { State = resting, SessionId = resting == CaptureStates.Idle ? null : "s1", ElapsedMs = resting == CaptureStates.Idle ? null : 9_000 });
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.False(clock.Running);
        Assert.Equal(0, time.Armed);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void ADroppedPipeStopsTheClockAndChangesNoState()
    {
        // INV-4, ADR-0003: a lost connection never means capture stopped. The pill goes to "unknown, may
        // still be recording", as it always did, and there is no clock to keep.
        var (state, time, clock) = Make();
        using var _ = clock;
        state.Connected(Recording(5_000));
        state.Lost();

        Assert.False(clock.Running);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("Capture state unknown", HudState.For(clock.Show(state.Snapshot.KnownCapture)).State.Text);
    }

    [Fact]
    public void ANewSnapshotIsTheNewBase()
    {
        var (state, time, clock) = Make();
        using var _ = clock;
        state.Connected(Recording(5_000));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("00:15", Pill(state, clock));

        // The service says more than the local count: it is the authority, and the clock jumps to it.
        state.Observe(Recording(20_000));
        Assert.Equal("00:20", Pill(state, clock));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("00:22", Pill(state, clock));
    }

    [Fact]
    public void TheSameSnapshotAgainIsNotANewArrival()
    {
        // The store raises on a navigation too, with the snapshot it already had. Taking that as the
        // snapshot arriving again would reset the clock to the number in it.
        var (state, time, clock) = Make();
        using var _ = clock;
        state.Connected(Recording(5_000));
        time.Advance(TimeSpan.FromSeconds(10));
        state.Navigate(ShellView.History);

        Assert.Equal("00:15", Pill(state, clock));
    }

    [Fact]
    public void TheClockNeverRunsBackwardsWithinASession()
    {
        var (state, time, clock) = Make();
        using var _ = clock;
        state.Connected(Recording(5_000));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("00:15", Pill(state, clock));

        // A snapshot that left the service before the pill had counted this far, or one that spent longer
        // on the pipe: the pill holds what it showed and goes on from there.
        state.Observe(Recording(12_000));
        Assert.Equal("00:15", Pill(state, clock));
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("00:16", Pill(state, clock));

        // Paused with less than was shown holds too.
        state.Observe(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "s1", Reason = CaptureReasons.User, ElapsedMs = 14_000 });
        Assert.Equal(16_000, clock.Show(state.Snapshot.KnownCapture)!.ElapsedMs);
    }

    [Fact]
    public void ANewSessionStartsFromItsOwnTime()
    {
        // The floor is per session. The next one starts at its own zero, not where the last one stopped.
        var (state, time, clock) = Make();
        using var _ = clock;
        state.Connected(Recording(600_000));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("10:01", Pill(state, clock));

        state.Observe(new CaptureStateSnapshot { State = CaptureStates.DraftReady, SessionId = "s1", ElapsedMs = 601_000 });
        state.Observe(Recording(0, session: "s2"));
        Assert.Equal("00:00", Pill(state, clock));
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("00:03", Pill(state, clock));
    }

    [Fact]
    public void ResumingCountsOnFromTheResumedSnapshot()
    {
        var (state, time, clock) = Make();
        using var _ = clock;
        state.Connected(Recording(5_000));
        time.Advance(TimeSpan.FromSeconds(3));
        state.Observe(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "s1", Reason = CaptureReasons.User, ElapsedMs = 8_000 });
        time.Advance(TimeSpan.FromMinutes(2));

        state.Observe(Recording(8_000));
        Assert.True(clock.Running);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("00:10", Pill(state, clock));
    }

    [Fact]
    public void TheTrayAndTheShellStatusMoveWithIt()
    {
        var (state, time, clock) = Make();
        using var _ = clock;
        state.Connected(Recording(5_000, tool: "screenconnect"));
        time.Advance(TimeSpan.FromSeconds(70));

        var shown = clock.Show(state.Snapshot);
        Assert.Equal("ScreenTail · Recording: screenconnect (1:15)", TrayPresence.From(shown).Tooltip);
        Assert.Equal("Recording: screenconnect (1:15)", ShellStatus.Describe(shown));

        // What the store holds is still what the service said.
        Assert.Equal(5_000, state.Snapshot.Capture!.ElapsedMs);
    }

    [Fact]
    public void TheAnnouncementDoesNotMoveWithTheClock()
    {
        // A screen reader hears the state when it changes, not every second (ST-084).
        var (state, time, clock) = Make();
        using var _ = clock;
        state.Connected(Recording(5_000));
        var first = HudState.For(clock.Show(state.Snapshot.KnownCapture));
        time.Advance(TimeSpan.FromSeconds(5));
        var later = HudState.For(clock.Show(state.Snapshot.KnownCapture));

        Assert.NotEqual(first.State.Text, later.State.Text);
        Assert.Equal(first.Announcement, later.Announcement);
    }

    [Theory]
    [InlineData(-16, 16)]
    [InlineData(-15, -15)]
    [InlineData(16, 16)]
    [InlineData(-16, 0)]
    public void EverySecondIsShownExactlyOnceWhenTheTimerLandsEarlyOrLate(int early, int late)
    {
        // Windows runs these timers on its interrupt clock, about 15.6 ms a step, so a tick lands a little
        // either side of where it was aimed. Aimed at the boundary, early shows the old second again and a
        // fixed period drifts through it: 00:05, 00:05, 00:07 (2026-11-05 review).
        var state = new ShellState();
        var time = new TickingTime(Start, n => TimeSpan.FromMilliseconds(n % 2 == 0 ? early : late));
        using var clock = new RecordingClock(state, time);
        var seen = new List<string>();
        clock.Ticked += () => seen.Add(Pill(state, clock));
        state.Connected(Recording(5_000));

        time.Advance(TimeSpan.FromMinutes(2));

        // One tick a second, each showing the next second: no second twice, none missed. Two minutes from
        // 00:05 is 02:05, and that tick is due just after the move ends, so 00:06 to 02:04.
        var expected = Enumerable.Range(6, 119).Select(s => HudState.Elapsed(s * 1_000L)).ToList();
        Assert.Equal(expected, seen);
    }

    [Fact]
    public void AHandlerThatThrowsStopsNeitherTheOthersNorTheClock()
    {
        // The tick runs on a timer thread. A handler's exception there would end the UI process, and a
        // multicast invoke would stop at it, leaving every later view's clock still.
        var (state, time, clock) = Make();
        using var _ = clock;
        var later = new List<string>();
        clock.Ticked += () => throw new InvalidOperationException("a view that broke");
        clock.Ticked += () => later.Add(Pill(state, clock));
        state.Connected(Recording(5_000));

        time.Advance(TimeSpan.FromSeconds(3.1));

        Assert.Equal(["00:06", "00:07", "00:08"], later);
        Assert.Equal(3, clock.HandlerFailures);
        Assert.True(clock.Running);
    }

    [Fact]
    public void ChangesHeardOutOfOrderLeaveTheClockOnWhatTheStoreHolds()
    {
        // The store raises its change outside its lock, so a change can reach the clock after a later
        // one. Here the later one (the pipe dropping) is raised from inside the earlier one's raise, so the
        // clock hears "lost" first and then the handshake's "recording" (2026-11-05 review).
        var state = new ShellState();
        var time = new TickingTime(Start);
        var dropped = false;
        state.Changed += _ =>
        {
            if (!dropped)
            {
                dropped = true;
                state.Lost();
            }
        };
        using var clock = new RecordingClock(state, time);

        state.Connected(Recording(5_000));

        Assert.Equal(ServiceConnection.Unavailable, state.Snapshot.Connection);
        Assert.False(clock.Running);
        Assert.Equal(0, time.Armed);
    }

    [Fact]
    public void DisposingStopsTheTimer()
    {
        var (state, time, clock) = Make();
        state.Connected(Recording(5_000));
        clock.Dispose();

        Assert.Equal(0, time.Live);
        state.Observe(Recording(6_000));
        Assert.Equal(0, time.Live);
    }

    private static (ShellState State, TickingTime Time, RecordingClock Clock) Make()
    {
        var state = new ShellState();
        var time = new TickingTime(Start);
        return (state, time, new RecordingClock(state, time));
    }

    private static string Pill(ShellState state, RecordingClock clock) =>
        HudState.For(clock.Show(state.Snapshot.KnownCapture)).State.Text;

    private static CaptureStateSnapshot Recording(long elapsedMs, string session = "s1", string? tool = null) =>
        new() { State = CaptureStates.Recording, SessionId = session, ElapsedMs = elapsedMs, RemoteTool = tool };
}
