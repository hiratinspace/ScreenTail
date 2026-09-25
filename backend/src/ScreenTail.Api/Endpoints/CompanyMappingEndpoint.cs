using System.Security.Claims;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Publish;

namespace ScreenTail.Api.Endpoints;

public sealed record MapCompanyRequest(string PsaCompany, string DocCompanyId);

public sealed record DocCompanyRow(string Id, string Name);

public sealed record CompanyMappingRow(string PsaCompany, string DocCompanyId, string DocCompanyName, string Confidence);

/// <param name="Companies">The documentation platform's companies, for the picker.</param>
/// <param name="Mappings">What this tenant has decided so far.</param>
public sealed record CompanyMappingsResponse(IReadOnlyList<DocCompanyRow> Companies, IReadOnlyList<CompanyMappingRow> Mappings);

/// <summary>
/// The company mappings behind Settings → Integrations (ST-097 AC3): list the documentation platform's
/// companies beside what the tenant has mapped, map one by hand, forget one. A mapping to a company the
/// platform does not have is refused, because that is the wrong-company publish waiting to happen.
/// </summary>
public static class CompanyMappingEndpoint
{
    public static RouteGroupBuilder MapCompanyMappings(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/integrations/hudu/companies", async (ClaimsPrincipal caller, ScreenTailContext db, IDocProviderFactory providers, TimeProvider time, CancellationToken ct) =>
        {
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            var docs = await providers.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false);
            if (docs is null)
            {
                return Results.Json(new { error = "no_docs", message = "Connect a documentation platform first." }, statusCode: StatusCodes.Status501NotImplemented);
            }

            var companies = await docs.ListCompaniesAsync(ct).ConfigureAwait(false);
            if (!companies.Ok)
            {
                return PsaEndpoint.Refused(companies.Error!);
            }

            var mappings = await new CompanyMappings(db, who.TenantId, time).ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(new CompanyMappingsResponse(
                [.. companies.Value!.Select(c => new DocCompanyRow(c.Id, c.Name))],
                [.. mappings.Select(m => new CompanyMappingRow(m.PsaCompany, m.DocCompanyId, m.DocCompanyName, m.Confidence))]));
        })
        .WithName("ListCompanyMappings")
        .WithSummary("The documentation platform's companies and what this tenant has mapped to them.");

        group.MapPut("/integrations/hudu/companies", async (MapCompanyRequest request, ClaimsPrincipal caller, ScreenTailContext db, IDocProviderFactory providers, TimeProvider time, CancellationToken ct) =>
        {
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.PsaCompany) || request.PsaCompany.Length > 200 || string.IsNullOrWhiteSpace(request.DocCompanyId))
            {
                return Results.BadRequest(new { error = "bad_mapping", message = "A PSA company name and a documentation company id are required." });
            }

            var docs = await providers.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false);
            if (docs is null)
            {
                return Results.Json(new { error = "no_docs", message = "Connect a documentation platform first." }, statusCode: StatusCodes.Status501NotImplemented);
            }

            var companies = await docs.ListCompaniesAsync(ct).ConfigureAwait(false);
            if (!companies.Ok)
            {
                return PsaEndpoint.Refused(companies.Error!);
            }

            var company = companies.Value!.SingleOrDefault(c => c.Id == request.DocCompanyId.Trim());
            if (company is null)
            {
                return Results.BadRequest(new { error = "no_such_company", message = "The documentation platform has no company with that id." });
            }

            _ = await new CompanyMappings(db, who.TenantId, time).RememberAsync(request.PsaCompany, company, "manual", ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("MapCompany")
        .WithSummary("Maps a PSA company to a documentation-platform company, by hand.");

        group.MapDelete("/integrations/hudu/companies/{psaCompany}", async (string psaCompany, ClaimsPrincipal caller, ScreenTailContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            return await new CompanyMappings(db, who.TenantId, time).ForgetAsync(psaCompany, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
        })
        .WithName("UnmapCompany")
        .WithSummary("Forgets a company mapping.");

        return group;
    }
}
