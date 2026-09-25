using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ScreenTail.Api.Data;

/// <summary>
/// The tenant database (ST-008).
///
/// <b>What is not here is the design.</b> INV-7 says the backend never persists a capture: there is no
/// table for a frame, a transcript, a note or an OCR string, and <see cref="SessionMetric"/> is shaped so
/// there is nowhere to put one by accident. A summarization request holds its bundle in memory for the
/// length of one call and nothing writes it anywhere.
///
/// Every table that belongs to a tenant carries <c>TenantId</c> and is indexed by it. Queries filter on
/// it explicitly rather than relying on a global filter, because a global query filter is silent when
/// somebody writes a raw SQL query around it, and returning another MSP's data is the worst thing this
/// service could do.
/// </summary>
public sealed class ScreenTailContext(DbContextOptions<ScreenTailContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<Integration> Integrations => Set<Integration>();

    public DbSet<CompanyMapping> CompanyMappings => Set<CompanyMapping>();

    public DbSet<Invite> Invites => Set<Invite>();

    public DbSet<Policy> Policies => Set<Policy>();

    public DbSet<SessionMetric> SessionMetrics => Set<SessionMetric>();

    public DbSet<DraftCost> DraftCosts => Set<DraftCost>();

    /// <summary>
    /// Makes <see cref="DateTimeOffset"/> columns comparable on SQLite, which the tests run on.
    ///
    /// SQLite has no date type and EF cannot translate a comparison between two of these, so
    /// <c>WHERE at >= @since</c> throws at query time rather than at compile time. Postgres has
    /// <c>timestamptz</c> and is unaffected — which is exactly why nobody noticed. The daily cost cap is
    /// built on such a comparison, its only test used a fake ledger, and CI's Postgres step applies
    /// migrations without running a query. So the one query in this service that decides whether money
    /// may be spent had never been executed anywhere, and it did not work (2026-09-19 review).
    ///
    /// Stored as UTC ticks, which sort in the same order as the instants they stand for. Everything here
    /// is written as UTC, so the offset that round-tripping drops was always zero.
    ///
    /// The provider is named rather than asked, so this project does not take a dependency on the SQLite
    /// one to describe a test's storage.
    /// </summary>
    private void StoreTimesComparablyOnSqlite(ModelBuilder modelBuilder)
    {
        if (!string.Equals(Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            return;
        }

        var ticks = new ValueConverter<DateTimeOffset, long>(
            at => at.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

        var nullableTicks = new ValueConverter<DateTimeOffset?, long?>(
            at => at == null ? null : at.Value.UtcTicks,
            ticks => ticks == null ? null : new DateTimeOffset(ticks.Value, TimeSpan.Zero));

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset))
            {
                property.SetValueConverter(ticks);
            }
            else if (property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(nullableTicks);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        StoreTimesComparablyOnSqlite(modelBuilder);

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants");
            tenant.HasKey(t => t.Id);
            tenant.HasIndex(t => t.Name);
        });

        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);

            // One address per tenant, not per system: the same person may work for two MSPs.
            user.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            user.HasOne(u => u.Tenant).WithMany(t => t.Users).HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Device>(device =>
        {
            device.ToTable("devices");
            device.HasKey(d => d.Id);
            device.HasIndex(d => d.TenantId);

            // The lookup every authenticated request makes. Unique because two devices sharing a token
            // hash would mean a collision or a copied token, and both should fail loudly.
            device.HasIndex(d => d.TokenHash).IsUnique();
            device.HasOne(d => d.User).WithMany(u => u.Devices).HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Integration>(integration =>
        {
            integration.ToTable("integrations");
            integration.HasKey(i => i.Id);
            integration.HasIndex(i => new { i.TenantId, i.Provider }).IsUnique();
        });

        modelBuilder.Entity<Invite>(invite =>
        {
            invite.ToTable("invites");
            invite.HasKey(i => i.Id);
            invite.HasIndex(i => i.TenantId);

            // The lookup activation makes. Unique for the same reason a device token's hash is.
            invite.HasIndex(i => i.CodeHash).IsUnique();
        });

        modelBuilder.Entity<CompanyMapping>(mapping =>
        {
            mapping.ToTable("company_mappings");
            mapping.HasKey(m => m.Id);

            // One answer per PSA name per tenant: the lookup before every article, and the row a person
            // edits in Settings.
            mapping.HasIndex(m => new { m.TenantId, m.PsaCompany }).IsUnique();
        });

        modelBuilder.Entity<Policy>(policy =>
        {
            policy.ToTable("policies");
            policy.HasKey(p => p.Id);
            policy.HasIndex(p => new { p.TenantId, p.Version }).IsUnique();
            policy.HasOne(p => p.Tenant).WithMany(t => t.Policies).HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DraftCost>(cost =>
        {
            cost.ToTable("draft_costs");
            cost.HasKey(c => c.Id);

            // The daily cap's query: everything one tenant spent since midnight.
            cost.HasIndex(c => new { c.TenantId, c.At });
            cost.Property(c => c.CostUsd).HasPrecision(12, 6);
        });

        modelBuilder.Entity<SessionMetric>(metric =>
        {
            metric.ToTable("session_metrics");
            metric.HasKey(m => m.Id);

            // One row per session per tenant. A client that retries a metrics push must not double-count
            // the pilot's headline number.
            metric.HasIndex(m => new { m.TenantId, m.SessionId }).IsUnique();
            metric.HasIndex(m => new { m.TenantId, m.StartedAt });
        });
    }
}
