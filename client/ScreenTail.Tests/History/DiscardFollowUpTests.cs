using System.Diagnostics;
using System.Reflection;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Capture;
using ScreenTail.Core.History;
using ScreenTail.Core.Net;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.History;

/// <summary>
/// The review of #243: what a named discard left open (Spec §5 S3, S4; INV-12, INV-6), against the real
/// encrypted store and the real machine.
///
/// A published session was protected only in tests, because nothing wrote <c>published</c>. A discarded
/// row is kept, so the foreign key no longer stopped a late screenshot, event or sentence landing in it. A
/// bulk discard held the machine's gate while it deleted every screenshot, so a password field's
/// suppression waited behind it. And a discarded session still had its ticket number, could still be
/// published, and could still have its metric queued.
/// </summary>
public sealed class DiscardFollowUpTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private static readonly Guid Window = Guid.NewGuid();
    private readonly LandingGateway _gateway = new();
    private MachineHarness? _harness;
    private SessionMachine? _slowMachine;

    [Fact]
    public async Task APublishedSessionShowsAsPublishedAndADiscardOfItIsRefused()
    {
        // End to end: publish through the service's own command, then try to throw the session away. Its
        // note is in the PSA, so the local copy is kept (owner decision, fail closed), and History says
        // Published rather than Draft.
        var (store, discards) = await StartAsync();
        await StoredAsync(store, "s1");

        Assert.IsType<SessionPublished>(await PublishAsync(store, "s1"));

        Assert.Equal(SessionStatus.Published, SessionHistory.StatusOf((await store.GetSessionSummaryAsync("s1", Ct))!));
        var refused = await DiscardAsync(discards, ["s1"]);
        Assert.False(refused.Ok);
        Assert.Contains("published", refused.Error, StringComparison.Ordinal);
        Assert.Single((await store.LoadSessionAsync("s1", Ct))!.Frames);
        Assert.Empty(SessionHistory.Discardable(await store.ListSessionsAsync(ct: Ct)));
    }

    [Fact]
    public async Task APublishWhereNothingLandedIsNotPublished()
    {
        var (store, _) = await StartAsync();
        await StoredAsync(store, "s1");
        _gateway.Lands = false;

        Assert.IsType<SessionPublished>(await PublishAsync(store, "s1"));

        Assert.Equal(SessionStatus.Draft, SessionHistory.StatusOf((await store.GetSessionSummaryAsync("s1", Ct))!));
    }

    [Fact]
    public async Task ADiscardedSessionIsNotPublished()
    {
        // The note comes from the pane, not the store, so a discarded session could still be sent with
        // whatever the window held. It is refused before the backend is asked.
        var (store, discards) = await StartAsync();
        await StoredAsync(store, "s1");
        Assert.True((await DiscardAsync(discards, ["s1"])).Ok);

        var publishing = new PublishCommands(store, _gateway);
        var command = Publish("s1");
        Assert.Null(await publishing.ReplyToAsync(command, Ct));
        var result = await publishing.HandleAsync(command, Ct);

        Assert.False(result!.Ok);
        Assert.Contains("discarded", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, _gateway.Publishes);
    }

    [Fact]
    public async Task APublishThatLandsAfterADiscardDoesNotBringTheSessionBack()
    {
        // The backend takes seconds; the technician discards meanwhile. The note is on the ticket, but the
        // row stays discarded: the write that marks it published refuses a discarded row.
        var (store, _) = await StartAsync();
        await StoredAsync(store, "s1");
        _gateway.WhilePublishing = () => store.DiscardSessionsAsync(["s1"], Ct);

        Assert.IsType<SessionPublished>(await PublishAsync(store, "s1"));

        Assert.Equal(SessionStatus.Discarded, SessionHistory.StatusOf((await store.GetSessionSummaryAsync("s1", Ct))!));
    }

    [Fact]
    public async Task ALateScreenshotEventOrSentenceDoesNotLandInADiscardedSession()
    {
        // The row of a discarded session is kept, so the foreign key no longer refuses what arrives for it.
        // A screenshot the redaction worker finished, a hook's event or a sentence the model finished after
        // the discard must not put raw data back into it (INV-12).
        var (store, discards) = await StartAsync();
        await StoredAsync(store, "s1");
        Assert.True((await DiscardAsync(discards, ["s1"])).Ok);
        Assert.Equal(0, await store.GetLastTimestampAsync("s1", Ct));

        await store.SaveRedactedFrameAsync("s1", new StagedFrame("late", 99_000, FrameTrigger.Click, 80, 60, null, new byte[] { 1 }), new RedactionOutcome(new byte[] { 2 }, "SECRET", [], false, DateTimeOffset.UtcNow), Ct);
        await store.StageFrameAsync("s1", new StagedFrame("late-staged", 98_000, FrameTrigger.Click, 80, 60, null, new byte[] { 1 }), Ct);
        await store.AppendEventAsync("s1", new ClickEvent { TsMs = 97_000, X = 1, Y = 1, Button = MouseButton.Left }, Ct);
        await store.AppendTranscriptAsync("s1", new TranscriptSegment { Id = "late-t", TsMs = 96_000, EndMs = 96_500, Speaker = Speaker.Tech, Text = "SECRET" }, Ct);

        // Every content table, unfiltered: nothing arrived.
        Assert.Equal(0, await store.GetLastTimestampAsync("s1", Ct));
        Assert.Null(await store.GetRedactedFrameImageAsync("late", Ct));
    }

    [Fact]
    public async Task ADiscardedSessionsContentIsUnreachableTheMomentItIsMarkedAndGoneOnceFinished()
    {
        // Review of #243: the store marks the sessions discarded under the machine's gate, cheaply, and
        // deletes their screenshots afterwards in chunks. Between the two nothing may read them.
        var (store, _) = await StartAsync();
        await StoredAsync(store, "s1", frames: 3, ticket: "48213");

        Assert.Equal(3, await store.DiscardSessionsAsync(["s1"], Ct));

        var marked = (await store.LoadSessionAsync("s1", Ct))!;
        Assert.Empty(marked.Frames);
        Assert.Empty(marked.Transcript);
        Assert.Empty(marked.Events);
        Assert.Null(marked.Draft);
        Assert.Null(marked.SuggestedTicket);
        Assert.Null(await store.GetRedactedFrameImageAsync("s1-f0", Ct));
        Assert.Null(await store.GetRedactedFrameTextAsync("s1-f0", Ct));
        Assert.Null(await store.GetRedactedFrameForBlurAsync("s1-f0", Ct));
        Assert.Empty(await store.GetFrameImageSizesAsync("s1", Ct));
        Assert.Equal(0, Assert.Single(await store.ListSessionsAsync(ct: Ct)).Frames);
        Assert.Equal(3, Assert.Single(await store.GetAuditAsync("s1", Ct), a => a.Type == AuditTypes.SessionDiscarded).Count);
        Assert.Equal(["s1"], await store.ListUnfinishedDiscardsAsync(Ct));
        Assert.NotEqual(0, await store.GetLastTimestampAsync("s1", Ct));

        // A service that stopped here (a crash, a restart) finishes the job when it starts again.
        await _harness!.Machine.FinishDiscardsAsync(Ct);

        Assert.Equal(0, await store.GetLastTimestampAsync("s1", Ct));
        Assert.Empty(await store.ListUnfinishedDiscardsAsync(Ct));
        Assert.Single(await store.GetAuditAsync("s1", Ct), a => a.Type == AuditTypes.SessionDiscarded);
    }

    [Fact]
    public async Task APasswordFieldIsSuppressedPromptlyDuringALargeBulkDiscard()
    {
        // INV-6. The guards suppress through the machine's gate, and a bulk discard held that gate while
        // it deleted every screenshot of every session (1.77 s for fifty sessions of twenty frames): the
        // password field's frames went on being accepted the whole time. The deleting is done outside the
        // gate now, so a suppression waits for a cheap mark at most, never for the deleting.
        var (store, _) = await StartAsync();
        for (var i = 0; i < 5; i++)
        {
            await StoredAsync(store, $"old-{i}", frames: 4);
        }

        var (slow, gate) = SlowPurge.Over(store);
        _slowMachine = new SessionMachine(slow, new NoCaptureSources(), new UnavailableDrafter());
        var discards = new DiscardCommands(_slowMachine);
        Assert.True(await _slowMachine.StartAsync(Rdp, ct: Ct));

        string[] ids = ["old-0", "old-1", "old-2", "old-3", "old-4"];
        var token = Assert.IsType<ConfirmationIssued>(discards.Confirm(new RequestConfirmationCommand { RequestId = 1, Action = "discard_sessions", SessionIds = ids }, Window)).Token;
        var discarding = discards.HandleAsync(new DiscardSessionsCommand { RequestId = 2, SessionIds = ids, Confirmation = token }, Window, Ct);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Bounded, so a discard that does hold the gate fails this rather than hanging it: the deleting
        // waits for the test, and the test would be waiting for the suppression.
        var clock = Stopwatch.StartNew();
        var suppressing = _slowMachine.SuppressAsync(CaptureStateReason.PasswordField, Ct);
        var first = await Task.WhenAny(suppressing, Task.Delay(TimeSpan.FromSeconds(2), Ct));
        clock.Stop();
        gate.Open();

        Assert.Same(suppressing, first);
        Assert.True(await suppressing);
        Assert.Equal(SessionState.Suppressed, _slowMachine.State);
        Assert.True(clock.ElapsedMilliseconds < 100, $"suppression waited {clock.ElapsedMilliseconds} ms behind the discard");

        Assert.True((await discarding)!.Ok);
        Assert.Empty(await store.ListUnfinishedDiscardsAsync(Ct));
        foreach (var id in ids)
        {
            Assert.Equal(0, await store.GetLastTimestampAsync(id, Ct));
        }
    }

    [Fact]
    public async Task NoMetricIsQueuedForADiscardedSession()
    {
        var (store, discards) = await StartAsync();
        await StoredAsync(store, "s1");
        Assert.True((await DiscardAsync(discards, ["s1"])).Ok);
        var outbox = new Core.Outbox.Outbox(store, (_, _) => Task.FromResult(SendOutcome.Done(null)));

        await new MetricsReporter(store, outbox, () => true).ReportAsync("s1", published: false, Ct);

        Assert.Equal(new OutboxWaiting(0, 0, 0), await outbox.WaitingAsync(Ct));
    }

    [Fact]
    public async Task ADiscardForgetsTheTicketNumberReadOffTheScreen()
    {
        // The suggested ticket is digits read off the customer's window at start (ST-077). It links the
        // session to a customer, and a discarded session keeps nothing of the kind.
        var (store, discards) = await StartAsync();
        await StoredAsync(store, "named", ticket: "48213");
        await StoredAsync(store, "single", ticket: "48214");

        Assert.True((await DiscardAsync(discards, ["named"])).Ok);
        _ = await store.DiscardSessionAsync("single", Ct);

        Assert.Null((await store.LoadSessionAsync("named", Ct))!.SuggestedTicket);
        Assert.Null((await store.LoadSessionAsync("single", Ct))!.SuggestedTicket);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(SqliteSessionStore Store, DiscardCommands Discards)> StartAsync()
    {
        _harness = await MachineHarness.StartAsync();
        return (_harness.Store, new DiscardCommands(_harness.Machine));
    }

    private static async Task StoredAsync(SqliteSessionStore store, string id, int frames = 1, string? ticket = null)
    {
        await store.CreateSessionAsync(new NewSession(id, DateTimeOffset.UtcNow, Rdp, false, null, ticket), Ct);
        for (var i = 0; i < frames; i++)
        {
            await store.SaveRedactedFrameAsync(id, new StagedFrame($"{id}-f{i}", 1_000 + i, FrameTrigger.Click, 80, 60, null, new byte[] { 1 }), new RedactionOutcome(new byte[] { 2 }, "SECRET OCR", [], false, DateTimeOffset.UtcNow), Ct);
        }

        await store.AppendEventAsync(id, new ClickEvent { TsMs = 1_500, X = 1, Y = 1, Button = MouseButton.Left }, Ct);
        await store.AppendTranscriptAsync(id, new TranscriptSegment { Id = $"{id}-t", TsMs = 1_000, EndMs = 1_400, Speaker = Speaker.Tech, Text = "restarting the spooler" }, Ct);
        await store.FinalizeSessionAsync(id, new FinalizeInfo(4_000, false), Ct);
        await store.SaveDraftAsync(id, Draft(), Ct);
    }

    private static PublishSessionCommand Publish(string id) => new()
    {
        RequestId = 7,
        SessionId = id,
        TicketId = "48213",
        NoteType = "internal",
        Minutes = 30,
        Destinations = ["ticket_note"],
        Note = Draft(),
        FrameIds = [],
    };

    private Task<IpcEvent?> PublishAsync(SqliteSessionStore store, string id) =>
        new PublishCommands(store, _gateway).ReplyToAsync(Publish(id), Ct);

    private static async Task<CommandResult> DiscardAsync(DiscardCommands discards, IReadOnlyList<string> ids)
    {
        var token = Assert.IsType<ConfirmationIssued>(discards.Confirm(new RequestConfirmationCommand { RequestId = 1, Action = "discard_sessions", SessionIds = ids }, Window)).Token;
        return Assert.IsType<CommandResult>(await discards.HandleAsync(new DiscardSessionsCommand { RequestId = 2, SessionIds = ids, Confirmation = token }, Window, Ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (_slowMachine is not null)
        {
            await _slowMachine.DisposeAsync();
        }

        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>A PSA that lands every destination, or none, and can do something while it is being asked.</summary>
    private sealed class LandingGateway : IPsaGateway
    {
        public bool Lands { get; set; } = true;

        public Func<Task>? WhilePublishing { get; set; }

        public int Publishes { get; private set; }

        public async Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default)
        {
            Publishes++;
            if (WhilePublishing is { } meanwhile)
            {
                await meanwhile();
            }

            IReadOnlyList<PublishOutcomeRow> rows = [.. bundle.Destinations.Select(d => new PublishOutcomeRow(d, Lands, Lands ? "1" : null, null, Lands ? null : "No."))];
            return GatewayAnswer.Of(rows);
        }

        public Task<GatewayAnswer<IReadOnlyList<IntegrationInfo>>> IntegrationsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GatewayAnswer<IReadOnlyList<TicketRow>>> SearchTicketsAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GatewayAnswer<CompanyMappingsAnswer>> CompanyMappingsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GatewayAnswer<bool>> MapCompanyAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GatewayAnswer<IReadOnlyList<IntegrationDetailRow>>> IntegrationDetailsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GatewayAnswer<bool>> StoreIntegrationAsync(string provider, string siteUrl, string secret, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GatewayAnswer<bool>> RemoveIntegrationAsync(string provider, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GatewayAnswer<IntegrationCheckRow>> CheckIntegrationAsync(string provider, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GatewayAnswer<bool>> UnmapCompanyAsync(string psaCompany, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// The real store, with the deleting of a discarded session's content held until the test opens it:
    /// a store as slow as one with secure delete and a thousand screenshots, for as long as the test needs.
    /// Not sealed, because DispatchProxy derives from it.
    /// </summary>
    public class SlowPurge : DispatchProxy
    {
        private ISessionStore? _inner;
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed once the deleting has begun and is waiting.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Both faces, so the machine still finds the audit log behind the proxy.</summary>
        public interface IAuditedStore : ISessionStore, IAuditLog
        {
        }

        public static (ISessionStore Store, SlowPurge Gate) Over(SqliteSessionStore inner)
        {
            var store = Create<IAuditedStore, SlowPurge>();
            var gate = (SlowPurge)(object)store;
            gate._inner = inner;
            return (store, gate);
        }

        public void Open() => _open.TrySetResult();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(ISessionStore.PurgeDiscardedContentAsync))
            {
                _ = Entered.TrySetResult();
                return PurgeWhenOpenAsync(targetMethod, args);
            }

            return targetMethod.Invoke(_inner, args);
        }

        private async Task<bool> PurgeWhenOpenAsync(MethodInfo purge, object?[]? args)
        {
            await _open.Task.ConfigureAwait(false);
            return await ((Task<bool>)purge.Invoke(_inner, args)!).ConfigureAwait(false);
        }
    }
}
