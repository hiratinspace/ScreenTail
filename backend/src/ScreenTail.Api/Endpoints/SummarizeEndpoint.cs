using System.Security.Claims;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Endpoints;

/// <param name="Status">
/// <c>ok</c>, <c>not_configured</c>, <c>cost_cap_reached</c>, <c>unavailable</c> or <c>invalid</c>. The
/// client acts on each differently: only <c>unavailable</c> is queued, and <c>cost_cap_reached</c> sends
/// it to draft on the device (Spec §6).
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

    public static RouteGroupBuilder MapSummarize(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/sessions/summarize", async (
            SummarizeBundle bundle,
            ClaimsPrincipal caller,
            ScreenTailContext db,
            SummarizationService summarizer,
            CancellationToken ct) =>
        {
            // A valid signature is not permission. The device may have been revoked, the technician
            // disabled or the tenant switched off since this token was issued, and until the 2026-09-19
            // review this endpoint asked none of that: a revoked laptop went on spending the tenant's
            // budget here while /v1/me already refused it.
            //
            // First, ahead of the bundle checks. It used to be second, so a revoked device could send
            // deliberately malformed bundles and read the rules back out of the 400s — the frame
            // ceiling, the identifier shape, the accepted media types — which is a map of the endpoint
            // handed to the one caller already established as not allowed to be here (2026-09-20
            // review). Two database reads before the size checks is the price, and it is a pair of
            // indexed lookups against a request that has already been parsed.
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

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

                // Queue it. The outbox retries with backoff rather than losing the session (ST-064).
                SummarizeStatus.Unavailable => Results.Json(
                    new SummarizeResponse("unavailable", result.Reason),
                    statusCode: StatusCodes.Status503ServiceUnavailable),

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
        .WithName("Summarize")
        .WithSummary("Draft a note from one session's bundle. Frames are held in memory for this request only (INV-7).");

        return group;
    }
}
