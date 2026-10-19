using ScreenTail.Api.Auth;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Endpoints;

/// <param name="Status">
/// <c>ok</c>, <c>rejected</c>, <c>cost_cap_reached</c>, <c>invalid</c>, <c>busy</c>, <c>not_configured</c>
/// or <c>unavailable</c>, each with its own HTTP status. The status code is what the client acts on, and
/// <c>shared/contracts/summarize-response.v1.json</c> is the list both ends are tested against.
/// </param>
public sealed record SummarizeResponse(string Status, string? Reason = null, DraftJson? Draft = null);

/// <summary>
/// Where a session is drafted (ST-063).
///
/// One model call per session, and <b>nothing is written</b>. INV-7 says the backend never persists a
/// capture: the bundle arrives, is handed to the model, and is released; what is stored afterwards is a
/// row with a tenant, a session id, a provider name and a number.
/// <c>SummarizationPersistsNothingTests</c> counts every row in every table across a request.
/// </summary>
public static class SummarizeEndpoint
{
    /// <summary>
    /// The largest request the endpoint will read.
    ///
    /// The client's byte budget is 4 MB of images, which is about 5.6 MB once base64 has expanded it.
    /// Sixteen leaves room for the JSON around it and refuses anything that is not a session.
    /// </summary>
    private const long MaxRequestBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The longest Retry-After passed on. Google names the time a quota reopens, which is within a day;
    /// anything longer is a provider having a bad day, and parks a session for nothing.
    /// </summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromDays(1);

    /// <summary>
    /// Gates the drafting endpoint before its body is read: at most
    /// <see cref="DraftingConcurrency.PerDevice"/> drafts in flight per device and
    /// <see cref="DraftingConcurrency.PerTenant"/> per tenant (2026-10-12 review). After authorization,
    /// so the caller is known.
    /// </summary>
    public static IApplicationBuilder UseDraftingConcurrency(this IApplicationBuilder app) => DraftingConcurrency.Use(app);

    public static RouteGroupBuilder MapSummarize(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/sessions/summarize", async (
            SummarizeBundle bundle,
            Caller who,
            SummarizationService summarizer,
            HttpContext http,
            CancellationToken ct) =>
        {
            // The gate runs before the body is bound, so this cannot stop a bundle being read. It stops a
            // deployment that lost the middleware line from drafting without a limit, and says so.
            if (!DraftingConcurrency.Holds(http))
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            // Who is asking was settled before the bundle was read: CallerPolicy refuses a revoked
            // device, a disabled technician or a switched-off tenant on the whole /v1 group, so none of
            // them can read the rules below back out of the 400s (2026-09-20 and 2026-10-12 reviews).

            // Everything the bundle could be wrong about, before a model call is paid for. Two of the
            // 2026-09-19 review's findings were requests that were accepted, billed, and only then found
            // to be unstorable — a billed draft with no ledger row, and a daily cap that never moved.
            if (BundleLimits.Check(bundle) is { } problem)
            {
                return Results.BadRequest(new SummarizeResponse("rejected", problem));
            }

            var result = await summarizer.DraftAsync(who.TenantId, bundle, ct).ConfigureAwait(false);

            return result.Status switch
            {
                SummarizeStatus.Ok => Results.Ok(new SummarizeResponse("ok", null, result.Draft)),

                // Not an error on the client's part, and not something a retry fixes. The client drafts
                // on the device instead and tells the technician why (Spec §6).
                SummarizeStatus.CostCapReached => Results.Json(
                    new SummarizeResponse("cost_cap_reached", result.Reason),
                    statusCode: StatusCodes.Status402PaymentRequired),

                // Queue it. The outbox retries with backoff rather than losing the session (ST-064), and
                // waits at least until the time the provider named when it named one: under a daily
                // quota that is hours, and eight attempts inside the hour was a session failed for nothing
                // (2026-10-12 review).
                SummarizeStatus.Unavailable => Unavailable(http, result),

                SummarizeStatus.NotConfigured => Results.Json(
                    new SummarizeResponse("not_configured", result.Reason),
                    statusCode: StatusCodes.Status501NotImplemented),

                // The model answered and what it said could not be shown. Retrying costs money for the
                // same answer, so the client is told rather than left to loop.
                _ => Results.Json(
                    new SummarizeResponse("invalid", result.Reason),
                    statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        })
        // Nothing a client sends is worth more than this, and the default is 30 MB of memory held several
        // times over while it is parsed. BundleLimits then applies the real ceilings to the shape.
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBytes))
        .WithMetadata(DraftingConcurrency.Gated)
        .WithName("Summarize")
        .WithSummary("Draft a note from one session's bundle. Frames are held in memory for this request only (INV-7).");

        return group;
    }

    /// <summary>
    /// The 503, with a Retry-After in whole seconds when the provider named a time. Rounded up, because a
    /// retry a fraction of a second early is the request that earned the rate limit.
    /// </summary>
    private static IResult Unavailable(HttpContext http, SummarizeResult result)
    {
        if (result.RetryAfter is { } after && after > TimeSpan.Zero)
        {
            var seconds = (long)Math.Ceiling(Math.Min(after.TotalSeconds, MaxRetryAfter.TotalSeconds));
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Results.Json(
            new SummarizeResponse("unavailable", result.Reason),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
