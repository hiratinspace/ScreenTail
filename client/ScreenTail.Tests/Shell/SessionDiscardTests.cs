using System.IO.Pipes;
using ScreenTail.Core.History;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Shell;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// Review's Discard and History's bulk discard from the UI's side (Spec §5 S3, S4; contract v6), end to
/// end over a real pipe to the service's <see cref="DiscardCommands"/> on the real machine and store.
///
/// The UI asks for a token naming the sessions, the technician types what the service says to type, and
/// the command carries the same ids and the token back. What the shell does afterwards is part of it:
/// a discarded session is not left open in Review, and Review goes to History (owner decision, fail
/// closed: Spec §5 S3 does not say).
/// </summary>
public sealed class SessionDiscardTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private readonly string _pipeName = IpcPipeNames.ForUser("test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _token = IpcToken.Generate();
    private readonly ShellState _state = new();
    private MachineHarness? _harness;
    private IpcServer? _server;
    private CaptureConnection? _connection;

    [Fact]
    public async Task ReviewsDiscardThrowsAwayTheSessionItShowsAndGoesToHistory()
    {
        var discard = await StartAsync();
        await StoredAsync("s1");
        await StoredAsync("s2");
        _state.OpenSession("s1");

        var result = await discard.FromReviewAsync("s1", Ct);

        Assert.True(result.Done, result.Error);
        Assert.Equal(SessionStatus.Discarded, await StatusAsync("s1"));
        Assert.NotEqual(SessionStatus.Discarded, await StatusAsync("s2"));
        Assert.Null(_state.Snapshot.Reviewing);
        Assert.Equal(ShellView.History, _state.Snapshot.View);
    }

    [Fact]
    public async Task ABulkDiscardAsksForTheCountAndTakesExactlyTheSelection()
    {
        // v0.4.1 Q6: the technician types how many sessions are selected. The number comes from the
        // service, which counted the ids it bound the token to.
        var discard = await StartAsync();
        await StoredAsync("s1");
        await StoredAsync("s2");
        await StoredAsync("s3");
        _state.OpenSession("s2");
        _state.Navigate(ShellView.History);
        var asked = new List<string>();

        var result = await discard.BulkAsync(["s1", "s2"], phrase =>
        {
            asked.Add(phrase);
            return Task.FromResult(true);
        }, Ct);

        Assert.True(result.Done, result.Error);
        Assert.Equal(["2"], asked);
        Assert.Equal(SessionStatus.Discarded, await StatusAsync("s1"));
        Assert.Equal(SessionStatus.Discarded, await StatusAsync("s2"));
        Assert.NotEqual(SessionStatus.Discarded, await StatusAsync("s3"));

        // The pane that was open on s2 is not left showing a session that no longer has anything in it,
        // and History, where the technician is, stays where it is.
        Assert.Null(_state.Snapshot.Reviewing);
        Assert.Equal(ShellView.History, _state.Snapshot.View);
    }

    [Fact]
    public async Task NotTypingTheCountDiscardsNothing()
    {
        var discard = await StartAsync();
        await StoredAsync("s1");
        _state.OpenSession("s1");

        var result = await discard.BulkAsync(["s1"], _ => Task.FromResult(false), Ct);

        Assert.Equal(ConfirmedOutcome.Declined, result.Outcome);
        Assert.NotEqual(SessionStatus.Discarded, await StatusAsync("s1"));
        Assert.Equal("s1", _state.Snapshot.Reviewing);
    }

    [Fact]
    public async Task ARefusalComesBackInTheServicesWordsAndLeavesReviewOpen()
    {
        var discard = await StartAsync();
        await StoredAsync("s1");
        _ = await _harness!.Store.DiscardSessionAsync("s1", Ct);
        _state.OpenSession("s1");

        var result = await discard.FromReviewAsync("s1", Ct);

        Assert.Equal(ConfirmedOutcome.Refused, result.Outcome);
        Assert.Contains("already discarded", result.Error, StringComparison.Ordinal);
        Assert.Equal("s1", _state.Snapshot.Reviewing);
        Assert.Equal(ShellView.Review, _state.Snapshot.View);
    }

    [Fact]
    public async Task AnotherConnectionCannotSpendTheTokenThisOneWasGiven()
    {
        // The service binds the token to the connection that asked. A second client on the same pipe, the
        // same user and the right session token, still cannot spend it.
        _ = await StartAsync();
        await StoredAsync("s1");
        await using var window = await ClientAsync();
        await using var other = await ClientAsync();

        var issued = await window.RequestAsync<ConfirmationIssued>(id => new RequestConfirmationCommand { RequestId = id, Action = "discard_session", SessionIds = ["s1"] }, Ct);
        var stolen = await other.SendAsync(id => new DiscardSessionsCommand { RequestId = id, SessionIds = ["s1"], Confirmation = issued.Token }, Ct);
        var owned = await window.SendAsync(id => new DiscardSessionsCommand { RequestId = id, SessionIds = ["s1"], Confirmation = issued.Token }, Ct);

        Assert.Equal("DISCARD", issued.Phrase);
        Assert.False(stolen.Ok);
        Assert.True(owned.Ok, owned.Error);
        Assert.Equal(SessionStatus.Discarded, await StatusAsync("s1"));
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    public async Task ASessionStillBeingCapturedIsNotOfferedADialog(string capturing)
    {
        // Spec §4 and v0.4.1 Q3: no modal while a session records. Review of the running session is
        // read-only until it stops (§5 S2), so its Discard says why instead of asking, and the service is
        // not asked for anything.
        var asked = 0;
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = capturing, SessionId = "live" });
        var discard = new SessionDiscard(
            state,
            (_, _) =>
            {
                asked++;
                return Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t", Phrase = "DISCARD" });
            },
            (_, _) => Task.FromResult(new CommandResult { RequestId = 1, Ok = true }));

        var review = await discard.FromReviewAsync("live", Ct);
        var bulk = await discard.BulkAsync(["old", "live"], _ => Task.FromResult(true), Ct);

        Assert.Equal((ConfirmedOutcome.Refused, SessionDiscard.StillCapturing), (review.Outcome, review.Error));
        Assert.Equal((ConfirmedOutcome.Refused, SessionDiscard.OneStillCapturing), (bulk.Outcome, bulk.Error));
        Assert.Equal(0, asked);
        Assert.Null(discard.BlockedBecause(["old"]));
    }

    [Fact]
    public async Task WithNoServiceNothingIsShownAndNothingIsSent()
    {
        // KnownCapture is null when the service is not answering, which may mean it is still recording.
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Idle });
        state.Lost();
        var sent = 0;
        var discard = new SessionDiscard(
            state,
            (_, _) => Task.FromResult<ConfirmationIssued?>(null),
            (_, _) =>
            {
                sent++;
                return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
            });

        var result = await discard.FromReviewAsync("s1", Ct);

        Assert.Equal((ConfirmedOutcome.NotAvailable, ConfirmedAction.NotConnected), (result.Outcome, result.Error));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AServiceAskingForOtherWordsIsNotSentTheDiscard()
    {
        // Review's dialog asked for DISCARD. A service that asks for something else is not one the
        // technician agreed with, so nothing is sent, as ConfirmedAction does for the tray.
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Idle });
        var sent = 0;
        var discard = new SessionDiscard(
            state,
            (_, _) => Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t", Phrase = "DELETE" }),
            (_, _) =>
            {
                sent++;
                return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
            });

        var result = await discard.FromReviewAsync("s1", Ct);

        Assert.Equal((ConfirmedOutcome.Refused, ConfirmedAction.PhraseChanged), (result.Outcome, result.Error));
        Assert.Equal(0, sent);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<SessionDiscard> StartAsync()
    {
        _harness = await MachineHarness.StartAsync();
        var handler = new DiscardsOnly(_harness.Machine, new DiscardCommands(_harness.Machine));
        _server = new IpcServer(IpcServer.DefaultPipeFactory(_pipeName), _token, new AcceptAll(), handler, _harness.Store, "0.0.1-test");
        _server.Start();
        _connection = new CaptureConnection(
            _state,
            async ct => await IpcClient.ConnectAsync(_pipeName, _token, "test-ui", serverVerifier: null, "0.0.1", ct: ct),
            _ => TimeSpan.FromMilliseconds(50));
        await _connection.StartAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!_connection.IsConnected)
        {
            Assert.True(DateTime.UtcNow < deadline, "the connection did not come up");
            await Task.Delay(10, Ct);
        }

        return new SessionDiscard(_state, _connection);
    }

    private Task<IpcClient> ClientAsync() => IpcClient.ConnectAsync(_pipeName, _token, "test-ui", serverVerifier: null, "0.0.1", ct: Ct);

    private async Task StoredAsync(string id)
    {
        var store = _harness!.Store;
        await store.CreateSessionAsync(new NewSession(id, DateTimeOffset.UtcNow, Rdp, false, null), Ct);
        await store.SaveRedactedFrameAsync(id, new StagedFrame($"{id}-f", 1_000, FrameTrigger.Click, 80, 60, null, new byte[] { 1 }), new RedactionOutcome(new byte[] { 2 }, null, [], false, DateTimeOffset.UtcNow), Ct);
        await store.FinalizeSessionAsync(id, new FinalizeInfo(4_000, false), Ct);
        await store.SaveDraftAsync(id, Draft(), Ct);
        _ = await new Core.Outbox.Outbox(store, (_, _) => Task.FromResult(SendOutcome.Done(null))).EnqueueAsync(new NewOutboxItem(id, OutboxKind.Draft, $"draft:{id}", "{}"), Ct);
    }

    private async Task<SessionStatus> StatusAsync(string id) =>
        SessionHistory.StatusOf((await _harness!.Store.GetSessionSummaryAsync(id, Ct))!);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>The service's discard commands and nothing else, which is all this pipe is asked.</summary>
    private sealed class DiscardsOnly(Core.Sessions.SessionMachine machine, DiscardCommands discards) : IIpcCommandHandler
    {
        public CaptureStateSnapshot CurrentState => machine.Snapshot;

        public CapabilitiesReported CurrentCapabilities => throw new NotSupportedException();

        public async Task<CommandResult> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            await discards.HandleAsync(command, caller, ct)
            ?? new CommandResult { RequestId = command.RequestId, Ok = false, Error = "Not a discard." };

        public Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            Task.FromResult<IpcEvent?>(command is RequestConfirmationCommand request ? discards.Confirm(request, caller) : null);
    }

    private sealed class AcceptAll : IClientVerifier
    {
        public ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    }
}
