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
