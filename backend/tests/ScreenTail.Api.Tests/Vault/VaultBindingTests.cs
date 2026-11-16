using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using ScreenTail.Api.Data;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Vault;

/// <summary>
/// Each sealed secret is bound to its row (2026-11-15 review): the tenant, the provider and the site's
/// host go in as associated data, and the row records that it is bound. A bound row copied onto another
/// tenant, provider or host is refused and never tried unbound; a row sealed before the binding opens
/// once and is stored bound from then on.
/// </summary>
public sealed class VaultBindingTests : IAsyncDisposable
{
    private const string Secret = "acme+PUB:PRIV-0001";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task ANewCredentialIsStoredBound()
    {
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await Vault(db).StoreAsync(tenant, "connectwise", "https://na.myconnectwise.net", Secret, TestContext.Current.CancellationToken);

        var row = await db.Integrations.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Envelope.BindingVersion, row.BindingVersion);
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(SealedOf(row), _key));
        Assert.Equal(Secret, Encoding.UTF8.GetString(Envelope.Open(SealedOf(row), _key, Envelope.BindingFor(tenant, "connectwise", "https://na.myconnectwise.net"))));
    }

    [Fact]
    public async Task ABoundRowCopiedToAnotherTenantIsRefused()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await using var db = await OpenAsync();
        var vault = Vault(db);
        await vault.StoreAsync(mine, "connectwise", "https://na.myconnectwise.net", Secret, TestContext.Current.CancellationToken);
        await vault.StoreAsync(theirs, "connectwise", "https://na.myconnectwise.net", "their+PUB:PRIV-9999", TestContext.Current.CancellationToken);

        await CopySealedAsync(db, from: mine, to: theirs, "connectwise");

        var refused = await Assert.ThrowsAsync<VaultRowRefusedException>(() => Vault(db).RevealAsync(theirs, "connectwise", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Secret, refused.Message, StringComparison.Ordinal);
        Assert.Equal(Secret, (await Vault(db).RevealAsync(mine, "connectwise", TestContext.Current.CancellationToken))?.Secret);
    }

    [Fact]
    public async Task ABoundRowCopiedToAnotherProviderIsRefused()
    {
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        var vault = Vault(db);
        await vault.StoreAsync(tenant, "connectwise", "https://shared.example.com", Secret, TestContext.Current.CancellationToken);
        await vault.StoreAsync(tenant, "hudu", "https://shared.example.com", "hudu-key-0002", TestContext.Current.CancellationToken);

        var cw = await db.Integrations.AsNoTracking().SingleAsync(i => i.Provider == "connectwise", TestContext.Current.CancellationToken);
        _ = await db.Integrations.Where(i => i.Provider == "hudu").ExecuteUpdateAsync(
            set => set.SetProperty(i => i.SecretCiphertext, cw.SecretCiphertext).SetProperty(i => i.SecretNonce, cw.SecretNonce).SetProperty(i => i.DataKeyWrapped, cw.DataKeyWrapped).SetProperty(i => i.DataKeyNonce, cw.DataKeyNonce),
            TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsAsync<VaultRowRefusedException>(() => Vault(db).RevealAsync(tenant, "hudu", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABoundRowPointedAtAnotherHostIsRefused()
    {
        // The site is where the credential is sent. Changed by a database writer, the row stops opening.
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await Vault(db).StoreAsync(tenant, "connectwise", "https://na.myconnectwise.net", Secret, TestContext.Current.CancellationToken);

        _ = await db.Integrations.ExecuteUpdateAsync(set => set.SetProperty(i => i.SiteUrl, "https://elsewhere.example.com"), TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsAsync<VaultRowRefusedException>(() => Vault(db).RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABoundRowMarkedLegacyByADatabaseWriterStillDoesNotOpen()
    {
        // The marker is a column, so it can be flipped; the tag still covers the binding it was sealed
        // with, so the unbound open fails too.
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await Vault(db).StoreAsync(tenant, "connectwise", "https://na.myconnectwise.net", Secret, TestContext.Current.CancellationToken);

        _ = await db.Integrations.ExecuteUpdateAsync(set => set.SetProperty(i => i.BindingVersion, 0), TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsAnyAsync<CryptographicException>(() => Vault(db).RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSameHostSpelledAnotherWayStillOpens()
    {
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await Vault(db).StoreAsync(tenant, "hudu", "https://acme.huducloud.com", "hudu-key-0002", TestContext.Current.CancellationToken);

        _ = await db.Integrations.ExecuteUpdateAsync(set => set.SetProperty(i => i.SiteUrl, "https://ACME.huducloud.com/"), TestContext.Current.CancellationToken);

        Assert.Equal("hudu-key-0002", (await Vault(db).RevealAsync(tenant, "hudu", TestContext.Current.CancellationToken))?.Secret);
    }

    [Fact]
    public async Task ALegacyRowOpensOnceAndIsStoredBoundAfterwards()
    {
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await SeedLegacyAsync(db, tenant, "connectwise", "https://na.myconnectwise.net", Secret, _key);
        var before = await db.Integrations.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        var revealed = await Vault(db).RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken);
        var after = await db.Integrations.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Secret, revealed?.Secret);
        Assert.Equal(0, before.BindingVersion);
        Assert.Equal(Envelope.BindingVersion, after.BindingVersion);
        Assert.NotEqual(before.SecretCiphertext, after.SecretCiphertext);
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(SealedOf(after), _key));
        Assert.Equal(Secret, (await Vault(db).RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken))?.Secret);
    }

    [Fact]
    public async Task AnUpgradeDoesNotOverwriteACredentialStoredInTheMeantime()
    {
        // Two readers open the same legacy row; between the read and the upgrade the owner stores a new
        // credential. The upgrade finds the row changed and writes nothing.
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await SeedLegacyAsync(db, tenant, "connectwise", "https://na.myconnectwise.net", Secret, _key);
        var stale = await db.Integrations.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        await Vault(db).StoreAsync(tenant, "connectwise", "https://na.myconnectwise.net", "acme+PUB:NEW-0002", TestContext.Current.CancellationToken);

        var wrote = await IntegrationVault.UpgradeAsync(db, stale, Encoding.UTF8.GetBytes(Secret), _key, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.False(wrote);
        Assert.Equal("acme+PUB:NEW-0002", (await Vault(db).RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken))?.Secret);
    }

    [Fact]
    public async Task WithLegacyRowsOffALegacyRowIsRefused()
    {
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await SeedLegacyAsync(db, tenant, "connectwise", "https://na.myconnectwise.net", Secret, _key);

        var refused = await Assert.ThrowsAsync<VaultRowRefusedException>(() => Vault(db, allowLegacy: false).RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken));

        Assert.Contains("AllowLegacyRows", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, (await db.Integrations.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).BindingVersion);
    }

    [Fact]
    public async Task UpgradingEveryLegacyRowNeedsNoRotationAndASecondRunFindsNothing()
    {
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await SeedLegacyAsync(db, tenant, "connectwise", "https://na.myconnectwise.net", Secret, _key);
        await SeedLegacyAsync(db, tenant, "hudu", "https://acme.huducloud.com", "hudu-key-0002", _key);
        await Vault(db).StoreAsync(Guid.NewGuid(), "hudu", "https://other.huducloud.com", "hudu-key-0003", TestContext.Current.CancellationToken);
        var options = new VaultOptions { MasterKey = Convert.ToBase64String(_key) };

        var upgraded = await KeyRotation.UpgradeLegacyAsync(db, options, ct: TestContext.Current.CancellationToken);
        var again = await KeyRotation.UpgradeLegacyAsync(db, options, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, upgraded.Changed);
        Assert.Equal(0, again.Changed);
        Assert.All(await db.Integrations.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken), row => Assert.Equal(Envelope.BindingVersion, row.BindingVersion));
        Assert.Equal(Secret, (await Vault(db, allowLegacy: false).RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken))?.Secret);
        Assert.Equal("hudu-key-0002", (await Vault(db, allowLegacy: false).RevealAsync(tenant, "hudu", TestContext.Current.CancellationToken))?.Secret);
    }

    [Fact]
    public async Task TheFirstSweepUpgradesEveryLegacyRowAndALaterOneIsOnlyWarnedAbout()
    {
        // Legacy rows exist only from before the binding. The first sweep upgrades them all and records
        // that none is left; a row marked legacy after that was written since, so the next sweep leaves it
        // as it is and names it by row and tenant id, and a read refuses it.
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await SeedLegacyAsync(db, tenant, "connectwise", "https://na.myconnectwise.net", Secret, _key);
        var options = new VaultOptions { MasterKey = Convert.ToBase64String(_key) };
        var log = new RecordingLogger();

        var first = await VaultSweep.SweepAsync(db, options, TimeProvider.System, log, TestContext.Current.CancellationToken);
        var second = await VaultSweep.SweepAsync(db, options, TimeProvider.System, log, TestContext.Current.CancellationToken);
        var other = Guid.NewGuid();
        await SeedLegacyAsync(db, other, "hudu", "https://acme.huducloud.com", "hudu-key-0002", _key);
        var planted = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == other, TestContext.Current.CancellationToken);
        var third = await VaultSweep.SweepAsync(db, options, TimeProvider.System, log, TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Upgraded);
        Assert.Equal(0, second.Upgraded);
        Assert.Empty(second.Suspect);
        Assert.NotNull((await db.VaultStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).LegacyRowsClearedAt);
        Assert.Equal([planted.Id], third.Suspect);
        Assert.Equal(0, third.Upgraded);
        Assert.Equal(planted.SecretCiphertext, (await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == other, TestContext.Current.CancellationToken)).SecretCiphertext);
        var warning = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning);
        Assert.Contains(planted.Id.ToString(), warning.Text, StringComparison.Ordinal);
        Assert.Contains(other.ToString(), warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("acme", warning.Text, StringComparison.OrdinalIgnoreCase);
        _ = await Assert.ThrowsAsync<VaultRowRefusedException>(() => Vault(db).RevealAsync(other, "hudu", TestContext.Current.CancellationToken));
        Assert.Equal(Secret, (await Vault(db).RevealAsync(tenant, "connectwise", TestContext.Current.CancellationToken))?.Secret);
    }

    [Fact]
    public async Task WithLegacyRowsOffTheSweepRefusesAndLeavesTheRow()
    {
        var tenant = Guid.NewGuid();
        await using var db = await OpenAsync();
        await SeedLegacyAsync(db, tenant, "connectwise", "https://na.myconnectwise.net", Secret, _key);
        var options = new VaultOptions { MasterKey = Convert.ToBase64String(_key), AllowLegacyRows = false };

        var swept = await VaultSweep.SweepAsync(db, options, TimeProvider.System, new RecordingLogger(), TestContext.Current.CancellationToken);

        Assert.Single(swept.Refused);
        Assert.Equal(0, swept.Upgraded);
        Assert.Equal(0, (await db.Integrations.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).BindingVersion);
        Assert.False(await db.VaultStates.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListingNeverReadsTheSealedColumns()
    {
        // The list shows the last four and the dates. The ciphertext, nonces and wrapped key stay in the
        // database: the query does not name them.
        var commands = new SeenCommands();
        await using var db = await OpenAsync(commands);
        await Vault(db).StoreAsync(Guid.NewGuid(), "hudu", "https://acme.huducloud.com", "hudu-key-0002", TestContext.Current.CancellationToken);
        commands.Text.Clear();

        var listed = await Vault(db).ListAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Empty(listed);
        var select = Assert.Single(commands.Text);
        foreach (var column in new[] { "SecretCiphertext", "SecretNonce", "DataKeyWrapped", "DataKeyNonce" })
        {
            Assert.DoesNotContain(column, select, StringComparison.Ordinal);
        }
    }

    internal static async Task SeedLegacyAsync(ScreenTailContext db, Guid tenant, string provider, string site, string secret, byte[] key)
    {
        // Exactly what the vault wrote before the binding: no associated data, BindingVersion 0.
        var sealed_ = Envelope.Seal(Encoding.UTF8.GetBytes(secret), key);
        db.Integrations.Add(new Integration
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Provider = provider,
            SiteUrl = site,
            SecretHint = secret[^4..],
            SecretCiphertext = sealed_.Ciphertext,
            SecretNonce = sealed_.Nonce,
            DataKeyWrapped = sealed_.WrappedKey,
            DataKeyNonce = sealed_.KeyNonce,
            KeyId = sealed_.KeyId,
            BindingVersion = 0,
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        _ = await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
    }

    private static Sealed SealedOf(Integration row) => new(row.SecretCiphertext!, row.SecretNonce!, row.DataKeyWrapped!, row.DataKeyNonce!, row.KeyId!);

    private static async Task CopySealedAsync(ScreenTailContext db, Guid from, Guid to, string provider)
    {
        var source = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == from && i.Provider == provider, TestContext.Current.CancellationToken);
        _ = await db.Integrations.Where(i => i.TenantId == to && i.Provider == provider).ExecuteUpdateAsync(
            set => set.SetProperty(i => i.SecretCiphertext, source.SecretCiphertext).SetProperty(i => i.SecretNonce, source.SecretNonce).SetProperty(i => i.DataKeyWrapped, source.DataKeyWrapped).SetProperty(i => i.DataKeyNonce, source.DataKeyNonce),
            TestContext.Current.CancellationToken);
    }

    private IntegrationVault Vault(ScreenTailContext db, bool allowLegacy = true) =>
        new(db, new VaultOptions { MasterKey = Convert.ToBase64String(_key), AllowLegacyRows = allowLegacy }, TimeProvider.System);

    private async Task<ScreenTailContext> OpenAsync(SeenCommands? commands = null)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        var options = new DbContextOptionsBuilder<ScreenTailContext>().UseSqlite(_connection);
        if (commands is not null)
        {
            _ = options.AddInterceptors(commands);
        }

        var db = new ScreenTailContext(options.Options);
        _ = await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>What the sweep logged, formatted.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Every SQL text the context runs, so a test can see which columns a query names.</summary>
    private sealed class SeenCommands : DbCommandInterceptor
    {
        public List<string> Text { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Text.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
