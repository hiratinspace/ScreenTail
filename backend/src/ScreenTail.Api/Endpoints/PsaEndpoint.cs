using System.Security.Claims;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.ConnectWise;

namespace ScreenTail.Api.Endpoints;

public sealed record TicketSearchRow(string Id, string Summary, string Company, string? Status);

public sealed record TicketSearchResponse(IReadOnlyList<TicketSearchRow> Tickets);

/// <summary>
/// The client's way to the tenant's PSA (ST-092): a search, answered from the provider the vault's
/// credential builds. The client never learns which PSA it is.
/// </summary>
public static class PsaEndpoint
{
    public static RouteGroupBuilder MapPsa(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/psa/tickets", async (string? q, ClaimsPrincipal caller, ScreenTailContext db, IPsaProviderFactory providers, CancellationToken ct) =>
        {
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            var query = (q ?? string.Empty).Trim();
            if (query.Length < 3 && !(query.Length > 0 && query.All(char.IsAsciiDigit)))
            {
                return Results.BadRequest(new { error = "query_too_short", message = "Type at least three characters, or a ticket number." });
            }

            var psa = await providers.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false);
            if (psa is null)
            {
                return Results.Json(new { error = "no_psa", message = "Connect a PSA to search its tickets." }, statusCode: StatusCodes.Status501NotImplemented);
            }

            var result = await psa.SearchTicketsAsync(query, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                return Refused(result.Error!);
            }

            return Results.Ok(new TicketSearchResponse([.. result.Value!.Select(t => new TicketSearchRow(t.Id, t.Summary, t.Company, t.Status))]));
        })
        .WithName("SearchTickets")
        .WithSummary("Searches the tenant's PSA for tickets: three characters or a ticket number.");

        return group;
    }

    /// <summary>A provider's refusal, passed on with its kind and both of its sentences, as a 502: the fault is upstream of us.</summary>
    public static IResult Refused(ProviderError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Results.Json(
            new { error = "psa_" + error.Kind.ToString().ToLowerInvariant(), message = error.ToString(), retryable = error.Retryable, retry_after_seconds = error.RetryAfter?.TotalSeconds },
            statusCode: StatusCodes.Status502BadGateway);
    }
}
