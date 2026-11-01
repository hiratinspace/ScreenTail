using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Tenancy;

/// <summary>
/// The one lock order for a tenant's rows (ST-010, review of #248): <b>the tenant's row first</b>, then
/// anything of the tenant's. Activation and offboarding both take it as the first statement of their
/// transaction, so the two never wait for each other in opposite orders.
/// </summary>
public static class TenantLock
{
    /// <summary>
    /// Locks the tenant's row until the current transaction ends, by writing it unchanged
    /// (<c>UPDATE tenants SET Seats = Seats</c>). Postgres holds a row it has updated locked against
    /// every other writer until the commit or rollback; SQLite holds the whole database for its one
    /// writer anyway.
    ///
    /// <b>Do not remove this as a no-op.</b> It changes no value, and it is what makes counting a
    /// tenant's seats race-safe on Postgres: without it two activations for the last seat both count a
    /// free one, and the tests on SQLite, where the transaction alone serialises, would not notice.
    /// <c>ActivationRaceTests</c> checks it runs inside the transaction before the seats are counted, and
    /// <c>PostgresActivationRaceTests</c> runs the race against a real Postgres.
    /// </summary>
    /// <returns>False when there is no such tenant (offboarded since it was read).</returns>
    public static async Task<bool> TakeAsync(ScreenTailContext db, Guid tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The tenant's row lock lasts only as long as a transaction; take it inside one.");
        }

        var rows = await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(set => set.SetProperty(t => t.Seats, t => t.Seats), ct).ConfigureAwait(false);
        return rows == 1;
    }
}
