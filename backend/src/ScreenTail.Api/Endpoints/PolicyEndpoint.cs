using System.Security.Claims;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Endpoints;

/// <summary>What the client applies (ST-047). The shape <c>client/ScreenTail.Core/Net/PolicySync.cs</c> reads.</summary>
public sealed record TenantPolicyResponse(string Version, int RetentionDays, bool LocalOnly, bool LocalOnlyLocked, bool CaptureAllWindows);

public static class PolicyEndpoint
{
    public static RouteGroupBuilder MapPolicy(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/policy", async (ClaimsPrincipal caller, ScreenTailContext db, CancellationToken ct) =>
        {
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            var latest = await Policies.LatestAsync(db, who.TenantId, ct).ConfigureAwait(false);
            return Results.Ok(latest is null
                ? new TenantPolicyResponse(Policies.DefaultVersion, 7, false, false, false)
                : new TenantPolicyResponse(latest.Version, latest.RetentionDays, latest.LocalOnly, latest.LocalOnlyLocked, latest.CaptureAllWindows));
        })
        .WithName("GetPolicy")
        .WithSummary("The tenant's policy for this device: retention, local-only and whether it is locked, capture scope.");

        return group;
    }
}
