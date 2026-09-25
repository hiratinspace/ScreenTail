using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
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
using ScreenTail.Api.Publish;
using ScreenTail.Api.Summarize;
using ScreenTail.Api.Tests.Providers.ConnectWise;

namespace ScreenTail.Api.Tests.Publish;

/// <summary>
/// POST /v1/sessions/publish (ST-093, ST-094): the reviewed note, its screenshots and the time entry
/// reach the tenant's PSA, each destination answered on its own so a retry sends only what failed, and
/// nothing about the session is written here (INV-7).
/// </summary>
public sealed class PublishEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task TheContractIsWhatPublishAccepts()
    {
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/publish", UriKind.Relative),
            new StringContent(Contract("request"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["ticket_note", "time_entry"], published!.Results.Select(r => r.Destination));
        Assert.All(published.Results, r => Assert.True(r.Ok));
        Assert.Equal("90001", published.Results[0].Id);
    }

    [Fact]
    public async Task TheNoteOnTheTicketIsTheEditorsTextWithItsScreenshotsAttached()
    {
        // ST-093 AC1 and AC2: the text matches the editor's layout, three included frames are three
        // documents on the ticket, and the redaction marker survives.
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);
        var bundle = Bundle() with
        {
            Note = Note() with { Problem = "The password was [REDACTED]." },
            Frames = [Frame("f-1"), Frame("f-2"), Frame("f-3")],
            Destinations = ["ticket_note"],
        };

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), bundle, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var note = JsonDocument.Parse(cw.Bodies[0]!).RootElement;
        var text = note.GetProperty("text").GetString()!;
        Assert.StartsWith("Problem\nThe password was [REDACTED].\n\nSteps\n1. ", text, StringComparison.Ordinal);
        Assert.EndsWith("Drafted with ScreenTail, reviewed by A Technician.", text, StringComparison.Ordinal);
        Assert.True(note.GetProperty("internalAnalysisFlag").GetBoolean());
        Assert.Equal(3, cw.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/system/documents", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheTimeEntryIsTheMinutesAsAWindowWithTheNoteAsDescription()
    {
        // ST-094 AC1: 30 min → a 0.5 h entry, the note as its description.
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle() with { Destinations = ["time_entry"] }, TestContext.Current.CancellationToken);

        var entry = JsonDocument.Parse(Assert.Single(cw.Bodies)!).RootElement;
        Assert.Equal("2026-09-24T14:02:00Z", entry.GetProperty("timeStart").GetString());
        Assert.Equal("2026-09-24T14:32:00Z", entry.GetProperty("timeEnd").GetString());
        Assert.StartsWith("Problem\n", entry.GetProperty("notes").GetString(), StringComparison.Ordinal);
        Assert.Equal("Billable", entry.GetProperty("billableOption").GetString());
    }

    [Fact]
    public async Task APartialFailureAnswersEachDestinationOnItsOwn()
    {
        // ST-094 AC3 from the server's side: the note landed, the time entry did not, and the response
        // says which so the client's Retry sends only the time entry.
        var cw = new ScriptedConnectWise { FailingRoute = ("/time/entries", HttpStatusCode.Forbidden) };
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle(), TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var note = published!.Results.Single(r => r.Destination == "ticket_note");
        var time = published.Results.Single(r => r.Destination == "time_entry");
        Assert.True(note.Ok);
        Assert.False(time.Ok);
        Assert.Equal("forbidden", time.Kind);
        Assert.Contains("administrator", time.Error, StringComparison.Ordinal);
        Assert.False(time.Retryable);
    }

    [Fact]
    public async Task ARetryOfTheTimeEntryPostsNoSecondNote()
    {
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle() with { Destinations = ["time_entry"] }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(cw.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/notes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKnowledgeBaseDestinationWithNoPlatformSaysSo()
    {
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle() with { Destinations = ["kb_article"] }, TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        var kb = Assert.Single(published!.Results);
        Assert.False(kb.Ok);
        Assert.Equal("not_configured", kb.Kind);
        Assert.Contains("documentation platform", kb.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoPsaThereIsNowhereToPublishAndItSaysSo()
    {
        using var host = Host(new ScriptedConnectWise());
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Bearer(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("no_psa", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ticket_id")]
    [InlineData("destination")]
    [InlineData("frames")]
    [InlineData("note_type")]
    public async Task AShapeThatIsWrongIsRefusedBeforeAnyProviderIsAsked(string what)
    {
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);
        var bundle = what switch
        {
            "ticket_id" => Bundle() with { TicketId = string.Empty },
            "destination" => Bundle() with { Destinations = ["email"] },
            "frames" => Bundle() with { Frames = [.. Enumerable.Range(0, 26).Select(i => Frame($"f-{i}"))] },
            _ => Bundle() with { NoteType = "public" },
        };

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), bundle, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(cw.Requests);
    }

    [Fact]
    public async Task PublishingLeavesTheDatabaseExactlyAsItWas()
    {
        // INV-7: the note and the screenshots pass through and are not kept.
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);
        var before = await CountEverythingAsync();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle() with { Frames = [Frame("f-1")] }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await CountEverythingAsync());
    }

    [Fact]
    public async Task WithoutATokenNothingIsPublished()
    {
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static PublishBundle Bundle() => new()
    {
        SessionId = "s-0001",
        TicketId = "48213",
        NoteType = "internal",
        Minutes = 30,
        StartedAt = new DateTimeOffset(2026, 9, 24, 14, 2, 0, TimeSpan.Zero),
        Billable = true,
        Reviewer = "A Technician",
        Footer = true,
        Destinations = ["ticket_note", "time_entry"],
        Note = Note(),
        Frames = [],
    };

    private static DraftJson Note() => new()
    {
        Problem = "Printer offline in reception.",
        Steps = [new DraftStepJson { Text = "Checked the spooler service; it was stopped.", Confidence = "high", FrameRefs = ["f-1"] }],
        Result = "Printing works again.",
        FollowUps = [],
        SuggestedTitle = "Printer offline — Acme Dental",
        SuggestedTimeMinutes = 23,
        KbCandidate = false,
        KbReason = "Not a KB candidate: one-off fix",
        Source = "cloud",
        PromptVersion = "note_v1",
    };

    private static PublishFrame Frame(string id) => new(id, "/9j/4AAQSkZJRg==", "image/jpeg");

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host(ScriptedConnectWise cw) => api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("ConnectWise:ClientId", "11111111-2222-3333-4444-555555555555");
        _ = builder.ConfigureTestServices(services => services.Configure<HttpClientFactoryOptions>(
            nameof(ConnectWiseProvider),
            options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = cw)));
    });

    /// <summary>A device whose tenant has a ConnectWise credential in the vault.</summary>
    private async Task<HttpClient> ConnectedClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        var client = Bearer(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        return client;
    }

    private static HttpClient Bearer(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string Contract(string property)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Api.Tests.publish-request.v1.json")
            ?? throw new InvalidOperationException("The publish contract fixture is not embedded in this assembly.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty(property).GetRawText();
    }

    private Task<string> CountEverythingAsync() => api.UseAsync(async db =>
    {
        var counts = new[]
        {
            await db.Tenants.CountAsync(),
            await db.Users.CountAsync(),
            await db.Devices.CountAsync(),
            await db.Integrations.CountAsync(),
            await db.Policies.CountAsync(),
            await db.SessionMetrics.CountAsync(),
            await db.DraftCosts.CountAsync(),
        };
        return string.Join(",", counts);
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
