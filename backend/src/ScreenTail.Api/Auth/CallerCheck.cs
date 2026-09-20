using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Auth;

/// <param name="TenantId">Who this caller belongs to. Every query in the request is scoped to it.</param>
/// <param name="Device">The device row, already loaded, so a caller that needs it does not read twice.</param>
/// <param name="User">
/// The technician. Carried rather than reached through <paramref name="Device"/> because this type is
/// the proof that they exist and are not disabled, and a caller should not have to check again to
/// satisfy the compiler.
/// </param>
/// <param name="Tenant">The MSP, likewise already proved to exist and not be disabled.</param>
public sealed record Caller(Guid TenantId, Guid UserId, Guid DeviceId, Device Device, User User, Tenant Tenant);

/// <summary>
/// Whether the holder of a valid token is still allowed to do anything (ST-008).
///
/// A signature is not permission. The token is good for its whole lifetime, so a device that was revoked
/// an hour ago still presents a perfectly valid one, and the only thing that can say otherwise is the
/// database.
///
/// <b>It lives here because it used to live in one endpoint.</b> Until the 2026-09-19 review the check
/// existed only in <c>/v1/me</c>: a revoked laptop got a 401 from that and went on drafting at
/// <c>/v1/sessions/summarize</c>, spending the tenant's budget, until its token expired. Both the README
/// and the model's own comments claimed revocation was checked on every request. One implementation, so
/// the next endpoint gets it by calling this rather than by remembering it.
/// </summary>
public static class CallerCheck
{
    /// <summary>
    /// The caller, or null when they may not act: no claims, an unknown or revoked device, a disabled
    /// user, or a disabled tenant.
    ///
    /// Null rather than a reason. Which of those it was is exactly what an attacker holding a stolen
    /// token would like to know, and a technician cannot act on the difference (INV-10 in spirit).
    /// </summary>
    public static async Task<Caller?> ReadAsync(
        ClaimsPrincipal principal,
        ScreenTailContext db,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(db);

        if (!Guid.TryParse(principal.FindFirstValue(ScreenTailClaims.TenantId), out var tenantId)
            || !Guid.TryParse(principal.FindFirstValue(ScreenTailClaims.UserId), out var userId)
            || !Guid.TryParse(principal.FindFirstValue(ScreenTailClaims.DeviceId), out var deviceId))
        {
            // A token that passed every signature check and still does not say who it is for.
            return null;
        }

        var device = await db.Devices
            .AsNoTracking()
            .Include(d => d.User)
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.TenantId == tenantId && d.UserId == userId, ct)
            .ConfigureAwait(false);

        if (device is null || device.RevokedAt is not null || device.User is null || device.User.DisabledAt is not null)
        {
            return null;
        }

        // The tenant as well as the device. An MSP that stopped paying, or was disabled for cause, stops
        // spending our provider budget at the same moment.
        var tenant = await db.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == tenantId && t.DisabledAt == null, ct)
            .ConfigureAwait(false);

        return tenant is null ? null : new Caller(tenantId, userId, deviceId, device, device.User, tenant);
    }
}
