using ScreenTail.Core.Review.Publish;

namespace ScreenTail.Core.Settings;

/// <param name="SecretHint">The last four characters behind bullets, as the backend shows them. Never more.</param>
public sealed record IntegrationDetail(string Provider, string SiteUrl, string SecretHint, DateTimeOffset? ConnectedAt, DateTimeOffset? LastCheckedAt, string? LastError);

/// <summary>
/// A provider the backend lists that has no card of its own: the backend's Development demo (ST-090), or
/// one this version does not know yet. Shown by name and site; there is nothing here to change or test.
/// </summary>
/// <param name="LastError">The backend's last word on it, said as this page says it (<see cref="IntegrationNames.OnThisPage"/>).</param>
public sealed record OtherIntegration(string Provider, string Title, string SiteUrl, string? LastError = null)
{
    /// <summary>
    /// Only the demo is called connected: it answers from the backend's memory, so it is. Of any other
    /// provider this version knows nothing, and cannot say it works (review 2026-10-27).
    /// </summary>
    public bool Connected => IntegrationNames.IsDemo(Provider);

    public string Status => Connected ? "Connected" : "Not supported by this version";
}

/// <summary>What each provider is called on screen. The backend names providers; the words are the client's.</summary>
public static class IntegrationNames
{
    /// <summary>The backend's Development demo PSA (ST-090). Never registered outside Development.</summary>
    public const string DemoPsa = "demo-psa";

    public const string DemoKb = "demo-kb";

    /// <summary>The title a provider goes by: a real product's name only for the real product, and the provider's own name for one this version does not know.</summary>
    public static string Title(string provider) => provider switch
    {
        IntegrationCard.ConnectWiseProvider => "ConnectWise Manage",
        IntegrationCard.HuduProvider => "Hudu",
        DemoPsa => "Demo PSA (not ConnectWise)",
        DemoKb => "Demo knowledge base (not Hudu)",
        _ => provider,
    };

    /// <summary>
    /// The name a sentence uses for the provider: "Open in ConnectWise", "Retry Hudu". The demo's are
    /// called what they are, never a real product's name; one this version does not know goes by its own.
    /// </summary>
    public static string InSentence(string provider) => provider switch
    {
        IntegrationCard.ConnectWiseProvider => "ConnectWise",
        IntegrationCard.HuduProvider => "Hudu",
        DemoPsa => "the demo PSA",
        DemoKb => "the demo knowledge base",
        _ => provider,
    };

    /// <summary>A PSA, which takes the ticket note and the time entry.</summary>
    public static bool IsPsa(string provider) => provider is IntegrationCard.ConnectWiseProvider or DemoPsa;

    /// <summary>A documentation platform, which takes the KB article.</summary>
    public static bool IsDocumentation(string provider) => provider is IntegrationCard.HuduProvider or DemoKb;

    /// <summary>One of the backend's demo providers, which publish nowhere a customer or colleague can read.</summary>
    public static bool IsDemo(string provider) =>
        provider is not null && provider.StartsWith("demo-", StringComparison.Ordinal);

    private const string Pointer = " in Settings → Integrations";

    private const string AskOwner = "Ask your ScreenTail owner to ";

    /// <summary>
    /// The backend's words, as they read on the Integrations page itself (Spec v0.6.8, v0.6.15). The
    /// backend asks the technician to ask the ScreenTail owner, in the web admin, and those words are kept.
    /// A check stored before integration credentials became the owner's can still end by sending the
    /// technician to "Settings → Integrations" to fix the credential, where nothing can be changed. So that
    /// sentence asks the owner instead: "Update it in Settings → Integrations." reads "Ask your ScreenTail
    /// owner to update it in the web admin."
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(message))]
    public static string? OnThisPage(string? message)
    {
        if (message is null)
        {
            return null;
        }

        var at = message.IndexOf(Pointer, StringComparison.Ordinal);
        while (at >= 0)
        {
            // The pointer ends an instruction ("Check the site address in Settings → Integrations."): that
            // sentence, from its start, becomes the owner's to do.
            var start = message.LastIndexOf(". ", at, StringComparison.Ordinal);
            start = start < 0 ? 0 : start + 2;
            var instruction = message[start..at];
            var asked = instruction.Length == 0
                ? "Ask your ScreenTail owner to fix it in the web admin"
                : AskOwner + char.ToLowerInvariant(instruction[0]) + instruction[1..] + " in the web admin";
            message = string.Concat(message.AsSpan(0, start), asked, message.AsSpan(at + Pointer.Length));
            at = message.IndexOf(Pointer, start + asked.Length, StringComparison.Ordinal);
        }

        return message;
    }
}

/// <summary>"Test connection"'s answer, in words either way (Spec §4: what happened, then what to do).</summary>
public sealed record CheckOutcome(bool Ok, string Message);

/// <param name="Confidence"><c>exact</c>, <c>likely</c> or <c>manual</c>: how the backend decided it.</param>
public sealed record CompanyMappingEntry(string PsaCompany, string DocCompanyId, string DocCompanyName, string Confidence);

/// <param name="Companies">The documentation platform's companies, to map to.</param>
/// <param name="Mappings">What this tenant has decided so far.</param>
public sealed record CompanyMappingsPage(IReadOnlyList<CompanyChoice> Companies, IReadOnlyList<CompanyMappingEntry> Mappings);

/// <summary>
/// What Settings → Integrations asks the service for (ST-082). Over the pipe in the running application
/// (<see cref="PipeIntegrations"/>); a fake in tests. A null list means the service did not answer; a
/// string from an action is the reason it was refused, null when it was done. There is no way to store or
/// remove a credential here: integration credentials can only be changed by the tenant owner, in the web
/// admin.
/// </summary>
public interface IIntegrationsGateway
{
    Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default);

    Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default);

    Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default);

    Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default);

    Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default);
}

/// <summary>
/// One provider's card (Spec §5 S7), read only. Integration credentials can only be changed by the
/// tenant owner, in the web admin, so the card shows whether the provider is connected, its site, the
/// credential's last four as the backend shows them, when it was last checked and what went wrong, and
/// offers Test connection, which checks the credential the backend holds. Nothing secret is ever here.
/// </summary>
public sealed class IntegrationCard
{
    public const string ConnectWiseProvider = "connectwise";

    public const string HuduProvider = "hudu";

    /// <summary>A connected card's line under its status: who changes it, and where.</summary>
    public const string OwnerManages = "Your owner manages this connection in the web admin.";

    /// <summary>A card with nothing connected: who connects it, and where.</summary>
    public const string OwnerConnects = "Not connected. Your owner connects it in the web admin.";

    private readonly IIntegrationsGateway _gateway;

    internal IntegrationCard(string provider, IIntegrationsGateway gateway)
    {
        Provider = provider;
        _gateway = gateway;
    }

    public string Provider { get; }

    public string Title => IntegrationNames.Title(Provider);

    public bool IsConnectWise => Provider == ConnectWiseProvider;

    public bool Connected { get; private set; }

    /// <summary>The site as the backend has it; empty with nothing connected.</summary>
    public string SiteUrl { get; private set; } = string.Empty;

    /// <summary>"••••1234" with a credential; empty without one.</summary>
    public string SecretHint { get; private set; } = string.Empty;

    public DateTimeOffset? LastCheckedAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>The last Test connection, or null before one; its words go in <see cref="CheckResult"/>.</summary>
    public bool? CheckOk { get; private set; }

    public string? CheckResult { get; private set; }

    public bool Busy { get; private set; }

    /// <summary>Test connection checks what the backend holds, so there is something to check only once connected.</summary>
    public bool CanCheck => Connected && !Busy;

    public string Status => Connected
        ? LastError is null
            ? LastCheckedAt is { } at ? $"Connected · checked {at.ToLocalTime():HH:mm}" : "Connected"
            : "Connected · the last check failed"
        : "Not connected";

    /// <summary>Who changes the connection, and where: the tenant owner, in the web admin.</summary>
    public string OwnerLine => Connected ? OwnerManages : OwnerConnects;

    internal void Load(IntegrationDetail? detail)
    {
        Connected = detail is not null;
        SiteUrl = detail?.SiteUrl ?? string.Empty;
        SecretHint = detail?.SecretHint ?? string.Empty;
        LastCheckedAt = detail?.LastCheckedAt;
        LastError = IntegrationNames.OnThisPage(detail?.LastError);
        if (!Connected)
        {
            // The owner disconnected it: a check of a credential that is gone says nothing now.
            CheckOk = null;
            CheckResult = null;
        }
    }

    public async Task<CheckOutcome> CheckAsync(CancellationToken ct = default)
    {
        Busy = true;
        try
        {
            var outcome = await _gateway.CheckAsync(Provider, ct).ConfigureAwait(false);
            CheckOk = outcome.Ok;
            CheckResult = IntegrationNames.OnThisPage(outcome.Message);
            LastError = outcome.Ok ? null : CheckResult;
            return outcome;
        }
        finally
        {
            Busy = false;
        }
    }

}

/// <summary>
/// Settings → Integrations (ST-082, Spec §5 S7): a read-only card per provider and the company mapping
/// table, which technicians still keep.
/// The mapping table exists only with Hudu connected, because it maps into Hudu's companies; without it
/// there is nothing to list and the service is not asked.
/// </summary>
public sealed class IntegrationsPanel
{
    private readonly IIntegrationsGateway _gateway;

    public IntegrationsPanel(IIntegrationsGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        ConnectWise = new IntegrationCard(IntegrationCard.ConnectWiseProvider, gateway);
        Hudu = new IntegrationCard(IntegrationCard.HuduProvider, gateway);
    }

    public IntegrationCard ConnectWise { get; }

    public IntegrationCard Hudu { get; }

    public IReadOnlyList<IntegrationCard> Cards => [ConnectWise, Hudu];

    /// <summary>What the backend lists beyond the two cards, by name: the Development demo (ST-090) or a provider this version does not know.</summary>
    public IReadOnlyList<OtherIntegration> Others { get; private set; } = [];

    public IReadOnlyList<CompanyChoice> Companies { get; private set; } = [];

    public IReadOnlyList<CompanyMappingEntry> Mappings { get; private set; } = [];

    public bool CanMapCompanies => Hudu.Connected;

    /// <summary>Why the screen is not showing the truth, or null: the service did not answer, or the mappings could not be read.</summary>
    public string? Notice { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Notice = null;
        var rows = await _gateway.ListAsync(ct).ConfigureAwait(false);
        if (rows is null)
        {
            Notice = "The capture service did not answer, so this screen may be out of date.";
        }

        foreach (var card in Cards)
        {
            card.Load(rows?.FirstOrDefault(r => r.Provider == card.Provider));
        }

        Others = rows is null
            ? []
            : [.. rows.Where(r => !Cards.Any(c => c.Provider == r.Provider)).Select(r => new OtherIntegration(r.Provider, IntegrationNames.Title(r.Provider), r.SiteUrl, IntegrationNames.OnThisPage(r.LastError)))];

        await RefreshMappingsAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default)
    {
        var refusal = await _gateway.MapAsync(psaCompany, docCompanyId, ct).ConfigureAwait(false);
        if (refusal is null)
        {
            await RefreshMappingsAsync(ct).ConfigureAwait(false);
        }

        return refusal;
    }

    public async Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default)
    {
        var refusal = await _gateway.UnmapAsync(psaCompany, ct).ConfigureAwait(false);
        if (refusal is null)
        {
            await RefreshMappingsAsync(ct).ConfigureAwait(false);
        }

        return refusal;
    }

    public async Task RefreshMappingsAsync(CancellationToken ct = default)
    {
        if (!CanMapCompanies)
        {
            Companies = [];
            Mappings = [];
            return;
        }

        var page = await _gateway.MappingsAsync(ct).ConfigureAwait(false);
        if (page is null)
        {
            Notice ??= "The company list could not be read from Hudu. Test the connection.";
            return;
        }

        Companies = [.. page.Companies.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
        Mappings = [.. page.Mappings.OrderBy(m => m.PsaCompany, StringComparer.CurrentCultureIgnoreCase)];
    }
}
