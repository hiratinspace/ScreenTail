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

    /// <summary>The answer to any code that is not one we issued, malformed or well-formed: it names no tenant and says nothing about which.</summary>
    public const string UnknownCodeMessage = "That code is not one we issued. Check it against the invite.";

    public const string UsedCodeMessage = "That code has already been used. Ask your admin for a new invite.";

    public static RouteGroupBuilder MapDevices(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/activate", async (ActivateDeviceRequest request, ScreenTailContext db, TokenIssuer issuer, ActivationWatch watch, TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 200)
            {
                return Results.BadRequest(new { error = "bad_device_name", message = "The device needs a name of up to 200 characters." });
            }

            // A code that could not be one we issued (the wrong length, a character outside the
            // alphabet, nothing at all) gets the same answer as one nobody issued, without a lookup.
            // Every well-formed guess costs exactly one indexed lookup by hash, whichever tenant's
            // invites exist, and is answered with the same bytes (ADR-0009, "Guessing invite codes").
            var now = time.GetUtcNow();
            var code = Invites.Normalise(request.Code);
            Invite? invite = null;
            if (Invites.IsWellFormed(code))
            {
                // The function that stored the hash when the invite was made, so the two cannot drift apart.
                var hash = Invites.Hash(code);
                invite = await db.Invites.SingleOrDefaultAsync(i => i.CodeHash == hash, ct).ConfigureAwait(false);
            }

            if (invite is null)
            {
                watch.Refused();
                return Results.BadRequest(new { error = "invite_unknown", message = UnknownCodeMessage });
            }

            // Reachable only with a code that was issued, which a guess finds with odds of about 2^-99.
            if (invite.AcceptedAt is not null)
            {
                watch.Refused();
                return Results.BadRequest(new { error = "invite_used", message = UsedCodeMessage });
            }

            if (invite.ExpiresAt <= now)
            {
                watch.Refused();
                return Results.BadRequest(new { error = "invite_expired", message = "That code has expired. Ask your admin for a new invite." });
            }

            var tenant = await db.Tenants.SingleAsync(t => t.Id == invite.TenantId, ct).ConfigureAwait(false);
            if (tenant.DisabledAt is not null)
            {
                return Results.Json(new { error = "tenant_disabled", message = "This organisation's ScreenTail is switched off. Contact your admin." }, statusCode: StatusCodes.Status403Forbidden);
            }

            // Two activations at once must not both win. Everything from here to the commit is one
            // transaction that starts by writing the tenant's row, which Postgres then holds locked until
            // the commit (SQLite locks the whole database for its one writer): activations of one tenant
            // queue here, and each counts the seats only after the one before it has committed or rolled
            // back. Activations of different tenants do not wait for each other.
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            _ = await db.Tenants.Where(t => t.Id == tenant.Id).ExecuteUpdateAsync(set => set.SetProperty(t => t.Seats, t => t.Seats), ct).ConfigureAwait(false);

            // The invite is claimed only if it is still unclaimed and unexpired when this write runs, not
            // when it was read above: of two activations with one code exactly one changes the row, and
            // the other is told the code has been used.
            var claimed = await db.Invites
                .Where(i => i.Id == invite.Id && i.AcceptedAt == null && i.ExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.AcceptedAt, now), ct).ConfigureAwait(false);
            if (claimed == 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                watch.Refused();
                return Results.BadRequest(new { error = "invite_used", message = UsedCodeMessage });
            }

            // A seat is an active device (AC2), counted under the tenant's lock, so no other activation of
            // this tenant can add one between this count and the insert below. A refusal rolls the claim
            // back, and the code works again once a seat is free.
            var seated = await db.Devices.CountAsync(d => d.TenantId == tenant.Id && d.RevokedAt == null, ct).ConfigureAwait(false);
            if (seated >= tenant.Seats)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
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

            // AcceptedAt is already written by the claim; this records which device the code went to.
            invite.AcceptedAt = now;
            invite.DeviceId = device.Id;
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            var (access, expires) = issuer.ForDevice(tenant.Id, user.Id, device.Id, time);
            return Results.Ok(new ActivatedDevice(tenant.Name, device.Id, refresh, access, expires));
        })
        .WithName("ActivateDevice")

        // Counted per client address with the admin's sign-in (ADR-0009): a guess at a 99-bit code is
        // hopeless, but each still costs a lookup. The token exchange is not, as its secret is 256 bits.
        // A body is a code and a machine's name, so the anonymous routes' 16 KB is plenty.
        .RequireRateLimiting(AnonymousRateLimit.Policy)
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AdminSession.AnonymousBodyLimit))
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
