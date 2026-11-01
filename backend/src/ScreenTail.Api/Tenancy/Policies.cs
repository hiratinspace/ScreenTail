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

    /// <summary>The version's resolution: two rows in the same millisecond would share a version.</summary>
    private static DateTimeOffset ToMillisecond(DateTimeOffset at) =>
        new(at.UtcTicks - (at.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

    public static Task<Policy?> LatestAsync(ScreenTailContext db, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Policies.Where(p => p.TenantId == tenantId).OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Version).FirstOrDefaultAsync(ct);
    }

    public static Task<Policy> SetAsync(ScreenTailContext db, Guid tenantId, int retentionDays, bool localOnly, bool localOnlyLocked, bool captureAllWindows, TimeProvider time, CancellationToken ct = default) =>
        SetAsync(db, tenantId, retentionDays, localOnly, localOnlyLocked, captureAllWindows, new UpdateSettings(), time, ct);

    /// <summary>ST-113's update fields, as an admin sets them. The defaults are the client's own.</summary>
    /// <param name="Channel">"stable" or "pilot"; anything else is refused.</param>
    /// <param name="CheckHours">0 to 168; 0 turns update checks off.</param>
    public sealed record UpdateSettings(string Channel = "stable", bool ChannelLocked = false, int CheckHours = 24, bool UnderLocalOnly = false);

    public static async Task<Policy> SetAsync(ScreenTailContext db, Guid tenantId, int retentionDays, bool localOnly, bool localOnlyLocked, bool captureAllWindows, UpdateSettings updates, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(time);
        if (updates.Channel is not ("stable" or "pilot"))
        {
            throw new ArgumentException("The update channel is stable or pilot.", nameof(updates));
        }

        // A version is the time to the millisecond, and two saves from the web admin can land in the same
        // one (a double click). Each new row is at least a millisecond after the latest, so its version is
        // new and it sorts last (ST-099). Two saves that are truly concurrent both read the same latest
        // and pick the same version; the unique index lets one through, and the other goes again once
        // from the new latest. A second collision is a conflict the caller answers as one.
        for (var attempt = 1; ; attempt++)
        {
            var now = ToMillisecond(time.GetUtcNow());
            if (await LatestAsync(db, tenantId, ct).ConfigureAwait(false) is { } latest && now <= ToMillisecond(latest.CreatedAt))
            {
                now = ToMillisecond(latest.CreatedAt).AddMilliseconds(1);
            }

            var policy = new Policy
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Version = "p" + now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
                RetentionDays = Math.Clamp(retentionDays, 1, 365),
                LocalOnly = localOnly,
                LocalOnlyLocked = localOnlyLocked,
                CaptureAllWindows = captureAllWindows,
                UpdateChannel = updates.Channel,
                UpdateChannelLocked = updates.ChannelLocked,
                UpdateCheckHours = Math.Clamp(updates.CheckHours, 0, 168),
                UpdateChecksUnderLocalOnly = updates.UnderLocalOnly,
                CreatedAt = now,
            };
            db.Policies.Add(policy);
            try
            {
                _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return policy;
            }
            catch (DbUpdateException)
            {
                db.Entry(policy).State = EntityState.Detached;
                var taken = await db.Policies.AsNoTracking().AnyAsync(p => p.TenantId == tenantId && p.Version == policy.Version, ct).ConfigureAwait(false);
                if (!taken)
                {
                    // Not a version collision: the error it is.
                    throw;
                }

                if (attempt >= 2)
                {
                    throw new PolicyVersionConflictException();
                }
            }
        }
    }
}

/// <summary>Another save took this policy's version twice running. The caller says so and asks for another try.</summary>
public sealed class PolicyVersionConflictException : Exception
{
    public PolicyVersionConflictException()
        : base("Another policy change was saved at the same moment.")
    {
    }

    public PolicyVersionConflictException(string message)
        : base(message)
    {
    }

    public PolicyVersionConflictException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
