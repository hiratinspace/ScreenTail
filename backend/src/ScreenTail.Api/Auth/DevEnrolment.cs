using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Auth;

/// <param name="Token">The bearer token, for <c>SCREENTAIL_DEVICE_TOKEN</c> on the client.</param>
/// <param name="ExpiresAt">When it stops working. An hour, like every other access token.</param>
public sealed record EnrolledDevice(Guid TenantId, Guid UserId, Guid DeviceId, string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Enrols one device by hand, so the drafting path can be run end to end before there is an enrolment
/// flow to do it properly (ST-010, ST-063).
///
/// ST-010 issues device tokens as part of signing in. It does not exist yet, and until it does a client
/// has nothing to put in an Authorization header — so the path the client gained in #123 could be
/// tested and could not be <em>run</em>. This is the smallest thing that closes that gap, and it is
/// meant to be deleted the day ST-010 lands.
///
/// <b>Development only, and it refuses rather than warns.</b> A utility that mints credentials is the
/// last thing that should be reachable in a deployment, and "we remembered not to call it" is not a
/// control. Somebody will one day start the production container with the flag still on a command line,
/// and the answer to that is an exception rather than a tenant.
///
/// <b>It is not an endpoint and must never become one.</b> Reaching it needs the process, the database
/// connection string and the signing key — everything an attacker who could use it would already have.
/// Exposed over HTTP it would be none of those things.
/// </summary>
public static class DevEnrolment
{
    /// <summary>What the seeded device is called. Fixed, so running this twice finds the first one.</summary>
    public const string DeviceName = "dev-enrolled-device";

    private const string TenantName = "Development";

    /// <summary>
    /// Finds or creates the development tenant, user and device, and returns a token for them.
    ///
    /// Idempotent on purpose. A token lasts an hour and the loop this exists for takes longer than that
    /// to get working, so it will be run again — and a second device every time would leave a trail of
    /// credentials nobody thinks to revoke.
    /// </summary>
    public static async Task<EnrolledDevice> EnrolAsync(
        ScreenTailContext db,
        TokenIssuer issuer,
        bool isDevelopment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(issuer);

        // First, before anything is written. A refusal that had already created the tenant would leave a
        // credential behind in the database it was refusing to create one in.
        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                "Device enrolment by hand is a development utility and this deployment is not in "
                + "Development. Devices are enrolled by signing in (ST-010).");
        }

        var device = await db.Devices
            .Where(d => d.Name == DeviceName && d.RevokedAt == null)
            .OrderBy(d => d.ActivatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (device is null)
        {
            var now = DateTimeOffset.UtcNow;
            var tenant = new Tenant { Id = Guid.NewGuid(), Name = TenantName, Seats = 1, CreatedAt = now };
            var user = new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Email = "developer@localhost",
                DisplayName = "Developer",
                CreatedAt = now,
            };

            device = new Device
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UserId = user.Id,
                Name = DeviceName,

                // A hash of a token nobody keeps. The column is not nullable and this utility has no
                // use for the refresh path; what the client is given is the access token below.
                TokenHash = DeviceTokens.Hash(DeviceTokens.Create()),
                ActivatedAt = now,
            };

            db.Tenants.Add(tenant);
            db.Users.Add(user);
            db.Devices.Add(device);
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var (token, expires) = issuer.ForDevice(device.TenantId, device.UserId, device.Id);
        return new EnrolledDevice(device.TenantId, device.UserId, device.Id, token, expires);
    }
}
