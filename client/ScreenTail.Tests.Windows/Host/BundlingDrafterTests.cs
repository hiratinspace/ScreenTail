using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Intel;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// ST-060 against the real encrypted store, which is the only place the claim can be tested honestly.
///
/// <see cref="BundleBuilderTests"/> proves the exclusions against a session built in memory. This proves
/// them against the store a session actually comes out of — the path where a frame nobody has redacted
/// would have to travel to reach a model provider.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class BundlingDrafterTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;

    [Fact]
    public async Task AStagedFrameNobodyRedactedNeverReachesTheBundle()
    {
        // The adversarial case ST-060 asks for by name. The store is told to stage a frame and never
        // redact it; the bundle must come back without it, and without having read its bytes.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await store.CreateSessionAsync(new NewSession("s1", At, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null), ct);
        await store.StageFrameAsync("s1", new StagedFrame("pending", 1_000, FrameTrigger.Click, 80, 60, null, new byte[] { 1, 2, 3 }), ct);

        var drafter = new BundlingDrafter(store, NullLogger.Instance);
        var outcome = await drafter.DraftAsync("s1", ct);

        Assert.False(outcome.Succeeded);
        Assert.Equal(BundlingDrafter.NoProviderReason, outcome.Reason);
        Assert.Equal(0, drafter.Last!.Chosen);
        Assert.Equal(0, drafter.Last.Considered);
    }

    [Fact]
    public async Task ASessionThatIsGoneIsReportedRatherThanThrowing()
    {
        // Retention can purge a session between finalize and drafting. A throw here would fault the
        // state machine mid-finalize and leave the session stuck.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);

        var outcome = await new BundlingDrafter(store, NullLogger.Instance).DraftAsync("missing", ct);

        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Reason);
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

    private async Task<SqliteSessionStore> OpenAsync(CancellationToken ct) =>
        _store ??= await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)), ct: ct);

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

}
