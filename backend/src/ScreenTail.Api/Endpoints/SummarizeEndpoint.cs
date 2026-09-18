using System.Security.Claims;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Endpoints;

/// <summary>
/// The bundle a client sends to be drafted. Read once, held for the length of the call, never written.
/// </summary>
/// <param name="SessionId">The client's own identifier. Opaque, and not a name.</param>
public sealed record SummarizeRequest(string SessionId, int Frames, int TranscriptSegments, int EstimatedTokens);

public sealed record SummarizeResponse(string Status, string Reason);

/// <summary>
/// Where a session is drafted (ST-063), and for now where it is refused (ST-008).
///
/// It exists already for one reason: <b>INV-7 is a claim about this endpoint</b>, and a claim about an
/// endpoint that does not exist cannot be tested. The invariant says the backend never persists a frame
/// — the summarization path holds them in memory for one request and lets them go — and
/// <c>SummarizationPersistsNothingTests</c> asserts that the database is byte-for-byte unchanged across
/// a request, which is only meaningful if there is a request to make.
///
/// The provider arrives with ST-063 and needs an API key and a spend cap. Until then this reads the
/// bundle, answers honestly, and writes nothing anywhere.
/// </summary>
public static class SummarizeEndpoint
{
    public const string NoProviderReason =
        "No summarization provider is configured on this deployment, so this session was not drafted.";

    public static RouteGroupBuilder MapSummarize(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/sessions/summarize", (SummarizeRequest request, ClaimsPrincipal caller) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.SessionId))
            {
                return Results.BadRequest(new SummarizeResponse("rejected", "A session id is required."));
            }

            if (caller.FindFirstValue(ScreenTailClaims.TenantId) is null)
            {
                return Results.Unauthorized();
            }

            // Nothing is written here, and nothing may be. The request object goes out of scope with the
            // response; there is no repository, no cache and no log line carrying any of it (INV-7).
            return Results.Json(
                new SummarizeResponse("unavailable", NoProviderReason),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("Summarize")
        .WithSummary("Draft a note from one session's bundle. Frames are held in memory for this request only (INV-7).");

        return group;
    }
}
