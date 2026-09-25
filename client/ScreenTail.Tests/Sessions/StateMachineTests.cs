using System.Diagnostics;
using System.Security.Cryptography;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

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
        await machine.TryStageFrameAsync(Frame("f1", machine.NowMs));

        Assert.True(await machine.DiscardAsync());

        Assert.Equal(SessionState.Idle, machine.State);
        Assert.Null(machine.Snapshot.SessionId);
        Assert.Null(await _store!.LoadSessionAsync(id));
        Assert.Null(await _store.TakeNextPendingFrameAsync());
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

    private async Task<SqliteSessionStore> OpenStoreAsync() => _store ??= await SqliteSessionStore.OpenAsync(_path, _key);

    /// <summary>Where a captured frame waits to be read (ADR-0006). The store no longer holds one.</summary>
    private readonly PendingFrames _pending = new(depth: 1000);

    private async Task<SessionMachine> MachineAsync(TimeSpan? grace = null, TimeProvider? time = null, RedactionEngine? scrubber = null)
    {
        var store = await OpenStoreAsync();

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

        public Task StopAsync(CancellationToken ct = default)
        {
            Stops++;
            return Task.CompletedTask;
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

        public Task<DraftOutcome> DraftAsync(string sessionId, CancellationToken ct = default) =>
            Throw ? throw new InvalidOperationException("provider down") : Task.FromResult(Outcome);
    }
}
