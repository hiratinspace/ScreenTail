using System.Diagnostics;
using System.Security.Cryptography;
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

        Assert.True(await machine.UnsuppressAsync());
        Assert.True(await machine.TryStageFrameAsync(Frame("f-ok", machine.NowMs)));

        // INV-6: nothing from the paused or suppressed intervals reached the store, but the intervals themselves are on the timeline.
        var stored = (await _store!.LoadSessionAsync(machine.SessionId!))!;
        Assert.Equal(1, await _store.CountPendingFramesAsync(machine.SessionId!));
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
            await _store!.MarkFrameRedactedAsync("f1", new RedactionOutcome(new byte[] { 1 }, "ok", [], false, DateTimeOffset.UtcNow));
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
        Assert.False(await machine.UnsuppressAsync());
        Assert.Equal(SessionState.Recording, machine.State);
    }

    [Fact]
    public async Task ActiveTimeExcludesPauses()
    {
        var machine = await MachineAsync();
        await machine.StartAsync(ScreenConnect);
        await Task.Delay(100);
        await machine.PauseAsync();
        await Task.Delay(200);
        var whilePaused = machine.Snapshot.ElapsedMs!.Value;
        await machine.ResumeAsync();
        await Task.Delay(50);

        Assert.InRange(whilePaused, 80, 190);
        Assert.InRange(machine.Snapshot.ElapsedMs!.Value, 130, 260);
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

    private async Task<SessionMachine> MachineAsync(TimeSpan? grace = null)
    {
        var store = await OpenStoreAsync();
        _machine = new SessionMachine(store, _sources, _drafter, options: new SessionMachineOptions
        {
            RedactionGrace = grace ?? TimeSpan.FromSeconds(1),
            RedactionPoll = TimeSpan.FromMilliseconds(20),
        });
        _machine.StateChanged += s => _observed.Add(s);
        return _machine;
    }

    private static StagedFrame Frame(string id, long tsMs) => new(id, tsMs, FrameTrigger.Click, 100, 100, null, new byte[] { 0xAA });

    private static TranscriptSegment Segment(string id, long tsMs) => new() { Id = id, TsMs = tsMs, EndMs = tsMs + 100, Speaker = Speaker.Tech, Text = "hi" };

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
