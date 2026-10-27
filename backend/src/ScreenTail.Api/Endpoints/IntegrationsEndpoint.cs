using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Demo;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Publish;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Endpoints;

/// <param name="SiteUrl">Where the provider lives for this tenant; not secret, shown in full.</param>
/// <param name="Secret">The credential, as the provider wants it — for ConnectWise the Basic-auth string, for Hudu the API key. Stored sealed, shown as its last four.</param>
public sealed record StoreIntegrationRequest(string SiteUrl, string Secret);

/// <param name="Secret">The last four characters behind bullets. Never more.</param>
public sealed record IntegrationRow(string Provider, string SiteUrl, string Secret, DateTimeOffset? ConnectedAt, DateTimeOffset? LastCheckedAt, string? LastError);

public sealed record IntegrationsResponse(IReadOnlyList<IntegrationRow> Integrations);

/// <summary>"Test connection" (ST-082): an answer in words either way. A refused credential is a result here, not a 502.</summary>
public sealed record IntegrationCheckResponse(bool Ok, string Message, DateTimeOffset CheckedAt);

/// <summary>
/// The integrations endpoints (ST-009): a credential goes in and is never read back over HTTP.
///
/// Any device of the tenant may manage them today; ST-010 brings roles and this narrows to admins then.
/// </summary>
public static partial class IntegrationsEndpoint
{
    /// <summary>The documentation platform, whose company mappings and cached list belong to the site it is connected to.</summary>
    private const string Hudu = "hudu";

    [GeneratedRegex("^[a-z][a-z0-9-]{1,49}$")]
    private static partial Regex ProviderName();

    public static RouteGroupBuilder MapIntegrations(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/integrations", async (Caller who, IIntegrationVault vault, DemoOptions demo, CancellationToken ct) =>
        {
            // With the demo on (Development only, ST-090) the demo is what a publish reaches, so it is
            // all the list names: a ConnectWise row beside it would say a note goes somewhere it does not.
            if (demo.Demo)
            {
                return Results.Ok(new IntegrationsResponse(DemoRows));
            }

            var rows = await vault.ListAsync(who.TenantId, ct).ConfigureAwait(false);
            return Results.Ok(new IntegrationsResponse([.. rows.Select(Masked)]));
        })
        .WithName("ListIntegrations")
        .WithSummary("The tenant's integrations, with each credential's last four characters and nothing more.");

        group.MapPut("/integrations/{provider}", async (string provider, StoreIntegrationRequest request, Caller who, IIntegrationVault vault, ScreenTailContext db, HuduCompanyCache companies, ProviderNetworkOptions network, TimeProvider time, CancellationToken ct) =>
        {
            if (!ProviderName().IsMatch(provider))
            {
                return Results.BadRequest(new { error = "bad_provider", message = "A provider is a short lowercase name, like connectwise or hudu." });
            }

            // The demo's names (ST-090). A credential stored under one would list a real tenant's
            // integration as the demo, in every environment.
            if (DemoNames.IsReserved(provider))
            {
                return Results.BadRequest(new { error = "reserved_provider", message = "Names starting with demo- are kept for the Development demo. Connect connectwise or hudu." });
            }

            if (!IntegrationVault.IsHttpsSite(request.SiteUrl))
            {
                return Results.BadRequest(new { error = "bad_site_url", message = "The site URL must be an absolute https address, with no user name or password in it." });
            }

            var site = new Uri(request.SiteUrl.Trim(), UriKind.Absolute);

            // The backend calls this site, so one that is plainly this machine or a private network is
            // refused here in words. A name that resolves to one is refused when the call connects.
            if (!network.AllowPrivateAddresses && PublicNetwork.IsPlainlyPrivate(site))
            {
                return Results.BadRequest(new { error = "private_site_url", message = "The site URL must be the provider's public address. ScreenTail does not call this machine or a private network." });
            }

            if (string.IsNullOrWhiteSpace(request.Secret))
            {
                return Results.BadRequest(new { error = "empty_secret", message = "The credential is empty." });
            }

            if (!vault.IsConfigured)
            {
                // True and actionable, the way a missing drafting key is: the deployment has no master
                // key, so there is nowhere safe to put this.
                return Results.Json(new { error = "not_configured", message = "This deployment has no vault master key, so credentials cannot be stored." }, statusCode: StatusCodes.Status501NotImplemented);
            }

            if (provider != Hudu)
            {
                await vault.StoreAsync(who.TenantId, provider, request.SiteUrl.Trim(), request.Secret, ct).ConfigureAwait(false);
                return Results.NoContent();
            }

            // Hudu's company ids mean a customer only on the site that issued them. Moved to another site
            // (or connected with nothing before it), the mappings made on the old one are forgotten in the
            // same transaction as the new credential, so no publish can see the new site with the old
            // decisions. A new key for the same site keeps them: the same Hudu numbers its companies the
            // same way whoever asks (2026-10-12 review, S-71).
            await using (var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                var previous = await db.Integrations.AsNoTracking()
                    .Where(i => i.TenantId == who.TenantId && i.Provider == Hudu)
                    .Select(i => i.SiteUrl)
                    .SingleOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (previous is null || HuduProviderFactory.BaseAddressFor(previous) != HuduProviderFactory.BaseAddressFor(request.SiteUrl))
                {
                    _ = await new CompanyMappings(db, who.TenantId, time).ForgetAllAsync(ct).ConfigureAwait(false);
                }

                await vault.StoreAsync(who.TenantId, provider, request.SiteUrl.Trim(), request.Secret, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }

            // The list is asked for again with the new key, whatever the site (T-214).
            companies.Forget(HuduCompanyCache.KeyFor(who.TenantId));
            return Results.NoContent();
        })
        .WithName("StoreIntegration")
        .WithSummary("Stores or replaces the tenant's credential for a provider. It is never returned.");

        group.MapDelete("/integrations/{provider}", async (string provider, Caller who, IIntegrationVault vault, ScreenTailContext db, HuduCompanyCache companies, TimeProvider time, CancellationToken ct) =>
        {
            if (provider != Hudu)
            {
                return await vault.RemoveAsync(who.TenantId, provider, ct).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.NotFound();
            }

            // Removing Hudu is the person saying these decisions no longer hold: the mappings go with the
            // credential, in one transaction, and the cached list with them (S-71, T-214).
            bool removed;
            await using (var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                removed = await vault.RemoveAsync(who.TenantId, provider, ct).ConfigureAwait(false);
                if (removed)
                {
                    _ = await new CompanyMappings(db, who.TenantId, time).ForgetAllAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }

            companies.Forget(HuduCompanyCache.KeyFor(who.TenantId));
            return removed ? Results.NoContent() : Results.NotFound();
        })
        .WithName("RemoveIntegration")
        .WithSummary("Forgets the tenant's credential for a provider.");

        group.MapPost("/integrations/{provider}/check", async (string provider, Caller who, ScreenTailContext db, IPsaProviderFactory psa, IDocProviderFactory docs, TimeProvider time, CancellationToken ct) =>
        {
            var row = await db.Integrations.SingleOrDefaultAsync(i => i.TenantId == who.TenantId && i.Provider == provider, ct).ConfigureAwait(false);
            if (row is null)
            {
                return Results.NotFound(new { error = "not_connected", message = $"Connect {provider} first." });
            }

            // The cheapest call that proves the credential, from the provider the vault's row builds.
            // A provider this deployment cannot build (a missing clientId, say) refuses inside CheckAsync
            // with a message naming the setting, which is exactly what the card should show.
            var result = provider switch
            {
                "connectwise" => await CheckAsync(await psa.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false), ct).ConfigureAwait(false),
                "hudu" => await CheckAsync(await docs.ForTenantAsync(who.TenantId, ct).ConfigureAwait(false), ct).ConfigureAwait(false),
                _ => ProviderResult.Failure<bool>(new ProviderError(ProviderErrorKind.Invalid, $"ScreenTail has no {provider} integration to check.", "Remove it, or connect one of ConnectWise or Hudu.")),
            };

            var message = result.Ok ? $"Connected to {row.SiteUrl}." : result.Error!.ToString();
            row.LastCheckedAt = time.GetUtcNow();
            row.LastError = result.Ok ? null : message;
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new IntegrationCheckResponse(result.Ok, message, row.LastCheckedAt.Value));
        })
        .WithName("CheckIntegration")
        .WithSummary("Tests the tenant's credential for a provider and records when and how it went.");

        return group;
    }

    private static async Task<ProviderResult<bool>> CheckAsync(IPsaProvider? provider, CancellationToken ct) =>
        provider is null ? Gone() : await provider.CheckAsync(ct).ConfigureAwait(false);

    private static async Task<ProviderResult<bool>> CheckAsync(IDocProvider? provider, CancellationToken ct) =>
        provider is null ? Gone() : await provider.CheckAsync(ct).ConfigureAwait(false);

    /// <summary>The row exists but the factory found no credential: removed between the two reads, or the vault cannot open it.</summary>
    private static ProviderResult<bool> Gone() =>
        ProviderResult.Failure<bool>(new ProviderError(ProviderErrorKind.Unavailable, "The stored credential could not be opened.", "Enter it again in Settings → Integrations."));

    /// <summary>The demo PSA and knowledge base, named so nobody can take them for ConnectWise or Hudu. No credential, so no last four.</summary>
    private static readonly IReadOnlyList<IntegrationRow> DemoRows =
    [
        new(DemoNames.Psa, DemoNames.Where, string.Empty, null, null, null),
        new(DemoNames.Kb, DemoNames.Where, string.Empty, null, null, null),
    ];

    private static IntegrationRow Masked(Integration row) => new(
        row.Provider,
        row.SiteUrl ?? string.Empty,
        row.SecretHint is { Length: > 0 } hint ? "••••" + hint : string.Empty,
        row.ConnectedAt,
        row.LastCheckedAt,
        row.LastError);
}
