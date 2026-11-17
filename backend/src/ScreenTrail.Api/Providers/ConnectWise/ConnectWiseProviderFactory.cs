using ScreenTrail.Api.Vault;

namespace ScreenTrail.Api.Providers.ConnectWise;

/// <summary>
/// A PSA provider for one tenant, or null when the tenant has none connected. Never registered as an
/// <see cref="IPsaProvider"/> in the container: there is no provider without a tenant's credential, and
/// a container-level one would be a fake by definition.
/// </summary>
public interface IPsaProviderFactory
{
    Task<IPsaProvider?> ForTenantAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>Builds a <see cref="ConnectWiseProvider"/> from the credential the vault holds for the tenant (ST-091 with ST-009).</summary>
public sealed class ConnectWiseProviderFactory(IIntegrationVault vault, IHttpClientFactory http, ConnectWiseOptions options, TimeProvider? time = null) : IPsaProviderFactory
{
    public const string ApiPath = "v4_6_release/apis/3.0/";

    public async Task<IPsaProvider?> ForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        RevealedIntegration? revealed;
        try
        {
            revealed = await vault.RevealAsync(tenantId, "connectwise", ct).ConfigureAwait(false);
        }
        catch (VaultRowRefusedException)
        {
            // A row the vault will not open answers every call in words, not with a 500 (2026-11-15 review).
            return new RefusedPsaProvider("connectwise");
        }

        if (revealed is null)
        {
            return null;
        }

        var client = http.CreateClient(nameof(ConnectWiseProvider));
        client.BaseAddress = new Uri(revealed.SiteUrl.TrimEnd('/') + "/" + ApiPath);
        return new ConnectWiseProvider(client, revealed.Secret, options, time);
    }
}
