using Microsoft.AspNetCore.Mvc;
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
/// <param name="Secret">The credential, as the provider wants it: for ConnectWise the Basic-auth string, for Hudu the API key. Stored sealed, shown as its last four.</param>
public sealed record StoreIntegrationRequest(string SiteUrl, string Secret);

/// <param name="Secret">The last four characters behind bullets. Never more.</param>
public sealed record IntegrationRow(string Provider, string SiteUrl, string Secret, DateTimeOffset? ConnectedAt, DateTimeOffset? LastCheckedAt, string? LastError);

public sealed record IntegrationsResponse(IReadOnlyList<IntegrationRow> Integrations);

/// <summary>"Test connection" (ST-082): an answer in words either way. A refused credential is a result here, not a 502.</summary>
public sealed record IntegrationCheckResponse(bool Ok, string Message, DateTimeOffset CheckedAt);

/// <param name="LastResult"><c>ok</c>, <c>failed</c>, or null before the first test.</param>
/// <param name="ConnectedBy">Who last connected or replaced it, or null when that was before changes were recorded.</param>
public sealed record AdminIntegration(string Provider, string Name, bool Connected, string? SiteUrl, DateTimeOffset? ConnectedAt, DateTimeOffset? LastCheckedAt, string? LastResult, string? LastError, ChangedBy? ConnectedBy = null);

/// <summary>One recorded change, for the owner. The company fields are set for a mapping change only.</summary>
public sealed record AdminIntegrationChange(DateTimeOffset At, string Provider, string Action, ChangedBy By, string? PsaCompany, string? DocCompanyId, string? DocCompanyName);

/// <param name="Changes">The latest changes to integrations and company mappings, newest first, at most <see cref="IntegrationChanges.Shown"/>.</param>
public sealed record AdminIntegrationsResponse(IReadOnlyList<AdminIntegration> Integrations, IReadOnlyList<AdminIntegrationChange> Changes);

/// <summary>
/// The integrations endpoints (ST-009): a credential goes in and is never read back over HTTP.
///
/// <b>Only the tenant's owner changes a credential</b> (2026-11-15 review, owner decision): connecting,
/// replacing and disconnecting are routes of the web admin, behind <see cref="OwnerPolicy"/> with its
/// cookie and CSRF header. A device lists what is connected (publishing needs it) and may test the
/// credential that is stored, which changes nothing; asked to store or remove one, it is told in words
/// that the owner does that. Every change is recorded for the owner's page (<see cref="IntegrationChanges"/>).
/// </summary>
public static class IntegrationsEndpoint
{
    public const string ConnectWise = "connectwise";

    /// <summary>The documentation platform, whose company mappings and cached list belong to the site it is connected to.</summary>
    public const string Hudu = "hudu";

    /// <summary>The longest credential stored. A ConnectWise Basic-auth string or a Hudu key is far shorter.</summary>
    public const int SecretMax = 4096;

    /// <summary>The longest site address stored, the column's width.</summary>
    public const int SiteUrlMax = 500;

    /// <summary>The largest body a write to these routes reads: a site and a credential fit in far less.</summary>
    public const long BodyLimit = 16 * 1024;

    private static readonly (string Provider, string Name)[] Known = [(ConnectWise, "ConnectWise Manage"), (Hudu, "Hudu")];

    /// <summary>The two providers ScreenTail integrates with, and no other name (2026-11-15 review).</summary>
    public static bool IsKnown(string provider) => provider is ConnectWise or Hudu;

    /// <summary>The refusal of a provider name that is not one of the two.</summary>
    internal static IResult UnknownProvider() =>
        Results.BadRequest(new { error = "bad_provider", message = "ScreenTail connects connectwise and hudu, and no other provider." });

    /// <summary>
    /// What a device is told when it asks to store or remove a credential: the owner does that now, in
    /// the web admin. A stable code, so a client of any age can show the message as it is.
    /// </summary>
    internal static IResult OwnerOnly() => Results.Json(
        new { error = "owner_only", message = "Your ScreenTail owner manages this connection in the web admin. Ask them to connect or change it there." },
        statusCode: StatusCodes.Status403Forbidden);

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

        // The device's PUT and DELETE stay mapped so that a client written before the owner took this
        // over is answered in words rather than with a bare 404 or 405. Neither reads a body.
        group.MapPut("/integrations/{provider}", (string provider, Caller who) => IsKnown(provider) ? OwnerOnly() : UnknownProvider())
            .WithMetadata(new RequestSizeLimitAttribute(BodyLimit))
            .WithName("StoreIntegration")
            .WithSummary("Refused: only the tenant's owner stores a credential, from the web admin.");

        group.MapDelete("/integrations/{provider}", (string provider, Caller who) => IsKnown(provider) ? OwnerOnly() : UnknownProvider())
            .WithName("RemoveIntegration")
            .WithSummary("Refused: only the tenant's owner removes a credential, from the web admin.");

        group.MapPost("/integrations/{provider}/check", (string provider, Caller who, ScreenTailContext db, IPsaProviderFactory psa, IDocProviderFactory docs, TimeProvider time, CancellationToken ct) =>
            CheckAsync(who.TenantId, provider, forOwner: false, db, psa, docs, time, ct))
        .WithName("CheckIntegration")
        .WithSummary("Tests the tenant's stored credential for a provider and records when and how it went.");

        return group;
    }

    /// <summary>The owner's integration routes (ST-099, 2026-11-15 review), mapped on the /v1/admin group behind <see cref="OwnerPolicy"/>.</summary>
    public static RouteGroupBuilder MapAdminIntegrations(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/integrations", async (AdminCaller owner, ScreenTailContext db, CancellationToken ct) =>
        {
            // Never a credential, not even its last four: the owner sees whether it is there, whether it
            // worked, who connected it and what changed since.
            var rows = await db.Integrations.AsNoTracking()
                .Where(i => i.TenantId == owner.TenantId && (i.Provider == ConnectWise || i.Provider == Hudu))
                .Select(i => new { i.Provider, i.SiteUrl, i.ConnectedAt, i.LastCheckedAt, i.LastError })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var changes = await IntegrationChanges.LatestAsync(db, owner.TenantId, ct).ConfigureAwait(false);
            var connected = await IntegrationChanges.LastConnectedAsync(db, owner.TenantId, rows.Select(r => r.Provider), ct).ConfigureAwait(false);
            var by = await IntegrationChanges.NamesAsync(db, owner.TenantId, [.. changes, .. connected.Values], ct).ConfigureAwait(false);

            return Results.Ok(new AdminIntegrationsResponse(
                [.. Known.Select(k =>
                {
                    var row = rows.SingleOrDefault(r => r.Provider == k.Provider);
                    return row is null
                        ? new AdminIntegration(k.Provider, k.Name, false, null, null, null, null, null)
                        : new AdminIntegration(
                            k.Provider,
                            k.Name,
                            true,
                            row.SiteUrl,
                            row.ConnectedAt,
                            row.LastCheckedAt,
                            row.LastCheckedAt is null ? null : row.LastError is null ? "ok" : "failed",
                            VaultRefusal.ForOwner(row.LastError),
                            connected.TryGetValue(k.Provider, out var last) ? by(last) : null);
                })],
                [.. changes.Select(c => new AdminIntegrationChange(c.At, c.Provider, c.Action, by(c), c.PsaCompany, c.DocCompanyId, c.DocCompanyName))]));
        })
        .WithName("AdminIntegrations")
        .WithSummary("Whether ConnectWise and Hudu are connected, who connected them, how their last test went, and the latest changes. No credentials.");

        group.MapPut("/integrations/{provider}", (string provider, StoreIntegrationRequest request, AdminCaller owner, IIntegrationVault vault, ScreenTailContext db, HuduCompanyCache companies, ProviderNetworkOptions network, DemoOptions demo, TimeProvider time, CancellationToken ct) =>
            StoreAsync(owner.TenantId, provider, request, ChangeActor.Owner(owner.OwnerId), vault, db, companies, network, demo, time, ct))
        .WithMetadata(new RequestSizeLimitAttribute(BodyLimit))
        .WithName("AdminStoreIntegration")
        .WithSummary("Stores or replaces the tenant's credential for ConnectWise or Hudu. It is never returned.");

        group.MapDelete("/integrations/{provider}", (string provider, AdminCaller owner, IIntegrationVault vault, ScreenTailContext db, HuduCompanyCache companies, DemoOptions demo, TimeProvider time, CancellationToken ct) =>
            RemoveAsync(owner.TenantId, provider, ChangeActor.Owner(owner.OwnerId), vault, db, companies, demo, time, ct))
        .WithName("AdminRemoveIntegration")
        .WithSummary("Forgets the tenant's credential for ConnectWise or Hudu.");

        group.MapPost("/integrations/{provider}/check", (string provider, AdminCaller owner, ScreenTailContext db, IPsaProviderFactory psa, IDocProviderFactory docs, TimeProvider time, CancellationToken ct) =>
            CheckAsync(owner.TenantId, provider, forOwner: true, db, psa, docs, time, ct))
        .WithName("AdminCheckIntegration")
        .WithSummary("Tests the tenant's stored credential for ConnectWise or Hudu and records when and how it went.");

        return group;
    }

    private static async Task<IResult> StoreAsync(Guid tenantId, string provider, StoreIntegrationRequest request, ChangeActor by, IIntegrationVault vault, ScreenTailContext db, HuduCompanyCache companies, ProviderNetworkOptions network, DemoOptions demo, TimeProvider time, CancellationToken ct)
    {
        if (demo.Demo)
        {
            return DemoOn();
        }

        if (!IsKnown(provider))
        {
            return UnknownProvider();
        }

        if (request.SiteUrl is { Length: > SiteUrlMax })
        {
            return Results.BadRequest(new { error = "site_url_too_long", message = $"The site URL can be up to {SiteUrlMax} characters." });
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

        if (request.Secret.Length > SecretMax)
        {
            return Results.BadRequest(new { error = "secret_too_long", message = $"The credential can be up to {SecretMax} characters." });
        }

        if (!vault.IsConfigured)
        {
            // True and actionable, the way a missing drafting key is: the deployment has no master
            // key, so there is nowhere safe to put this.
            return Results.Json(new { error = "not_configured", message = "This deployment has no vault master key, so credentials cannot be stored." }, statusCode: StatusCodes.Status501NotImplemented);
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            var previous = await db.Integrations.AsNoTracking()
                .Where(i => i.TenantId == tenantId && i.Provider == provider)
                .Select(i => new { i.SiteUrl })
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var now = time.GetUtcNow();

            // Hudu's company ids mean a customer only on the site that issued them. Moved to another site
            // (or connected with nothing before it), the mappings made on the old one are forgotten in the
            // same transaction as the new credential, so no publish can see the new site with the old
            // decisions. A new key for the same site keeps them: the same Hudu numbers its companies the
            // same way whoever asks (2026-10-12 review, S-71).
            if (provider == Hudu
                && (previous?.SiteUrl is not { } before || HuduProviderFactory.BaseAddressFor(before) != HuduProviderFactory.BaseAddressFor(request.SiteUrl))
                && await new CompanyMappings(db, tenantId, time).ForgetAllAsync(ct).ConfigureAwait(false) > 0)
            {
                IntegrationChanges.Record(db, tenantId, provider, IntegrationChanges.MappingsCleared, by, now);
            }

            // A microsecond later (the database's precision), so the page lists the two in the order they happened.
            IntegrationChanges.Record(db, tenantId, provider, previous is null ? IntegrationChanges.Connected : IntegrationChanges.Replaced, by, now.AddTicks(10));

            // The vault saves the context it shares with this request, which writes the change with it.
            await vault.StoreAsync(tenantId, provider, request.SiteUrl.Trim(), request.Secret, ct).ConfigureAwait(false);
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        if (provider == Hudu)
        {
            // The list is asked for again with the new key, whatever the site (T-214).
            companies.Forget(HuduCompanyCache.KeyFor(tenantId));
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RemoveAsync(Guid tenantId, string provider, ChangeActor by, IIntegrationVault vault, ScreenTailContext db, HuduCompanyCache companies, DemoOptions demo, TimeProvider time, CancellationToken ct)
    {
        if (demo.Demo)
        {
            return DemoOn();
        }

        if (!IsKnown(provider))
        {
            return UnknownProvider();
        }

        // Removing Hudu is the person saying these decisions no longer hold: the mappings go with the
        // credential, in one transaction, and the cached list with them (S-71, T-214).
        bool removed;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            removed = await vault.RemoveAsync(tenantId, provider, ct).ConfigureAwait(false);
            if (removed)
            {
                var now = time.GetUtcNow();
                IntegrationChanges.Record(db, tenantId, provider, IntegrationChanges.Disconnected, by, now);
                if (provider == Hudu && await new CompanyMappings(db, tenantId, time).ForgetAllAsync(ct).ConfigureAwait(false) > 0)
                {
                    IntegrationChanges.Record(db, tenantId, provider, IntegrationChanges.MappingsCleared, by, now.AddTicks(10));
                }

                _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        if (provider == Hudu)
        {
            companies.Forget(HuduCompanyCache.KeyFor(tenantId));
        }

        return removed ? Results.NoContent() : Results.NotFound(new { error = "not_connected", message = "There is nothing connected to remove." });
    }

    private static async Task<IResult> CheckAsync(Guid tenantId, string provider, bool forOwner, ScreenTailContext db, IPsaProviderFactory psa, IDocProviderFactory docs, TimeProvider time, CancellationToken ct)
    {
        if (!IsKnown(provider))
        {
            return UnknownProvider();
        }

        var row = await db.Integrations.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Provider == provider, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Results.NotFound(new { error = "not_connected", message = $"Connect {provider} first." });
        }

        // The cheapest call that proves the credential, from the provider the vault's row builds.
        // A provider this deployment cannot build (a missing clientId, say) refuses inside CheckAsync
        // with a message naming the setting, which is exactly what the card should show.
        var result = provider == ConnectWise
            ? await CheckAsync(await psa.ForTenantAsync(tenantId, ct).ConfigureAwait(false), ct).ConfigureAwait(false)
            : await CheckAsync(await docs.ForTenantAsync(tenantId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        var message = result.Ok ? $"Connected to {row.SiteUrl}." : result.Error!.ToString();
        row.LastCheckedAt = time.GetUtcNow();
        row.LastError = result.Ok ? null : message;
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // Stored as a technician reads it (devices list it); the owner is told what to do, not to ask the owner.
        return Results.Ok(new IntegrationCheckResponse(result.Ok, forOwner ? VaultRefusal.ForOwner(message)! : message, row.LastCheckedAt.Value));
    }

    /// <summary>
    /// The refusal of any change to a tenant's credentials or company mappings while the demo is on
    /// (ST-090, review 2026-10-27). Settings shows the demo then, so a change would land on rows nobody can
    /// see: a credential stored or removed, or a mapping overwritten with a demo company or deleted.
    /// </summary>
    internal static IResult DemoOn() => Results.Json(
        new { error = "demo_on", message = $"The demo PSA is on ({DemoOptions.Setting}), so integrations and company mappings cannot be changed. Switch it off and restart the backend first." },
        statusCode: StatusCodes.Status409Conflict);

    private static async Task<ProviderResult<bool>> CheckAsync(IPsaProvider? provider, CancellationToken ct) =>
        provider is null ? Gone() : await provider.CheckAsync(ct).ConfigureAwait(false);

    private static async Task<ProviderResult<bool>> CheckAsync(IDocProvider? provider, CancellationToken ct) =>
        provider is null ? Gone() : await provider.CheckAsync(ct).ConfigureAwait(false);

    /// <summary>The row exists but the factory found no credential: removed between the two reads, or the vault cannot open it.</summary>
    private static ProviderResult<bool> Gone() =>
        ProviderResult.Failure<bool>(VaultRefusal.Error);

    /// <summary>The demo PSA and knowledge base, named so nobody can take them for ConnectWise or Hudu. No credential, so no last four.</summary>
    private static readonly IReadOnlyList<IntegrationRow> DemoRows =
    [
        new(DemoNames.Psa, DemoNames.Where, string.Empty, null, null, null),
        new(DemoNames.Kb, DemoNames.Where, string.Empty, null, null, null),
    ];

    private static IntegrationRow Masked(IntegrationListing row) => new(
        row.Provider,
        row.SiteUrl ?? string.Empty,
        row.SecretHint is { Length: > 0 } hint ? "••••" + hint : string.Empty,
        row.ConnectedAt,
        row.LastCheckedAt,
        row.LastError);
}
