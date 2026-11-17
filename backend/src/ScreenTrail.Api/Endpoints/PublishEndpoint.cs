using Microsoft.Extensions.Options;
using ScreenTrail.Api.Auth;
using ScreenTrail.Api.Data;
using ScreenTrail.Api.Providers.ConnectWise;
using ScreenTrail.Api.Providers.Demo;
using ScreenTrail.Api.Providers.Hudu;
using ScreenTrail.Api.Publish;

namespace ScreenTrail.Api.Endpoints;

/// <summary>
/// POST /v1/sessions/publish (ST-093, ST-094). The reviewed note, its screenshots and the time entry go
/// to the tenant's PSA; each destination is answered on its own, within the publish's own deadline;
/// nothing is written here (INV-7).
/// </summary>
public static class PublishEndpoint
{
    private const long MaxRequestBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Gates the publish endpoint before its body is read: at most <see cref="PublishConcurrency.PerDevice"/>
    /// publishes in flight per device and <see cref="PublishConcurrency.PerTenant"/> per tenant
    /// (2026-11-15 review). After authorization, so the caller is known.
    /// </summary>
    public static IApplicationBuilder UsePublishConcurrency(this IApplicationBuilder app) => PublishConcurrency.Use(app);

    public static RouteGroupBuilder MapPublish(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/sessions/publish", async (HttpContext http, PublishBundle bundle, Caller who, ScreenTrailContext db, IPsaProviderFactory providers, IDocProviderFactory docProviders, DemoOptions demo, TimeProvider time, IOptions<PublishOptions> publish, ILoggerFactory loggers, CancellationToken ct) =>
        {
            // The gate runs before the body is bound, so this cannot stop a body being read. It stops a
            // deployment that lost the middleware line from publishing without a limit.
            if (!PublishConcurrency.Holds(http))
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (PublishLimits.Check(bundle) is { } problem)
            {
                return Results.BadRequest(new { error = "rejected", message = problem });
            }

            var psa = await providers.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false);
            if (psa is null)
            {
                return Results.Json(new { error = "no_psa", message = "No PSA is connected. Your ScreenTrail owner connects one in the web admin." }, statusCode: StatusCodes.Status501NotImplemented);
            }

            // The footer names whoever reviewed it, and the token says who that is. The request's
            // reviewer is still accepted, so an older client's body binds, but never used: a device could
            // otherwise sign a note with another technician's name (2026-11-15 review).
            var reviewed = bundle with { Reviewer = who.User.DisplayName };

            // The knowledge base is optional: none connected means that one destination says so.
            var docs = bundle.Destinations.Contains(Destinations.KbArticle, StringComparer.Ordinal)
                ? await docProviders.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false)
                : null;
            // The deadline counts from when the request arrived, as the request's timeout and the
            // client's do, so the time the body took to upload is already spent (S-68).
            return Results.Ok(await Publisher.PublishAsync(
                psa,
                reviewed,
                docs,
                // While the demo is on, the tenant's real mappings are neither read, written nor deleted.
                demo.Demo ? new DemoCompanyMappings() : new CompanyMappings(db, who.TenantId, time),
                publish.Value,
                time,
                spent: RequestStart.Elapsed(http, time),
                log: loggers.CreateLogger(typeof(Publisher)),
                ct).ConfigureAwait(false));
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBytes))
        .WithMetadata(PublishConcurrency.Gated)
        .WithName("Publish")
        .WithSummary("Publish a reviewed note, its screenshots and a time entry to the tenant's PSA. Each destination is answered on its own; nothing is stored (INV-7).");

        return group;
    }
}
