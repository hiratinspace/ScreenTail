using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers.ConnectWise;
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
/// platform does not have is refused, because that is the wrong-company publish waiting to happen, and so
/// is one from a company the PSA does not have, or that the PSA could not be asked about.
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

        group.MapPut("/integrations/hudu/companies", async (MapCompanyRequest request, Caller who, ScreenTailContext db, IDocProviderFactory providers, IPsaProviderFactory psaProviders, DemoOptions demo, TimeProvider time, MappingChangeLimit limit, HttpContext http, CancellationToken ct) =>
        {
            if (demo.Demo)
            {
                return IntegrationsEndpoint.DemoOn();
            }

            if (limit.TryTake(who.DeviceId, out var first) is { } wait)
            {
                return await TooManyAsync(http, db, who, first, wait, time, ct).ConfigureAwait(false);
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

            // The PSA company is checked against the PSA as the documentation company is checked against
            // the platform, so only a company the tenant's PSA has can be mapped. A PSA that cannot answer
            // refuses the mapping: nothing is saved on a name nobody could check. The PSA's own spelling is
            // saved, which is the name its tickets carry.
            var psa = await psaProviders.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false);
            if (psa is null)
            {
                return Results.Json(new { error = "no_psa", message = "No PSA is connected, so its companies cannot be mapped. Your owner connects one in the web admin." }, statusCode: StatusCodes.Status501NotImplemented);
            }

            var found = await psa.FindCompanyAsync(request.PsaCompany.Trim(), ct).ConfigureAwait(false);
            if (!found.Ok)
            {
                return PsaEndpoint.Refused(found.Error!);
            }

            if (found.Value is not { } psaCompany)
            {
                return Results.BadRequest(new { error = "no_such_psa_company", message = "The PSA has no company with that name. Choose the company as the PSA names it on the ticket." });
            }

            // Technicians keep this, since the mapping prompt comes up while they publish; the owner sees
            // each one on the Integrations page (2026-11-15 review). Recorded in the same transaction.
            await using (var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                var mappings = new CompanyMappings(db, who.TenantId, time);

                // A row kept under the name as sent, when the PSA spells it otherwise, is the same company
                // under a name no ticket carries: it goes, and the owner is told, so Settings does not list
                // the company twice.
                var sent = request.PsaCompany.Trim();
                if (!string.Equals(sent, psaCompany.Name, StringComparison.Ordinal) && await mappings.ForgetAsync(sent, ct).ConfigureAwait(false))
                {
                    await IntegrationChanges.RecordMappingAsync(db, who.TenantId, IntegrationsEndpoint.Hudu, IntegrationChanges.Unmapped, ChangeActor.Technician(who.UserId, who.DeviceId), time.GetUtcNow(), sent, ct: ct).ConfigureAwait(false);
                }

                var mapped = await mappings.RememberAsync(psaCompany.Name, company, "manual", ct).ConfigureAwait(false);
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

            if (limit.TryTake(who.DeviceId, out var first) is { } wait)
            {
                return await TooManyAsync(http, db, who, first, wait, time, ct).ConfigureAwait(false);
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
    /// The window's first refusal is recorded for the owner, once per device per window, so a flood shows
    /// on the Integrations page and the owner can revoke the device.
    /// </summary>
    private static async Task<IResult> TooManyAsync(HttpContext http, ScreenTailContext db, Caller who, bool first, TimeSpan wait, TimeProvider time, CancellationToken ct)
    {
        if (first)
        {
            IntegrationChanges.RecordSlowed(db, who.TenantId, ChangeActor.Technician(who.UserId, who.DeviceId), time.GetUtcNow());
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

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
