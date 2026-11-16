using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Tests.Tenancy;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Vault;

/// <summary>
/// The mapping record's pruning and per-device limit on a real Postgres (2026-11-15 review): both are a
/// correlated delete, and the current mappings beside who chose them, which SQLite and Npgsql translate on their own. Skips without
/// <see cref="PostgresApiFixture.Variable"/>; fails on CI without it.
/// </summary>
public sealed class PostgresIntegrationChangesTests(PostgresApiFixture api) : IClassFixture<PostgresApiFixture>
{
    [Fact]
    public async Task PruningByAgeKeepsTheLatestOnlyForMappedCompaniesOnPostgres()
    {
        api.RequirePostgres();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        IntegrationChange Old(string company, int daysAgo) =>
            new() { Id = Guid.NewGuid(), TenantId = tenantId, At = now.AddDays(-daysAgo), Provider = "hudu", Action = IntegrationChanges.Mapped, PsaCompany = company };
        var superseded = Old("Acme Dental", 300);
        var latest = Old("Acme Dental", 250);
        var only = Old("Borough Legal", 400);
        var unmapped = Old("Gone Co", 400);
        unmapped.Action = IntegrationChanges.Unmapped;
        _ = await api.UseAsync(db =>
        {
            db.IntegrationChanges.AddRange(superseded, latest, only, unmapped);
            db.CompanyMappings.Add(new CompanyMapping { Id = Guid.NewGuid(), TenantId = tenantId, PsaCompany = "Acme Dental", DocCompanyId = "7", DocCompanyName = "Acme Dental", Confidence = "manual", CreatedAt = now });
            db.CompanyMappings.Add(new CompanyMapping { Id = Guid.NewGuid(), TenantId = tenantId, PsaCompany = "Borough Legal", DocCompanyId = "7", DocCompanyName = "Borough Legal", Confidence = "manual", CreatedAt = now });
            return db.SaveChangesAsync();
        });

        var removed = await api.UseAsync(async db =>
        {
            var count = await IntegrationChanges.PruneAsync(db, tenantId, now, TestContext.Current.CancellationToken);
            _ = await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return count;
        });
        var left = await api.UseAsync(db => db.IntegrationChanges.Where(c => c.TenantId == tenantId).ToListAsync());

        Assert.Equal(2, removed);
        Assert.DoesNotContain(left, c => c.Id == superseded.Id);
        Assert.DoesNotContain(left, c => c.Id == unmapped.Id);
        Assert.Contains(left, c => c.Id == latest.Id);
        Assert.Contains(left, c => c.Id == only.Id);
        Assert.Equal(2, Assert.Single(left, c => c.Action == IntegrationChanges.MappingsPruned).Removed);
    }

    [Fact]
    public async Task PrunesAtOnceKeepOneSummaryRowAndLoseNoCountOnPostgres()
    {
        // Twenty first prunes at once: one row (the unique index), and every count added to it.
        api.RequirePostgres();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var start = new ManualResetEventSlim();
        var prunes = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            await api.UseAsync(async db =>
            {
                await IntegrationChanges.CountPrunedAsync(db, tenantId, 3, now, TestContext.Current.CancellationToken);
                return 0;
            });
        })).ToList();
        start.Set();
        await Task.WhenAll(prunes);

        var rows = await api.UseAsync(db => db.IntegrationChanges.Where(c => c.TenantId == tenantId).ToListAsync());

        Assert.Equal(60, Assert.Single(rows).Removed);
    }

    [Fact]
    public async Task CurrentMappingsBesideWhoChoseThemOnPostgres()
    {
        api.RequirePostgres();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var chosen = new IntegrationChange { Id = Guid.NewGuid(), TenantId = tenantId, At = now, Provider = "hudu", Action = IntegrationChanges.Mapped, PsaCompany = "Acme Dental", DocCompanyId = "7", DocCompanyName = "Acme Dental Group" };
        _ = await api.UseAsync(db =>
        {
            db.IntegrationChanges.Add(chosen);
            db.CompanyMappings.Add(new CompanyMapping { Id = Guid.NewGuid(), TenantId = tenantId, PsaCompany = "Acme Dental", DocCompanyId = "7", DocCompanyName = "Acme Dental Group", Confidence = "manual", CreatedAt = now });
            db.CompanyMappings.Add(new CompanyMapping { Id = Guid.NewGuid(), TenantId = tenantId, PsaCompany = "Borough Legal", DocCompanyId = "9", DocCompanyName = "Borough Legal", Confidence = "exact", CreatedAt = now });
            return db.SaveChangesAsync();
        });

        var current = await api.UseAsync(db => IntegrationChanges.CurrentMappingsAsync(db, tenantId, TestContext.Current.CancellationToken));

        Assert.Equal(["Acme Dental", "Borough Legal"], current.Select(m => m.Mapping.PsaCompany));
        Assert.Equal(chosen.Id, current[0].SetBy?.Id);
        Assert.Null(current[1].SetBy);
    }
}
