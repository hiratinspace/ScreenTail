using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Tenancy;

/// <param name="Rows">How many rows went, across every table, so the receipt can say so.</param>
public sealed record Offboarded(Guid TenantId, int Rows);

/// <summary>
/// Tenant offboarding (ST-010 AC4): every row of the tenant, in every table, in one transaction — the
/// credentials in the vault included, since a sealed credential with no tenant is a credential nobody
/// can rotate. Nothing is kept; a tenant that comes back starts again. The receipt email waits for a mail
/// provider; the CLI prints the count instead.
/// </summary>
public static class Offboarding
{
    public static async Task<Offboarded> DeleteTenantAsync(ScreenTailContext db, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var rows = 0;
        rows += await db.DraftCosts.Where(c => c.TenantId == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        rows += await db.SessionMetrics.Where(m => m.TenantId == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        rows += await db.Policies.Where(p => p.TenantId == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        rows += await db.CompanyMappings.Where(m => m.TenantId == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        rows += await db.Integrations.Where(i => i.TenantId == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        rows += await db.Invites.Where(i => i.TenantId == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        rows += await db.Devices.Where(d => d.TenantId == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        rows += await db.Users.Where(u => u.TenantId == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        rows += await db.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new Offboarded(tenantId, rows);
    }
}
