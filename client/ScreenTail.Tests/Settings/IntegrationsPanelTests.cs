using System.Reflection;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// Settings → Integrations' rules (ST-082, Spec §5 S7, v0.6.15): a read-only card per provider that is
/// connected or not, the credential shown as its last four, who manages it (the tenant owner, in the web
/// admin), Test connection answered in words either way, and the company mapping table, which
/// technicians keep. The service is a fake here; <c>PipeIntegrationsTests</c> covers the real one.
/// </summary>
public sealed class IntegrationsPanelTests
{
    [Fact]
    public async Task LoadShowsEachProviderConnectedOrNot()
    {
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("connectwise", "https://na.myconnectwise.net", "••••1234", DateTimeOffset.UnixEpoch, null, null)],
        };
        var panel = new IntegrationsPanel(gateway);

        await panel.LoadAsync();

        Assert.True(panel.ConnectWise.Connected);
        Assert.Equal("https://na.myconnectwise.net", panel.ConnectWise.SiteUrl);
        Assert.Equal("••••1234", panel.ConnectWise.SecretHint);
        Assert.False(panel.Hudu.Connected);
        Assert.Equal(string.Empty, panel.Hudu.SiteUrl);
        Assert.Equal(string.Empty, panel.Hudu.SecretHint);
        Assert.Null(panel.Notice);
        Assert.Empty(panel.Others);
    }

    [Fact]
    public async Task TheDevelopmentDemoIsListedUnderNamesNobodyCanTakeForConnectWiseOrHudu()
    {
        // ST-090. With the backend's demo on, the list names only the demo. It used to be dropped,
        // because only the two cards were ever looked for, and the screen said nothing was connected
        // while Publish worked.
        var gateway = new FakeIntegrations
        {
            Rows =
            [
                new IntegrationDetail("demo-psa", "In memory on this backend, Development only", string.Empty, null, null, null),
                new IntegrationDetail("demo-kb", "In memory on this backend, Development only", string.Empty, null, null, null),
            ],
        };
        var panel = new IntegrationsPanel(gateway);

        await panel.LoadAsync();

        Assert.Equal(["Demo PSA (not ConnectWise)", "Demo knowledge base (not Hudu)"], panel.Others.Select(o => o.Title));
        Assert.All(panel.Others, o => Assert.Equal("In memory on this backend, Development only", o.SiteUrl));
        Assert.All(panel.Others, o => Assert.True(o.Connected));
        Assert.All(panel.Others, o => Assert.Equal("Connected", o.Status));
        Assert.False(panel.ConnectWise.Connected);
        Assert.False(panel.Hudu.Connected);
        Assert.Equal("ConnectWise Manage", panel.ConnectWise.Title);
        Assert.Equal("Hudu", panel.Hudu.Title);
    }

    [Fact]
    public async Task AProviderThisVersionDoesNotKnowIsListedByItsOwnNameAndNotCalledConnected()
    {
        // Review 2026-10-27: it was badged Connected, which this version cannot know, and its last
        // error was dropped. Only the demo is Connected; anything else says this version does not
        // support it, and keeps the backend's last error.
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("halopsa", "https://acme.halopsa.com", "••••4321", DateTimeOffset.UnixEpoch, null, "HaloPSA rejected the key. Update it in Settings.")],
        };
        var panel = new IntegrationsPanel(gateway);

        await panel.LoadAsync();

        var other = Assert.Single(panel.Others);
        Assert.Equal(("halopsa", "halopsa", "https://acme.halopsa.com"), (other.Provider, other.Title, other.SiteUrl));
        Assert.False(other.Connected);
        Assert.Equal("Not supported by this version", other.Status);
        Assert.Equal("HaloPSA rejected the key. Update it in Settings.", other.LastError);
    }

    [Fact]
    public async Task WhenTheServiceDoesNotAnswerNothingElseIsListed()
    {
        var panel = new IntegrationsPanel(new FakeIntegrations { Rows = null });

        await panel.LoadAsync();

        Assert.Empty(panel.Others);
        Assert.NotNull(panel.Notice);
    }

    [Fact]
    public async Task AConnectedCardSaysTheOwnerManagesIt()
    {
        // Spec v0.6.15 S7: integration credentials can only be changed by the tenant owner, in the web
        // admin. A connected card shows its status, site and last four, and says who manages it.
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("connectwise", "https://na.myconnectwise.net", "••••1234", DateTimeOffset.UnixEpoch, null, null)],
        };
        var panel = new IntegrationsPanel(gateway);

        await panel.LoadAsync();

        Assert.Equal("Connected", panel.ConnectWise.Status);
        Assert.Equal("Your owner manages this connection in the web admin.", panel.ConnectWise.OwnerLine);
        Assert.Equal(IntegrationCard.OwnerManages, panel.ConnectWise.OwnerLine);
    }

    [Fact]
    public async Task ACardWithNothingConnectedSaysTheOwnerConnectsIt()
    {
        var panel = new IntegrationsPanel(new FakeIntegrations());

        await panel.LoadAsync();

        Assert.All(panel.Cards, card =>
        {
            Assert.Equal("Not connected", card.Status);
            Assert.Equal("Not connected. Your owner connects it in the web admin.", card.OwnerLine);
        });
    }

    [Fact]
    public async Task TestConnectionIsOfferedOnlyForAConnectedCard()
    {
        // It checks the credential the backend holds, so with nothing connected there is nothing to check.
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("hudu", "https://acme.huducloud.com", "••••5678", DateTimeOffset.UnixEpoch, null, null)],
        };
        var panel = new IntegrationsPanel(gateway);

        await panel.LoadAsync();

        Assert.True(panel.Hudu.CanCheck);
        Assert.False(panel.ConnectWise.CanCheck);
    }

    [Fact]
    public async Task ACheckOfACredentialTheOwnerHasSinceRemovedIsForgottenOnTheNextLoad()
    {
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("hudu", "https://acme.huducloud.com", "••••5678", DateTimeOffset.UnixEpoch, null, null)],
            Check = new CheckOutcome(true, "Connected to https://acme.huducloud.com."),
        };
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();
        _ = await panel.Hudu.CheckAsync();
        Assert.True(panel.Hudu.CheckOk);

        gateway.Rows = [];
        await panel.LoadAsync();

        Assert.False(panel.Hudu.Connected);
        Assert.Null(panel.Hudu.CheckOk);
        Assert.Null(panel.Hudu.CheckResult);
        Assert.Equal(string.Empty, panel.Hudu.SecretHint);
        Assert.Equal(IntegrationCard.OwnerConnects, panel.Hudu.OwnerLine);
    }

    [Fact]
    public async Task AReloadShowsWhatTheOwnerChanged()
    {
        // There is nothing typed on the card to keep, so a reload always shows the backend's truth: a key
        // the owner replaced shows its new last four.
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("connectwise", "https://na.myconnectwise.net", "••••1234", DateTimeOffset.UnixEpoch, null, null)],
        };
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();

        gateway.Rows = [new IntegrationDetail("connectwise", "https://eu.myconnectwise.net", "••••9876", DateTimeOffset.UnixEpoch, null, null)];
        await panel.LoadAsync();

        Assert.Equal("https://eu.myconnectwise.net", panel.ConnectWise.SiteUrl);
        Assert.Equal("••••9876", panel.ConnectWise.SecretHint);
    }

    [Theory]
    [InlineData("SaveAsync")]
    [InlineData("RemoveAsync")]
    [InlineData("Change")]
    [InlineData("Undo")]
    [InlineData("CompanyId")]
    [InlineData("PublicKey")]
    [InlineData("PrivateKey")]
    [InlineData("ApiKey")]
    [InlineData("Editing")]
    [InlineData("HasPendingChange")]
    [InlineData("HasUnsavedEdits")]
    public void TheCardHasNoWayToTypeStoreOrRemoveACredential(string member)
    {
        // Spec v0.6.15 S7. The fields, Change, Save and Remove went with the move to the web admin; this
        // keeps them from coming back on the card or the panel.
        Assert.Empty(typeof(IntegrationCard).GetMember(member));
        Assert.Empty(typeof(IntegrationsPanel).GetMember(member));
    }

    [Fact]
    public void NothingOnTheCardCanBeSetFromOutside()
    {
        var settable = typeof(IntegrationCard)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name);

        Assert.Empty(settable);
    }

    [Theory]
    [InlineData("StoreAsync")]
    [InlineData("RemoveAsync")]
    public void TheGatewayCannotStoreOrRemoveACredential(string member)
    {
        Assert.Empty(typeof(IIntegrationsGateway).GetMember(member));
        Assert.Empty(typeof(PipeIntegrations).GetMember(member));
    }

    [Fact]
    public async Task TestConnectionShowsTheAnswerEitherWay()
    {
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("connectwise", "https://na.myconnectwise.net", "••••1234", DateTimeOffset.UnixEpoch, null, null)],
            Check = new CheckOutcome(false, "ConnectWise rejected the key. Update it in Settings → Integrations."),
        };
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();

        var outcome = await panel.ConnectWise.CheckAsync();

        Assert.False(outcome.Ok);

        // Spec v0.6.8, v0.6.15: the backend's words point at "Settings → Integrations", which is this page,
        // where nothing can be changed, so the card asks the owner. (Was: "Press Change and enter a new key.")
        Assert.Equal("ConnectWise rejected the key. Ask your owner to update it in the web admin.", panel.ConnectWise.CheckResult);
        Assert.False(panel.ConnectWise.CheckOk);
        Assert.Equal(["connectwise"], gateway.Checked);
    }

    [Theory]
    [InlineData("Hudu rejected the API key. Update it in Settings → Integrations.", "Hudu rejected the API key. Ask your owner to update it in the web admin.")]
    [InlineData("The site did not answer. Check the site address in Settings → Integrations.", "The site did not answer. Ask your owner to check the site address in the web admin.")]
    [InlineData("The stored credential could not be opened. Enter it again in Settings → Integrations.", "The stored credential could not be opened. Ask your owner to enter it again in the web admin.")]
    [InlineData("Enter the provider's public address in Settings → Integrations.", "Ask your owner to enter the provider's public address in the web admin.")]
    [InlineData("HaloPSA rejected the key. Update it in Settings.", "HaloPSA rejected the key. Update it in Settings.")]
    [InlineData("Connected to https://na.myconnectwise.net.", "Connected to https://na.myconnectwise.net.")]
    public void OnTheIntegrationsPageNothingSendsTheTechnicianToTheIntegrationsPage(string fromTheBackend, string onThePage)
    {
        Assert.Equal(onThePage, IntegrationNames.OnThisPage(fromTheBackend));
        Assert.DoesNotContain("Settings → Integrations", IntegrationNames.OnThisPage(fromTheBackend), StringComparison.Ordinal);
        Assert.DoesNotContain("Press Change", IntegrationNames.OnThisPage(fromTheBackend), StringComparison.Ordinal);
    }

    [Fact]
    public void OnThisPageLeavesNullAndEmptyAsTheyAre()
    {
        Assert.Null(IntegrationNames.OnThisPage(null));
        Assert.Equal(string.Empty, IntegrationNames.OnThisPage(string.Empty));
    }

    [Fact]
    public async Task TheLastErrorSaysWhoCanFixItOnThePageToo()
    {
        var gateway = new FakeIntegrations
        {
            Rows =
            [
                new IntegrationDetail("hudu", "https://acme.huducloud.com", "••••5678", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Hudu rejected the API key. Update it in Settings → Integrations."),
                new IntegrationDetail("demo-kb", "https://demo.example", "••••0000", DateTimeOffset.UnixEpoch, null, "The site did not answer. Check the site address in Settings → Integrations."),
            ],
        };
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();

        Assert.Equal("Hudu rejected the API key. Ask your owner to update it in the web admin.", panel.Hudu.LastError);
        Assert.Equal("Connected · the last check failed", panel.Hudu.Status);
        Assert.Equal("The site did not answer. Ask your owner to check the site address in the web admin.", Assert.Single(panel.Others).LastError);
    }

    [Fact]
    public async Task CompanyMappingsAreListedWhenHuduIsConnectedAndCanBeChangedHere()
    {
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("hudu", "https://acme.huducloud.com", "••••5678", DateTimeOffset.UnixEpoch, null, null)],
            Companies = [new CompanyChoice("7", "Acme Dental"), new CompanyChoice("9", "Bright Smiles")],
            Mappings = [new CompanyMappingEntry("Acme Dental", "7", "Acme Dental", "exact")],
        };
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();

        Assert.True(panel.CanMapCompanies);
        Assert.Equal("exact", Assert.Single(panel.Mappings).Confidence);
        Assert.Equal(2, panel.Companies.Count);

        Assert.Null(await panel.MapAsync("Bright Smiles Dental", "9"));
        Assert.Equal(("Bright Smiles Dental", "9"), gateway.Mapped.Single());
        Assert.Null(await panel.UnmapAsync("Acme Dental"));
        Assert.Equal(["Acme Dental"], gateway.Unmapped);
    }

    [Fact]
    public async Task WithoutHuduThereIsNothingToMapAndTheServiceIsNotAsked()
    {
        var gateway = new FakeIntegrations();
        var panel = new IntegrationsPanel(gateway);

        await panel.LoadAsync();

        Assert.False(panel.CanMapCompanies);
        Assert.Empty(panel.Mappings);
        Assert.False(gateway.MappingsAsked);
    }

    [Fact]
    public async Task WhenTheServiceDoesNotAnswerThePanelSaysSo()
    {
        var gateway = new FakeIntegrations { Rows = null };
        var panel = new IntegrationsPanel(gateway);

        await panel.LoadAsync();

        Assert.NotNull(panel.Notice);
        Assert.False(panel.ConnectWise.Connected);
    }

    private sealed class FakeIntegrations : IIntegrationsGateway
    {
        public IReadOnlyList<IntegrationDetail>? Rows { get; set; } = [];

        public CheckOutcome Check { get; set; } = new(true, "Connected.");

        public IReadOnlyList<CompanyChoice> Companies { get; set; } = [];

        public IReadOnlyList<CompanyMappingEntry> Mappings { get; set; } = [];

        public List<string> Checked { get; } = [];

        public List<(string Psa, string Doc)> Mapped { get; } = [];

        public List<string> Unmapped { get; } = [];

        public bool MappingsAsked { get; private set; }

        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) => Task.FromResult(Rows);

        public Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default)
        {
            Checked.Add(provider);
            return Task.FromResult(Check);
        }

        public Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default)
        {
            MappingsAsked = true;
            return Task.FromResult<CompanyMappingsPage?>(new CompanyMappingsPage(Companies, Mappings));
        }

        public Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default)
        {
            Mapped.Add((psaCompany, docCompanyId));
            return Task.FromResult<string?>(null);
        }

        public Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default)
        {
            Unmapped.Add(psaCompany);
            return Task.FromResult<string?>(null);
        }
    }
}
