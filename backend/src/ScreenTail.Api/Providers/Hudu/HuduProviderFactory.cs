using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Providers.Hudu;

/// <summary>A documentation provider for one tenant, or null when the tenant has none connected. Never registered as a bare <see cref="IDocProvider"/>.</summary>
public interface IDocProviderFactory
{
    Task<IDocProvider?> ForTenantAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>Builds a <see cref="HuduProvider"/> from the vault's <c>hudu</c> credential (ST-095 with ST-009).</summary>
public sealed class HuduProviderFactory(IIntegrationVault vault, IHttpClientFactory http, HuduOptions options, HuduCompanyCache cache, TimeProvider? time = null) : IDocProviderFactory
{
    public async Task<IDocProvider?> ForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var revealed = await vault.RevealAsync(tenantId, "hudu", ct).ConfigureAwait(false);
        if (revealed is null)
        {
            return null;
        }

        var client = http.CreateClient(nameof(HuduProvider));
        client.BaseAddress = new Uri(revealed.SiteUrl.TrimEnd('/') + "/");
        return new HuduProvider(client, revealed.Secret, options, cache, tenantId.ToString(), time);
    }
}
