using ScreenTail.Core.Capture;
using ScreenTail.Core.History;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.History;

/// <summary>
/// The service's half of Review's Discard and History's bulk discard (Spec §5 S3, S4; contract v6),
/// against the real encrypted store and the real state machine.
///
/// The tray's discard acts on whatever the machine is resting on. These name their sessions, so the
/// questions are different ones: a token earned for some sessions must not discard others, a session
/// that is not the current one must lose everything a discard promises (its frames, transcript, note
/// and queued draft, which must then never be sent), and the current one must still go through the
/// machine, so the pill and the tray stop talking about it.
/// </summary>
public sealed class NamedDiscardTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };

    /// <summary>The window every request comes from. Tokens are bound to it.</summary>
    private static readonly Guid Window = Guid.NewGuid();

    private readonly CountingSources _sources = new();
    private readonly ScriptedDrafter _drafter = new();
    private readonly List<OutboxItem> _sent = [];
    private readonly List<CaptureStateSnapshot> _announced = [];
    private MachineHarness? _harness;
    private Outbox? _outbox;

    [Fact]
    public async Task ASessionThatIsNotCurrentLosesItsScreenshotsTranscriptNoteAndQueuedDraft()
    {
        var (store, discards) = await StartAsync();
        await StoredSessionAsync(store, "s1", frames: 2);
        Assert.Equal(1, (await _outbox!.WaitingAsync(Ct)).Drafts);

        var result = await DiscardAsync(discards, "discard_session", ["s1"]);

        Assert.True(result.Ok, result.Error);
        var gone = (await store.LoadSessionAsync("s1", Ct))!;
        Assert.Empty(gone.Frames);
        Assert.Empty(gone.Transcript);
        Assert.Empty(gone.Events);
        Assert.Null(gone.Draft);
        Assert.Equal(SessionStatus.Discarded, SessionHistory.StatusOf((await store.GetSessionSummaryAsync("s1", Ct))!));

        // The queued draft goes with it, and draining the outbox afterwards sends nothing: the bundle with
        // its screenshots and transcript never leaves the machine.
        Assert.Equal(0, (await _outbox.WaitingAsync(Ct)).Drafts);
        Assert.False(await _outbox.DrainAsync(Ct));
        Assert.Empty(_sent);

        // Nothing the machine was doing changed: it was not this session.
        Assert.Equal(SessionState.Idle, _harness!.Machine.State);
    }

    [Fact]
    public async Task TheDiscardIsAuditedWithACountAndNoContent()
    {
        // INV-10. The row says that a technician threw the session away and how many screenshots went
        // with it, the way every discard is audited, and nothing that was on the screen or said aloud.
        var (store, discards) = await StartAsync();
        await StoredSessionAsync(store, "s1", frames: 2);

        Assert.True((await DiscardAsync(discards, "discard_session", ["s1"])).Ok);

        var row = Assert.Single(await store.GetAuditRecordsAsync("s1", Ct), r => r.Type == AuditTypes.SessionDiscarded);
        Assert.Equal(2, row.Count);
        Assert.Null(row.Detail);
        var everything = string.Join("|", (await store.GetAuditRecordsAsync(ct: Ct)).Select(r => $"{r.Type} {r.Detail}"));
        Assert.DoesNotContain("SECRET", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("spooler", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SessionState.DraftPending)]
    [InlineData(SessionState.DraftReady)]
    [InlineData(SessionState.DraftFailed)]
    public async Task TheCurrentSessionInEachRestingStateGoesThroughTheMachine(SessionState resting)
    {
        // The machine rests on the session it ran last. Discarded by name from Review, the session has to
        // leave the machine too, or the pill would go on saying "Draft ready" about a session that is gone
        // and the tray's Discard would act on a deleted row. The announcement is how every window learns.
        var (store, discards) = await StartAsync();
        var id = await RestingAsync(resting);
        _announced.Clear();

        var result = await DiscardAsync(discards, "discard_session", [id]);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(SessionState.Idle, _harness!.Machine.State);
        Assert.Null(_harness.Machine.Snapshot.SessionId);
        Assert.Equal(CaptureStates.Idle, Assert.Single(_announced).State);
        Assert.Equal(0, _harness.Machine.Snapshot.DraftsReady);
        Assert.Equal(SessionStatus.Discarded, SessionHistory.StatusOf((await store.GetSessionSummaryAsync(id, Ct))!));
        Assert.Contains(await store.GetAuditAsync(id, Ct), a => a.Type == AuditTypes.SessionDiscarded);
        Assert.Equal(0, (await _outbox!.WaitingAsync(Ct)).Drafts);
        Assert.False(await _outbox.DrainAsync(Ct));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task ARecordingSessionIsStoppedBeforeItIsDiscarded()
    {
        // Spec v0.4.1 Q3 and INV-4. Capture stops before anything is deleted: a capture source still
        // running while its session's rows are deleted would write the next click into a session the
        // technician threw away. The frame still waiting to be read goes too (ADR-0004).
        var (store, discards) = await StartAsync();
        Assert.True(await _harness!.Machine.StartAsync(Rdp, ct: Ct));
        var id = _harness.Machine.SessionId!;
        Assert.True(await _harness.Machine.TryStageFrameAsync(new StagedFrame("f-live", _harness.Machine.NowMs, FrameTrigger.Click, 80, 60, null, new byte[] { 1 }), Ct));
        _sources.WhenStopping = async () => _sources.SessionWhenStopped = (await store.GetSessionSummaryAsync(id, Ct))?.State;

        var result = await DiscardAsync(discards, "discard_session", [id]);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, _sources.Stops);
        Assert.NotNull(_sources.SessionWhenStopped);
        Assert.NotEqual(SessionStateNames.Discarded, _sources.SessionWhenStopped);
        Assert.Equal(SessionState.Idle, _harness.Machine.State);
        Assert.Equal(0, _harness.Pending.DepthFor(id));
        Assert.Equal(SessionStatus.Discarded, SessionHistory.StatusOf((await store.GetSessionSummaryAsync(id, Ct))!));
    }

    [Fact]
    public async Task ABulkDiscardTakesEverySessionItNamesAndOnlyThose()
    {
        // History's multi-select: the current session and two older ones, confirmed by typing 3. The
        // fourth, not selected, keeps everything.
        var (store, discards) = await StartAsync();
        await StoredSessionAsync(store, "old-1", frames: 1);
        await StoredSessionAsync(store, "old-2", frames: 1);
        await StoredSessionAsync(store, "kept", frames: 1);
        var current = await RestingAsync(SessionState.DraftReady);

        var issued = Issue(discards, "discard_sessions", ["old-1", current, "old-2"]);
        Assert.Equal("3", issued.Phrase);
        var result = await HandleAsync(discards, ["old-2", "old-1", current], issued.Token);

        Assert.True(result.Ok, result.Error);
        foreach (var id in new[] { "old-1", "old-2", current })
        {
            Assert.Equal(SessionStatus.Discarded, SessionHistory.StatusOf((await store.GetSessionSummaryAsync(id, Ct))!));
        }

        Assert.Single((await store.LoadSessionAsync("kept", Ct))!.Frames);
        Assert.NotNull((await store.LoadSessionAsync("kept", Ct))!.Draft);
        Assert.Equal(SessionState.Idle, _harness!.Machine.State);

        // Only the session nobody selected still has its draft queued.
        Assert.Equal(1, (await _outbox!.WaitingAsync(Ct)).Drafts);
    }

    [Fact]
    public async Task ATokenReplayedForOtherSessionsDiscardsNothingAndIsSpent()
    {
        // The token is bound to the ids it was issued for. One earned by typing DISCARD for s1 is no use
        // for s2, nor for s1 and s2 together; and the attempt spends it, so it is no use for s1 afterwards
        // either.
        var (store, discards) = await StartAsync();
        await StoredSessionAsync(store, "s1", frames: 1);
        await StoredSessionAsync(store, "s2", frames: 1);
        var token = Issue(discards, "discard_session", ["s1"]).Token;

        var other = await HandleAsync(discards, ["s2"], token);
        var more = await HandleAsync(discards, ["s1", "s2"], Issue(discards, "discard_session", ["s1"]).Token);
        var again = await HandleAsync(discards, ["s1"], token);

        Assert.False(other.Ok);
        Assert.False(more.Ok);
        Assert.False(again.Ok);
        Assert.Equal(DiscardCommands.NotConfirmed, other.Error);
        foreach (var id in new[] { "s1", "s2" })
        {
            Assert.NotEqual(SessionStatus.Discarded, SessionHistory.StatusOf((await store.GetSessionSummaryAsync(id, Ct))!));
            Assert.Single((await store.LoadSessionAsync(id, Ct))!.Frames);
        }
    }

    [Fact]
    public async Task AnotherWindowCannotSpendTheToken()
    {
        var (store, discards) = await StartAsync();
        await StoredSessionAsync(store, "s1", frames: 1);
        var token = Issue(discards, "discard_session", ["s1"]).Token;

        var result = await discards.HandleAsync(new DiscardSessionsCommand { RequestId = 1, SessionIds = ["s1"], Confirmation = token }, Guid.NewGuid(), Ct);

        Assert.False(Assert.IsType<CommandResult>(result).Ok);
        Assert.Single((await store.LoadSessionAsync("s1", Ct))!.Frames);
    }

    [Fact]
    public async Task UnknownAndAlreadyDiscardedSessionsAreRefusedAndNothingIsDiscarded()
    {
        // All or nothing. The technician typed a count for a selection; discarding some of it and
        // refusing the rest would destroy a number of sessions they did not type.
        var (store, discards) = await StartAsync();
        await StoredSessionAsync(store, "fine", frames: 1);
        await StoredSessionAsync(store, "gone", frames: 1);
        _ = await store.DiscardSessionAsync("gone", Ct);

        var unknown = await DiscardAsync(discards, "discard_sessions", ["fine", "nobody"]);
        var discarded = await DiscardAsync(discards, "discard_sessions", ["fine", "gone"]);

        Assert.False(unknown.Ok);
        Assert.Contains("no longer exists", unknown.Error, StringComparison.Ordinal);
        Assert.False(discarded.Ok);
        Assert.Contains("already discarded", discarded.Error, StringComparison.Ordinal);
        Assert.StartsWith("Nothing was discarded", discarded.Error, StringComparison.Ordinal);
        Assert.Single((await store.LoadSessionAsync("fine", Ct))!.Frames);
        Assert.Single(await store.GetAuditRecordsAsync("gone", Ct), r => r.Type == AuditTypes.SessionDiscarded);
    }

    [Fact]
    public async Task APublishedSessionIsKept()
    {
        // Owner decision, fail closed: Spec §5 S4 does not say whether a published session may be
        // discarded, and its note is already in the PSA, so the local copy is kept until retention.
        var (store, discards) = await StartAsync();
        await StoredSessionAsync(store, "fine", frames: 1);
        await StoredSessionAsync(store, "sent", frames: 1);
        await store.SetSessionStateAsync("sent", SessionStateNames.Published, null, Ct);

        var result = await DiscardAsync(discards, "discard_sessions", ["fine", "sent"]);

        Assert.False(result.Ok);
        Assert.Contains("published", result.Error, StringComparison.Ordinal);
        Assert.Single((await store.LoadSessionAsync("fine", Ct))!.Frames);
        Assert.Single((await store.LoadSessionAsync("sent", Ct))!.Frames);
    }

    [Fact]
    public async Task ARefusedDiscardLeavesTheCurrentSessionWhereItWas()
    {
        // The current session and an unknown one: refused, so the machine is still resting on the
        // session, the pill still says so, and nothing was announced.
        var (store, discards) = await StartAsync();
        var current = await RestingAsync(SessionState.DraftReady);
        _announced.Clear();

        var result = await DiscardAsync(discards, "discard_sessions", [current, "nobody"]);

        Assert.False(result.Ok);
        Assert.Equal(SessionState.DraftReady, _harness!.Machine.State);
        Assert.Equal(current, _harness.Machine.SessionId);
        Assert.Empty(_announced);
        Assert.NotNull((await store.LoadSessionAsync(current, Ct))!.Draft);
    }

    [Theory]
    [InlineData("discard_session", "")]
    [InlineData("discard_session", "s1,s2")]
    [InlineData("discard_sessions", "")]
    [InlineData("discard_sessions", "s1,s1")]
    [InlineData("erase_everything", "s1")]
    public async Task ARequestThatCannotBeConfirmedIsGivenNoToken(string action, string ids)
    {
        // DISCARD is the word for one session in Review; a count is for a selection. A request with no
        // ids, a doubled id, or ids on an action that has none is a request for something the service
        // cannot name, and inventing a token for it would be a token for that.
        var (_, discards) = await StartAsync();

        var named = ids.Length == 0 ? [] : ids.Split(',');

        Assert.Null(discards.Confirm(new RequestConfirmationCommand { RequestId = 1, Action = action, SessionIds = named }, Window));
    }

    [Fact]
    public async Task TheTraysRequestIsNotOneThisAnswers()
    {
        // The tray's discard names nothing. Its request and its command stay the controller's, exactly
        // as they were before named discards existed.
        var (_, discards) = await StartAsync();

        Assert.Null(discards.Confirm(new RequestConfirmationCommand { RequestId = 1, Action = "discard_session" }, Window));
        Assert.Null(await discards.HandleAsync(new DiscardCommand { RequestId = 2, Confirmation = "x" }, Window, Ct));
    }

    [Fact]
    public async Task TheTraysDiscardStillDeletesTheCurrentSession()
    {
        // The tray path is unchanged: the machine's own Discard removes the row it is resting on, and a
        // token issued for a named discard cannot stand in for the tray's.
        var (store, discards) = await StartAsync();
        var id = await RestingAsync(SessionState.DraftReady);

        Assert.True(await _harness!.Machine.DiscardAsync(Ct));

        Assert.Equal(SessionState.Idle, _harness.Machine.State);
        Assert.Null(await store.GetSessionSummaryAsync(id, Ct));
        Assert.Contains(await store.GetAuditAsync(id, Ct), a => a.Type == AuditTypes.SessionDiscarded);
        _ = discards;
    }

    [Fact]
    public async Task TheStoreDiscardsSeveralSessionsInOneTransactionOrNoneOfThem()
    {
        // Every named discard is checked under the machine's gate first, so this refusal is the second
        // line: a session that changed after the check rolls the whole set back rather than leaving half
        // a selection discarded.
        var (store, _) = await StartAsync();
        await StoredSessionAsync(store, "a", frames: 1);
        await StoredSessionAsync(store, "b", frames: 1);
        await store.SetSessionStateAsync("b", SessionStateNames.Published, null, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DiscardSessionsAsync(["a", "b"], Ct));
        Assert.Single((await store.LoadSessionAsync("a", Ct))!.Frames);
        Assert.DoesNotContain(await store.GetAuditAsync("a", Ct), r => r.Type == AuditTypes.SessionDiscarded);

        await store.SetSessionStateAsync("b", CaptureStates.DraftReady, null, Ct);
        Assert.Equal(2, await store.DiscardSessionsAsync(["a", "b"], Ct));
        Assert.Equal(2, (await store.GetAuditAsync(ct: Ct)).Count(r => r.Type == AuditTypes.SessionDiscarded));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(SqliteSessionStore Store, DiscardCommands Discards)> StartAsync()
    {
        _harness = await MachineHarness.StartAsync(sources: _sources, drafter: _drafter);
        _outbox = new Outbox(_harness.Store, (item, _) =>
        {
            _sent.Add(item);
            return Task.FromResult(SendOutcome.Done(null));
        });
        _harness.Machine.StateChanged += _announced.Add;
        return (_harness.Store, new DiscardCommands(_harness.Machine));
    }

    /// <summary>A finished session that is not the machine's: two redacted screenshots, a sentence, a note and a queued draft.</summary>
    private async Task StoredSessionAsync(SqliteSessionStore store, string id, int frames)
    {
        await store.CreateSessionAsync(new NewSession(id, DateTimeOffset.UtcNow, Rdp, false, null), Ct);
        for (var i = 0; i < frames; i++)
        {
            var frame = $"{id}-f{i}";
            await store.StageFrameAsync(id, new StagedFrame(frame, 1_000 + i, FrameTrigger.Click, 80, 60, null, new byte[] { 0xAA }), Ct);
            await store.MarkFrameRedactedAsync(frame, new RedactionOutcome(new byte[] { 0xBB }, "SECRET OCR TEXT", [], SensitiveContext: false, DateTimeOffset.UtcNow), Ct);
        }

        await store.AppendTranscriptAsync(id, new TranscriptSegment { Id = $"{id}-t", TsMs = 1_000, EndMs = 1_500, Speaker = Speaker.Tech, Text = "SECRET restarting the spooler" }, Ct);
        await store.FinalizeSessionAsync(id, new FinalizeInfo(4_000, false), Ct);
        await store.SaveDraftAsync(id, Draft(), Ct);
        Assert.True(await _outbox!.EnqueueAsync(new NewOutboxItem(id, OutboxKind.Draft, $"draft:{id}", "{}"), Ct));
    }

    /// <summary>Runs a session to the resting state asked for and says which it was.</summary>
    private async Task<string> RestingAsync(SessionState resting)
    {
        var machine = _harness!.Machine;
        _drafter.Outcome = resting switch
        {
            SessionState.DraftPending => DraftOutcome.Pending("The draft is queued for the backend."),
            SessionState.DraftReady => DraftOutcome.Success(Draft()),
            _ => DraftOutcome.Failure("The backend could not draft it."),
        };
        _drafter.Drafting = resting == SessionState.DraftPending
            ? id => _outbox!.EnqueueAsync(new NewOutboxItem(id, OutboxKind.Draft, $"draft:{id}", "{}"), Ct)
            : null;

        Assert.True(await machine.StartAsync(Rdp, ct: Ct));
        var id = machine.SessionId!;
        Assert.True(await machine.StopAsync(Ct));
        Assert.Equal(resting, machine.State);
        return id;
    }

    private static ConfirmationIssued Issue(DiscardCommands discards, string action, IReadOnlyList<string> ids) =>
        Assert.IsType<ConfirmationIssued>(discards.Confirm(new RequestConfirmationCommand { RequestId = 1, Action = action, SessionIds = ids }, Window));

    private static async Task<CommandResult> HandleAsync(DiscardCommands discards, IReadOnlyList<string> ids, string token) =>
        Assert.IsType<CommandResult>(await discards.HandleAsync(new DiscardSessionsCommand { RequestId = 2, SessionIds = ids, Confirmation = token }, Window, Ct));

    private static Task<CommandResult> DiscardAsync(DiscardCommands discards, string action, IReadOnlyList<string> ids) =>
        HandleAsync(discards, ids, Issue(discards, action, ids).Token);

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private sealed class CountingSources : ICaptureSources
    {
        public int Stops { get; private set; }

        /// <summary>Called as capture stops, so a test can see what the store held at that moment.</summary>
        public Func<Task>? WhenStopping { get; set; }

        public string? SessionWhenStopped { get; set; }

        public Task StartAsync(SessionMachine machine, CancellationToken ct = default) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken ct = default)
        {
            Stops++;
            if (WhenStopping is { } stopping)
            {
                await stopping();
            }
        }

        public Task MarkMomentAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ScriptedDrafter : IDrafter
    {
        public DraftOutcome Outcome { get; set; } = DraftOutcome.Failure("not drafted");

        public Func<string, Task<bool>>? Drafting { get; set; }

        public async Task<DraftOutcome> DraftAsync(string sessionId, CancellationToken ct = default)
        {
            if (Drafting is { } drafting)
            {
                _ = await drafting(sessionId);
            }

            return Outcome;
        }
    }
}
