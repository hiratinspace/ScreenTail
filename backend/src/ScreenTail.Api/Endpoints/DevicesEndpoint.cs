using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Endpoints;

/// <param name="Code">From the invite, as typed: dashes, spaces and case are forgiven.</param>
/// <param name="DeviceName">The machine's name, for the admin's device list. Never a user's name.</param>
public sealed record ActivateDeviceRequest(string Code, string DeviceName);

/// <param name="RefreshToken">Long-lived, shown once, kept by the device and never by us in the clear.</param>
public sealed record ActivatedDevice(string TenantName, Guid DeviceId, string RefreshToken, string AccessToken, DateTimeOffset ExpiresAt);

public sealed record RefreshRequest(string RefreshToken);

public sealed record AccessTokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// The two calls a device makes before it has an access token (ST-010): activation with an invite's
/// code (Spec §5 S8 step 2), and the exchange of its refresh token for an access token, an hour at a
/// time. Anonymous by nature, so each says as little as it can: a code that is wrong, used or expired
/// is a 400 with a sentence, a token that is wrong or revoked is a 401 with none.
/// </summary>
public static class DevicesEndpoint
{
    public const string SeatLimitMessage = "Seat limit reached — contact your admin";

    public static RouteGroupBuilder MapDevices(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/activate", async (ActivateDeviceRequest request, ScreenTailContext db, TokenIssuer issuer, TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 200)
            {
                return Results.BadRequest(new { error = "bad_device_name", message = "The device needs a name of up to 200 characters." });
            }

            var now = time.GetUtcNow();
            var invite = await db.Invites.SingleOrDefaultAsync(i => i.CodeHash == Invites.Hash(request.Code), ct).ConfigureAwait(false);
            if (invite is null)
            {
                return Results.BadRequest(new { error = "invite_unknown", message = "That code is not one we issued. Check it against the invite." });
            }

            if (invite.AcceptedAt is not null)
            {
                return Results.BadRequest(new { error = "invite_used", message = "That code has already been used. Ask your admin for a new invite." });
            }

            if (invite.ExpiresAt <= now)
            {
                return Results.BadRequest(new { error = "invite_expired", message = "That code has expired. Ask your admin for a new invite." });
            }

            var tenant = await db.Tenants.SingleAsync(t => t.Id == invite.TenantId, ct).ConfigureAwait(false);
            if (tenant.DisabledAt is not null)
            {
                return Results.Json(new { error = "tenant_disabled", message = "This organisation's ScreenTail is switched off. Contact your admin." }, statusCode: StatusCodes.Status403Forbidden);
            }

            // A seat is an active device (AC2). Counted now, inside the same request that adds one, so
            // two activations racing for the last seat are settled by the database's row count rather
            // than by a number read a moment earlier.
            var seated = await db.Devices.CountAsync(d => d.TenantId == tenant.Id && d.RevokedAt == null, ct).ConfigureAwait(false);
            if (seated >= tenant.Seats)
            {
                return Results.Json(new { error = "seat_limit", message = SeatLimitMessage }, statusCode: StatusCodes.Status409Conflict);
            }

            var user = await db.Users.SingleOrDefaultAsync(u => u.TenantId == tenant.Id && u.Email == invite.Email, ct).ConfigureAwait(false)
                ?? new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = invite.Email, DisplayName = invite.DisplayName, CreatedAt = now };
            if (db.Entry(user).State == EntityState.Detached)
            {
                db.Users.Add(user);
            }

            var refresh = DeviceTokens.Create();
            var device = new Device
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UserId = user.Id,
                Name = request.DeviceName.Trim(),
                TokenHash = DeviceTokens.Hash(refresh),
                ActivatedAt = now,
                LastSeenAt = now,
            };
            db.Devices.Add(device);
            invite.AcceptedAt = now;
            invite.DeviceId = device.Id;
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var (access, expires) = issuer.ForDevice(tenant.Id, user.Id, device.Id, time);
            return Results.Ok(new ActivatedDevice(tenant.Name, device.Id, refresh, access, expires));
        })
        .WithName("ActivateDevice")
        .WithSummary("Activates this device with an invite's code and binds it to the tenant.");

        group.MapPost("/token", async (RefreshRequest request, ScreenTailContext db, TokenIssuer issuer, TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                return Results.Unauthorized();
            }

            var hash = DeviceTokens.Hash(request.RefreshToken);
            var device = await db.Devices
                .Where(d => d.TokenHash == hash && d.RevokedAt == null)
                .Select(d => new { d.Id, d.TenantId, d.UserId, TenantDisabled = db.Tenants.Any(t => t.Id == d.TenantId && t.DisabledAt != null) })
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (device is null || device.TenantDisabled)
            {
                return Results.Unauthorized();
            }

            var now = time.GetUtcNow();
            _ = await db.Devices.Where(d => d.Id == device.Id).ExecuteUpdateAsync(set => set.SetProperty(d => d.LastSeenAt, now), ct).ConfigureAwait(false);
            var (access, expires) = issuer.ForDevice(device.TenantId, device.UserId, device.Id, time);
            return Results.Ok(new AccessTokenResponse(access, expires));
        })
        .WithName("RefreshDeviceToken")
        .WithSummary("Exchanges a device's refresh token for an access token.");

        return group;
    }
}
