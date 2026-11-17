using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ScreenTrail.Api.Data;
using ScreenTrail.Api.Vault;

namespace ScreenTrail.Api.Tests.Vault;

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

        VaultRewrite rotated;
        await using (var db = await OpenAsync())
        {
            rotated = await KeyRotation.RotateAsync(db, Options(current: _new, previous: _old), ct: TestContext.Current.CancellationToken);
        }

        await using (var db = await OpenAsync())
        {
            var after = new IntegrationVault(db, Options(current: _new), TimeProvider.System);
            Assert.Equal(2, rotated.Changed);
            Assert.Equal("cw-secret-0001", (await after.RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken))?.Secret);
            Assert.Equal("hudu-key-0002", (await after.RevealAsync(tenant, "hudu", TestContext.Current.CancellationToken))?.Secret);
            Assert.All(await db.Integrations.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), row => Assert.Equal(Envelope.KeyIdOf(_new), row.KeyId));
            Assert.All(await db.Integrations.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), row => Assert.NotNull(row.RotatedAt));

            // Nothing left under the old key: the second run is the check that the first one finished.
            Assert.Equal(0, (await KeyRotation.RotateAsync(db, Options(current: _new, previous: _old), ct: TestContext.Current.CancellationToken)).Changed);
        }
    }

    [Fact]
    public async Task ARowUnderThePreviousKeyStillOpensBeforeTheRewrap()
    {
        // 2026-10-12 review, T-190. Step 3 of docs/security/key-rotation.md: restarted with both keys and
        // not yet rewrapped, a row under the previous key opens as before, and a credential stored now
        // goes under the new one. Nothing is unreadable at any point.
        var tenant = Guid.NewGuid();
        await using (var db = await OpenAsync())
        {
            var before = new IntegrationVault(db, Options(current: _old), TimeProvider.System);
            await before.StoreAsync(tenant, "connectwise", "https://na", "cw-secret-0001", TestContext.Current.CancellationToken);
        }

        await using (var db = await OpenAsync())
        {
            var during = new IntegrationVault(db, Options(current: _new, previous: _old), TimeProvider.System);
            Assert.Equal("cw-secret-0001", (await during.RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken))?.Secret);

            await during.StoreAsync(tenant, "hudu", "https://h", "hudu-key-0002", TestContext.Current.CancellationToken);
            Assert.Equal("hudu-key-0002", (await during.RevealAsync(tenant, "hudu", TestContext.Current.CancellationToken))?.Secret);

            var keys = await db.Integrations.AsNoTracking().OrderBy(i => i.Provider).Select(i => i.KeyId).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal([Envelope.KeyIdOf(_old), Envelope.KeyIdOf(_new)], keys);
        }
    }

    [Fact]
    public async Task ARowUnderAKeyThisDeploymentDoesNotHoldSaysWhichKey()
    {
        // Step 5 run before step 4 finished: the previous key is gone and a row still needs it. The vault
        // names the key id rather than handing a provider garbage.
        var tenant = Guid.NewGuid();
        await using (var db = await OpenAsync())
        {
            await new IntegrationVault(db, Options(current: _old), TimeProvider.System).StoreAsync(tenant, "connectwise", "https://na", "cw-secret-0001", TestContext.Current.CancellationToken);
        }

        await using (var db = await OpenAsync())
        {
            var dropped = new IntegrationVault(db, Options(current: _new), TimeProvider.System);
            var thrown = await Assert.ThrowsAsync<VaultRowRefusedException>(() => dropped.RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken));
            Assert.Contains(Envelope.KeyIdOf(_old), thrown.Message, StringComparison.Ordinal);
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

        Assert.Equal(0, rotated.Changed);
        Assert.Equal(wrappedBefore, (await db.Integrations.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DataKeyWrapped);
    }

    [Fact]
    public async Task RotationSealsLegacyRowsAgainInTheBoundForm()
    {
        // 2026-11-15 review. Rows stored before secrets were bound to their row, under the previous key
        // and under the current one, come out of the rotation bound and under the current key, and open
        // with legacy rows refused. The second run finds nothing.
        var tenant = Guid.NewGuid();
        await using (var db = await OpenAsync())
        {
            await VaultBindingTests.SeedLegacyAsync(db, tenant, "connectwise", "https://na.myconnectwise.net", "cw-secret-0001", _old);
            await VaultBindingTests.SeedLegacyAsync(db, tenant, "hudu", "https://acme.huducloud.com", "hudu-key-0002", _new);
        }

        await using (var db = await OpenAsync())
        {
            Assert.Equal(2, (await KeyRotation.RotateAsync(db, Options(current: _new, previous: _old), ct: TestContext.Current.CancellationToken)).Changed);
        }

        await using (var db = await OpenAsync())
        {
            var after = new IntegrationVault(db, new VaultOptions { MasterKey = Convert.ToBase64String(_new), AllowLegacyRows = false }, TimeProvider.System);
            Assert.Equal("cw-secret-0001", (await after.RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken))?.Secret);
            Assert.Equal("hudu-key-0002", (await after.RevealAsync(tenant, "hudu", TestContext.Current.CancellationToken))?.Secret);
            Assert.All(await db.Integrations.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken), row =>
            {
                Assert.Equal(Envelope.BindingVersion, row.BindingVersion);
                Assert.Equal(Envelope.KeyIdOf(_new), row.KeyId);
            });
            Assert.Equal(0, (await KeyRotation.RotateAsync(db, Options(current: _new, previous: _old), ct: TestContext.Current.CancellationToken)).Changed);
        }
    }

    [Fact]
    public async Task WithLegacyRowsOffTheRotationLeavesALegacyRowUntouchedAndSaysWhich()
    {
        // A row marked legacy once legacy rows are off is not turned into a bound one by the rotation:
        // it is reported by id and left byte for byte, while the bound rows still rotate.
        var tenant = Guid.NewGuid();
        Guid legacyId;
        byte[]? before;
        await using (var db = await OpenAsync())
        {
            await VaultBindingTests.SeedLegacyAsync(db, tenant, "connectwise", "https://na.myconnectwise.net", "cw-secret-0001", _old);
            await new IntegrationVault(db, Options(current: _old), TimeProvider.System).StoreAsync(tenant, "hudu", "https://acme.huducloud.com", "hudu-key-0002", TestContext.Current.CancellationToken);
            var legacy = await db.Integrations.AsNoTracking().SingleAsync(i => i.Provider == "connectwise", TestContext.Current.CancellationToken);
            (legacyId, before) = (legacy.Id, legacy.SecretCiphertext);
        }

        VaultRewrite rotated;
        await using (var db = await OpenAsync())
        {
            rotated = await KeyRotation.RotateAsync(db, Options(current: _new, previous: _old, allowLegacy: false), ct: TestContext.Current.CancellationToken);
        }

        await using (var db = await OpenAsync())
        {
            var legacy = await db.Integrations.AsNoTracking().SingleAsync(i => i.Id == legacyId, TestContext.Current.CancellationToken);
            Assert.Equal(1, rotated.Changed);
            Assert.Equal([legacyId], rotated.RefusedLegacy);
            Assert.Equal(0, legacy.BindingVersion);
            Assert.Equal(before, legacy.SecretCiphertext);
            Assert.Equal(Envelope.KeyIdOf(_old), legacy.KeyId);
            Assert.Equal(Envelope.KeyIdOf(_new), (await db.Integrations.AsNoTracking().SingleAsync(i => i.Provider == "hudu", TestContext.Current.CancellationToken)).KeyId);
        }
    }

    [Fact]
    public async Task WithoutAPreviousKeyThereIsNothingToRotateFrom()
    {
        await using var db = await OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => KeyRotation.RotateAsync(db, Options(current: _new), ct: TestContext.Current.CancellationToken));
    }

    private static VaultOptions Options(byte[] current, byte[]? previous = null, bool allowLegacy = true) => new()
    {
        MasterKey = Convert.ToBase64String(current),
        PreviousMasterKey = previous is null ? string.Empty : Convert.ToBase64String(previous),
        AllowLegacyRows = allowLegacy,
    };

    private async Task<ScreenTrailContext> OpenAsync()
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        var db = new ScreenTrailContext(new DbContextOptionsBuilder<ScreenTrailContext>().UseSqlite(_connection).Options);
        _ = await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
