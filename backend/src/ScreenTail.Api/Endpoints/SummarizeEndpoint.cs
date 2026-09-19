using System.Security.Claims;
using ScreenTail.Api.Auth;
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
    public static RouteGroupBuilder MapSummarize(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/sessions/summarize", async (
            SummarizeBundle bundle,
            ClaimsPrincipal caller,
            SummarizationService summarizer,
            CancellationToken ct) =>
        {
            if (bundle is null || string.IsNullOrWhiteSpace(bundle.SessionId))
            {
                return Results.BadRequest(new SummarizeResponse("rejected", "A session id is required."));
            }

            if (!Guid.TryParse(caller.FindFirstValue(ScreenTailClaims.TenantId), out var tenantId))
            {
                return Results.Unauthorized();
            }

            var result = await summarizer.DraftAsync(tenantId, bundle, ct).ConfigureAwait(false);

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
        .WithName("Summarize")
        .WithSummary("Draft a note from one session's bundle. Frames are held in memory for this request only (INV-7).");

        return group;
    }
}
