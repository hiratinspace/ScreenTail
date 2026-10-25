using System.Security.Cryptography;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Store;

/// <summary>
/// The draft as it was first written is kept beside the draft as edited (ST-098), so the edit ratio
/// measures the technician's changes against what the model produced rather than against the last save.
/// </summary>
public sealed class OriginalDraftTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private SqliteSessionStore? _store;

    [Fact]
    public async Task TheFirstDraftWrittenIsTheOriginalAndLaterSavesDoNotMoveIt()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        Assert.Null(await store.LoadOriginalDraftAsync("s1"));

        await store.SaveDraftAsync("s1", Draft(Step("as drafted")));
        await store.SaveDraftAsync("s1", Draft(Step("as edited by the technician")));

        Assert.Equal("as drafted", Assert.Single((await store.LoadOriginalDraftAsync("s1"))!.Steps).Text);
        Assert.Equal("as edited by the technician", Assert.Single((await store.LoadSessionAsync("s1"))!.Draft!.Steps).Text);
    }

    [Fact]
    public async Task DiscardingASessionTakesTheOriginalDraftToo()
    {
        // INV-12 (T-61). Discard clears the edited note; the copy kept for the edit ratio is the same
        // text and has to go with it, or a discarded session keeps a readable note under another name.
        var store = await OpenAsync();
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await store.SaveDraftAsync("s1", Draft(Step("the customer's password is hunter2")));
        await store.SaveDraftAsync("s1", Draft(Step("edited")));

        Assert.Equal(0, await store.DiscardSessionAsync("s1"));

        Assert.Null(await store.LoadOriginalDraftAsync("s1"));
        Assert.Null((await store.LoadSessionAsync("s1"))!.Draft);
    }

    [Fact]
    public async Task ASaveForADiscardedSessionIsRefusedAndWritesNothing()
    {
        // T-123. SaveDraftAsync used to ignore the row count, so a save that arrived after a discard
        // (Review's autosave firing one last time) put the note back into a row the technician had just
        // been told was gone: an INV-12 resurrection reported to the UI as Saved.
        var store = await OpenAsync();
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await store.SaveDraftAsync("s1", Draft(Step("as drafted")));
        await store.DiscardSessionAsync("s1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveDraftAsync("s1", Draft(Step("one more autosave"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveDraftAsync("never-existed", Draft(Step("a note for nobody"))));

        Assert.Null((await store.LoadSessionAsync("s1"))!.Draft);
        Assert.Null(await store.LoadOriginalDraftAsync("s1"));
        Assert.Null(await store.LoadSessionAsync("never-existed"));
    }

    [Fact]
    public async Task ANoteTheServiceCouldNotSaveIsFalseNotSaved()
    {
        // The command the UI's autosave sends. The "no such session" branch in ReviewCommands was dead
        // because the store never said no; now it does, and the result has to carry that to the UI.
        var store = await OpenAsync();
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await store.DiscardSessionAsync("s1");
        var commands = new Core.Review.ReviewCommands(store, new NoMasker());

        var discarded = await commands.HandleAsync(new Shared.Ipc.SaveDraftCommand { RequestId = 1, SessionId = "s1", Draft = Draft(Step("late")) });
        var unknown = await commands.HandleAsync(new Shared.Ipc.SaveDraftCommand { RequestId = 2, SessionId = "s2", Draft = Draft(Step("late")) });

        Assert.False(discarded!.Ok);
        Assert.Equal("There is no such session.", discarded.Error);
        Assert.False(unknown!.Ok);
        Assert.Null((await store.LoadSessionAsync("s1"))!.Draft);
    }

    [Fact]
    public async Task LoadingTheOriginalDraftWhileAnotherCallerWritesNeverFails()
    {
        // T-62. The read went to the shared connection without the store's gate, so it raced whatever
        // was writing at the time: after a publish, the style learner reads the original while the
        // outbox or an autosave writes, and the publish reply failed with the note already landed.
        // Probabilistic by nature; with the gate it cannot fail at all.
        var store = await OpenAsync();
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await store.SaveDraftAsync("s1", Draft(Step("as drafted")));

        for (var round = 0; round < 200; round++)
        {
            var writing = Task.Run(() => store.SaveDraftAsync("s1", Draft(Step($"edit {round}"))));
            var reading = Task.Run(() => store.LoadOriginalDraftAsync("s1"));
            var auditing = Task.Run(() => store.RecordAsync(AuditTypes.FrameCaptured, "s1", 1, "Click"));
            await Task.WhenAll(writing, reading, auditing);

            Assert.Equal("as drafted", Assert.Single((await reading)!.Steps).Text);
        }
    }

    private sealed class NoMasker : Core.Privacy.IFrameMasker
    {
        public Core.Privacy.MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge) =>
            throw new NotSupportedException("Saving a draft paints nothing.");
    }

    private async Task<SqliteSessionStore> OpenAsync() =>
        _store ??= await SqliteSessionStore.OpenAsync(Path.Combine(_dir, "store.db"), new FixedKey(RandomNumberGenerator.GetBytes(32)));

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
