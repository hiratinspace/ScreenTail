using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// Settings → Integrations' rules (ST-082, Spec §5 S7): a card per provider that is connected or not,
/// secrets masked once saved and revealed by Change, a credential composed the way the provider wants
/// it, Test connection answered in words either way, and the company mapping table. The service is a
/// fake here; <c>PipeIntegrationsTests</c> covers the real one.
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
        Assert.False(panel.ConnectWise.Editing);
        Assert.False(panel.Hudu.Connected);
        Assert.True(panel.Hudu.Editing);
        Assert.Null(panel.Notice);
    }

    [Fact]
    public async Task AConnectWiseCredentialIsComposedTheWayConnectWiseWantsItAndMaskedOnceSaved()
    {
        var gateway = new FakeIntegrations();
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();
        var card = panel.ConnectWise;
        card.SiteUrl = "https://na.myconnectwise.net/";
        card.CompanyId = "Acme";
        card.PublicKey = "PUBLICKEY";
        card.PrivateKey = "PRIVATE-9876";

        Assert.Null(card.Problem);
        Assert.True(await card.SaveAsync());

        Assert.Equal(("connectwise", "https://na.myconnectwise.net", "acme+PUBLICKEY:PRIVATE-9876"), gateway.Stored.Single());
        Assert.True(card.Connected);
        Assert.False(card.Editing);
        Assert.Equal("••••9876", card.SecretHint);
        Assert.Equal(string.Empty, card.PrivateKey);
        Assert.Equal(string.Empty, card.PublicKey);
    }

    [Fact]
    public async Task AHuduKeyIsStoredAsItIs()
    {
        var gateway = new FakeIntegrations();
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();
        panel.Hudu.SiteUrl = "https://acme.huducloud.com";
        panel.Hudu.ApiKey = " hudu-key-5678 ";

        Assert.True(await panel.Hudu.SaveAsync());

        Assert.Equal(("hudu", "https://acme.huducloud.com", "hudu-key-5678"), gateway.Stored.Single());
        Assert.Equal(string.Empty, panel.Hudu.ApiKey);
    }

    [Theory]
    [InlineData("http://na.myconnectwise.net", "acme", "PUB", "PRIV", "https")]
    [InlineData("https://na.myconnectwise.net", "", "PUB", "PRIV", "company ID")]
    [InlineData("https://na.myconnectwise.net", "acme", "PUB", "", "private key")]
    [InlineData("https://na.myconnectwise.net", "ac me", "PUB", "PRIV", "spaces")]
    [InlineData("https://na.myconnectwise.net", "acme", "PU:B", "PRIV", "':'")]
    public async Task ABadSiteOrKeyBlocksSaveAndSaysWhy(string site, string company, string publicKey, string privateKey, string expected)
    {
        var gateway = new FakeIntegrations();
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();
        var card = panel.ConnectWise;
        card.SiteUrl = site;
        card.CompanyId = company;
        card.PublicKey = publicKey;
        card.PrivateKey = privateKey;

        Assert.Contains(expected, card.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.False(card.CanSave);
        Assert.False(await card.SaveAsync());
        Assert.Empty(gateway.Stored);
    }

    [Fact]
    public async Task ChangeRevealsTheFieldsAgainAndTheBackendsRefusalIsShown()
    {
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("hudu", "https://acme.huducloud.com", "••••5678", DateTimeOffset.UnixEpoch, null, null)],
            StoreRefusal = "This deployment has no vault master key, so credentials cannot be stored.",
        };
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();

        panel.Hudu.Change();
        Assert.True(panel.Hudu.Editing);
        Assert.Equal("https://acme.huducloud.com", panel.Hudu.SiteUrl);

        panel.Hudu.ApiKey = "new-key-0001";
        Assert.False(await panel.Hudu.SaveAsync());

        Assert.Equal(gateway.StoreRefusal, panel.Hudu.Problem);
        Assert.True(panel.Hudu.Editing);
        Assert.Equal("••••5678", panel.Hudu.SecretHint);
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
        Assert.Equal(outcome.Message, panel.ConnectWise.CheckResult);
        Assert.False(panel.ConnectWise.CheckOk);
        Assert.Equal(["connectwise"], gateway.Checked);
    }

    [Fact]
    public async Task RemovingForgetsTheCredentialAndOpensTheFields()
    {
        var gateway = new FakeIntegrations
        {
            Rows = [new IntegrationDetail("connectwise", "https://na.myconnectwise.net", "••••1234", DateTimeOffset.UnixEpoch, null, null)],
        };
        var panel = new IntegrationsPanel(gateway);
        await panel.LoadAsync();

        Assert.True(await panel.ConnectWise.RemoveAsync());

        Assert.Equal(["connectwise"], gateway.Removed);
        Assert.False(panel.ConnectWise.Connected);
        Assert.True(panel.ConnectWise.Editing);
        Assert.Equal(string.Empty, panel.ConnectWise.SecretHint);
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

        public string? StoreRefusal { get; set; }

        public CheckOutcome Check { get; set; } = new(true, "Connected.");

        public IReadOnlyList<CompanyChoice> Companies { get; set; } = [];

        public IReadOnlyList<CompanyMappingEntry> Mappings { get; set; } = [];

        public List<(string Provider, string SiteUrl, string Secret)> Stored { get; } = [];

        public List<string> Removed { get; } = [];

        public List<string> Checked { get; } = [];

        public List<(string Psa, string Doc)> Mapped { get; } = [];

        public List<string> Unmapped { get; } = [];

        public bool MappingsAsked { get; private set; }

        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) => Task.FromResult(Rows);

        public Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default)
        {
            if (StoreRefusal is null)
            {
                Stored.Add((provider, siteUrl, secret));
            }

            return Task.FromResult(StoreRefusal);
        }

        public Task<string?> RemoveAsync(string provider, CancellationToken ct = default)
        {
            Removed.Add(provider);
            return Task.FromResult<string?>(null);
        }

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
