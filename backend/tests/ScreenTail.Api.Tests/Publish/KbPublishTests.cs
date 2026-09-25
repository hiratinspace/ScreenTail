using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Publish;
using ScreenTail.Api.Summarize;
using ScreenTail.Api.Tests.Providers.ConnectWise;
using ScreenTail.Api.Tests.Providers.Hudu;

namespace ScreenTail.Api.Tests.Publish;

/// <summary>
/// The knowledge-base destination (ST-096, ST-097): the ticket's company is mapped to a Hudu company —
/// exactly, and remembered — or the publish says a mapping is needed; the article is a draft under that
/// company with the screenshots attached and the ticket named.
/// </summary>
public sealed class KbPublishTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task AnExactCompanyMatchPublishesADraftArticleAndIsRemembered()
    {
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental") with { Frames = [Frame("f-1")] }, TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var kb = Assert.Single(published!.Results);
        Assert.True(kb.Ok);
        Assert.Equal("5101", kb.Id);
        Assert.Equal(new Uri("https://acme.huducloud.com/a/printer-offline-5101"), kb.Link);
        var create = hudu.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
        var article = JsonDocument.Parse(hudu.Bodies[hudu.Requests.IndexOf(create)]!).RootElement.GetProperty("article");
        Assert.Equal(7, article.GetProperty("company_id").GetInt32());
        Assert.True(article.GetProperty("draft").GetBoolean());
        Assert.Equal("Printer offline — Acme Dental", article.GetProperty("name").GetString());
        Assert.Contains("#48213", article.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Single(hudu.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/uploads", StringComparison.Ordinal));

        var mapping = await api.UseAsync(db => db.CompanyMappings.SingleAsync(m => m.TenantId == tenantId));
        Assert.Equal("Acme Dental", mapping.PsaCompany);
        Assert.Equal("7", mapping.DocCompanyId);
        Assert.Equal("exact", mapping.Confidence);
    }

    [Fact]
    public async Task ACompanyNobodyCanMatchAsksForAMappingAndPublishesNothing()
    {
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, _) = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Nowhere Inc"), TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        var kb = Assert.Single(published!.Results);
        Assert.False(kb.Ok);
        Assert.Equal("needs_mapping", kb.Kind);
        Assert.Contains("Nowhere Inc", kb.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(hudu.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMappingSetByHandIsUsedAndListed()
    {
        // ST-097 AC2 and AC3: the prompt's answer is remembered, and the list is what Settings edits.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, _) = await ConnectedClientAsync(host);

        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), new MapCompanyRequest("Nowhere Inc", "9"), TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<CompanyMappingsResponse>(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), TestContext.Current.CancellationToken);
        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Nowhere Inc"), TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Contains(list!.Companies, c => c.Id == "9" && c.Name == "Bright Smiles");
        var mapping = Assert.Single(list.Mappings);
        Assert.Equal(("Nowhere Inc", "9", "manual"), (mapping.PsaCompany, mapping.DocCompanyId, mapping.Confidence));
        Assert.True(Assert.Single(published!.Results).Ok);
        var create = hudu.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
        Assert.Equal(9, JsonDocument.Parse(hudu.Bodies[hudu.Requests.IndexOf(create)]!).RootElement.GetProperty("article").GetProperty("company_id").GetInt32());
    }

    [Fact]
    public async Task AMappingToACompanyHuduDoesNotHaveIsRefused()
    {
        using var host = Host(new ScriptedConnectWise(), new ScriptedHudu());
        var (client, _) = await ConnectedClientAsync(host);

        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), new MapCompanyRequest("Nowhere Inc", "999"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task WithoutHuduTheKbDestinationSaysSoAndTheRestStillGoes()
    {
        using var host = Host(new ScriptedConnectWise(), new ScriptedHudu());
        var (client, _) = await ConnectedClientAsync(host, hudu: false);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental") with { Destinations = ["ticket_note", "kb_article"] }, TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.True(published!.Results.Single(r => r.Destination == "ticket_note").Ok);
        var kb = published.Results.Single(r => r.Destination == "kb_article");
        Assert.False(kb.Ok);
        Assert.Equal("not_configured", kb.Kind);
    }

    private static PublishBundle Bundle(string company) => new()
    {
        SessionId = "s-0001",
        TicketId = "48213",
        Company = company,
        NoteType = "internal",
        Minutes = 30,
        StartedAt = new DateTimeOffset(2026, 9, 24, 14, 2, 0, TimeSpan.Zero),
        Destinations = ["kb_article"],
        Note = new DraftJson
        {
            Problem = "Printer offline in reception.",
            Steps = [new DraftStepJson { Text = "Checked the spooler service.", Confidence = "high" }],
            Result = "Printing works again.",
            FollowUps = [],
            SuggestedTitle = "Printer offline — Acme Dental",
            SuggestedTimeMinutes = 23,
            KbCandidate = true,
            KbReason = "Recurring",
            Source = "cloud",
            PromptVersion = "note_v1",
        },
        Frames = [],
    };

    private static PublishFrame Frame(string id) => new(id, "/9j/4AAQSkZJRg==", "image/jpeg");

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host(ScriptedConnectWise cw, ScriptedHudu hudu) => api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("ConnectWise:ClientId", "11111111-2222-3333-4444-555555555555");
        _ = builder.ConfigureTestServices(services =>
        {
            services.Configure<HttpClientFactoryOptions>(nameof(ConnectWiseProvider), options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = cw));
            services.Configure<HttpClientFactoryOptions>(nameof(HuduProvider), options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = hudu));
        });
    });

    private async Task<(HttpClient Client, Guid TenantId)> ConnectedClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, bool hudu = true)
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var cw = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"), TestContext.Current.CancellationToken);
        if (hudu)
        {
            using var h = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://acme.huducloud.com", "hudu-key-5678"), TestContext.Current.CancellationToken);
        }

        return (client, tenantId);
    }

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme IT", Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId:N}@acme.example", DisplayName = "A Technician", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = tenantId, UserId = userId, Name = "TECH-LAPTOP", TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return (tenantId, userId, deviceId);
    }
}
