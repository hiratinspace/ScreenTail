using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
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

        // The fields with defaults, read rather than defaulted: the contract's one frame is one document
        // on the ticket, its internal note type is the note's flag, and its billable time is billed.
        Assert.Single(cw.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/system/documents", StringComparison.Ordinal));
        var note = JsonDocument.Parse(BodyOf(cw, "/notes")).RootElement;
        var entry = JsonDocument.Parse(BodyOf(cw, "/time/entries")).RootElement;
        Assert.True(note.GetProperty("internalAnalysisFlag").GetBoolean());
        Assert.EndsWith("reviewed by A Technician.", note.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("Billable", entry.GetProperty("billableOption").GetString());
        Assert.Equal("2026-09-24T14:02:00Z", entry.GetProperty("timeStart").GetString());
        Assert.Equal("2026-09-24T14:32:00Z", entry.GetProperty("timeEnd").GetString());
    }

    [Fact]
    public async Task EveryFieldWithADefaultIsReadFromTheContract()
    {
        // T-219: the contract's note type, billable and footer equal the bundle's defaults, so a renamed
        // field would have been defaulted and the test above would not have noticed. Flipped, each one
        // has to arrive to be seen.
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);
        var request = JsonNode.Parse(Contract("request"))!;
        request["note_type"] = "discussion";
        request["billable"] = false;
        request["footer"] = false;
        request["reviewer"] = "Someone Else";

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/publish", UriKind.Relative),
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var note = JsonDocument.Parse(BodyOf(cw, "/notes")).RootElement;
        var entry = JsonDocument.Parse(BodyOf(cw, "/time/entries")).RootElement;
        Assert.False(note.GetProperty("internalAnalysisFlag").GetBoolean());
        Assert.DoesNotContain("Drafted with ScreenTail", note.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("DoNotBill", entry.GetProperty("billableOption").GetString());
        Assert.StartsWith("Problem\n", entry.GetProperty("notes").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheContractNamesExactlyTheFieldsTheBundleReads()
    {
        // Both directions: nothing in the contract is ignored by the bundle, and nothing the bundle
        // reads is missing from the contract. The note inside is the draft, whose own contract
        // (summarize-request.v1.json) holds it to its fields.
        var strict = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        var request = JsonDocument.Parse(Contract("request")).RootElement;

        _ = JsonSerializer.Deserialize<PublishResponse>(Contract("response"), strict);

        Assert.Equal(WireNames<PublishBundle>(), request.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(WireNames<PublishFrame>(), request.GetProperty("frames")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(JsonDocument.Parse(Contract("response")).RootElement.GetProperty("results").EnumerateArray(), row =>
            Assert.Equal(WireNames<PublishResultJson>(), row.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task TheResponseIsTheContractsShape()
    {
        // What the endpoint writes, field for field, is what the contract's response shows, for a
        // destination that went, one that was refused, and one that landed without a screenshot.
        var cw = new ScriptedConnectWise { FailingRoute = ("/system/documents", HttpStatusCode.BadRequest) };
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);
        var contractRows = JsonDocument.Parse(Contract("response")).RootElement.GetProperty("results").EnumerateArray().ToList();
        var request = JsonNode.Parse(Contract("request"))!;
        request["destinations"] = new JsonArray("ticket_note", "time_entry", "kb_article");

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/publish", UriKind.Relative),
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var written = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(["results"], written.EnumerateObject().Select(p => p.Name));
        var rows = written.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        var expected = contractRows[0].EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(rows, row => Assert.Equal(expected, row.EnumerateObject().Select(p => p.Name)));
        Assert.Contains(contractRows, r => r.GetProperty("kind").ValueKind == JsonValueKind.String && r.GetProperty("kind").GetString() == "partial" && r.GetProperty("ok").GetBoolean());
        Assert.Equal("partial", rows[0].GetProperty("kind").GetString());
        Assert.True(rows[0].GetProperty("ok").GetBoolean());
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
        Assert.StartsWith("Problem\n", entry.GetProperty("internalNotes").GetString(), StringComparison.Ordinal);
        Assert.False(entry.TryGetProperty("notes", out _));
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
    public async Task AnAttachmentRefusedAfterTheNoteLandedIsReportedAsLanded()
    {
        // T-211, S-69, E-39: the note is on the ticket and a screenshot is not. The client's Retry sends
        // every destination that is not ok, so the note is ok, with its id, and says what is missing.
        var cw = new ScriptedConnectWise { FailingRoute = ("/system/documents", HttpStatusCode.BadRequest) };
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle() with { Frames = [Frame("f-1"), Frame("f-2")] }, TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var note = published!.Results.Single(r => r.Destination == "ticket_note");
        Assert.True(note.Ok);
        Assert.Equal("90001", note.Id);
        Assert.Equal("partial", note.Kind);
        Assert.False(note.Retryable);
        Assert.Contains("90001", note.Error, StringComparison.Ordinal);
        Assert.Contains("2 of 2 screenshots", note.Error, StringComparison.Ordinal);
        Assert.True(published.Results.Single(r => r.Destination == "time_entry").Ok);
        Assert.Single(cw.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/notes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APublishThatRunsOutOfTimeAnswersWhatLanded()
    {
        // S-68, E-42: the time entry never answers. The note still lands, because the destinations go
        // together rather than one after another, and the publish answers at its own deadline with both
        // results instead of a 504 that loses the note's.
        var cw = new ScriptedConnectWise { HangingRoute = "/time/entries" };
        using var host = Host(cw, publish => publish.Deadline = TimeSpan.FromMilliseconds(500));
        using var client = await ConnectedClientAsync(host);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle() with { Destinations = ["time_entry", "ticket_note"] }, TestContext.Current.CancellationToken);
        var published = await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["time_entry", "ticket_note"], published!.Results.Select(r => r.Destination));
        var time = published.Results[0];
        Assert.False(time.Ok);
        Assert.Equal("timed_out", time.Kind);
        Assert.False(time.Retryable);
        Assert.Contains("may already be on the ticket", time.Error, StringComparison.Ordinal);
        Assert.True(published.Results[1].Ok);
        Assert.Equal("90001", published.Results[1].Id);
    }

    [Fact]
    public async Task ThePublishDeadlineEndsBeforeTheRequestDoes()
    {
        // The request's own timeout is a 504 with nothing in it; the publish's deadline has to come
        // first for the answer to carry what landed.
        using var host = Host(new ScriptedConnectWise());

        var request = host.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value.DefaultPolicy!.Timeout!.Value;
        var publish = host.Services.GetRequiredService<IOptions<PublishOptions>>().Value.Deadline;

        Assert.True(publish < request - TimeSpan.FromSeconds(10), $"publish {publish} against request {request}");
        Assert.True(publish >= TimeSpan.FromSeconds(30), "Long enough for a slow PSA to take a note with its screenshots.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task AMissingReviewerIsTheSignedInTechnician(string? reviewer)
    {
        // T-411: the shipped client sends no reviewer, so this is the only real path; the technician's
        // name here differs from every bundle in this file so the fallback cannot pass by coincidence.
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host, displayName: "Sam Rivera");

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), Bundle() with { Reviewer = reviewer, Destinations = ["ticket_note"] }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = JsonDocument.Parse(BodyOf(cw, "/notes")).RootElement.GetProperty("text").GetString();
        Assert.EndsWith("Drafted with ScreenTail, reviewed by Sam Rivera.", text, StringComparison.Ordinal);
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
    [InlineData("session_id", "A session id is required.")]
    [InlineData("ticket_id", "A ticket id is required.")]
    [InlineData("note_type", "The note type must be internal or discussion.")]
    [InlineData("minutes", "The time entry must be between 0 and 1440 minutes.")]
    [InlineData("no_destinations", "At least one destination is required.")]
    [InlineData("destination", "A destination must be ticket_note, time_entry or kb_article.")]
    [InlineData("no_minutes", "A time entry of no minutes cannot be logged.")]
    [InlineData("note", "The note is required.")]
    [InlineData("frames_null", "The frames must be present, even when empty.")]
    [InlineData("frames", "A publish may carry at most 25 frames.")]
    [InlineData("frame_id", "Every frame needs its own id.")]
    [InlineData("media_type", "A frame must be a JPEG or a PNG.")]
    [InlineData("image_empty", "A frame's image must be base64 and under a megabyte.")]
    [InlineData("image_base64", "A frame's image must be base64 and under a megabyte.")]
    [InlineData("image_size", "A frame's image must be base64 and under a megabyte.")]
    public async Task EveryWrongShapeIsRefusedWithItsOwnReason(string what, string reason)
    {
        // T-220: each rule, by its own words, and nothing sent. Without the frame rules a bad frame was
        // found mid-publish, after the time entry had landed, as a 500 that Retry answered with a
        // second time entry.
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        using var client = await ConnectedClientAsync(host);
        var bundle = JsonSerializer.SerializeToNode(Bundle() with { Destinations = ["time_entry", "ticket_note"], Frames = [Frame("f-1")] })!.AsObject();
        switch (what)
        {
            case "session_id": bundle["session_id"] = string.Empty; break;
            case "ticket_id": bundle["ticket_id"] = "48 213"; break;
            case "note_type": bundle["note_type"] = "public"; break;
            case "minutes": bundle["minutes"] = 1441; break;
            case "no_destinations": bundle["destinations"] = new JsonArray(); break;
            case "destination": bundle["destinations"] = new JsonArray("email"); break;
            case "no_minutes": bundle["minutes"] = 0; break;
            case "note": bundle["note"] = null; break;
            case "frames_null": bundle["frames"] = null; break;
            case "frames": bundle["frames"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 26).Select(i => Frame($"f-{i}")).ToList()); break;
            case "frame_id": bundle["frames"] = JsonSerializer.SerializeToNode(new[] { Frame("f-1"), Frame("f-1") }); break;
            case "media_type": bundle["frames"]![0]!["media_type"] = "image/gif"; break;
            case "image_empty": bundle["frames"]![0]!["image"] = string.Empty; break;
            case "image_base64": bundle["frames"]![0]!["image"] = "not*base64!"; break;
            default: bundle["frames"]![0]!["image"] = new string('A', PublishLimits.MaxFrameChars + 4); break;
        }

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/publish", UriKind.Relative),
            new StringContent(bundle.ToJsonString(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(reason, answer.GetProperty("message").GetString());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARevokedDevicePublishesNothing(bool malformed)
    {
        // A revoked laptop still holds a signed token. Permission before shape, so a malformed bundle is
        // a 401 too and the rules stay unread; nothing reaches the PSA either way.
        var cw = new ScriptedConnectWise();
        using var host = Host(cw);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Bearer(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        _ = await api.UseAsync(db => db.Devices
            .Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));
        var bundle = malformed ? Bundle() with { TicketId = string.Empty } : Bundle();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), bundle, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(cw.Requests);
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

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host(ScriptedConnectWise cw, Action<PublishOptions>? publish = null) => api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("ConnectWise:ClientId", "11111111-2222-3333-4444-555555555555");
        _ = builder.ConfigureTestServices(services =>
        {
            _ = services.Configure<HttpClientFactoryOptions>(
                nameof(ConnectWiseProvider),
                options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = cw));
            if (publish is not null)
            {
                _ = services.Configure(publish);
            }
        });
    });

    /// <summary>The body of the one request whose path ends in this.</summary>
    private static string BodyOf(ScriptedConnectWise cw, string pathSuffix)
    {
        var requests = cw.Requests;
        var bodies = cw.Bodies;
        return bodies[requests.FindIndex(r => r.RequestUri!.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal))]!;
    }

    /// <summary>The JSON names a type is read and written by, sorted.</summary>
    private static IEnumerable<string> WireNames<T>() =>
        typeof(T).GetProperties()
            .Select(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name)
            .OfType<string>()
            .Order(StringComparer.Ordinal);

    /// <summary>A device whose tenant has a ConnectWise credential in the vault.</summary>
    private async Task<HttpClient> ConnectedClientAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, string displayName = "A Technician")
    {
        var (tenantId, userId, deviceId) = await SeedAsync(displayName);
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

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync(string displayName = "A Technician")
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme IT", Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId:N}@acme.example", DisplayName = displayName, CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = tenantId, UserId = userId, Name = "TECH-LAPTOP", TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return (tenantId, userId, deviceId);
    }
}
