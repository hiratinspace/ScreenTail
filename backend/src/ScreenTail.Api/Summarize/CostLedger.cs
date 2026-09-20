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
public sealed class CostLedger(ScreenTailContext db, TimeProvider time) : ICostLedger
{
    public async Task<decimal> SpentTodayAsync(Guid tenantId, CancellationToken ct = default)
    {
        var since = time.GetUtcNow().UtcDateTime.Date;
        return await db.DraftCosts
            .Where(cost => cost.TenantId == tenantId && cost.At >= since)
            .SumAsync(cost => cost.CostUsd, ct)
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
