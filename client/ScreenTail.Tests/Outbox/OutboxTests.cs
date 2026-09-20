using ScreenTail.Core.Outbox;

namespace ScreenTail.Tests.Outbox;

/// <summary>
/// ST-064. What happens to a draft or a publish when the network is not there.
///
/// The queue itself is easy; the two rules that are not are why this exists. Work must survive a restart,
/// because a technician who closes their laptop at the end of a job should not lose the note. And an
/// attempt whose outcome nobody knows — the request went, the answer never came — must never simply be
/// tried again, because the second copy of a note lands in a customer's ticket and the technician is the
/// last to find out.
/// </summary>
public sealed class OutboxTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WorkQueuedWhileOfflineIsDoneOnReconnect()
    {
        // ST-064 AC1. The session finished on a train. Nothing is lost, and nothing is attempted until
        // there is something to attempt it against.
        var store = new FakeOutboxStore();
        var time = new ManualTime(At);
        var network = new FakeNetwork { Up = false };
        var outbox = new Core.Outbox.Outbox(store, network.SendAsync, time);

        await outbox.EnqueueAsync(Work("s1", OutboxKind.Draft));

        // A drain while offline still counts as work done — it tried — but nothing left the machine and
        // the item is still waiting.
        Assert.True(await outbox.DrainAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, network.Sent);
        Assert.Equal(OutboxState.Pending, store.Items[0].State);

        network.Up = true;
        time.Advance(Core.Outbox.Outbox.RetryAfter(1));

        Assert.True(await outbox.DrainAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, network.Sent);
        Assert.Equal(OutboxState.Done, store.Items[0].State);
    }

    [Fact]
    public async Task AnAttemptWhoseOutcomeIsUnknownIsNeverQuietlyRepeated()
    {
        // ST-064 AC2, and the reason this class is not a list with a retry loop. The request reached the
        // PSA and the answer did not come back: the note may exist. Sending it again is how a customer
        // gets the same note twice, which looks like carelessness in the one artefact they read.
        var store = new FakeOutboxStore();
        var time = new ManualTime(At);
        var network = new FakeNetwork { Outcome = SendOutcome.Unknown("The PSA accepted the request and the reply timed out.") };
        var outbox = new Core.Outbox.Outbox(store, network.SendAsync, time);

        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Uncertain, store.Items[0].State);
        Assert.Equal(1, network.Sent);

        // However long it waits, and however many times it drains, it does not try again.
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromHours(1));
            _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, network.Sent);
        Assert.Equal(OutboxState.Uncertain, store.Items[0].State);
    }

    [Fact]
    public async Task AnUncertainItemIsResolvedByAskingRatherThanBySending()
    {
        // The way out of Uncertain: ask the provider whether the thing exists. If it does, the item is
        // done and nothing was sent twice. This is the only path that clears it.
        var store = new FakeOutboxStore();
        var time = new ManualTime(At);
        var network = new FakeNetwork { Outcome = SendOutcome.Unknown("timed out") };
        var outbox = new Core.Outbox.Outbox(store, network.SendAsync, time)
        {
            Confirm = (item, ct) => Task.FromResult<string?>("remote-42"),
        };

        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutboxState.Uncertain, store.Items[0].State);

        time.Advance(TimeSpan.FromMinutes(1));
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Done, store.Items[0].State);
        Assert.Equal("remote-42", store.Items[0].RemoteId);
        Assert.Equal(1, network.Sent);
    }

    [Fact]
    public async Task AnUncertainItemTheProviderNeverReceivedGoesBackInTheQueue()
    {
        var store = new FakeOutboxStore();
        var time = new ManualTime(At);
        var network = new FakeNetwork { Outcome = SendOutcome.Unknown("timed out") };
        var outbox = new Core.Outbox.Outbox(store, network.SendAsync, time)
        {
            Confirm = (item, ct) => Task.FromResult<string?>(null),
        };

        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        network.Outcome = SendOutcome.Done("remote-7");
        time.Advance(TimeSpan.FromMinutes(1));

        // One drain resolves it back to pending; the next sends it. Each drain does one thing, so a
        // caller's loop stays a loop rather than a cascade.
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutboxState.Pending, store.Items[0].State);

        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Done, store.Items[0].State);
        Assert.Equal(2, network.Sent);
    }

    [Fact]
    public async Task TheSameWorkIsOnlyQueuedOnce()
    {
        // Finalize can run twice — a crash between drafting and marking it done, a recovered session —
        // and the idempotency key is what stops that becoming two drafts of one session.
        var store = new FakeOutboxStore();
        var outbox = new Core.Outbox.Outbox(store, new FakeNetwork().SendAsync, new ManualTime(At));

        await outbox.EnqueueAsync(Work("s1", OutboxKind.Draft));
        await outbox.EnqueueAsync(Work("s1", OutboxKind.Draft));

        Assert.Single(store.Items);
    }

    [Fact]
    public async Task ARetryWaitsLongerEachTimeAndThenGivesUp()
    {
        // A provider that is down stays down for minutes, not milliseconds. Retrying every second wastes
        // a technician's battery and looks like an attack from the far end.
        var store = new FakeOutboxStore();
        var time = new ManualTime(At);
        var network = new FakeNetwork { Outcome = SendOutcome.Retry("The PSA did not answer.") };
        var outbox = new Core.Outbox.Outbox(store, network.SendAsync, time);

        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));

        var attempts = 0;
        for (var i = 0; i < 40; i++)
        {
            if (await outbox.DrainAsync(TestContext.Current.CancellationToken))
            {
                attempts++;
            }

            time.Advance(TimeSpan.FromMinutes(30));
        }

        Assert.Equal(OutboxState.Failed, store.Items[0].State);
        Assert.True(attempts <= Core.Outbox.Outbox.MaxAttempts, $"{attempts} attempts is more than the ceiling");
        Assert.NotNull(store.Items[0].LastError);
    }

    [Fact]
    public async Task ARefusalIsNotRetriedAtAll()
    {
        // A ticket that does not exist will not start existing. Retrying a permanent failure writes the
        // same rejection into a provider's audit log several hundred times.
        var store = new FakeOutboxStore();
        var time = new ManualTime(At);
        var network = new FakeNetwork { Outcome = SendOutcome.Failed("Ticket 48213 is not in the PSA.") };
        var outbox = new Core.Outbox.Outbox(store, network.SendAsync, time);

        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Failed, store.Items[0].State);

        time.Advance(TimeSpan.FromDays(1));
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, network.Sent);
    }

    [Fact]
    public async Task NothingIsAttemptedBeforeItIsDue()
    {
        var store = new FakeOutboxStore();
        var time = new ManualTime(At);
        var network = new FakeNetwork { Outcome = SendOutcome.Retry("not yet") };
        var outbox = new Core.Outbox.Outbox(store, network.SendAsync, time);

        await outbox.EnqueueAsync(Work("s1", OutboxKind.Draft));
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, network.Sent);

        // Straight back round, before the backoff has passed.
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, network.Sent);
    }

    [Fact]
    public async Task WorkIsTakenOldestFirst()
    {
        var store = new FakeOutboxStore();
        var time = new ManualTime(At);
        var network = new FakeNetwork();
        var outbox = new Core.Outbox.Outbox(store, network.SendAsync, time);

        await outbox.EnqueueAsync(Work("s1", OutboxKind.Draft));
        time.Advance(TimeSpan.FromMinutes(1));
        await outbox.EnqueueAsync(Work("s2", OutboxKind.Draft));

        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["s1", "s2"], network.Order);
    }

    [Fact]
    public async Task APendingDraftIsWhatTheOfflineBannerCountsFrom()
    {
        // Spec §5 S3: "Draft pending — offline". The banner needs to know there is something waiting,
        // and that it is a draft rather than a publish, because the wording differs.
        var store = new FakeOutboxStore();
        var outbox = new Core.Outbox.Outbox(store, new FakeNetwork { Up = false }.SendAsync, new ManualTime(At));

        await outbox.EnqueueAsync(Work("s1", OutboxKind.Draft));
        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));

        var waiting = await outbox.WaitingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, waiting.Drafts);
        Assert.Equal(1, waiting.Publishes);
        Assert.Equal(0, waiting.Uncertain);
    }

    [Fact]
    public async Task AnUncertainItemIsCountedSeparatelyBecauseSomebodyHasToLookAtIt()
    {
        var store = new FakeOutboxStore();
        var network = new FakeNetwork { Outcome = SendOutcome.Unknown("timed out") };
        var outbox = new Core.Outbox.Outbox(store, network.SendAsync, new ManualTime(At));

        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);

        var waiting = await outbox.WaitingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, waiting.Uncertain);
        Assert.Equal(0, waiting.Publishes);
    }

    [Fact]
    public async Task ASendThatTimesOutIsUncertainRatherThanSilence()
    {
        // 2026-09-19 review. An HttpClient timeout throws TaskCanceledException, which the filter did not
        // catch: it left DrainAsync, the host's loop caught it as cancellation, and the loop ended for
        // the life of the service. The item stayed Pending with its attempt count untouched, so the next
        // run sent it again — the duplicate publish this whole class exists to prevent.
        var store = new FakeOutboxStore();
        var outbox = Build(store, (_, _) => throw new TaskCanceledException("the request timed out"));
        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));

        Assert.True(await outbox.DrainAsync(TestContext.Current.CancellationToken));

        Assert.Equal(OutboxState.Uncertain, store.Items[0].State);
        Assert.Equal(1, store.Items[0].Attempts);
    }

    [Fact]
    public async Task BeingAskedToStopIsNotATimeout()
    {
        // The same exception type means two different things, and only the token says which. A drain
        // cancelled at shutdown must leave the item exactly as it was, not mark it uncertain forever.
        var store = new FakeOutboxStore();
        using var stopping = new CancellationTokenSource();
        var outbox = Build(store, (_, ct) =>
        {
            stopping.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(SendOutcome.Done("never"));
        });
        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.DrainAsync(stopping.Token));

        Assert.Equal(OutboxState.Pending, store.Items[0].State);
        Assert.Equal(0, store.Items[0].Attempts);
    }

    [Fact]
    public async Task AnItemThatCannotBeBuiltIsFailedRatherThanRetriedForEver()
    {
        // A payload the sender cannot make sense of, or an egress policy that refuses the destination.
        // Neither gets better by waiting, and the old code let the exception out of the loop, which
        // stopped every other item behind it too.
        var store = new FakeOutboxStore();
        var outbox = Build(store, (_, _) => throw new InvalidOperationException("this payload is not publishable"));
        await outbox.EnqueueAsync(Work("s1", OutboxKind.PublishNote));

        Assert.True(await outbox.DrainAsync(TestContext.Current.CancellationToken));

        Assert.Equal(OutboxState.Failed, store.Items[0].State);
        Assert.NotNull(store.Items[0].LastError);
    }

    [Fact]
    public async Task OnePoisonItemDoesNotStopTheWorkBehindIt()
    {
        var store = new FakeOutboxStore();
        var network = new FakeNetwork();
        var outbox = Build(store, (item, ct) => item.SessionId == "poison"
            ? throw new InvalidOperationException("not publishable")
            : network.SendAsync(item, ct));

        await outbox.EnqueueAsync(Work("poison", OutboxKind.PublishNote));
        await outbox.EnqueueAsync(Work("s2", OutboxKind.PublishNote));

        Assert.True(await outbox.DrainAsync(TestContext.Current.CancellationToken));
        Assert.True(await outbox.DrainAsync(TestContext.Current.CancellationToken));

        Assert.Equal(OutboxState.Failed, store.Items[0].State);
        Assert.Equal(OutboxState.Done, store.Items[1].State);
    }

    [Fact]
    public async Task AnUncertainItemNobodyCanResolveDoesNotStarveTheQueue()
    {
        // The class comment says an uncertain item "blocks nothing". It blocked everything: resolution
        // ran first on every drain, and a Confirm that threw returned before the due work was ever
        // looked at. One PSA that will not answer a lookup, and the queue stops for good.
        var store = new FakeOutboxStore();
        var network = new FakeNetwork();
        var outbox = Build(store, network.SendAsync, _ => throw new HttpRequestException("the lookup is down"));

        await outbox.EnqueueAsync(Work("stuck", OutboxKind.PublishNote));
        await outbox.EnqueueAsync(Work("s2", OutboxKind.PublishNote));
        store.Items[0] = store.Items[0] with { State = OutboxState.Uncertain };

        // One drain. A resolution that could not answer did not do the work, so the same drain goes on
        // to the item behind it rather than reporting a job well done and leaving the queue where it was.
        Assert.True(await outbox.DrainAsync(TestContext.Current.CancellationToken));

        Assert.Equal(OutboxState.Uncertain, store.Items[0].State);
        Assert.Equal(OutboxState.Done, store.Items[1].State);

        // And with nothing left but the stuck item, the drain says there is nothing to do rather than
        // spinning on it.
        Assert.False(await outbox.DrainAsync(TestContext.Current.CancellationToken));
    }

    private static Core.Outbox.Outbox Build(
        FakeOutboxStore store,
        Func<OutboxItem, CancellationToken, Task<SendOutcome>> send,
        Func<OutboxItem, string?>? confirm = null) =>
        new(store, send, new ManualTime(At))
        {
            Confirm = confirm is null ? null : (item, _) => Task.FromResult(confirm(item)),
        };

    private static NewOutboxItem Work(string sessionId, OutboxKind kind) =>
        new(sessionId, kind, $"{kind}:{sessionId}", "{}");

    private sealed class FakeNetwork
    {
        public bool Up { get; set; } = true;

        public SendOutcome Outcome { get; set; } = SendOutcome.Done("remote-1");

        public int Sent { get; private set; }

        public List<string> Order { get; } = [];

        public Task<SendOutcome> SendAsync(OutboxItem item, CancellationToken ct)
        {
            if (!Up)
            {
                return Task.FromResult(SendOutcome.Retry("There is no network."));
            }

            Sent++;
            Order.Add(item.SessionId);
            return Task.FromResult(Outcome);
        }
    }
}
