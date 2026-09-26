using ScreenTail.Core.Review.Publish;

namespace ScreenTail.Core.Settings;

/// <param name="SecretHint">The last four characters behind bullets, as the backend shows them. Never more.</param>
public sealed record IntegrationDetail(string Provider, string SiteUrl, string SecretHint, DateTimeOffset? ConnectedAt, DateTimeOffset? LastCheckedAt, string? LastError);

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
/// string from an action is the reason it was refused, null when it was done.
/// </summary>
public interface IIntegrationsGateway
{
    Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default);

    /// <summary>Stores or replaces the credential. It is never read back; the list shows its last four.</summary>
    Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default);

    Task<string?> RemoveAsync(string provider, CancellationToken ct = default);

    Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default);

    Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default);

    Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default);

    Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default);
}

/// <summary>
/// One provider's card (Spec §5 S7). Connected or not; the fields open when there is nothing saved and
/// after Change, and close again once a credential is stored. The secret parts live here only between
/// typing and saving: the moment the backend has taken them they are cleared, and the card shows the
/// hint the backend shows.
///
/// ConnectWise's credential is three things the technician copies from three places, joined the way
/// ConnectWise's Basic auth wants them (<c>companyId+publicKey:privateKey</c>); the card takes the
/// three and joins them, because asking for the joined form is asking for a typo.
/// </summary>
public sealed class IntegrationCard
{
    public const string ConnectWiseProvider = "connectwise";

    public const string HuduProvider = "hudu";

    private readonly IIntegrationsGateway _gateway;

    internal IntegrationCard(string provider, IIntegrationsGateway gateway)
    {
        Provider = provider;
        _gateway = gateway;
    }

    public string Provider { get; }

    public string Title => Provider == ConnectWiseProvider ? "ConnectWise Manage" : "Hudu";

    public bool IsConnectWise => Provider == ConnectWiseProvider;

    public bool Connected { get; private set; }

    /// <summary>Fields shown: nothing saved yet, or Change was pressed.</summary>
    public bool Editing { get; private set; } = true;

    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>"••••1234" once saved; empty before.</summary>
    public string SecretHint { get; private set; } = string.Empty;

    public DateTimeOffset? LastCheckedAt { get; private set; }

    public string? LastError { get; private set; }

    // ConnectWise's three parts. Cleared the moment they are stored.
    public string CompanyId { get; set; } = string.Empty;

    public string PublicKey { get; set; } = string.Empty;

    public string PrivateKey { get; set; } = string.Empty;

    // Hudu's one.
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The last Test connection, or null before one; its words go in <see cref="CheckResult"/>.</summary>
    public bool? CheckOk { get; private set; }

    public string? CheckResult { get; private set; }

    public bool Busy { get; private set; }

    /// <summary>Why the fields cannot be saved as they are, or null. The backend's refusal lands here too.</summary>
    public string? Problem => _refusal ?? Validate();

    public bool CanSave => Editing && Problem is null && !Busy;

    public string Status => Connected
        ? LastError is null
            ? LastCheckedAt is { } at ? $"Connected · checked {at.ToLocalTime():HH:mm}" : "Connected"
            : "Connected · the last check failed"
        : "Not connected";

    private string? _refusal;

    internal void Load(IntegrationDetail? detail)
    {
        Connected = detail is not null;
        Editing = detail is null;
        SiteUrl = detail?.SiteUrl ?? string.Empty;
        SecretHint = detail?.SecretHint ?? string.Empty;
        LastCheckedAt = detail?.LastCheckedAt;
        LastError = detail?.LastError;
        _refusal = null;
    }

    /// <summary>Reveals the fields again. The site stays; the secret was never here to reveal.</summary>
    public void Change()
    {
        Editing = true;
        _refusal = null;
    }

    /// <summary>Stores the credential. False, with <see cref="Problem"/> saying why, when it could not be.</summary>
    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        _refusal = null;
        if (!CanSave)
        {
            return false;
        }

        var site = SiteUrl.Trim().TrimEnd('/');
        var secret = IsConnectWise
            ? ComposeConnectWise(CompanyId, PublicKey, PrivateKey)
            : ApiKey.Trim();

        Busy = true;
        try
        {
            _refusal = await _gateway.StoreAsync(Provider, site, secret, ct).ConfigureAwait(false);
        }
        finally
        {
            Busy = false;
        }

        if (_refusal is not null)
        {
            return false;
        }

        // Stored: the parts are cleared now, not when the card closes, so a window left open does not
        // hold a private key in memory for the afternoon.
        SiteUrl = site;
        SecretHint = "••••" + secret[Math.Max(0, secret.Length - 4)..];
        CompanyId = string.Empty;
        PublicKey = string.Empty;
        PrivateKey = string.Empty;
        ApiKey = string.Empty;
        Connected = true;
        Editing = false;
        LastError = null;
        CheckOk = null;
        CheckResult = null;
        return true;
    }

    public async Task<CheckOutcome> CheckAsync(CancellationToken ct = default)
    {
        Busy = true;
        try
        {
            var outcome = await _gateway.CheckAsync(Provider, ct).ConfigureAwait(false);
            CheckOk = outcome.Ok;
            CheckResult = outcome.Message;
            LastError = outcome.Ok ? null : outcome.Message;
            return outcome;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task<bool> RemoveAsync(CancellationToken ct = default)
    {
        _refusal = await _gateway.RemoveAsync(Provider, ct).ConfigureAwait(false);
        if (_refusal is not null)
        {
            return false;
        }

        Load(null);
        CheckOk = null;
        CheckResult = null;
        return true;
    }

    /// <summary><c>companyId+publicKey:privateKey</c>, the company lower-cased as ConnectWise's sign-in wants it.</summary>
    internal static string ComposeConnectWise(string companyId, string publicKey, string privateKey) =>
        $"{companyId.Trim().ToLowerInvariant()}+{publicKey.Trim()}:{privateKey.Trim()}";

    private string? Validate()
    {
        if (!Uri.TryCreate(SiteUrl.Trim(), UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps)
        {
            return IsConnectWise
                ? "The site must be an https address, like https://na.myconnectwise.net."
                : "The site must be an https address, like https://acme.huducloud.com.";
        }

        if (!IsConnectWise)
        {
            return string.IsNullOrWhiteSpace(ApiKey) ? "Paste the API key from Hudu → Admin → API Keys." : null;
        }

        if (string.IsNullOrWhiteSpace(CompanyId))
        {
            return "The company ID is the one typed on ConnectWise's sign-in screen.";
        }

        if (string.IsNullOrWhiteSpace(PublicKey))
        {
            return "The public key is on the API member's API Keys tab.";
        }

        if (string.IsNullOrWhiteSpace(PrivateKey))
        {
            return "The private key is shown once when the API key is created.";
        }

        foreach (var part in new[] { CompanyId.Trim(), PublicKey.Trim(), PrivateKey.Trim() })
        {
            if (part.Any(char.IsWhiteSpace))
            {
                return "The company ID and keys cannot contain spaces.";
            }

            if (part.Contains('+', StringComparison.Ordinal) || part.Contains(':', StringComparison.Ordinal))
            {
                return "The company ID and keys cannot contain '+' or ':'.";
            }
        }

        return null;
    }
}

/// <summary>
/// Settings → Integrations (ST-082, Spec §5 S7): a card per provider and the company mapping table.
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
