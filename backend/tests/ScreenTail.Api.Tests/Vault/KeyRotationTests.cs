using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Vault;

/// <summary>
/// Rotating the master key (ST-009 AC3): every row wrapped under the previous key is rewrapped under the
/// current one, the secrets themselves are untouched, and a second run finds nothing to do. This is the
/// procedure docs/security/key-rotation.md describes, exercised.
/// </summary>
public sealed class KeyRotationTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly byte[] _old = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _new = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task EveryRowUnderTheOldKeyIsRewrappedAndStillOpens()
    {
        var tenant = Guid.NewGuid();
        await using (var db = await OpenAsync())
        {
            var before = new IntegrationVault(db, Options(current: _old), TimeProvider.System);
            await before.StoreAsync(tenant, "connectwise", "https://na", "cw-secret-0001", TestContext.Current.CancellationToken);
            await before.StoreAsync(tenant, "hudu", "https://h", "hudu-key-0002", TestContext.Current.CancellationToken);
        }

        int rotated;
        await using (var db = await OpenAsync())
        {
            rotated = await KeyRotation.RotateAsync(db, Options(current: _new, previous: _old), ct: TestContext.Current.CancellationToken);
        }

        await using (var db = await OpenAsync())
        {
            var after = new IntegrationVault(db, Options(current: _new), TimeProvider.System);
            Assert.Equal(2, rotated);
            Assert.Equal("cw-secret-0001", (await after.RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken))?.Secret);
            Assert.Equal("hudu-key-0002", (await after.RevealAsync(tenant, "hudu", TestContext.Current.CancellationToken))?.Secret);
            Assert.All(await db.Integrations.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), row => Assert.Equal(Envelope.KeyIdOf(_new), row.KeyId));
            Assert.All(await db.Integrations.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), row => Assert.NotNull(row.RotatedAt));

            // Nothing left under the old key: the second run is the check that the first one finished.
            Assert.Equal(0, await KeyRotation.RotateAsync(db, Options(current: _new, previous: _old), ct: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ARowAlreadyUnderTheCurrentKeyIsLeftAlone()
    {
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        var vault = new IntegrationVault(db, Options(current: _new), TimeProvider.System);
        await vault.StoreAsync(tenant, "hudu", "https://h", "hudu-key-0002", TestContext.Current.CancellationToken);
        var wrappedBefore = (await db.Integrations.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DataKeyWrapped;

        var rotated = await KeyRotation.RotateAsync(db, Options(current: _new, previous: _old), ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, rotated);
        Assert.Equal(wrappedBefore, (await db.Integrations.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DataKeyWrapped);
    }

    [Fact]
    public async Task WithoutAPreviousKeyThereIsNothingToRotateFrom()
    {
        await using var db = await OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => KeyRotation.RotateAsync(db, Options(current: _new), ct: TestContext.Current.CancellationToken));
    }

    private static VaultOptions Options(byte[] current, byte[]? previous = null) => new()
    {
        MasterKey = Convert.ToBase64String(current),
        PreviousMasterKey = previous is null ? string.Empty : Convert.ToBase64String(previous),
    };

    private async Task<ScreenTailContext> OpenAsync()
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        var db = new ScreenTailContext(new DbContextOptionsBuilder<ScreenTailContext>().UseSqlite(_connection).Options);
        _ = await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
