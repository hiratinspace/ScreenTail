using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Summarize;

/// <summary>
/// What each tenant has spent on drafting today (ST-063).
///
/// Summed from rows rather than held in a counter: a counter resets when the service restarts, and a cap
/// that a restart clears is a cap a crash loop walks straight through.
///
/// The day is UTC. A tenant's own midnight would be friendlier and needs a timezone this service does not
/// have yet; the cap is a budget rather than a billing period, so the difference costs nobody anything.
/// </summary>
public sealed class CostLedger(ScreenTailContext db, TimeProvider time, SummarizationOptions options) : ICostLedger
{
    /// <summary>
    /// What this tenant may spend today.
    ///
    /// One figure for the deployment for now. A per-tenant override belongs here rather than at the call
    /// site, because the cap has to be applied in the same breath as the row that claims against it.
    /// </summary>
    private decimal Cap(Guid tenantId)
    {
        _ = tenantId;
        return options.DailyCostCapUsd;
    }

    public async Task<decimal> SpentTodayAsync(Guid tenantId, CancellationToken ct = default)
    {
        // A DateTimeOffset, not a DateTime. Comparing the two is an implicit conversion no provider can
        // translate, so this query threw wherever it ran — which was nowhere, because until the
        // 2026-09-19 review the real ledger had no test and every caller used a fake.
        var since = new DateTimeOffset(time.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        return await db.DraftCosts
            .Where(cost => cost.TenantId == tenantId && cost.At >= since)
            .SumAsync(cost => cost.CostUsd, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the claim, then looks. Whoever pushed the total over the cap takes their own row back.
    ///
    /// Insert-then-check rather than a transaction with serializable isolation, which on Postgres means
    /// retrying serialization failures under exactly the load this exists for. Two requests that arrive
    /// together may both stand down where one could have gone ahead; a tenant losing one draft it could
    /// have afforded is the right way round from spending a hundred it could not.
    ///
    /// What it cannot do is survive the process dying between here and <see cref="SettleAsync"/>: the
    /// claim stands at the full estimate until UTC midnight. That is the conservative direction, and the
    /// alternative is a lease with an expiry, which is a background job to clean up after.
    /// </summary>
    public async Task<Guid?> ReserveAsync(Guid tenantId, string sessionId, string provider, decimal estimateUsd, CancellationToken ct = default)
    {
        var reservation = new DraftCost
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SessionId = sessionId,
            Provider = provider,
            CostUsd = estimateUsd,
            At = time.GetUtcNow(),
        };

        db.DraftCosts.Add(reservation);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var spent = await SpentTodayAsync(tenantId, ct).ConfigureAwait(false);
        if (spent - estimateUsd < Cap(tenantId))
        {
            return reservation.Id;
        }

        db.DraftCosts.Remove(reservation);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return null;
    }

    public async Task SettleAsync(Guid reservationId, decimal costUsd, CancellationToken ct = default)
    {
        // A settle of zero leaves a row saying a session cost nothing, which is true and is worth
        // keeping: it is the difference between a provider outage and a session nobody ran.
        _ = await db.DraftCosts
            .Where(cost => cost.Id == reservationId)
            .ExecuteUpdateAsync(set => set.SetProperty(cost => cost.CostUsd, costUsd), ct)
            .ConfigureAwait(false);
    }

    public async Task RecordAsync(Guid tenantId, string sessionId, string provider, decimal costUsd, CancellationToken ct = default)
    {
        db.DraftCosts.Add(new DraftCost
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SessionId = sessionId,
            Provider = provider,
            CostUsd = costUsd,
            At = time.GetUtcNow(),
        });

        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
