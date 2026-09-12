using System.Runtime.Versioning;
using ScreenTail.Core.Store;
using ScreenTail.Service.Store;

namespace ScreenTail.Tests.Windows.Store;

/// <summary>
/// ST-005 on Windows: the store key lives under DPAPI. A blob this user didn't protect (another user,
/// another machine, or a tampered file) is refused with a clear error rather than opening a store with
/// the wrong key. Full DifferentUserCannotDecrypt needs a second Windows account and stays a manual check.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs DPAPI (Windows)")]
    public void KeyIsCreatedOnceAndStable()
    {
        var provider = new DpapiKeyProvider(Path.Combine(_dir, "store.key"));

        var first = provider.GetKey();
        var second = provider.GetKey();

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, new byte[32]);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs DPAPI (Windows)")]
    public void KeyFileOnDiskIsNotTheKey()
    {
        var path = Path.Combine(_dir, "store.key");
        var key = new DpapiKeyProvider(path).GetKey();

        Assert.Equal(-1, File.ReadAllBytes(path).AsSpan().IndexOf(key));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs DPAPI (Windows)")]
    public void ForeignOrTamperedKeyFileIsRefused()
    {
        var path = Path.Combine(_dir, "store.key");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, new byte[64]); // not a DPAPI blob this user protected

        Assert.Throws<StoreKeyException>(() => new DpapiKeyProvider(path).GetKey());
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs DPAPI (Windows)")]
    public async Task StoreOpensWithTheDpapiKey()
    {
        var provider = new DpapiKeyProvider(Path.Combine(_dir, "store.key"));
        var dbPath = Path.Combine(_dir, "store.db");

        await using (var store = await SqliteSessionStore.OpenAsync(dbPath, provider, TestContext.Current.CancellationToken))
        {
            Assert.Equal(SqliteSessionStore.LatestSchemaVersion, await store.GetSchemaVersionAsync(TestContext.Current.CancellationToken));
        }

        await using var reopened = await SqliteSessionStore.OpenAsync(dbPath, provider, TestContext.Current.CancellationToken);
        Assert.Equal(SqliteSessionStore.LatestSchemaVersion, await reopened.GetSchemaVersionAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
