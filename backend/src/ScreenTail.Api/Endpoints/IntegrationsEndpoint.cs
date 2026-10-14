using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Hudu;
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
    [GeneratedRegex("^[a-z][a-z0-9-]{1,49}$")]
    private static partial Regex ProviderName();

    public static RouteGroupBuilder MapIntegrations(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/integrations", async (Caller who, IIntegrationVault vault, CancellationToken ct) =>
        {
            var rows = await vault.ListAsync(who.TenantId, ct).ConfigureAwait(false);
            return Results.Ok(new IntegrationsResponse([.. rows.Select(Masked)]));
        })
        .WithName("ListIntegrations")
        .WithSummary("The tenant's integrations, with each credential's last four characters and nothing more.");

        group.MapPut("/integrations/{provider}", async (string provider, StoreIntegrationRequest request, Caller who, IIntegrationVault vault, ProviderNetworkOptions network, CancellationToken ct) =>
        {
            if (!ProviderName().IsMatch(provider))
            {
                return Results.BadRequest(new { error = "bad_provider", message = "A provider is a short lowercase name, like connectwise or hudu." });
            }

            if (!Uri.TryCreate(request.SiteUrl, UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps)
            {
                return Results.BadRequest(new { error = "bad_site_url", message = "The site URL must be an absolute https address." });
            }

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

            await vault.StoreAsync(who.TenantId, provider, request.SiteUrl.Trim(), request.Secret, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("StoreIntegration")
        .WithSummary("Stores or replaces the tenant's credential for a provider. It is never returned.");

        group.MapDelete("/integrations/{provider}", async (string provider, Caller who, IIntegrationVault vault, CancellationToken ct) =>
        {
            return await vault.RemoveAsync(who.TenantId, provider, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
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

    private static IntegrationRow Masked(Integration row) => new(
        row.Provider,
        row.SiteUrl ?? string.Empty,
        row.SecretHint is { Length: > 0 } hint ? "••••" + hint : string.Empty,
        row.ConnectedAt,
        row.LastCheckedAt,
        row.LastError);
}
