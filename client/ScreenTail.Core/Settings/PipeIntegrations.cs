using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Settings;

/// <summary>
/// Settings → Integrations' gateway, over the pipe (ST-082). The UI never talks to the backend: each
/// call is a command the service answers from the backend, and a refusal comes back as the reason.
/// A question whose answer is an event comes back null when refused; the reason is then fetched with a
/// plain send, which returns the failed result with its words.
/// </summary>
public sealed class PipeIntegrations(CaptureConnection connection) : IIntegrationsGateway
{
    private readonly CaptureConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default)
    {
        var listed = await _connection.RequestAsync<IntegrationDetailsListed>(id => new ListIntegrationsCommand { RequestId = id }, ct).ConfigureAwait(false);
        return listed is null
            ? null
            : [.. listed.Integrations.Select(i => new IntegrationDetail(i.Provider, i.SiteUrl, i.Secret, i.ConnectedAt, i.LastCheckedAt, i.LastError))];
    }

    public async Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default) =>
        Refusal(await _connection.SendAsync(id => new StoreIntegrationCommand { RequestId = id, Provider = provider, SiteUrl = siteUrl, Secret = secret }, ct).ConfigureAwait(false));

    public async Task<string?> RemoveAsync(string provider, CancellationToken ct = default) =>
        Refusal(await _connection.SendAsync(id => new RemoveIntegrationCommand { RequestId = id, Provider = provider }, ct).ConfigureAwait(false));

    public async Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default)
    {
        var checkedEvent = await _connection.RequestAsync<IntegrationChecked>(id => new CheckIntegrationCommand { RequestId = id, Provider = provider }, ct).ConfigureAwait(false);
        if (checkedEvent is not null)
        {
            return new CheckOutcome(checkedEvent.Ok, checkedEvent.Message);
        }

        var result = await _connection.SendAsync(id => new CheckIntegrationCommand { RequestId = id, Provider = provider }, ct).ConfigureAwait(false);
        return new CheckOutcome(false, result.Error ?? "The capture service could not check the connection.");
    }

    public async Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default)
    {
        var listed = await _connection.RequestAsync<CompanyMappingsListed>(id => new GetCompanyMappingsCommand { RequestId = id }, ct).ConfigureAwait(false);
        return listed is null
            ? null
            : new CompanyMappingsPage(
                [.. listed.Companies.Select(c => new CompanyChoice(c.Id, c.Name))],
                [.. listed.Mappings.Select(m => new CompanyMappingEntry(m.PsaCompany, m.DocCompanyId, m.DocCompanyName, m.Confidence))]);
    }

    public async Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) =>
        Refusal(await _connection.SendAsync(id => new MapCompanyCommand { RequestId = id, PsaCompany = psaCompany, DocCompanyId = docCompanyId }, ct).ConfigureAwait(false));

    public async Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default) =>
        Refusal(await _connection.SendAsync(id => new UnmapCompanyCommand { RequestId = id, PsaCompany = psaCompany }, ct).ConfigureAwait(false));

    private static string? Refusal(CommandResult result) => result.Ok ? null : result.Error ?? "The capture service refused.";
}
