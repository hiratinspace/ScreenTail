using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Endpoints;

/// <param name="Tenant">The MSP this device belongs to.</param>
/// <param name="User">The technician. Their work address and display name, and nothing else.</param>
/// <param name="Device">This installation.</param>
public sealed record MeResponse(TenantInfo Tenant, UserInfo User, DeviceInfo Device);

public sealed record TenantInfo(Guid Id, string Name, int Seats);

public sealed record UserInfo(Guid Id, string Email, string DisplayName);

public sealed record DeviceInfo(Guid Id, string Name, DateTimeOffset ActivatedAt);

/// <summary>
/// Who the caller is (ST-008).
///
/// The client asks once at startup to confirm its token still works and to learn the tenant it belongs
/// to. It is also the endpoint that proves the whole authentication chain: a request with no token, an
/// expired one, one signed with the wrong key or one for a device that has been revoked all get 401 and
/// nothing else.
///
/// <b>Every lookup is scoped by the tenant in the token, not by an id in the request.</b> There is no
/// parameter a caller could change to read another MSP's row.
/// </summary>
public static class MeEndpoint
{
    public static RouteGroupBuilder MapMe(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/me", async (ClaimsPrincipal caller, ScreenTailContext db, TimeProvider time, CancellationToken ct) =>
        {
            // One implementation of "may this caller still act", shared with every other endpoint
            // under /v1. It used to live only here, which is how a revoked device went on drafting.
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            var device = who.Device;

            // Recorded on a read because it is the one request every client makes: it gives the seat list
            // a "last seen" without the client having to send a heartbeat of its own.
            await db.Devices
                .Where(d => d.Id == device.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.LastSeenAt, time.GetUtcNow()), ct)
                .ConfigureAwait(false);

            return Results.Ok(new MeResponse(
                new TenantInfo(who.Tenant.Id, who.Tenant.Name, who.Tenant.Seats),
                new UserInfo(who.User.Id, who.User.Email, who.User.DisplayName),
                new DeviceInfo(device.Id, device.Name, device.ActivatedAt)));
        })
        .WithName("GetMe")
        .WithSummary("The tenant, technician and device this token belongs to.");

        return group;
    }
}
