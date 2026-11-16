using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Net;

/// <summary>
/// The PSA gateway with the device signed in first (2026-11-15 review).
///
/// Every call through the gateway is something the technician did: pressed Publish, searched for a
/// ticket, opened Integrations. Under local-only the background renewal does not run (INV-8), so after a
/// long idle the access token in hand has run out; each call here asks for a fresh one first, on behalf
/// of the publish, which local-only lets out. With a token that has not run out nothing extra is sent.
/// A sign-in that fails changes nothing: the inner gateway then says the device is not signed in, in the
/// words it always used.
/// </summary>
public sealed class SigningInPsaGateway(IPsaGateway inner, Func<CancellationToken, Task> signIn) : IPsaGateway
{
    private readonly IPsaGateway _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<CancellationToken, Task> _signIn = signIn ?? throw new ArgumentNullException(nameof(signIn));

    /// <summary>Signs in through <paramref name="device"/> on behalf of a publish.</summary>
    public static SigningInPsaGateway For(IPsaGateway inner, DeviceSession device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new SigningInPsaGateway(inner, ct => device.EnsureTokenAsync(EgressPurpose.Publish, ct));
    }

    public async Task<GatewayAnswer<IReadOnlyList<IntegrationInfo>>> IntegrationsAsync(CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.IntegrationsAsync(ct).ConfigureAwait(false);
    }

    public async Task<GatewayAnswer<IReadOnlyList<TicketRow>>> SearchTicketsAsync(string query, CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.SearchTicketsAsync(query, ct).ConfigureAwait(false);
    }

    public async Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.PublishAsync(bundle, ct).ConfigureAwait(false);
    }

    public async Task<GatewayAnswer<CompanyMappingsAnswer>> CompanyMappingsAsync(CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.CompanyMappingsAsync(ct).ConfigureAwait(false);
    }

    public async Task<GatewayAnswer<bool>> MapCompanyAsync(string psaCompany, string docCompanyId, CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.MapCompanyAsync(psaCompany, docCompanyId, ct).ConfigureAwait(false);
    }

    public async Task<GatewayAnswer<IReadOnlyList<IntegrationDetailRow>>> IntegrationDetailsAsync(CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.IntegrationDetailsAsync(ct).ConfigureAwait(false);
    }

    public async Task<GatewayAnswer<bool>> StoreIntegrationAsync(string provider, string siteUrl, string secret, CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.StoreIntegrationAsync(provider, siteUrl, secret, ct).ConfigureAwait(false);
    }

    public async Task<GatewayAnswer<bool>> RemoveIntegrationAsync(string provider, CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.RemoveIntegrationAsync(provider, ct).ConfigureAwait(false);
    }

    public async Task<GatewayAnswer<IntegrationCheckRow>> CheckIntegrationAsync(string provider, CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.CheckIntegrationAsync(provider, ct).ConfigureAwait(false);
    }

    public async Task<GatewayAnswer<bool>> UnmapCompanyAsync(string psaCompany, CancellationToken ct = default)
    {
        await _signIn(ct).ConfigureAwait(false);
        return await _inner.UnmapCompanyAsync(psaCompany, ct).ConfigureAwait(false);
    }
}
