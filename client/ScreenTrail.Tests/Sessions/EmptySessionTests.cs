using ScreenTrail.Core.History;
using ScreenTrail.Core.Sessions;
using ScreenTrail.Core.Store;
using ScreenTrail.Shared.Ipc;
using ScreenTrail.Shared.Schema;

namespace ScreenTrail.Tests.Sessions;

/// <summary>
/// A session with nothing in it is not drafted (M1, 2026-11-13). A brief AnyDesk focus started a session,
/// as it should (the pill showed from the first moment, INV-4), and the session ended eighteen seconds
/// later as a draft about nothing: a "Draft ready" toast, a row to check, and a request to the backend.
/// The rule, first on its own and then through the machine with the production minimum.
/// </summary>
public sealed class EmptySessionTests
{
    private static readonly RemoteTool AnyDesk = new() { Kind = RemoteToolKind.Anydesk };
    private static readonly TimeSpan Minimum = EmptySession.Minimum;

    // ---- the rule ------------------------------------------------------------------------------------

    [Fact]
    public void TheMachineRunsTheRuleByDefault()
    {
        // The rule is the product's behaviour, not something the host has to remember to turn on.
        Assert.Equal(EmptySession.Minimum, new SessionMachineOptions().EmptySessionMinimum);
        Assert.Equal(TimeSpan.FromSeconds(60), EmptySession.Minimum);
    }

    [Fact]
    public void EighteenSecondsWithNothingInThemIsEmpty()
    {
        // The M1 session: the tool took focus, the technician moved on, nothing was clicked, said or kept.
        var verdict = EmptySession.Judge(SessionOf(18_000, Recording(0), Focus(5, CaptureScope.RemoteTool), Focus(2_000, CaptureScope.OutOfScope), Finalizing(18_000)), Minimum);

        Assert.True(verdict.Empty);
        Assert.Equal(18_000, verdict.DurationMs);
        Assert.Equal(2_000, verdict.InScopeMs);
        Assert.Equal(4, verdict.Events);
    }

    [Fact]
    public void ABriefFocusFollowedByTheWholeGraceIsEmpty()
    {
        // With the default grace the same pass through the tool ends ninety seconds later. The session is
        // long; the time in the tool is three seconds. The minimum is about the second.
        var verdict = EmptySession.Judge(SessionOf(93_000, Recording(0), Focus(3_000, CaptureScope.OutOfScope), Finalizing(93_000)), Minimum);

        Assert.True(verdict.Empty);
        Assert.Equal(3_000, verdict.InScopeMs);
    }

    [Fact]
    public void ClicksOutsideScopeDoNotMakeASessionWorthKeeping()
    {
        // Clicking around Outlook while the grace runs out is not remote work, and those clicks are kept
        // without their position (INV-6): a time and a button, nothing a note could say.
        var verdict = EmptySession.Judge(
            SessionOf(95_000, Recording(0), Focus(2_000, CaptureScope.OutOfScope), Click(10_000, positioned: false), Click(40_000, positioned: false), Finalizing(95_000)),
            Minimum);

        Assert.True(verdict.Empty);
    }

    [Fact]
    public void AClickInACapturedWindowIsKept()
    {
        Assert.False(EmptySession.Judge(SessionOf(18_000, Recording(0), Click(1_000, positioned: true)), Minimum).Empty);

        // Without a position too: in scope, a click lost its position only because the window in front
        // moved under it, and it is still the technician working in the tool.
        Assert.False(EmptySession.Judge(SessionOf(18_000, Recording(0), Click(1_000, positioned: false)), Minimum).Empty);
    }

    [Fact]
    public void ACapturedWindowInFrontForTheMinimumIsKept()
    {
        // A minute watching a remote screen with nothing to click may still be billable time.
        Assert.False(EmptySession.Judge(SessionOf(60_000, Recording(0)), Minimum).Empty);
        Assert.True(EmptySession.Judge(SessionOf(59_999, Recording(0)), Minimum).Empty);

        // An admin tool beside the session is in scope as much as the remote tool is.
        var admin = EmptySession.Judge(
            SessionOf(150_000, Recording(0), Focus(1_000, CaptureScope.AdminTool), Focus(70_000, CaptureScope.OutOfScope)),
            Minimum);
        Assert.False(admin.Empty);
        Assert.Equal(70_000, admin.InScopeMs);
    }

    [Fact]
    public void TimeInAnExcludedOrOutOfScopeWindowDoesNotCountTowardsTheMinimum()
    {
        var verdict = EmptySession.Judge(
            SessionOf(200_000, Recording(0), Focus(1_000, CaptureScope.Excluded), Focus(100_000, CaptureScope.OutOfScope), Focus(150_000, CaptureScope.RemoteTool), Focus(151_000, CaptureScope.OutOfScope)),
            Minimum);

        Assert.True(verdict.Empty);
        Assert.Equal(2_000, verdict.InScopeMs);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void AnythingTheTechnicianDidInScopeIsKept(SessionEvent action)
    {
        Assert.False(EmptySession.Judge(SessionOf(10_000, Recording(0), action), Minimum).Empty);
    }

    public static TheoryData<SessionEvent> Actions() =>
    [
        new TypingBurstEvent { TsMs = 1_000, CharCount = 12 },
        new ShortcutEvent { TsMs = 1_000 },
        new EnterEvent { TsMs = 1_000 },
        new MarkerEvent { TsMs = 1_000 },
        new NarrationEvent { TsMs = 1_000, SegmentId = "t-1" },
        new CaptureStateEvent { TsMs = 1_000, State = Shared.Schema.CaptureState.Paused },
        new CaptureStateEvent { TsMs = 1_000, State = Shared.Schema.CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField },
    ];

    [Fact]
    public void ASessionWithAnythingKeptOrLostIsKept()
    {
        var empty = SessionOf(10_000, Recording(0));
        Assert.True(EmptySession.Judge(empty, Minimum).Empty);

        Assert.False(EmptySession.Judge(empty with { Frames = [Frame()] }, Minimum).Empty);
        Assert.False(EmptySession.Judge(empty with { FramesPurgedUnredacted = 1 }, Minimum).Empty);
        Assert.False(EmptySession.Judge(empty with { Transcript = [new TranscriptSegment { Id = "t-1", TsMs = 1_000, EndMs = 2_000, Speaker = Speaker.Tech, Text = "ok" }] }, Minimum).Empty);
        Assert.False(EmptySession.Judge(empty, Minimum, unheard: 1).Empty);
        Assert.False(EmptySession.Judge(empty with { PartialCapture = true }, Minimum).Empty);
    }

    [Fact]
    public void AMinimumOfZeroKeepsEverySession()
    {
        Assert.False(EmptySession.Judge(SessionOf(1_000, Recording(0)), TimeSpan.Zero).Empty);
    }

    // ---- through the machine -------------------------------------------------------------------------

    [Fact]
    public async Task AnEmptySessionIsDiscardedNotDraftedAndTheMachineGoesIdle()
    {
        var ct = TestContext.Current.CancellationToken;
        var drafter = new CountingDrafter();
        await using var harness = await MachineHarness.StartAsync(drafter: drafter, emptySessionMinimum: Minimum);
        var machine = harness.Machine;
        var states = new List<string>();
        machine.StateChanged += s => states.Add(s.State);
        (string Id, EmptySessionVerdict Verdict)? dropped = null;
        machine.DroppedEmpty += (id, verdict) => dropped = (id, verdict);

        Assert.True(await machine.StartAsync(AnyDesk, ct: ct));
        var id = machine.SessionId!;
        Assert.True(await machine.TryRecordEventAsync(new FocusEvent { TsMs = machine.NowMs, Process = "AnyDesk", Scope = CaptureScope.RemoteTool }, ct));
        Assert.True(await machine.TryRecordEventAsync(new FocusEvent { TsMs = machine.NowMs, Process = "OUTLOOK", Scope = CaptureScope.OutOfScope }, ct));
        Assert.True(await machine.StopAsync(ct));

        // Nothing drafted, so no toast, no row to check and nothing queued for the backend.
        Assert.Equal(0, drafter.Calls);
        Assert.Equal(SessionState.Idle, machine.State);
        Assert.Null(machine.SessionId);
        Assert.Equal([CaptureStates.Recording, CaptureStates.Finalizing, CaptureStates.Idle], states);
        Assert.Equal(0, machine.Snapshot.DraftsReady);

        // History keeps a quiet row: discarded, under Earlier, with nothing left in it.
        var row = Assert.Single(await harness.Store.ListSessionsAsync(ct: ct));
        Assert.Equal(id, row.Id);
        Assert.Equal(SessionStatus.Discarded, SessionHistory.StatusOf(row));
        Assert.Equal(0, row.Frames);
        Assert.True(row.RawPurged);
        Assert.Null(row.Title);
        Assert.Empty(await harness.Store.ListUnfinishedDiscardsAsync(ct));

        // The audit log says a session was discarded and held no screenshots.
        var audit = Assert.Single(await harness.Store.GetAuditRecordsAsync(id, ct), r => r.Type == AuditTypes.SessionDiscarded);
        Assert.Equal(0, audit.Count);

        // What the host logs: the session's id and numbers, nothing else.
        Assert.NotNull(dropped);
        Assert.Equal(id, dropped.Value.Id);
        Assert.True(dropped.Value.Verdict.Empty);
        Assert.Equal(4, dropped.Value.Verdict.Events);
    }

    [Fact]
    public async Task TheNextSessionStartsAsUsualAfterOneWasDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await MachineHarness.StartAsync(drafter: new CountingDrafter(), emptySessionMinimum: Minimum);

        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));
        Assert.True(await harness.Machine.StopAsync(ct));
        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));

        Assert.Equal(SessionState.Recording, harness.Machine.State);
    }

    [Fact]
    public async Task ASessionWithSomethingSaidIsDraftedAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        var drafter = new CountingDrafter();
        await using var harness = await MachineHarness.StartAsync(drafter: drafter, emptySessionMinimum: Minimum);

        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));
        Assert.True(await harness.Machine.TryAppendTranscriptAsync(new TranscriptSegment { Id = "t-1", TsMs = harness.Machine.NowMs, EndMs = harness.Machine.NowMs + 500, Speaker = Speaker.Tech, Text = "restarting the spooler" }, ct));
        Assert.True(await harness.Machine.StopAsync(ct));

        Assert.Equal(1, drafter.Calls);
        Assert.Equal(SessionState.DraftReady, harness.Machine.State);
    }

    [Fact]
    public async Task ASessionWithAClickInTheToolIsDraftedAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        var drafter = new CountingDrafter();
        await using var harness = await MachineHarness.StartAsync(drafter: drafter, emptySessionMinimum: Minimum);

        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));
        Assert.True(await harness.Machine.TryRecordEventAsync(new ClickEvent { TsMs = harness.Machine.NowMs, X = 10, Y = 10, Button = MouseButton.Left }, ct));
        Assert.True(await harness.Machine.StopAsync(ct));

        Assert.Equal(1, drafter.Calls);
        Assert.Equal(SessionState.DraftReady, harness.Machine.State);
    }

    [Fact]
    public async Task AMarkedMomentOrAPauseIsTheTechniciansAndIsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var drafter = new CountingDrafter();
        await using var harness = await MachineHarness.StartAsync(drafter: drafter, emptySessionMinimum: Minimum);

        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));
        Assert.True(await harness.Machine.MarkMomentAsync(ct));
        Assert.True(await harness.Machine.StopAsync(ct));
        Assert.Equal(SessionState.DraftReady, harness.Machine.State);

        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));
        Assert.True(await harness.Machine.PauseAsync(ct));
        Assert.True(await harness.Machine.StopAsync(ct));
        Assert.Equal(SessionState.DraftReady, harness.Machine.State);

        Assert.Equal(2, drafter.Calls);
    }

    [Fact]
    public async Task AScreenshotLostToRedactionIsSomethingOnScreenAndIsKept()
    {
        // No redaction worker runs here, so the frame is still waiting when finalize gives up on it and is
        // counted lost. Something was on screen that the note has to hedge about.
        var ct = TestContext.Current.CancellationToken;
        var drafter = new CountingDrafter();
        await using var harness = await MachineHarness.StartAsync(drafter: drafter, emptySessionMinimum: Minimum);

        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));
        Assert.True(await harness.Machine.TryStageFrameAsync(new StagedFrame("f-1", harness.Machine.NowMs, FrameTrigger.Click, 100, 100, null, new byte[] { 0xAA }), ct));
        Assert.True(await harness.Machine.StopAsync(ct));

        Assert.Equal(1, drafter.Calls);
        Assert.Equal(SessionState.DraftReady, harness.Machine.State);
    }

    [Fact]
    public async Task NarrationStillBeingTranscribedAtTheEndIsSomethingSaidAndIsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var drafter = new CountingDrafter();
        await using var harness = await MachineHarness.StartAsync(drafter: drafter, emptySessionMinimum: Minimum);

        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));
        harness.Machine.Narration.Owe(harness.Machine.SessionId!);
        Assert.True(await harness.Machine.StopAsync(ct));

        Assert.Equal(1, drafter.Calls);
        Assert.Equal(SessionState.DraftReady, harness.Machine.State);
    }

    [Fact]
    public async Task AQuietMinuteInTheToolIsKeptAndABriefFocusFollowedByTheGraceIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTime(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));
        var drafter = new CountingDrafter();
        await using var harness = await MachineHarness.StartAsync(time: clock, drafter: drafter, emptySessionMinimum: Minimum);

        // Watching a static remote screen for a minute and a bit: nothing to click, still time worked.
        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(await harness.Machine.StopAsync(ct));
        Assert.Equal(SessionState.DraftReady, harness.Machine.State);
        Assert.Equal(1, drafter.Calls);

        // Three seconds in the tool and the ninety-second grace after it: as long, and nothing in it.
        Assert.True(await harness.Machine.StartAsync(AnyDesk, ct: ct));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await harness.Machine.TryRecordEventAsync(new FocusEvent { TsMs = harness.Machine.NowMs, Process = "OUTLOOK", Scope = CaptureScope.OutOfScope }, ct));
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.True(await harness.Machine.StopAsync(ct));
        Assert.Equal(SessionState.Idle, harness.Machine.State);
        Assert.Equal(1, drafter.Calls);
    }

    [Fact]
    public async Task ASessionRecoveredAfterACrashIsDraftedEvenWithNothingInIt()
    {
        // Partial capture says something went wrong; that session is the technician's to look at.
        var ct = TestContext.Current.CancellationToken;
        var drafter = new CountingDrafter();
        await using var harness = await MachineHarness.StartAsync(drafter: drafter, emptySessionMinimum: Minimum);
        await harness.Store.CreateSessionAsync(new NewSession("crashed", DateTimeOffset.UtcNow, AnyDesk, LocalOnly: false, PolicyVersion: null, SuggestedTicket: null), ct);
        await harness.Store.SetSessionStateAsync("crashed", CaptureStates.Recording, null, ct);

        Assert.Equal(1, await harness.Machine.RecoverAsync(ct));

        Assert.Equal(1, drafter.Calls);
        Assert.Equal(SessionState.DraftReady, harness.Machine.State);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static Session SessionOf(long durationMs, params SessionEvent[] events) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s-1",
        StartedAt = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero),
        DurationMs = durationMs,
        RemoteTool = AnyDesk,
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = false,
        Events = events,
        Frames = [],
        Transcript = [],
    };

    private static CaptureStateEvent Recording(long ts) => new() { TsMs = ts, State = Shared.Schema.CaptureState.Recording };

    private static CaptureStateEvent Finalizing(long ts) => new() { TsMs = ts, State = Shared.Schema.CaptureState.Finalizing };

    private static FocusEvent Focus(long ts, CaptureScope scope) => new() { TsMs = ts, Process = "p", Scope = scope };

    private static ClickEvent Click(long ts, bool positioned) =>
        new() { TsMs = ts, X = positioned ? 10 : null, Y = positioned ? 10 : null, Button = MouseButton.Left };

    private static Frame Frame() => new()
    {
        Id = "f-1",
        TsMs = 1_000,
        Trigger = FrameTrigger.Click,
        Image = "frames/f-1.jpg",
        Width = 100,
        Height = 100,
        RedactionPending = false,
        MaskedRegions = [],
        SensitiveContext = false,
        ExcludedByUser = false,
    };

    /// <summary>Drafts every session it is asked to, and counts how many that was.</summary>
    private sealed class CountingDrafter : IDrafter
    {
        public int Calls { get; private set; }

        public Task<DraftOutcome> DraftAsync(string sessionId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(DraftOutcome.Success(new DraftNote
            {
                Problem = "p",
                Steps = [],
                Result = "r",
                FollowUps = [],
                SuggestedTitle = "t",
                SuggestedTimeMinutes = 1,
                KbCandidate = false,
                KbReason = "n",
                Source = DraftSource.Cloud,
                PromptVersion = "note_v2",
            }));
        }
    }
}
