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
    private static readonly Uri Companies = new("/v1/integrations/hudu/companies", UriKind.Relative);

    /// <summary>
    /// Long enough for any machine to finish what a test waits for here, which is never a deadline: that
    /// runs on a <see cref="HeldClock"/>. Only a test that is already failing waits this out.
    /// </summary>
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(30);

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
    public async Task ALikelyMatchIsOfferedAndNeverAssumed()
    {
        // T-221: "Borough Legal, LLC" is very probably Hudu's "Borough Legal", and probably is not good
        // enough to file one customer's runbook under. The name is offered, nothing is written, and
        // nothing is remembered, so the next publish asks again.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);

        using var first = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Borough Legal, LLC"), TestContext.Current.CancellationToken);
        using var second = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Borough Legal, LLC"), TestContext.Current.CancellationToken);
        var answers = new[]
        {
            await first.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken),
            await second.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken),
        };

        Assert.All(answers, answer =>
        {
            var kb = Assert.Single(answer!.Results);
            Assert.False(kb.Ok);
            Assert.Equal("needs_mapping", kb.Kind);
            Assert.Contains("It may be \"Borough Legal\"", kb.Error, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(hudu.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
    }

    [Fact]
    public async Task AnArticleWhoseScreenshotsWereRefusedIsReportedAsWritten()
    {
        // S-69's twin: the article exists in Hudu. Reported as failed, Retry wrote a second one.
        var hudu = new ScriptedHudu { FailingRoute = ("/api/v1/uploads", HttpStatusCode.UnprocessableEntity) };
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, _) = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental") with { Frames = [Frame("f-1")] }, TestContext.Current.CancellationToken);
        var kb = Assert.Single((await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken))!.Results);

        Assert.True(kb.Ok);
        Assert.Equal("5101", kb.Id);
        Assert.Equal(new Uri("https://acme.huducloud.com/a/printer-offline-5101"), kb.Link);
        Assert.Equal("partial", kb.Kind);
        Assert.False(kb.Retryable);
        Assert.Contains("1 of 1 screenshots", kb.Error, StringComparison.Ordinal);
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

    [Fact]
    public async Task ADeadlineBeforeTheArticleWasSentSaysNothingWasWritten()
    {
        // The publish ran out of time while it was still looking up the company. No article was asked
        // for, so the answer says nothing was written and the article may simply be published again,
        // rather than telling the technician to go and look for one that cannot exist.
        //
        // The deadline runs on a held clock, moved past only once the lookup is waiting, so it ends the
        // publish during the lookup every time rather than wherever a busy machine had got to.
        var clock = new HeldClock();
        var hudu = new ScriptedHudu { HangingRoute = "/api/v1/companies" };
        using var host = Host(new ScriptedConnectWise(), hudu, publish => publish.Deadline = TimeSpan.FromMilliseconds(500), clock);
        var (client, _) = await ConnectedClientAsync(host);

        var responding = client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        await hudu.Hung.WaitAsync(Settled, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        using var response = await responding.WaitAsync(Settled, TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var kb = Assert.Single(published!.Results);
        Assert.False(kb.Ok);
        Assert.Equal("unavailable", kb.Kind);
        Assert.True(kb.Retryable);
        Assert.Contains("nothing was written", kb.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("may already", kb.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(hudu.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACompanyListThatCannotBeReadWritesNothingAndTheNoteStillGoes()
    {
        // Hudu answered the company lookup with something that is not a list. That is a read, so it is
        // the provider's invalid (the site is not answering as its API) and not one that may have landed:
        // nothing was written, and the note beside it is answered with its id.
        var hudu = new ScriptedHudu { UnreadableRoute = ("/api/v1/companies", "<html>Maintenance</html>") };
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, _) = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental") with { Destinations = ["ticket_note", "kb_article"] }, TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("90001", published!.Results.Single(r => r.Destination == "ticket_note").Id);
        var kb = published.Results.Single(r => r.Destination == "kb_article");
        Assert.False(kb.Ok);
        Assert.Equal("invalid", kb.Kind);
        Assert.DoesNotContain("may have been saved", kb.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Maintenance", kb.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(hudu.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnArticleWhoseAnswerCannotBeReadIsNotSentAgain()
    {
        // The article was asked for and Hudu's answer cannot be read, so it may exist. Not retryable,
        // and the words say to look first: a second article is the duplicate ST-094 forbids.
        var hudu = new ScriptedHudu { UnreadableRoute = ("/api/v1/articles", string.Empty) };
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, _) = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var kb = Assert.Single(published!.Results);
        Assert.False(kb.Ok);
        Assert.Equal("unknown", kb.Kind);
        Assert.False(kb.Retryable);
        Assert.Contains("may have been saved", kb.Error, StringComparison.Ordinal);
        Assert.Contains("before publishing again", kb.Error, StringComparison.Ordinal);
        Assert.Single(hudu.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARevokedDeviceCannotReadOrChangeCompanyMappings()
    {
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);
        using var seeded = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), new MapCompanyRequest("Acme Dental", "7"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, seeded.StatusCode);
        var before = hudu.Requests.Count;
        _ = await api.UseAsync(db => db.Devices
            .Where(d => d.TenantId == tenantId)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));

        using var list = await client.GetAsync(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), TestContext.Current.CancellationToken);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), new MapCompanyRequest("Nowhere Inc", "9"), TestContext.Current.CancellationToken);
        using var badPut = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), new MapCompanyRequest(string.Empty, string.Empty), TestContext.Current.CancellationToken);
        using var delete = await client.DeleteAsync(new Uri("/v1/integrations/hudu/companies/Acme%20Dental", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, badPut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal(before, hudu.Requests.Count);
        var mapping = await api.UseAsync(db => db.CompanyMappings.SingleAsync(m => m.TenantId == tenantId));
        Assert.Equal("7", mapping.DocCompanyId);
    }

    [Fact]
    public async Task ForgettingAMappingRemovesOnlyThisTenantsRow()
    {
        // 2026-10-12 review, T-174 (boundary 7). Both tenants file "Acme Dental" under Hudu company 7;
        // one forgets it, and the other's mapping is exactly where it was.
        using var host = Host(new ScriptedConnectWise(), new ScriptedHudu());
        var (mine, myTenant) = await ConnectedClientAsync(host);
        var (theirs, theirTenant) = await ConnectedClientAsync(host);
        using var a = await mine.PutAsJsonAsync(Companies, new MapCompanyRequest("Acme Dental", "7"), TestContext.Current.CancellationToken);
        using var b = await mine.PutAsJsonAsync(Companies, new MapCompanyRequest("Borough Legal", "8"), TestContext.Current.CancellationToken);
        using var c = await theirs.PutAsJsonAsync(Companies, new MapCompanyRequest("Acme Dental", "7"), TestContext.Current.CancellationToken);

        using var forgotten = await theirs.DeleteAsync(new Uri("/v1/integrations/hudu/companies/Acme%20Dental", UriKind.Relative), TestContext.Current.CancellationToken);
        using var again = await theirs.DeleteAsync(new Uri("/v1/integrations/hudu/companies/Acme%20Dental", UriKind.Relative), TestContext.Current.CancellationToken);
        using var notTheirs = await theirs.DeleteAsync(new Uri("/v1/integrations/hudu/companies/Borough%20Legal", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, forgotten.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notTheirs.StatusCode);
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == theirTenant)));
        var kept = await api.UseAsync(db => db.CompanyMappings.Where(m => m.TenantId == myTenant).OrderBy(m => m.PsaCompany).Select(m => m.PsaCompany + "=" + m.DocCompanyId).ToListAsync());
        Assert.Equal(["Acme Dental=7", "Borough Legal=8"], kept);
    }

    [Fact]
    public async Task AnotherTenantsMappingIsNeverUsed()
    {
        // T-174. "Nowhere Inc" matches nothing in Hudu, so only a mapping can file it. One tenant has
        // mapped it by hand; the other publishes the same name and is asked for its own mapping.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (mine, _) = await ConnectedClientAsync(host);
        var (theirs, theirTenant) = await ConnectedClientAsync(host);
        using var mapped = await mine.PutAsJsonAsync(Companies, new MapCompanyRequest("Nowhere Inc", "9"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);

        using var response = await theirs.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Nowhere Inc"), TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);
        var list = await theirs.GetFromJsonAsync<CompanyMappingsResponse>(Companies, TestContext.Current.CancellationToken);

        var kb = Assert.Single(published!.Results);
        Assert.False(kb.Ok);
        Assert.Equal("needs_mapping", kb.Kind);
        Assert.DoesNotContain(hudu.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
        Assert.Empty(list!.Mappings);
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == theirTenant)));
    }

    [Fact]
    public async Task ReplacingTheHuduConnectionForgetsItsCompanies()
    {
        // 2026-10-12 review, T-214. The list is cached ten minutes. Pointed at another site, the next
        // list must be that site's, not the old one's served from memory.
        var hudu = new ScriptedHudu { RenamedAt = (site, id) => site == "other.huducloud.com" && id == 7 ? "Zenith Accounting" : null };
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, _) = await ConnectedClientAsync(host);
        var before = await client.GetFromJsonAsync<CompanyMappingsResponse>(Companies, TestContext.Current.CancellationToken);

        using var repointed = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://other.huducloud.com", "hudu-key-9999"), TestContext.Current.CancellationToken);
        var after = await client.GetFromJsonAsync<CompanyMappingsResponse>(Companies, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, repointed.StatusCode);
        Assert.Contains(before!.Companies, c => c.Id == "7" && c.Name == "Acme Dental");
        Assert.Contains(after!.Companies, c => c.Id == "7" && c.Name == "Zenith Accounting");
        Assert.Contains(hudu.Requests, r => r.RequestUri!.Host == "other.huducloud.com" && r.RequestUri.AbsolutePath.EndsWith("/api/v1/companies", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANewKeyForTheSameSiteAsksForTheCompaniesAgain()
    {
        // T-214's other half: the key changed and the site did not. The list may differ with what the
        // new key can see, so it is asked for again rather than served from memory.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, _) = await ConnectedClientAsync(host);
        _ = await client.GetFromJsonAsync<CompanyMappingsResponse>(Companies, TestContext.Current.CancellationToken);
        var asked = hudu.Requests.Count(IsCompanyList);

        using var rotated = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://acme.huducloud.com", "hudu-key-0000"), TestContext.Current.CancellationToken);
        _ = await client.GetFromJsonAsync<CompanyMappingsResponse>(Companies, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
        Assert.True(hudu.Requests.Count(IsCompanyList) > asked);
    }

    [Fact]
    public async Task RepointingHuduForgetsTheMappingsMadeOnTheOldSite()
    {
        // 2026-10-12 review, S-71. Company 7 is Acme Dental on the old site and somebody else entirely on
        // the new one. A mapping that survived the move would file Acme Dental's article under that other
        // customer; instead the publish asks for a mapping on the new site, and nothing is posted.
        var hudu = new ScriptedHudu { RenamedAt = (site, id) => site == "other.huducloud.com" && id == 7 ? "Zenith Accounting" : null };
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);
        var (bystander, bystanderTenant) = await ConnectedClientAsync(host);
        using var first = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        using var theirs = await bystander.PutAsJsonAsync(Companies, new MapCompanyRequest("Acme Dental", "7"), TestContext.Current.CancellationToken);
        Assert.Equal("7", (await api.UseAsync(db => db.CompanyMappings.SingleAsync(m => m.TenantId == tenantId))).DocCompanyId);

        using var repointed = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://other.huducloud.com", "hudu-key-9999"), TestContext.Current.CancellationToken);
        var posted = hudu.Requests.Count(IsArticle);
        using var second = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        var published = await second.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, repointed.StatusCode);
        var kb = Assert.Single(published!.Results);
        Assert.False(kb.Ok);
        Assert.Equal("needs_mapping", kb.Kind);
        Assert.Equal(posted, hudu.Requests.Count(IsArticle));
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
        Assert.Equal(1, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == bystanderTenant)));
    }

    [Fact]
    public async Task AMappingFromTheOldSiteThatSurvivedTheMoveIsNotTrusted()
    {
        // S-71, second review round. A publish that read the old site's list while the tenant was moving
        // can remember company 7 after the move cleared the mappings. On the new site 7 is Zenith
        // Accounting, so the remembered "Acme Dental" no longer names it: nothing is posted, and the stale
        // row is forgotten so the next publish starts from the new site's own list.
        var hudu = new ScriptedHudu { RenamedAt = (site, id) => site == "other.huducloud.com" && id == 7 ? "Zenith Accounting" : null };
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);
        using var repointed = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://other.huducloud.com", "hudu-key-9999"), TestContext.Current.CancellationToken);
        _ = await RememberDirectlyAsync(tenantId, "Acme Dental", "7", "Acme Dental");

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, repointed.StatusCode);
        var kb = Assert.Single(published!.Results);
        Assert.False(kb.Ok);
        Assert.Equal("needs_mapping", kb.Kind);
        Assert.DoesNotContain(hudu.Requests, IsArticle);
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
    }

    [Theory]
    [InlineData("Acme Dental Group")]
    [InlineData("acme dental")]
    [InlineData("Acme Dental ")]
    [InlineData("")]
    public async Task AMappingWhoseCompanyNoLongerHasThatNameAsksOnceThenMatchesAgain(string remembered)
    {
        // The id is trusted only while it still carries the name the mapping was made with. A company
        // renamed in Hudu is asked about once; after that the publish is a fresh one, and an exact match
        // on today's list is remembered as usual.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);
        _ = await RememberDirectlyAsync(tenantId, "Acme Dental", "7", remembered);

        using var first = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        var asked = Assert.Single((await first.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken))!.Results);
        var posted = hudu.Requests.Count(IsArticle);
        using var second = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        var again = Assert.Single((await second.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken))!.Results);

        Assert.False(asked.Ok);
        Assert.Equal("needs_mapping", asked.Kind);
        Assert.Equal(0, posted);
        Assert.True(again.Ok);
        var mapping = await api.UseAsync(db => db.CompanyMappings.SingleAsync(m => m.TenantId == tenantId));
        Assert.Equal(("7", "Acme Dental", "exact"), (mapping.DocCompanyId, mapping.DocCompanyName, mapping.Confidence));
    }

    [Fact]
    public async Task ACompanyRemovedInHuduWhileItWasCachedAsksForAnotherAndIsForgotten()
    {
        // The first article files "Acme Dental" under Hudu's company 7 and caches the list. Someone then
        // deletes company 7 in Hudu. Within the cache's ten minutes the list still holds it, so the
        // article goes and Hudu answers that the company must exist. The technician is asked to choose
        // another company rather than told it is a ScreenTail bug, the mapping to the deleted company is
        // forgotten, and the companies the mapping prompt lists come from Hudu, not the cache.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);
        using var first = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        Assert.True(Assert.Single((await first.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken))!.Results).Ok);
        _ = hudu.Removed.Add(7);

        using var second = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        var kb = Assert.Single((await second.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken))!.Results);
        var listed = await client.GetFromJsonAsync<CompanyMappingsResponse>(Companies, TestContext.Current.CancellationToken);

        Assert.False(kb.Ok);
        Assert.Equal("needs_mapping", kb.Kind);
        Assert.False(kb.Retryable);
        Assert.DoesNotContain("ScreenTail bug", kb.Error, StringComparison.Ordinal);
        Assert.Equal(
            "The documentation platform no longer has \"Acme Dental\", so the mapping was forgotten and nothing was written. \"Acme Dental\" is not mapped to a company in the documentation platform. Map it in Settings → Integrations, then publish the article again.",
            kb.Error);
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
        Assert.DoesNotContain(listed!.Companies, c => c.Id == "7");
    }

    [Fact]
    public async Task AnExactMatchMadeOnACompanyHuduHasJustDeletedIsNotKept()
    {
        // The match is made and remembered in the same publish whose article Hudu then refuses: the row
        // the forget compares against is the one just written, not one read back, and it still goes.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);
        _ = await client.GetFromJsonAsync<CompanyMappingsResponse>(Companies, TestContext.Current.CancellationToken);
        _ = hudu.Removed.Add(7);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        var kb = Assert.Single((await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken))!.Results);

        Assert.Equal("needs_mapping", kb.Kind);
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
    }

    [Fact]
    public async Task APlain404OnTheArticleIsNotTakenToMeanTheCompanyIsGone()
    {
        // A 404 from the articles route says nothing about the company: a proxy in front of a
        // self-hosted Hudu, the route turned off, an API change. Only Hudu's own "company must exist"
        // says the company is gone. Anything else keeps the mapping (which a person may have confirmed)
        // and the cache, and passes the refusal on as it was, rather than sending the technician to
        // pick another customer's company and back round the same 404.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);
        using var mapped = await client.PutAsJsonAsync(Companies, new MapCompanyRequest("Acme Dental", "7"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
        hudu.FailingRoute = ("/api/v1/articles", HttpStatusCode.NotFound);
        var listed = hudu.Requests.Count(IsCompanyList);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Acme Dental"), TestContext.Current.CancellationToken);
        var kb = Assert.Single((await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken))!.Results);
        _ = await client.GetFromJsonAsync<CompanyMappingsResponse>(Companies, TestContext.Current.CancellationToken);

        Assert.False(kb.Ok);
        Assert.Equal("notfound", kb.Kind);
        Assert.DoesNotContain("no longer", kb.Error, StringComparison.Ordinal);
        var mapping = await api.UseAsync(db => db.CompanyMappings.SingleAsync(m => m.TenantId == tenantId));
        Assert.Equal(("7", "manual"), (mapping.DocCompanyId, mapping.Confidence));
        Assert.Equal(listed, hudu.Requests.Count(IsCompanyList));
    }

    [Fact]
    public async Task AMappingIsStampedInWholeMicrosecondsSoTheRowJustWrittenCanStillBeForgotten()
    {
        // The company-gone path forgets the row RememberAsync handed back, which was never read from the
        // database. Postgres keeps timestamptz to the microsecond and .NET to the 100 ns tick, so a time
        // with sub-microsecond ticks would be stored as one instant and compared as another, and the
        // forget would never match. The time is held to whole microseconds where it is written, so the
        // row in hand is the row stored. SQLite (these tests) keeps every tick, which is why the stamp is
        // asserted directly rather than only through the forget.
        var (tenantId, _, _) = await SeedAsync();
        var clock = new FixedTime(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero).AddTicks(1_234_567));
        var written = await api.UseAsync(db => new CompanyMappings(db, tenantId, clock).RememberAsync("Acme Dental", new ScreenTail.Api.Providers.CompanyRef("7", "Acme Dental"), "exact"));

        Assert.Equal(0, written.CreatedAt.Ticks % 10);
        Assert.Equal(clock.GetUtcNow().AddTicks(-7), written.CreatedAt);

        // Confirmed again in between, at a later instant: the row in hand is out of date and stays put.
        clock.Now = clock.Now.AddTicks(25);
        var again = await api.UseAsync(db => new CompanyMappings(db, tenantId, clock).RememberAsync("Acme Dental", new ScreenTail.Api.Providers.CompanyRef("7", "Acme Dental"), "exact"));
        Assert.False(await api.UseAsync(db => new CompanyMappings(db, tenantId, clock).ForgetIfUnchangedAsync(written)));

        // The row in hand from the latest write is the row stored, and goes.
        Assert.True(await api.UseAsync(db => new CompanyMappings(db, tenantId, clock).ForgetIfUnchangedAsync(again)));
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("exact")]
    public async Task AMappingReconfirmedSinceItWasReadIsKept(string confidence)
    {
        // The same company, confirmed again between the read and the forget: by a person in Settings
        // (manual), or by a publish's exact match. It names what it named before, so only when it was
        // made says it is a new decision, and that decision is not thrown away with the stale read.
        var (tenantId, _, _) = await SeedAsync();
        _ = await RememberDirectlyAsync(tenantId, "Acme Dental", "7", "Acme Dental");
        var stale = await api.UseAsync(db => new CompanyMappings(db, tenantId, TimeProvider.System).FindAsync("Acme Dental"));
        await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        _ = await api.UseAsync(db => new CompanyMappings(db, tenantId, TimeProvider.System).RememberAsync("Acme Dental", new ScreenTail.Api.Providers.CompanyRef("7", "Acme Dental"), confidence));

        var forgot = await api.UseAsync(db => new CompanyMappings(db, tenantId, TimeProvider.System).ForgetIfUnchangedAsync(stale!));

        Assert.False(forgot);
        var kept = await api.UseAsync(db => db.CompanyMappings.SingleAsync(m => m.TenantId == tenantId));
        Assert.Equal(("7", confidence), (kept.DocCompanyId, kept.Confidence));
    }

    [Fact]
    public async Task AStaleMappingRewrittenSinceItWasReadIsKept()
    {
        // The stale row is forgotten as it was read. If a person or an exact match rewrote it in the
        // meantime, the new decision is theirs and is not thrown away with the old one.
        var (tenantId, _, _) = await SeedAsync();
        _ = await RememberDirectlyAsync(tenantId, "Acme Dental", "7", "Acme Dental Group");
        var stale = await api.UseAsync(db => new CompanyMappings(db, tenantId, TimeProvider.System).FindAsync("Acme Dental"));
        _ = await api.UseAsync(db => new CompanyMappings(db, tenantId, TimeProvider.System).RememberAsync("Acme Dental", new ScreenTail.Api.Providers.CompanyRef("12", "Acme Dental"), "manual"));

        var forgot = await api.UseAsync(db => new CompanyMappings(db, tenantId, TimeProvider.System).ForgetIfUnchangedAsync(stale!));
        var unchanged = await api.UseAsync(db => new CompanyMappings(db, tenantId, TimeProvider.System).FindAsync("Acme Dental"));
        var gone = await api.UseAsync(db => new CompanyMappings(db, tenantId, TimeProvider.System).ForgetIfUnchangedAsync(unchanged!));

        Assert.False(forgot);
        Assert.Equal(("12", "Acme Dental"), (unchanged!.DocCompanyId, unchanged.DocCompanyName));
        Assert.True(gone);
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
    }

    [Fact]
    public async Task AMappingStillNamingItsCompanyIsUsedWithoutAskingHuduAgain()
    {
        // The check reads the same cached list the article check reads, so a trusted mapping costs no
        // extra request to Hudu.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, _) = await ConnectedClientAsync(host);
        using var mapped = await client.PutAsJsonAsync(Companies, new MapCompanyRequest("Nowhere Inc", "9"), TestContext.Current.CancellationToken);
        var asked = hudu.Requests.Count(IsCompanyList);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle("Nowhere Inc"), TestContext.Current.CancellationToken);
        var kb = Assert.Single((await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken))!.Results);

        Assert.True(kb.Ok);
        Assert.Equal(asked, hudu.Requests.Count(IsCompanyList));
    }

    [Theory]
    [InlineData("https://acme.huducloud.com")]
    [InlineData("https://ACME.huducloud.com/")]
    [InlineData("  https://acme.huducloud.com/  ")]
    public async Task ANewKeyForTheSameSiteKeepsTheMappings(string sameSite)
    {
        // The same Hudu numbers its companies the same way whatever key reads them, so rotating the key
        // keeps what a person decided. The spelling of the site does not make it another site.
        using var host = Host(new ScriptedConnectWise(), new ScriptedHudu());
        var (client, tenantId) = await ConnectedClientAsync(host);
        using var mapped = await client.PutAsJsonAsync(Companies, new MapCompanyRequest("Nowhere Inc", "9"), TestContext.Current.CancellationToken);

        using var rotated = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest(sameSite, "hudu-key-0000"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
        Assert.Equal("9", (await api.UseAsync(db => db.CompanyMappings.SingleAsync(m => m.TenantId == tenantId))).DocCompanyId);
    }

    [Fact]
    public async Task RemovingHuduForgetsItsMappingsAndItsCompanies()
    {
        // S-71 and T-214 on DELETE. Connecting again afterwards, even to the same site, starts from
        // nothing: the removal is the person saying these decisions no longer hold.
        var hudu = new ScriptedHudu();
        using var host = Host(new ScriptedConnectWise(), hudu);
        var (client, tenantId) = await ConnectedClientAsync(host);
        using var mapped = await client.PutAsJsonAsync(Companies, new MapCompanyRequest("Nowhere Inc", "9"), TestContext.Current.CancellationToken);
        var asked = hudu.Requests.Count(IsCompanyList);

        using var removed = await client.DeleteAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), TestContext.Current.CancellationToken);
        var left = await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId));
        using var back = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://acme.huducloud.com", "hudu-key-5678"), TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<CompanyMappingsResponse>(Companies, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, left);
        Assert.Empty(list!.Mappings);
        Assert.True(hudu.Requests.Count(IsCompanyList) > asked);
    }

    [Fact]
    public async Task ARefusedReplacementForgetsNothing()
    {
        // A PUT that is turned away changed nothing, so it takes nothing with it either.
        using var host = Host(new ScriptedConnectWise(), new ScriptedHudu());
        var (client, tenantId) = await ConnectedClientAsync(host);
        using var mapped = await client.PutAsJsonAsync(Companies, new MapCompanyRequest("Nowhere Inc", "9"), TestContext.Current.CancellationToken);

        using var refused = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("http://other.huducloud.com", "hudu-key-9999"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(1, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
    }

    [Fact]
    public async Task ConnectingHuduWithNothingBeforeItStartsWithNoMappings()
    {
        // Mappings with no Hudu behind them were made on a site nobody can name any more; the first
        // connection does not inherit them.
        using var host = Host(new ScriptedConnectWise(), new ScriptedHudu());
        var (client, tenantId) = await ConnectedClientAsync(host, hudu: false);
        _ = await api.UseAsync(async db =>
        {
            db.CompanyMappings.Add(new CompanyMapping { Id = Guid.NewGuid(), TenantId = tenantId, PsaCompany = "Acme Dental", DocCompanyId = "7", DocCompanyName = "Acme Dental", Confidence = "manual", CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });

        using var connected = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://acme.huducloud.com", "hudu-key-5678"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, connected.StatusCode);
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
    }

    [Fact]
    public async Task RemovingConnectWiseLeavesTheHuduMappingsAlone()
    {
        // The mappings belong to the documentation platform; the PSA coming and going is not a reason
        // to throw away what a person decided about Hudu.
        using var host = Host(new ScriptedConnectWise(), new ScriptedHudu());
        var (client, tenantId) = await ConnectedClientAsync(host);
        using var mapped = await client.PutAsJsonAsync(Companies, new MapCompanyRequest("Nowhere Inc", "9"), TestContext.Current.CancellationToken);

        using var removed = await client.DeleteAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), TestContext.Current.CancellationToken);
        using var back = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://eu.myconnectwise.net", "acme+PUB:PRIV-0000"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(1, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == tenantId)));
    }

    private static bool IsCompanyList(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/api/v1/companies", StringComparison.Ordinal);

    private static bool IsArticle(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal);

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

    /// <param name="clock">The service's clock in place of the machine's, when given.</param>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host(ScriptedConnectWise cw, ScriptedHudu hudu, Action<PublishOptions>? publish = null, HeldClock? clock = null) => api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("ConnectWise:ClientId", "11111111-2222-3333-4444-555555555555");
        _ = builder.ConfigureTestServices(services =>
        {
            services.Configure<HttpClientFactoryOptions>(nameof(ConnectWiseProvider), options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = cw));
            services.Configure<HttpClientFactoryOptions>(nameof(HuduProvider), options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = hudu));
            if (publish is not null)
            {
                services.Configure(publish);
            }

            clock?.Install(services);
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

    /// <summary>A mapping written the way a publish racing a repoint would write it: straight in, after the move.</summary>
    private Task<int> RememberDirectlyAsync(Guid tenantId, string psaCompany, string docCompanyId, string docCompanyName) => api.UseAsync(async db =>
    {
        db.CompanyMappings.Add(new CompanyMapping { Id = Guid.NewGuid(), TenantId = tenantId, PsaCompany = psaCompany, DocCompanyId = docCompanyId, DocCompanyName = docCompanyName, Confidence = "exact", CreatedAt = DateTimeOffset.UtcNow });
        return await db.SaveChangesAsync();
    });

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
