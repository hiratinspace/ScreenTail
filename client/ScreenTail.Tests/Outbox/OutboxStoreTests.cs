using System.Security.Cryptography;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Outbox;

/// <summary>
/// The outbox against the real encrypted store (ST-064).
///
/// <see cref="OutboxTests"/> proves the rules with a list. This proves the two things a list cannot:
/// that queued work survives the process that queued it, which is the whole reason the outbox exists,
/// and that it is deleted with the session's raw data rather than outliving it in the one table nobody
/// thinks to look at (INV-12).
/// </summary>
public sealed class OutboxStoreTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKey _key = new(RandomNumberGenerator.GetBytes(32));
    private SqliteSessionStore? _store;

    [Fact]
    public async Task QueuedWorkSurvivesTheProcessThatQueuedIt()
    {
        // The reason this is a table and not a list. A technician finishes a job, closes the laptop, and
        // the draft is still owed when the machine comes back.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await SessionAsync(store, "s1", ct);
        Assert.True(await store.EnqueueAsync(Item("o1", "s1", OutboxKind.Draft), ct));

        await store.DisposeAsync();
        _store = null;

        var reopened = await OpenAsync(ct);
        var due = await reopened.TakeDueAsync(At.AddMinutes(1), ct);

        Assert.NotNull(due);
        Assert.Equal("o1", due.Id);
        Assert.Equal(OutboxKind.Draft, due.Kind);
    }

    [Fact]
    public async Task TheSameWorkCannotBeQueuedTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await SessionAsync(store, "s1", ct);

        Assert.True(await store.EnqueueAsync(Item("o1", "s1", OutboxKind.Draft), ct));
        Assert.False(await store.EnqueueAsync(Item("o2", "s1", OutboxKind.Draft), ct));
    }

    [Fact]
    public async Task WorkThatFailedCanBeQueuedAgainByHand()
    {
        // The uniqueness is over live work only. A technician who fixes their API key and presses Retry
        // must not be told the work is already queued by a row that records why it failed.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await SessionAsync(store, "s1", ct);
        var first = Item("o1", "s1", OutboxKind.PublishNote);
        _ = await store.EnqueueAsync(first, ct);
        await store.UpdateAsync(first with { State = OutboxState.Failed, LastError = "Ticket not found." }, ct);

        Assert.True(await store.EnqueueAsync(Item("o2", "s1", OutboxKind.PublishNote), ct));
    }

    [Fact]
    public async Task RetentionTakesTheQueuedWorkWithTheSessionsRawData()
    {
        // ST-064 AC3. A queued publish holds the note it was going to send; leaving it would keep the
        // session's text past the tenant's window in a table nobody looks at (INV-12).
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await SessionAsync(store, "s1", ct);
        _ = await store.EnqueueAsync(Item("o1", "s1", OutboxKind.PublishNote), ct);

        _ = await store.PurgeRawDataAsync("s1", ct);

        Assert.Null(await store.TakeDueAsync(At.AddYears(1), ct));
        Assert.Equal(0, (await store.CountWaitingAsync(ct)).Total);
    }

    [Fact]
    public async Task DiscardingASessionTakesItsQueuedWorkToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await SessionAsync(store, "s1", ct);
        _ = await store.EnqueueAsync(Item("o1", "s1", OutboxKind.Draft), ct);

        _ = await store.DiscardSessionAsync("s1", ct);

        Assert.Null(await store.TakeDueAsync(At.AddYears(1), ct));
    }

    [Fact]
    public async Task AnUncertainItemIsNotHandedOutAsDueWork()
    {
        // The safety property, at the storage layer. Whatever the drain loop does, the query that feeds
        // it cannot return something that may already have landed.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await SessionAsync(store, "s1", ct);
        var item = Item("o1", "s1", OutboxKind.PublishNote);
        _ = await store.EnqueueAsync(item, ct);
        await store.UpdateAsync(item with { State = OutboxState.Uncertain }, ct);

        Assert.Null(await store.TakeDueAsync(At.AddYears(1), ct));
        Assert.Equal("o1", (await store.TakeUncertainAsync(ct))!.Id);
    }

    [Fact]
    public async Task WhatIsWaitingIsCountedByKind()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await SessionAsync(store, "s1", ct);
        _ = await store.EnqueueAsync(Item("o1", "s1", OutboxKind.Draft), ct);
        _ = await store.EnqueueAsync(Item("o2", "s1", OutboxKind.PublishNote), ct);
        _ = await store.EnqueueAsync(Item("o3", "s1", OutboxKind.PublishArticle), ct);

        var waiting = await store.CountWaitingAsync(ct);

        Assert.Equal(1, waiting.Drafts);
        Assert.Equal(2, waiting.Publishes);
    }

    [Fact]
    public async Task EverythingAboutAnItemComesBackAsItWentIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await SessionAsync(store, "s1", ct);
        var item = Item("o1", "s1", OutboxKind.PublishTimeEntry) with
        {
            Attempts = 3,
            LastAttemptAt = At.AddMinutes(5),
            LastError = "The PSA did not answer.",
            RemoteId = "remote-9",
        };
        _ = await store.EnqueueAsync(item, ct);

        var read = await store.TakeDueAsync(At.AddYears(1), ct);

        Assert.Equal(item, read);
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private static OutboxItem Item(string id, string sessionId, OutboxKind kind) => new(
        id,
        sessionId,
        kind,
        $"{kind}:{sessionId}",
        "{}",
        OutboxState.Pending,
        Attempts: 0,
        CreatedAt: At,
        DueAt: At,
        LastAttemptAt: null,
        LastError: null,
        RemoteId: null);

    private static async Task SessionAsync(SqliteSessionStore store, string id, CancellationToken ct) =>
        await store.CreateSessionAsync(new NewSession(id, At, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null), ct);

    private async Task<SqliteSessionStore> OpenAsync(CancellationToken ct) =>
        _store ??= await SqliteSessionStore.OpenAsync(_path, _key, ct: ct);

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
