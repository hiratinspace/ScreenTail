using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Endpoints;

/// <summary>
/// What the client applies (ST-047). The shape <c>client/ScreenTail.Core/Net/PolicySync.cs</c> reads:
/// <c>shared/contracts/tenant-policy.v1.json</c>. The client refuses an answer with a field missing, so a
/// renamed property here stops policy sync rather than unlocking a locked field (INV-11).
/// </summary>
public sealed record TenantPolicyResponse(
    string Version,
    int RetentionDays,
    bool LocalOnly,
    bool LocalOnlyLocked,
    bool CaptureAllWindows,
    string UpdateChannel,
    bool UpdateChannelLocked,
    int UpdateCheckHours,
    bool UpdateChecksUnderLocalOnly);

public static class PolicyEndpoint
{
    public static RouteGroupBuilder MapPolicy(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/policy", async (Caller who, ScreenTailContext db, CancellationToken ct) =>
        {
            var latest = await Policies.LatestAsync(db, who.TenantId, ct).ConfigureAwait(false);
            return Results.Ok(Describe(latest));
        })
        .WithName("GetPolicy")
        .WithSummary("The tenant's policy for this device: retention, local-only and whether it is locked, capture scope, and updates.");

        return group;
    }

    /// <summary>A policy row on the wire, or the defaults for none. The web admin reads the same shape (ST-099).</summary>
    public static TenantPolicyResponse Describe(Policy? latest) => latest is null
        ? new TenantPolicyResponse(Policies.DefaultVersion, 7, false, false, false, "stable", false, 24, false)
        : new TenantPolicyResponse(latest.Version, latest.RetentionDays, latest.LocalOnly, latest.LocalOnlyLocked, latest.CaptureAllWindows, latest.UpdateChannel, latest.UpdateChannelLocked, latest.UpdateCheckHours, latest.UpdateChecksUnderLocalOnly);
}
