using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers.Demo;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Publish;
using ScreenTail.Api.Vault;

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

        group.MapGet("/integrations/hudu/companies", async (Caller who, ScreenTailContext db, IDocProviderFactory providers, DemoOptions demo, TimeProvider time, CancellationToken ct) =>
        {
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

            // The demo's companies beside the tenant's real mappings would be a table of mismatches.
            var mappings = demo.Demo ? [] : await new CompanyMappings(db, who.TenantId, time).ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(new CompanyMappingsResponse(
                [.. companies.Value!.Select(c => new DocCompanyRow(c.Id, c.Name))],
                [.. mappings.Select(m => new CompanyMappingRow(m.PsaCompany, m.DocCompanyId, m.DocCompanyName, m.Confidence))]));
        })
        .WithName("ListCompanyMappings")
        .WithSummary("The documentation platform's companies and what this tenant has mapped to them.");

        group.MapPut("/integrations/hudu/companies", async (MapCompanyRequest request, Caller who, ScreenTailContext db, IDocProviderFactory providers, DemoOptions demo, TimeProvider time, MappingChangeLimit limit, HttpContext http, CancellationToken ct) =>
        {
            if (demo.Demo)
            {
                return IntegrationsEndpoint.DemoOn();
            }

            if (limit.TryTake(who.DeviceId) is { } wait)
            {
                return TooMany(http, wait);
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

            // The PSA company is a name the device sends, not checked against the PSA, and each company's
            // latest record is kept: past the ceiling a company not seen before is refused.
            if (await IntegrationChanges.AtCompanyCeilingAsync(db, who.TenantId, request.PsaCompany.Trim(), ct).ConfigureAwait(false))
            {
                return Results.Json(
                    new { error = "too_many_companies", message = string.Create(CultureInfo.InvariantCulture, $"ScreenTail keeps mappings for up to {IntegrationChanges.CompanyCeiling:N0} PSA companies, and this one would be past that. Map a company already mapped, or ask your ScreenTail owner for help.") },
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Technicians keep this, since the mapping prompt comes up while they publish; the owner sees
            // each one on the Integrations page (2026-11-15 review). Recorded in the same transaction.
            await using (var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                var mapped = await new CompanyMappings(db, who.TenantId, time).RememberAsync(request.PsaCompany, company, "manual", ct).ConfigureAwait(false);
                await IntegrationChanges.RecordMappingAsync(db, who.TenantId, IntegrationsEndpoint.Hudu, IntegrationChanges.Mapped, ChangeActor.Technician(who.UserId, who.DeviceId), time.GetUtcNow(), mapped.PsaCompany, mapped.DocCompanyId, mapped.DocCompanyName, ct).ConfigureAwait(false);
                _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }

            return Results.NoContent();
        })
        .WithMetadata(new RequestSizeLimitAttribute(IntegrationsEndpoint.BodyLimit))
        .WithName("MapCompany")
        .WithSummary("Maps a PSA company to a documentation-platform company, by hand.");

        group.MapDelete("/integrations/hudu/companies/{psaCompany}", async (string psaCompany, Caller who, ScreenTailContext db, DemoOptions demo, TimeProvider time, MappingChangeLimit limit, HttpContext http, CancellationToken ct) =>
        {
            if (demo.Demo)
            {
                return IntegrationsEndpoint.DemoOn();
            }

            if (limit.TryTake(who.DeviceId) is { } wait)
            {
                return TooMany(http, wait);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            if (!await new CompanyMappings(db, who.TenantId, time).ForgetAsync(psaCompany, ct).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await IntegrationChanges.RecordMappingAsync(db, who.TenantId, IntegrationsEndpoint.Hudu, IntegrationChanges.Unmapped, ChangeActor.Technician(who.UserId, who.DeviceId), time.GetUtcNow(), psaCompany.Trim(), ct: ct).ConfigureAwait(false);
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("UnmapCompany")
        .WithSummary("Forgets a company mapping.");

        return group;
    }

    /// <summary>
    /// A device past its mapping changes for the window (2026-11-15 review): 429, Retry-After, and a
    /// sentence the client shows. Nothing is changed, so the technician's choice can be sent again later.
    /// </summary>
    private static IResult TooMany(HttpContext http, TimeSpan wait)
    {
        var seconds = (long)Math.Ceiling(wait.TotalSeconds);
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return Results.Json(
            new
            {
                error = "too_many_mapping_changes",
                message = string.Create(CultureInfo.InvariantCulture, $"This PC has changed company mappings {MappingChangeLimit.PerDevice} times in {(int)MappingChangeLimit.Window.TotalMinutes} minutes. Wait {minutes} minute{(minutes == 1 ? string.Empty : "s")}, then choose the company again."),
            },
            statusCode: StatusCodes.Status429TooManyRequests);
    }
}
