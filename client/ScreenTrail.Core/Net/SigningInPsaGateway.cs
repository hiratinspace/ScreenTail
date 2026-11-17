using ScreenTrail.Shared.Ipc;

namespace ScreenTrail.Core.Net;

/// <summary>
/// The PSA gateway with the device signed in first (2026-11-15 review).
///
/// Every call through the gateway is something the technician did: pressed Publish, searched for a
/// ticket, opened Integrations. Under local-only the background renewal does not run (INV-8), so after a
/// long idle the access token in hand has run out; each call here asks for a fresh one first, on behalf
/// of the publish, which local-only lets out. With a token that has not run out nothing extra is sent.
///
/// A sign-in that fails for an activated device answers with the sign-in's own reason (the backend did
/// not answer, the device was revoked) rather than letting the gateway say the device is not enrolled,
/// which it is. A device that was never activated still hears the gateway's own words.
/// </summary>
/// <param name="signIn">Signs in; returns the refusal to answer with, or null to go on to the gateway.</param>
public sealed class SigningInPsaGateway(IPsaGateway inner, Func<CancellationToken, Task<string?>> signIn) : IPsaGateway
{
    private readonly IPsaGateway _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<CancellationToken, Task<string?>> _signIn = signIn ?? throw new ArgumentNullException(nameof(signIn));

    /// <summary>Signs in through <paramref name="device"/> on behalf of a publish.</summary>
    public static SigningInPsaGateway For(IPsaGateway inner, DeviceSession device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new SigningInPsaGateway(inner, async ct =>
        {
            var signedIn = await device.SignInAsync(EgressPurpose.Publish, ct).ConfigureAwait(false);
            return signedIn.Token is null && device.Activated ? signedIn.Refusal : null;
        });
    }

    public Task<GatewayAnswer<IReadOnlyList<IntegrationInfo>>> IntegrationsAsync(CancellationToken ct = default) =>
        ThenAsync(_inner.IntegrationsAsync, ct);

    public Task<GatewayAnswer<IReadOnlyList<TicketRow>>> SearchTicketsAsync(string query, CancellationToken ct = default) =>
        ThenAsync(c => _inner.SearchTicketsAsync(query, c), ct);

    public Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default) =>
        ThenAsync(c => _inner.PublishAsync(bundle, c), ct);

    public Task<GatewayAnswer<CompanyMappingsAnswer>> CompanyMappingsAsync(CancellationToken ct = default) =>
        ThenAsync(_inner.CompanyMappingsAsync, ct);

    public Task<GatewayAnswer<bool>> MapCompanyAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) =>
        ThenAsync(c => _inner.MapCompanyAsync(psaCompany, docCompanyId, c), ct);

    public Task<GatewayAnswer<IReadOnlyList<IntegrationDetailRow>>> IntegrationDetailsAsync(CancellationToken ct = default) =>
        ThenAsync(_inner.IntegrationDetailsAsync, ct);

    public Task<GatewayAnswer<bool>> StoreIntegrationAsync(string provider, string siteUrl, string secret, CancellationToken ct = default) =>
        ThenAsync(c => _inner.StoreIntegrationAsync(provider, siteUrl, secret, c), ct);

    public Task<GatewayAnswer<bool>> RemoveIntegrationAsync(string provider, CancellationToken ct = default) =>
        ThenAsync(c => _inner.RemoveIntegrationAsync(provider, c), ct);

    public Task<GatewayAnswer<IntegrationCheckRow>> CheckIntegrationAsync(string provider, CancellationToken ct = default) =>
        ThenAsync(c => _inner.CheckIntegrationAsync(provider, c), ct);

    public Task<GatewayAnswer<bool>> UnmapCompanyAsync(string psaCompany, CancellationToken ct = default) =>
        ThenAsync(c => _inner.UnmapCompanyAsync(psaCompany, c), ct);

    private async Task<GatewayAnswer<T>> ThenAsync<T>(Func<CancellationToken, Task<GatewayAnswer<T>>> call, CancellationToken ct)
    {
        if (await _signIn(ct).ConfigureAwait(false) is { } refusal)
        {
            return GatewayAnswer.Refused<T>(refusal);
        }

        return await call(ct).ConfigureAwait(false);
    }
}
