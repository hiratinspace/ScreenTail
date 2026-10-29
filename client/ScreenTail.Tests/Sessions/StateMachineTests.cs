using System.Diagnostics;
using System.Security.Cryptography;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Speech;

namespace ScreenTail.Tests.Sessions;

/// <summary>ST-020: every transition, INV-6 gating, the redaction grace at finalize, and crash recovery — against the real store.</summary>
public sealed class StateMachineTests : IAsyncDisposable
{
    private static readonly RemoteTool ScreenConnect = new() { Kind = RemoteToolKind.Screenconnect, ClientVersion = "24.1" };
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKeyProvider _key = new(RandomNumberGenerator.GetBytes(32));
    private readonly FakeSources _sources = new();
    private readonly FakeDrafter _drafter = new();
    private readonly List<CaptureStateSnapshot> _observed = [];
    private SqliteSessionStore? _store;
    private SessionMachine? _machine;

    [Fact]
    public async Task StartCreatesTheSessionAndReportsRecordingWithin200Ms()
    {
        var machine = await MachineAsync();

        var clock = Stopwatch.StartNew();
        var started = await machine.StartAsync(ScreenConnect, localOnly: false, policyVersion: "v14");
        clock.Stop();

        Assert.True(started);
        Assert.True(clock.ElapsedMilliseconds < 200, $"start took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(SessionState.Recording, machine.State);
        var snapshot = Assert.Single(_observed);
        Assert.Equal(CaptureStates.Recording, snapshot.State);
        Assert.Equal(machine.SessionId, snapshot.SessionId);
        Assert.Equal("screenconnect", snapshot.RemoteTool);
        Assert.Equal(1, _sources.Starts);

        var stored = (await _store!.LoadSessionAsync(machine.SessionId!))!;
        Assert.Equal("v14", stored.PolicyVersion);
        var transition = Assert.IsType<CaptureStateEvent>(Assert.Single(stored.Events));
        Assert.Equal(Shared.Schema.CaptureState.Recording, transition.State);
    }

    [Theory]
    [InlineData("the card is 4111 1111 1111 1111 and the code is 123", "4111")]
    [InlineData("her social is 123-45-6789", "123-45-6789")]
    [InlineData("the password is Winter2026!", "Winter2026")]
    public async Task WhatIsSaidAloudIsScrubbedBeforeItIsStored(string spoken, string secret)
    {
        // Found in the 2026-09-19 review. RedactionEngine.ScrubText was written for exactly this, was
        // tested, and had no caller: every transcript segment went into the store as spoken, and from
        // there into the bundle and to the summarizer. The schema's own description of this field says
        // "already scrubbed". A technician reading a card number back to a customer is one of the most
        // ordinary things said on a support call.
        //
        // Asserted against the real encrypted store rather than a fake, because "what is on disk" is
        // the claim.
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);

        Assert.True(await machine.TryAppendTranscriptAsync(Said("t-1", machine.NowMs, spoken)));

        var stored = (await _store!.LoadSessionAsync(machine.SessionId!))!;
        var segment = Assert.Single(stored.Transcript);
        Assert.DoesNotContain(secret, segment.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMaskedWordLeavesACountBehindAndNothingElse()
    {
        // The audit log says that something was masked and what kind of thing it was. It must never be
        // a second copy of what was said (INV-10).
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);

        Assert.True(await machine.TryAppendTranscriptAsync(Said("t-1", machine.NowMs, "her social is 123-45-6789")));

        var rows = await _store!.GetAuditRecordsAsync();
        var row = Assert.Single(rows, r => r.Type == AuditTypes.TranscriptRedacted);
        Assert.Equal(1, row.Count);
        Assert.Equal("Ssn", row.Detail, ignoreCase: true);
        Assert.DoesNotContain(rows, r => r.Detail?.Contains("123", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task WhatWasNotASecretIsStoredAsItWasSaid()
    {
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);

        Assert.True(await machine.TryAppendTranscriptAsync(Said("t-1", machine.NowMs, "restarting the print spooler now")));

        var stored = (await _store!.LoadSessionAsync(machine.SessionId!))!;
        Assert.Equal("restarting the print spooler now", Assert.Single(stored.Transcript).Text);
    }

    [Fact]
    public async Task ASegmentThatCouldNotBeFullyCheckedIsDroppedRatherThanStored()
    {
        // ScrubResult.Complete is false when a detector timed out, and its own contract says the caller
        // must discard. The same rule ADR-0004 applies to frames: not-checked is not the same as clean.
        var giveUp = new RedactionEngine(
            policy: null,
            find: (_, _, onIncomplete) =>
            {
                onIncomplete();
                return [];
            });
        var machine = await MachineAsync(scrubber: giveUp);
        await machine.StartAsync(ScreenConnect);

        Assert.False(await machine.TryAppendTranscriptAsync(Said("t-1", machine.NowMs, "the password is Winter2026!")));

        var stored = (await _store!.LoadSessionAsync(machine.SessionId!))!;
        Assert.Empty(stored.Transcript);
    }

    [Fact]
    public async Task PausedAndSuppressedWriteNothing()
    {
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        Assert.True(await machine.TryRecordEventAsync(new ClickEvent { TsMs = machine.NowMs, X = 1, Y = 1, Button = MouseButton.Left }));

        Assert.True(await machine.PauseAsync());
        Assert.False(await machine.TryRecordEventAsync(new ClickEvent { TsMs = machine.NowMs, X = 1, Y = 1, Button = MouseButton.Left }));
        Assert.False(await machine.TryStageFrameAsync(Frame("f-paused", machine.NowMs)));
        Assert.False(await machine.TryAppendTranscriptAsync(Segment("t-paused", machine.NowMs)));

        Assert.True(await machine.ResumeAsync());
        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField));
        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.False(await machine.TryRecordEventAsync(new EnterEvent { TsMs = machine.NowMs }));
        Assert.False(await machine.TryStageFrameAsync(Frame("f-suppressed", machine.NowMs)));
        Assert.False(await machine.TryAppendTranscriptAsync(Segment("t-suppressed", machine.NowMs)));

        Assert.True(await machine.UnsuppressAsync(CaptureStateReason.PasswordField));
        Assert.True(await machine.TryStageFrameAsync(Frame("f-ok", machine.NowMs)));

        // INV-6: nothing from the paused or suppressed intervals reached the store, but the intervals themselves are on the timeline.
        var stored = (await _store!.LoadSessionAsync(machine.SessionId!))!;
        // One frame was accepted and is waiting to be read. It waits in memory, so the store has
        // nothing to say about it (ADR-0006).
        Assert.Equal(1, _pending.DepthFor(machine.SessionId!));
        Assert.Empty(stored.Transcript);
        Assert.Single(stored.Events.OfType<ClickEvent>());
        Assert.Equal(
            [Shared.Schema.CaptureState.Recording, Shared.Schema.CaptureState.Paused, Shared.Schema.CaptureState.Recording, Shared.Schema.CaptureState.Suppressed, Shared.Schema.CaptureState.Recording],
            stored.Events.OfType<CaptureStateEvent>().Select(e => e.State));
        Assert.Equal(CaptureStateReason.PasswordField, stored.Events.OfType<CaptureStateEvent>().ElementAt(3).Reason);
    }

    [Fact]
    public async Task StopWaitsForRedactionThenDraftsReady()
    {
        var machine = await MachineAsync(grace: TimeSpan.FromSeconds(2));
        await machine.StartAsync(ScreenConnect);
        await machine.TryStageFrameAsync(Frame("f1", machine.NowMs));
        var redactor = Task.Run(async () =>
        {
            await Task.Delay(150);

            // What the worker does: take from the queue, and write a frame that is already redacted.
            // Nothing was ever staged, so there is nothing to update (ADR-0006).
            Assert.True(_pending.TryTake(out var queued));
            await _store!.SaveRedactedFrameAsync(
                queued.SessionId, queued.Frame, new RedactionOutcome(new byte[] { 1 }, "ok", [], false, DateTimeOffset.UtcNow));
            _pending.Done(queued);
        });

        Assert.True(await machine.StopAsync());
        await redactor;

        Assert.Equal(SessionState.DraftReady, machine.State);
        Assert.Equal(1, _sources.Stops);
        Assert.Equal([CaptureStates.Recording, CaptureStates.Finalizing, CaptureStates.DraftReady], _observed.Select(s => s.State));
        var stored = (await _store!.LoadSessionAsync(machine.SessionId!))!;
        Assert.Equal(0, stored.FramesPurgedUnredacted);
        Assert.Single(stored.Frames);
        Assert.NotNull(stored.Draft);
        Assert.NotNull(stored.DurationMs);
        Assert.Equal(1, machine.Snapshot.DraftsReady);
        Assert.Equal([machine.SessionId!], await _store.ListSessionsInStatesAsync([CaptureStates.DraftReady]));
    }

    [Fact]
    public async Task StopPurgesStragglersAfterTheGrace()
    {
        var machine = await MachineAsync(grace: TimeSpan.FromMilliseconds(200));
        await machine.StartAsync(ScreenConnect);
        foreach (var id in new[] { "p1", "p2", "p3" })
        {
            await machine.TryStageFrameAsync(Frame(id, machine.NowMs));
        }

        var clock = Stopwatch.StartNew();
        await machine.StopAsync();

        Assert.True(clock.ElapsedMilliseconds >= 200, "did not wait for the grace period");
        Assert.Equal(SessionState.DraftReady, machine.State);
        var stored = (await _store!.LoadSessionAsync(machine.SessionId!))!;
        Assert.Equal(3, stored.FramesPurgedUnredacted);
        Assert.Empty(stored.Frames);
        Assert.Contains(await _store.GetAuditAsync(machine.SessionId!), a => a.Type == AuditTypes.FramesPurgedUnredacted && a.Count == 3);
    }

    [Fact]
    public async Task AFrameRefusedForAFullQueueIsCountedAgainstTheSession()
    {
        // 2026-10-12 review (S-1, T-5). ADR-0006 says a frame that finds the queue full is "dropped and
        // counted", and the count it means is frames_purged_unredacted, the one the bundle reads and the
        // Review banner hedges on. The machine refused the frame and counted it nowhere: the queue kept
        // a service-wide tally that nothing read per session, so the draft claimed to have seen a
        // session it only partly saw. The known full-disk case is separate and was already counted.
        var machine = await MachineAsync(grace: TimeSpan.FromMilliseconds(100), pendingDepth: 1);
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;

        Assert.True(await machine.TryStageFrameAsync(Frame("kept", machine.NowMs)));
        Assert.False(await machine.TryStageFrameAsync(Frame("refused", machine.NowMs)));

        // Counted the moment it is refused, not at finalize: diagnostics read the session row live.
        var stored = (await _store!.LoadSessionAsync(id))!;
        Assert.Equal(1, stored.FramesPurgedUnredacted);
        var audit = await _store.GetAuditAsync(id);
        Assert.Contains(audit, a => a.Type == AuditTypes.FramesPurgedUnredacted && a.Count == 1);
        // The refused frame was never captured as far as the record is concerned (ST-045's count is
        // of frames that were accepted).
        Assert.Single(audit, a => a.Type == AuditTypes.FrameCaptured);

        // The straggler is purged at finalize on top of it, so the draft hedges for both.
        Assert.True(await machine.StopAsync());
        Assert.Equal(2, (await _store.LoadSessionAsync(id))!.FramesPurgedUnredacted);
    }

    [Fact]
    public async Task AFrameRefusedAsTheCaptureLoopIsCancelledIsStillCounted()
    {
        // 2026-10-12 review, second pass. The refusal is a fact the moment the queue says no, and the
        // count is what makes the draft hedge for it. Written on the capture loop's token, a refusal that
        // landed as Stop cancelled that loop threw before the count was written, and the frame was lost
        // with nothing to say so. A token that is already cancelled is that race with the timing taken out.
        var machine = await MachineAsync(pendingDepth: 1);
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        Assert.True(await machine.TryStageFrameAsync(Frame("kept", machine.NowMs)));

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        Assert.False(await machine.TryStageFrameAsync(Frame("refused", machine.NowMs), stopping.Token));

        Assert.Equal(1, (await _store!.LoadSessionAsync(id))!.FramesPurgedUnredacted);
        Assert.Contains(await _store.GetAuditAsync(id), a => a.Type == AuditTypes.FramesPurgedUnredacted && a.Count == 1);
    }

    [Fact]
    public async Task StopWaitsForAFrameAWorkerIsHolding()
    {
        // 2026-10-12 review (T-1). The queue holds two states, waiting and taken-but-unwritten, and
        // finalize must wait for both: a DepthFor that counted only the waiting half would draft the
        // note while the worker was still writing the last frame, and that frame would land after the
        // line was drawn.
        //
        // On a clock that moves only by hand, as the recovery test above does it, with one addition: the
        // clock says when the machine asks it for a timer, which is the machine deciding to wait. The
        // grace can never run out, so a Stop that has asked for a timer cannot finish until the frame is
        // handed back; a Stop that did not think it owed anything never asks, and finishes on its own.
        // No sleeping and no polling, so the answer does not depend on how busy the runner is.
        var clock = new HeldClock(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
        var machine = await MachineAsync(grace: TimeSpan.FromSeconds(5), time: clock);
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        Assert.True(await machine.TryStageFrameAsync(Frame("held", machine.NowMs)));
        Assert.True(_pending.TryTake(out var held));
        Assert.Equal(id, held.SessionId);

        var stopping = machine.StopAsync();
        _ = await Task.WhenAny(clock.Waiting, stopping).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(stopping.IsCompleted, "Stop drafted the note while a worker was still holding a frame of the session.");
        Assert.Equal(SessionState.Finalizing, machine.State);

        await _store!.SaveRedactedFrameAsync(
            held.SessionId, held.Frame, new RedactionOutcome(new byte[] { 1 }, "ok", [], false, DateTimeOffset.UtcNow));
        _pending.Done(held);
        clock.Release();
        Assert.True(await stopping.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(SessionState.DraftReady, machine.State);
        var stored = (await _store.LoadSessionAsync(id))!;
        Assert.Equal(0, stored.FramesPurgedUnredacted);
        Assert.Single(stored.Frames);
    }

    [Fact]
    public async Task DraftFailureIsReportedWithItsReason()
    {
        _drafter.Outcome = DraftOutcome.Failure("Cloud drafting paused for today.");
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);

        await machine.StopAsync();

        Assert.Equal(SessionState.DraftFailed, machine.State);
        Assert.Equal("Cloud drafting paused for today.", machine.Snapshot.DraftFailureReason);
        Assert.Equal([machine.SessionId!], await _store!.ListSessionsInStatesAsync([CaptureStates.DraftFailed]));
        Assert.Equal(0, machine.Snapshot.DraftsReady);
    }

    [Fact]
    public async Task ADraftQueuedForTheBackendIsNotReportedAsFailed()
    {
        // ST-013's end-to-end run, on the first real loop: every session on a device with a backend ended
        // in draft_failed, "No summarization provider is configured", while its draft sat in the outbox
        // on its way to a backend that was configured and answering. The pill and the tray said the draft
        // had failed after every session. Queued is not failed; nothing is being captured, so the machine
        // rests idle, and the store says the draft is pending.
        _drafter.Outcome = DraftOutcome.Pending("The draft is queued for the backend.");
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;

        await machine.StopAsync();

        Assert.Equal(SessionState.Idle, machine.State);
        Assert.Null(machine.Snapshot.DraftFailureReason);
        Assert.Empty(await _store!.ListSessionsInStatesAsync([CaptureStates.DraftFailed]));
        Assert.Equal([id], await _store.ListSessionsInStatesAsync([SessionStateNames.DraftPending]));
        Assert.DoesNotContain(_observed, s => s.State == CaptureStates.DraftFailed);
    }

    [Fact]
    public async Task AQueuedDraftThatLandsMakesTheSessionDraftReady()
    {
        // The other half: when the outbox delivers the note, the technician is told. Before, the sender
        // wrote it to the store and nothing moved the machine, so "Draft ready" never came.
        _drafter.Outcome = DraftOutcome.Pending("The draft is queued for the backend.");
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        await machine.StopAsync();

        // What DraftSender writes when the backend answers.
        await _store!.SaveDraftAsync(id, Note("Printer offline in reception"));
        await _store.SetSessionStateAsync(id, CaptureStates.DraftReady, null);
        _observed.Clear();

        Assert.True(await machine.DraftSettledAsync(id, ready: true, reason: null));

        Assert.Equal(SessionState.DraftReady, machine.State);
        var announced = Assert.Single(_observed);
        Assert.Equal(CaptureStates.DraftReady, announced.State);
        Assert.Equal(id, announced.SessionId);
        Assert.Equal(1, announced.DraftsReady);
    }

    [Fact]
    public async Task AQueuedDraftThatFailsForGoodIsDraftFailedWithItsReason()
    {
        _drafter.Outcome = DraftOutcome.Pending("The draft is queued for the backend.");
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        await machine.StopAsync();

        Assert.True(await machine.DraftSettledAsync(id, ready: false, reason: "The backend refused this session."));

        Assert.Equal(SessionState.DraftFailed, machine.State);
        Assert.Equal("The backend refused this session.", machine.Snapshot.DraftFailureReason);
        Assert.Equal([id], await _store!.ListSessionsInStatesAsync([CaptureStates.DraftFailed]));
    }

    [Fact]
    public async Task ADraftThatLandsDuringTheNextSessionDoesNotInterruptIt()
    {
        _drafter.Outcome = DraftOutcome.Pending("The draft is queued for the backend.");
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var first = machine.SessionId!;
        await machine.StopAsync();
        await machine.StartAsync(ScreenConnect);
        await _store!.SaveDraftAsync(first, Note("Printer offline in reception"));
        await _store.SetSessionStateAsync(first, CaptureStates.DraftReady, null);

        Assert.False(await machine.DraftSettledAsync(first, ready: true, reason: null));

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.NotEqual(first, machine.SessionId);
        Assert.Equal(1, machine.Snapshot.DraftsReady);
    }

    [Fact]
    public async Task DrafterExceptionBecomesDraftFailed()
    {
        _drafter.Throw = true;
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);

        await machine.StopAsync();

        Assert.Equal(SessionState.DraftFailed, machine.State);
        Assert.Contains("Drafting failed", machine.Snapshot.DraftFailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashMidSessionIsRecoveredAsFinalizingWithPartialCapture()
    {
        // A previous service run died mid-session: the store still says "recording".
        var store = await OpenStoreAsync();
        await store.CreateSessionAsync(new NewSession("orphan", DateTimeOffset.UtcNow, ScreenConnect, false, null));
        await store.SetSessionStateAsync("orphan", CaptureStates.Recording, null);
        await store.AppendEventAsync("orphan", new ClickEvent { TsMs = 5_000, X = 0, Y = 0, Button = MouseButton.Left });
        await store.StageFrameAsync("orphan", Frame("straggler", 6_000));

        var machine = await MachineAsync(grace: TimeSpan.FromMilliseconds(100));
        var recovered = await machine.RecoverAsync();

        Assert.Equal(1, recovered);
        Assert.Equal([CaptureStates.Finalizing, CaptureStates.DraftReady], _observed.Select(s => s.State));
        var stored = (await store.LoadSessionAsync("orphan"))!;
        Assert.True(stored.PartialCapture);
        Assert.Equal(6_000, stored.DurationMs);
        Assert.Equal(1, stored.FramesPurgedUnredacted);
        Assert.NotNull(stored.Draft);
        Assert.Empty(await store.ListSessionsInStatesAsync(SessionStateNames.Orphanable));
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    public async Task ACrashInAnyLiveStateIsRecovered(string leftIn)
    {
        // 2026-10-12 review (T-8). Recovery was tested for a Recording orphan only. A session a crash
        // left paused, suppressed or finalizing is just as live, and one dropped from Orphanable would
        // sit in the store for ever with no draft and no way to reach one.
        var store = await OpenStoreAsync();
        await store.CreateSessionAsync(new NewSession("orphan", DateTimeOffset.UtcNow, ScreenConnect, false, null));
        await store.SetSessionStateAsync("orphan", CaptureStates.Recording, null);
        await store.AppendEventAsync("orphan", new ClickEvent { TsMs = 5_000, X = 0, Y = 0, Button = MouseButton.Left });
        await store.SetSessionStateAsync("orphan", leftIn, null);

        var machine = await MachineAsync();
        var recovered = await machine.RecoverAsync();

        Assert.Equal(1, recovered);
        Assert.Equal(SessionState.DraftReady, machine.State);
        var stored = (await store.LoadSessionAsync("orphan"))!;
        Assert.True(stored.PartialCapture);
        Assert.NotNull(stored.Draft);
        Assert.Equal(5_000, stored.DurationMs);
        Assert.Empty(await store.ListSessionsInStatesAsync(SessionStateNames.Orphanable));
    }

    [Fact]
    public async Task TwoOrphansAreBothRecovered()
    {
        // A crash during recovery itself, or a store an older build left two sessions in, is how there
        // comes to be more than one. Each is finalized and drafted on its own; the second must not be
        // skipped because the first moved the machine out of Idle.
        var store = await OpenStoreAsync();
        foreach (var (id, state) in new[] { ("first", CaptureStates.Recording), ("second", CaptureStates.Paused) })
        {
            await store.CreateSessionAsync(new NewSession(id, DateTimeOffset.UtcNow, ScreenConnect, false, null));
            await store.SetSessionStateAsync(id, state, null);
        }

        var machine = await MachineAsync();
        var recovered = await machine.RecoverAsync();

        Assert.Equal(2, recovered);
        Assert.Equal(
            [CaptureStates.Finalizing, CaptureStates.DraftReady, CaptureStates.Finalizing, CaptureStates.DraftReady],
            _observed.Select(s => s.State));
        foreach (var id in new[] { "first", "second" })
        {
            var stored = (await store.LoadSessionAsync(id))!;
            Assert.True(stored.PartialCapture);
            Assert.NotNull(stored.Draft);
        }

        Assert.Equal(2, machine.Snapshot.DraftsReady);
        Assert.Empty(await store.ListSessionsInStatesAsync(SessionStateNames.Orphanable));
    }

    [Fact]
    public async Task RecoveryDoesNotWaitForARedactionWorkerThatIsNotRunningYet()
    {
        // 2026-09-20 efficiency review. Finalizing waits up to the redaction grace -- twenty seconds in
        // production -- for pending frames to be redacted before purging what is left. During recovery
        // there is nothing to wait for: CaptureHost recovers before it starts the worker, so the backlog
        // cannot drain, and the wait ends by expiring. Twenty seconds of polling the store every 250 ms,
        // per orphaned session, before the service will serve its first request.
        //
        // The frames are purged either way -- an unredacted frame does not survive a crash (INV-1) --
        // so the wait bought nothing at all.
        //
        // A clock that never moves is how this is asserted: with the wait in place the loop can never
        // finish, so today's code hangs and the timeout is the failure.
        var store = await OpenStoreAsync();
        await store.CreateSessionAsync(new NewSession("orphan", DateTimeOffset.UtcNow, ScreenConnect, false, null));
        await store.SetSessionStateAsync("orphan", CaptureStates.Recording, null);
        await store.StageFrameAsync("orphan", Frame("straggler", 6_000));

        var frozen = new ManualTime(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));
        var machine = await MachineAsync(grace: TimeSpan.FromSeconds(20), time: frozen);

        var recovered = await machine.RecoverAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, recovered);
        Assert.Equal(0, await store.CountPendingFramesAsync("orphan"));
    }

    [Fact]
    public async Task StoppingASessionStillWaitsForTheFramesBeingRedacted()
    {
        // The half that must not change. A technician pressing stop has a worker running behind them,
        // and the grace is what lets the last frames finish rather than being thrown away.
        var machine = await MachineAsync(grace: TimeSpan.FromMilliseconds(200));
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await machine.StartAsync(ScreenConnect, ct: ct));
        Assert.True(await machine.TryStageFrameAsync(Frame("pending", 1_000), ct));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await machine.StopAsync(ct));

        Assert.True(clock.ElapsedMilliseconds >= 150, $"Stop waited {clock.ElapsedMilliseconds} ms, so the grace is not being honoured.");
    }

    [Fact]
    public async Task RecoverWithNothingToRecoverIsIdle()
    {
        var machine = await MachineAsync();

        Assert.Equal(0, await machine.RecoverAsync());
        Assert.Equal(SessionState.Idle, machine.State);
    }

    [Fact]
    public async Task DiscardDeletesEverythingAndReturnsToIdle()
    {
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        Assert.True(await machine.TryStageFrameAsync(Frame("f1", machine.NowMs)));
        Assert.Equal(1, _pending.DepthFor(id));

        Assert.True(await machine.DiscardAsync());

        Assert.Equal(SessionState.Idle, machine.State);
        Assert.Null(machine.Snapshot.SessionId);
        Assert.Null(await _store!.LoadSessionAsync(id));
        // The frame that was waiting to be read goes with the session (T-17). This used to ask the
        // store, which has held no pending frame since ADR-0006 and so answered "none" whatever the
        // machine did; the queue is where the frame was, and it has to be empty of this session now,
        // or the worker would go on reading frames of a session the technician threw away.
        Assert.Equal(0, _pending.DepthFor(id));
        Assert.Contains(await _store.GetAuditAsync(id), a => a.Type == AuditTypes.SessionDiscarded);
        Assert.Equal(1, _sources.Stops);
    }

    [Fact]
    public async Task InvalidTransitionsAreRefused()
    {
        var machine = await MachineAsync();

        Assert.False(await machine.PauseAsync());
        Assert.False(await machine.ResumeAsync());
        Assert.False(await machine.StopAsync());
        Assert.False(await machine.DiscardAsync());
        Assert.False(await machine.SuppressAsync(CaptureStateReason.ExcludedApp));
        Assert.False(await machine.MarkMomentAsync());

        await machine.StartAsync(ScreenConnect);
        Assert.False(await machine.StartAsync(ScreenConnect));
        Assert.False(await machine.ResumeAsync());
        Assert.False(await machine.UnsuppressAsync(CaptureStateReason.PasswordField));
        Assert.Equal(SessionState.Recording, machine.State);
    }

    [Fact]
    public async Task ActiveTimeExcludesPauses()
    {
        // On a hand-moved clock, because the claim is arithmetic: paused milliseconds are not counted.
        // This test used to sleep and assert a band of real milliseconds, which measures the runner's
        // scheduler as much as the code, and it duly failed on a busy CI machine at 267 ms against a
        // ceiling of 260. Widening the band would only have moved the next failure further out; the
        // numbers below are exact.
        var clock = new ManualTime(DateTimeOffset.UnixEpoch);
        var machine = await MachineAsync(time: clock);

        await machine.StartAsync(ScreenConnect);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await machine.PauseAsync();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        var whilePaused = machine.Snapshot.ElapsedMs!.Value;
        await machine.ResumeAsync();
        clock.Advance(TimeSpan.FromMilliseconds(50));

        Assert.Equal(100, whilePaused);
        Assert.Equal(150, machine.Snapshot.ElapsedMs!.Value);
    }

    [Fact]
    public async Task MarkMomentForcesAFrameAndAMarker()
    {
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);

        Assert.True(await machine.MarkMomentAsync());

        Assert.Equal(1, _sources.Marks);
        Assert.Single((await _store!.LoadSessionAsync(machine.SessionId!))!.Events.OfType<MarkerEvent>());
    }

    [Fact]
    public async Task ANewSessionCanStartAfterADraft()
    {
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var first = machine.SessionId;
        await machine.StopAsync();

        Assert.True(await machine.StartAsync(ScreenConnect));

        Assert.NotEqual(first, machine.SessionId);
        Assert.Equal(SessionState.Recording, machine.State);
        Assert.Equal(1, machine.Snapshot.DraftsReady);
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

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    [Fact]
    public async Task WhatWasSaidAtTheEndOfASessionReachesThatSessionsNote()
    {
        // 2026-10-12 review. The model takes seconds per segment, so the last few things a technician
        // says are usually still being transcribed when they press Stop. Finalizing refused them and the
        // note was drafted without them, and the end of a session is usually its outcome.
        var machine = await MachineAsync(grace: TimeSpan.FromSeconds(5));
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        var drafted = -1;
        _drafter.Drafting = async session => drafted = (await _store!.LoadSessionAsync(session))!.Transcript.Count;

        // Heard while recording, and still with the model.
        Assert.Equal(id, machine.Hearing?.SessionId);
        machine.Narration.Owe(id);

        var stopping = machine.StopAsync();
        for (var i = 0; i < 100 && machine.State != SessionState.Finalizing; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(SessionState.Finalizing, machine.State);
        Assert.False(stopping.IsCompleted, "Stop drafted the note while narration was still owed to it.");

        Assert.True(await machine.TryAppendTranscriptAsync(id, Said("t-1", 5_000, "and that fixed the spooler")));
        machine.Narration.Settle(id);
        Assert.True(await stopping);

        Assert.Equal(1, drafted);
        Assert.Equal(SessionState.DraftReady, machine.State);
    }

    [Fact]
    public async Task NarrationStillOwedWhenTheGraceRunsOutIsNotWaitedForAnyLonger()
    {
        // A model that never answers must not hold the note hostage: the wait has the frames' bound, and
        // whatever arrives after it is refused rather than written behind the draft's back.
        var machine = await MachineAsync(grace: TimeSpan.FromMilliseconds(200));
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        machine.Narration.Owe(id);

        var clock = Stopwatch.StartNew();
        Assert.True(await machine.StopAsync());
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds >= 150, $"Stop waited {clock.ElapsedMilliseconds} ms for narration.");
        Assert.False(machine.Narration.Wants(id));
        Assert.False(await machine.TryAppendTranscriptAsync(id, Said("t-late", 5_000, "too late")));
        Assert.Empty((await _store!.LoadSessionAsync(id))!.Transcript);

        // Heard just before Stop and queued just after it stopped waiting: not handed to the model.
        machine.Narration.Owe(id);
        Assert.False(machine.Narration.Wants(id));
    }

    [Fact]
    public async Task NothingIsWrittenIntoTheTranscriptWhileTheNoteIsBeingDrafted()
    {
        // The session is still finalizing while it drafts, and that state takes narration. Once the wait
        // is over the note is being drawn from the transcript, so a segment the model finishes now would
        // be stored without being in the note it was supposed to be part of.
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        bool? accepted = null;
        _drafter.Drafting = async session =>
            accepted = await machine.TryAppendTranscriptAsync(session, Said("t-1", 5_000, "and that fixed it"));

        Assert.True(await machine.StopAsync());

        Assert.False(accepted);
        Assert.Empty((await _store!.LoadSessionAsync(id))!.Transcript);
    }

    [Fact]
    public async Task ASegmentHeardInOneSessionIsRefusedByTheNext()
    {
        // The other half of the same finding: the customer after is the one who would have been sent the
        // customer before's words.
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var first = machine.SessionId!;
        await machine.StopAsync();
        await machine.StartAsync(ScreenConnect);
        var second = machine.SessionId!;

        Assert.False(await machine.TryAppendTranscriptAsync(first, Said("t-1", 100, "the server is called acme-dc01")));

        Assert.Empty((await _store!.LoadSessionAsync(second))!.Transcript);
        Assert.Empty((await _store!.LoadSessionAsync(first))!.Transcript);
        Assert.True(await machine.TryAppendTranscriptAsync(second, Said("t-1", 100, "restarting the spooler")));
    }

    [Fact]
    public async Task ASegmentHeardInADiscardedSessionGoesNowhere()
    {
        // Discard is the technician saying this session should not exist. What it was still owed is
        // forgotten with it, and a segment that finishes afterwards is refused by whatever runs next.
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var discarded = machine.SessionId!;
        machine.Narration.Owe(discarded);

        Assert.True(await machine.DiscardAsync());
        Assert.False(machine.Narration.Wants(discarded));

        await machine.StartAsync(ScreenConnect);
        Assert.False(await machine.TryAppendTranscriptAsync(discarded, Said("t-1", 100, "the password is Winter2026!")));
        Assert.Empty((await _store!.LoadSessionAsync(machine.SessionId!))!.Transcript);
    }

    [Fact]
    public async Task NothingIsHeardOrWrittenForASessionWhileItIsBeingDiscarded()
    {
        // Discard awaits the capture sources and then the store, and the session is still the current one,
        // still recording, all that time. Narration used to be let go of in the middle of it, so a chunk
        // heard in that window was queued for the discarded session again and a segment the model finished
        // in it passed the door. Discard is the technician saying this session should not exist: from its
        // first moment nothing more is heard for it and nothing is written into it.
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        var held = new TaskCompletionSource();
        _sources.Stopping = () => held.Task;

        var discarding = machine.DiscardAsync();
        Assert.Equal(1, _sources.Stops);
        Assert.False(discarding.IsCompleted);
        Assert.Equal(SessionState.Recording, machine.State);

        Assert.Null(machine.Hearing);
        machine.Narration.Owe(id);
        Assert.False(machine.Narration.Wants(id));
        Assert.False(await machine.TryAppendTranscriptAsync(id, Said("t-1", 100, "the password is Winter2026!")));

        held.SetResult();
        Assert.True(await discarding);
        Assert.Null(await _store!.LoadSessionAsync(id));
    }

    [Fact]
    public async Task WhatTheRecorderHeardBeforeStopIsInTheNote()
    {
        // The host's wiring in miniature: the real recorder, asking the real machine what it is hearing,
        // sharing its backlog and writing through its stamped door. Stop is pressed while the model is
        // still on the last sentence, and the note waits for it.
        var machine = await MachineAsync(grace: TimeSpan.FromSeconds(5));
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;
        var drafted = new List<string>();
        _drafter.Drafting = async session =>
            drafted.AddRange((await _store!.LoadSessionAsync(session))!.Transcript.Select(segment => segment.Text));

        var recogniser = new NarrationRecorderTests.FakeRecogniser("and that fixed the spooler") { Blocked = new TaskCompletionSource() };
        var recorder = new NarrationRecorder(
            new NarrationRecorderTests.FakeMicrophone(
                NarrationRecorderTests.Silence(20), NarrationRecorderTests.Speech(40), NarrationRecorderTests.Silence(40)),
            recogniser,
            (heardIn, segment, ct) => machine.TryAppendTranscriptAsync(heardIn, segment, ct),
            () => machine.Hearing,
            hearing: () => true,
            backlog: machine.Narration);

        // The recorder's own token. Once Stop is seen the recorder waits for the next session rather than
        // returning, and it only returns by itself when the microphone runs out first. Under load Stop can
        // come first, and the test then waited on it until the run timed out.
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = recorder.RunAsync(listening.Token);
        for (var i = 0; i < 250 && recogniser.Asked == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, machine.Narration.DepthFor(id));

        var stopping = machine.StopAsync();
        await Task.Delay(200);
        Assert.False(stopping.IsCompleted, "Stop drafted the note while the recorder's segment was still with the model.");

        recogniser.Blocked.SetResult();
        Assert.True(await stopping);
        await listening.CancelAsync();
        await running;

        Assert.Equal(["and that fixed the spooler"], drafted);
        Assert.Equal(1, recorder.Kept);
    }

    [Theory]
    [InlineData(CaptureStateReason.PasswordField)]
    [InlineData(CaptureStateReason.SensitiveContext)]
    [InlineData(CaptureStateReason.NoIndicator)]
    public async Task ASentenceUnderWayWhenCaptureIsSuppressedIsDropped(CaptureStateReason reason)
    {
        // The real recorder over the real machine. The technician is mid-sentence when a password field
        // takes focus. What was being said is the start of what is read off the screen next, so it is
        // dropped at the suppression, never cut and kept as a Stop's last sentence is (INV-6).
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;

        var microphone = new NarrationRecorderTests.LiveMicrophone(NarrationRecorderTests.Silence(20))
        {
            KeepsSaying = NarrationRecorderTests.Speech(1)[0],
        };
        var recogniser = new NarrationRecorderTests.FakeRecogniser("the admin password is");
        var recorder = new NarrationRecorder(
            microphone,
            recogniser,
            (heardIn, segment, ct) => machine.TryAppendTranscriptAsync(heardIn, segment, ct),
            () => machine.Hearing,
            hearing: () => true,
            backlog: machine.Narration);
        machine.StateChanged += _ => recorder.Nudge();
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await NarrationRecorderTests.UntilAsync(() => microphone.Yielded >= 80);
        Assert.True(await machine.SuppressAsync(reason));
        await NarrationRecorderTests.UntilAsync(() => !recorder.Listening);
        await Task.Delay(200);

        await stop.CancelAsync();
        await running;

        Assert.Equal(0, recogniser.Asked);
        Assert.Equal(0, machine.Narration.DepthFor(id));
        Assert.Empty((await _store!.LoadSessionAsync(id))!.Transcript);
    }

    [Fact]
    public async Task ASentenceUnderWayIsDroppedEvenWhenTheSuppressionLiftsBeforeTheNextChunk()
    {
        // A password field takes focus and loses it again between two chunks of audio, so the recorder
        // never sees the session stop recording. What was being said before it is still dropped, and so
        // is the chunk that spans it; what is said afterwards is a sentence of its own.
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;

        var microphone = new NarrationRecorderTests.FakeMicrophone(
            NarrationRecorderTests.Silence(20), NarrationRecorderTests.Speech(80), NarrationRecorderTests.Silence(40));
        var recogniser = new NarrationRecorderTests.FakeRecogniser("restarting the spooler");
        var flickered = false;
        var recorder = new NarrationRecorder(
            microphone,
            recogniser,
            (heardIn, segment, ct) => machine.TryAppendTranscriptAsync(heardIn, segment, ct),
            () =>
            {
                // Forty frames into the sentence, before this chunk is judged.
                if (microphone.Yielded == 60 && !flickered)
                {
                    flickered = true;
                    Assert.True(machine.SuppressAsync(CaptureStateReason.PasswordField).GetAwaiter().GetResult());
                    Assert.True(machine.UnsuppressAsync(CaptureStateReason.PasswordField).GetAwaiter().GetResult());
                }

                return machine.Hearing;
            },
            hearing: () => true,
            backlog: machine.Narration);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(flickered);
        Assert.NotEmpty(recogniser.Handed);
        var frame = NarrationRecorderTests.Rate * 32 / 1000;
        Assert.All(
            recogniser.Handed,
            handed => Assert.True(
                handed.Samples <= 42 * frame,
                $"a segment of {handed.Samples} samples reaches back before the password field took focus"));
        Assert.Equal(0, machine.Narration.DepthFor(id));
    }

    [Fact]
    public async Task NarrationIsHeardOnlyWhileRecording()
    {
        // What the recorder asks of every chunk. Anything but recording is "nothing to hear" (INV-6), and
        // while recording it names the session and where in it this moment is.
        var machine = await MachineAsync();
        Assert.Null(machine.Hearing);

        await machine.StartAsync(ScreenConnect);
        var hearing = Assert.NotNull(machine.Hearing);
        Assert.Equal(machine.SessionId, hearing.SessionId);
        Assert.InRange(hearing.AtMs, 0, machine.NowMs);

        await machine.PauseAsync();
        Assert.Null(machine.Hearing);
        await machine.ResumeAsync();
        Assert.NotNull(machine.Hearing);

        await machine.SuppressAsync(CaptureStateReason.PasswordField);
        Assert.Null(machine.Hearing);
        await machine.UnsuppressAsync(CaptureStateReason.PasswordField);
        Assert.NotNull(machine.Hearing);

        await machine.StopAsync();
        Assert.Null(machine.Hearing);
    }

    [Fact]
    public async Task ASegmentHeardBeforeAPauseIsKeptWhenTheModelFinishesDuringIt()
    {
        // Heard while recording, so it belongs to the session; the pause began after it was said. A
        // frame taken before a pause is redacted and written during it for the same reason.
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;

        await machine.PauseAsync();
        Assert.True(await machine.TryAppendTranscriptAsync(id, Said("t-1", 100, "restarting the spooler")));

        Assert.Single((await _store!.LoadSessionAsync(id))!.Transcript);
    }

    [Fact]
    public async Task AStampedSegmentIsScrubbedLikeAnyOther()
    {
        // The stamped door is the same door: nothing reaches the store without the scrubber.
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        var id = machine.SessionId!;

        Assert.True(await machine.TryAppendTranscriptAsync(id, Said("t-1", 100, "her social is 123-45-6789")));

        Assert.DoesNotContain("123-45-6789", Assert.Single((await _store!.LoadSessionAsync(id))!.Transcript).Text, StringComparison.Ordinal);
    }

    private async Task<SqliteSessionStore> OpenStoreAsync() => _store ??= await SqliteSessionStore.OpenAsync(_path, _key);

    /// <summary>
    /// Where a captured frame waits to be read (ADR-0006). The store no longer holds one. Deep, so a
    /// test that stages a handful of frames is not measuring the production depth; a test about the
    /// full queue asks <see cref="MachineAsync"/> for a shallow one.
    /// </summary>
    private PendingFrames _pending = new(depth: 1000);

    private async Task<SessionMachine> MachineAsync(TimeSpan? grace = null, TimeProvider? time = null, RedactionEngine? scrubber = null, int? pendingDepth = null)
    {
        var store = await OpenStoreAsync();
        if (pendingDepth is { } depth)
        {
            _pending = new PendingFrames(depth);
        }

        // Scrubber is only set when a test asks for one: left alone, the machine has to scrub by itself,
        // which is the behaviour under test.
        var options = scrubber is null
            ? new SessionMachineOptions
            {
                RedactionGrace = grace ?? TimeSpan.FromSeconds(1),
                RedactionPoll = TimeSpan.FromMilliseconds(20),
                Pending = _pending,
            }
            : new SessionMachineOptions
            {
                RedactionGrace = grace ?? TimeSpan.FromSeconds(1),
                RedactionPoll = TimeSpan.FromMilliseconds(20),
                Scrubber = scrubber,
                Pending = _pending,
            };
        _machine = new SessionMachine(store, _sources, _drafter, time: time, options: options);
        _machine.StateChanged += s => _observed.Add(s);
        return _machine;
    }

    private static StagedFrame Frame(string id, long tsMs) => new(id, tsMs, FrameTrigger.Click, 100, 100, null, new byte[] { 0xAA });

    private static DraftNote Note(string title) => new()
    {
        Problem = "p",
        Steps = [],
        Result = "r",
        FollowUps = [],
        SuggestedTitle = title,
        SuggestedTimeMinutes = 15,
        KbCandidate = false,
        KbReason = "n",
        Source = DraftSource.Cloud,
        PromptVersion = "note_v1",
    };

    private static TranscriptSegment Said(string id, long tsMs, string text) =>
        new() { Id = id, TsMs = tsMs, EndMs = tsMs + 100, Speaker = Speaker.Tech, Text = text };

    private static TranscriptSegment Segment(string id, long tsMs) => new() { Id = id, TsMs = tsMs, EndMs = tsMs + 100, Speaker = Speaker.Tech, Text = "hi" };

    [Fact]
    public async Task StartingWithATicketHintRemembersIt()
    {
        // ST-077: the coordinator read a ticket number off the window when it started the session;
        // Review needs it later, from the store, after any number of restarts.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Screenconnect }, suggestedTicket: "48213", ct: ct));

        var session = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Equal("48213", session.SuggestedTicket);
    }

    /// <summary>
    /// A clock that stands still, like <see cref="ManualTime"/>, and holds every timer it is asked for
    /// until <see cref="Release"/>. <see cref="Waiting"/> completes the first time one is asked for, which
    /// is the machine deciding there is something to wait for. After Release a timer fires at once.
    /// </summary>
    private sealed class HeldClock(DateTimeOffset now) : TimeProvider
    {
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<HeldTimer> _held = [];
        private bool _released;

        public Task Waiting => _waiting.Task;

        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new HeldTimer(callback, state);
            bool fire;
            lock (_held)
            {
                fire = _released;
                if (!fire)
                {
                    _held.Add(timer);
                }
            }

            _ = _waiting.TrySetResult();
            if (fire)
            {
                _ = ThreadPool.QueueUserWorkItem(_ => timer.Fire());
            }

            return timer;
        }

        public void Release()
        {
            HeldTimer[] due;
            lock (_held)
            {
                _released = true;
                due = [.. _held];
                _held.Clear();
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class HeldTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _done;

            public void Fire()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => _ = Interlocked.Exchange(ref _done, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FixedKeyProvider(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    private sealed class FakeSources : ICaptureSources
    {
        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public int Marks { get; private set; }

        public Task StartAsync(SessionMachine machine, CancellationToken ct = default)
        {
            Starts++;
            return Task.CompletedTask;
        }

        /// <summary>Held by a test that needs to act while the sources are being stopped.</summary>
        public Func<Task>? Stopping { get; set; }

        public Task StopAsync(CancellationToken ct = default)
        {
            Stops++;
            return Stopping?.Invoke() ?? Task.CompletedTask;
        }

        public Task MarkMomentAsync(CancellationToken ct = default)
        {
            Marks++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDrafter : IDrafter
    {
        public DraftOutcome Outcome { get; set; } = DraftOutcome.Success(new DraftNote
        {
            Problem = "p",
            Steps = [],
            Result = "r",
            FollowUps = [],
            SuggestedTitle = "t",
            SuggestedTimeMinutes = 15,
            KbCandidate = false,
            KbReason = "n",
            Source = DraftSource.Cloud,
            PromptVersion = "note_v1",
        });

        public bool Throw { get; set; }

        /// <summary>Called as drafting begins, so a test can see what the note was drafted from.</summary>
        public Func<string, Task>? Drafting { get; set; }

        public async Task<DraftOutcome> DraftAsync(string sessionId, CancellationToken ct = default)
        {
            if (Drafting is { } drafting)
            {
                await drafting(sessionId);
            }

            return Throw ? throw new InvalidOperationException("provider down") : Outcome;
        }
    }
}
