using System.Security.Claims;
using System.Text.RegularExpressions;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Endpoints;

/// <param name="SiteUrl">Where the provider lives for this tenant; not secret, shown in full.</param>
/// <param name="Secret">The credential, as the provider wants it — for ConnectWise the Basic-auth string, for Hudu the API key. Stored sealed, shown as its last four.</param>
public sealed record StoreIntegrationRequest(string SiteUrl, string Secret);

/// <param name="Secret">The last four characters behind bullets. Never more.</param>
public sealed record IntegrationRow(string Provider, string SiteUrl, string Secret, DateTimeOffset? ConnectedAt, DateTimeOffset? LastCheckedAt, string? LastError);

public sealed record IntegrationsResponse(IReadOnlyList<IntegrationRow> Integrations);

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

        group.MapGet("/integrations", async (ClaimsPrincipal caller, ScreenTailContext db, IIntegrationVault vault, CancellationToken ct) =>
        {
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            var rows = await vault.ListAsync(who.TenantId, ct).ConfigureAwait(false);
            return Results.Ok(new IntegrationsResponse([.. rows.Select(Masked)]));
        })
        .WithName("ListIntegrations")
        .WithSummary("The tenant's integrations, with each credential's last four characters and nothing more.");

        group.MapPut("/integrations/{provider}", async (string provider, StoreIntegrationRequest request, ClaimsPrincipal caller, ScreenTailContext db, IIntegrationVault vault, CancellationToken ct) =>
        {
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            if (!ProviderName().IsMatch(provider))
            {
                return Results.BadRequest(new { error = "bad_provider", message = "A provider is a short lowercase name, like connectwise or hudu." });
            }

            if (!Uri.TryCreate(request.SiteUrl, UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps)
            {
                return Results.BadRequest(new { error = "bad_site_url", message = "The site URL must be an absolute https address." });
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

        group.MapDelete("/integrations/{provider}", async (string provider, ClaimsPrincipal caller, ScreenTailContext db, IIntegrationVault vault, CancellationToken ct) =>
        {
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            return await vault.RemoveAsync(who.TenantId, provider, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        })
        .WithName("RemoveIntegration")
        .WithSummary("Forgets the tenant's credential for a provider.");

        return group;
    }

    private static IntegrationRow Masked(Integration row) => new(
        row.Provider,
        row.SiteUrl ?? string.Empty,
        row.SecretHint is { Length: > 0 } hint ? "••••" + hint : string.Empty,
        row.ConnectedAt,
        row.LastCheckedAt,
        row.LastError);
}
