using System.Security.Claims;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Publish;

namespace ScreenTail.Api.Endpoints;

/// <summary>
/// POST /v1/sessions/publish (ST-093, ST-094). The reviewed note, its screenshots and the time entry go
/// to the tenant's PSA; each destination is answered on its own; nothing is written here (INV-7).
/// </summary>
public static class PublishEndpoint
{
    private const long MaxRequestBytes = 32 * 1024 * 1024;

    public static RouteGroupBuilder MapPublish(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/sessions/publish", async (PublishBundle bundle, ClaimsPrincipal caller, ScreenTailContext db, IPsaProviderFactory providers, IDocProviderFactory docProviders, TimeProvider time, CancellationToken ct) =>
        {
            // Permission first, then shape, for the reason SummarizeEndpoint gives: a revoked device
            // must not read the rules back out of the 400s.
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            if (PublishLimits.Check(bundle) is { } problem)
            {
                return Results.BadRequest(new { error = "rejected", message = problem });
            }

            var psa = await providers.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false);
            if (psa is null)
            {
                return Results.Json(new { error = "no_psa", message = "Connect a PSA to publish." }, statusCode: StatusCodes.Status501NotImplemented);
            }

            // The footer names whoever reviewed it. The client does not know the technician's display
            // name and should not have to: the token says who they are.
            var reviewed = bundle with { Reviewer = string.IsNullOrWhiteSpace(bundle.Reviewer) ? who.User.DisplayName : bundle.Reviewer };

            // The knowledge base is optional: none connected means that one destination says so.
            var docs = bundle.Destinations.Contains(Destinations.KbArticle, StringComparer.Ordinal)
                ? await docProviders.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false)
                : null;
            return Results.Ok(await Publisher.PublishAsync(psa, reviewed, docs, new CompanyMappings(db, who.TenantId, time), ct).ConfigureAwait(false));
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBytes))
        .WithName("Publish")
        .WithSummary("Publish a reviewed note, its screenshots and a time entry to the tenant's PSA. Each destination is answered on its own; nothing is stored (INV-7).");

        return group;
    }
}
