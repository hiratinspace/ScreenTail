using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Tenancy;

/// <summary>
/// The tenant's policy rows (ST-047): every change is a new row with a new version, so a session's
/// audit log can name the policy it ran under and an admin can see what changed when. The latest row
/// is the policy; none means the defaults.
/// </summary>
public static class Policies
{
    public const string DefaultVersion = "default";

    public static Task<Policy?> LatestAsync(ScreenTailContext db, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Policies.Where(p => p.TenantId == tenantId).OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Version).FirstOrDefaultAsync(ct);
    }

    public static async Task<Policy> SetAsync(ScreenTailContext db, Guid tenantId, int retentionDays, bool localOnly, bool localOnlyLocked, bool captureAllWindows, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow();
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Version = "p" + now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
            RetentionDays = Math.Clamp(retentionDays, 1, 365),
            LocalOnly = localOnly,
            LocalOnlyLocked = localOnlyLocked,
            CaptureAllWindows = captureAllWindows,
            CreatedAt = now,
        };
        db.Policies.Add(policy);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return policy;
    }
}
