using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Net;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Net;

/// <summary>
/// The service's way to the backend for publishing (ST-093): three calls, one bearer token, the egress
/// guard's user-initiated purpose, and every refusal as words the technician can read rather than a
/// status code.
/// </summary>
public sealed class PsaGatewayTests
{
    [Fact]
    public async Task ClientWritesThePublishContract()
    {
        // shared/contracts/publish-request.v1.json is what the backend accepts; this is the pair test on
        // the writing side. Field names and shapes, not values.
        using var handler = new RecordingHandler(Json(new { results = Array.Empty<object>() }));
        var gateway = Gateway(handler);

        _ = await gateway.PublishAsync(Wire(), TestContext.Current.CancellationToken);

        Assert.Equal(Shape(JsonDocument.Parse(Contract("request")).RootElement), Shape(Sent(handler)));
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("a-device-token", handler.Authorization?.Parameter);
        Assert.EndsWith("/v1/sessions/publish", handler.Asked!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBackendsPerDestinationAnswerComesThrough()
    {
        using var handler = new RecordingHandler(Json(JsonDocument.Parse(Contract("response")).RootElement));

        var answer = await Gateway(handler).PublishAsync(Wire(), TestContext.Current.CancellationToken);

        Assert.True(answer.Ok);
        Assert.Equal(3, answer.Value!.Count);
        Assert.True(answer.Value[0].Ok);
        Assert.Equal("90001", answer.Value[0].Id);
        Assert.False(answer.Value[1].Ok);
        Assert.Equal("forbidden", answer.Value[1].Kind);

        // Landed without a screenshot: a success, with its id and link, and the words about what is missing.
        Assert.True(answer.Value[2].Ok);
        Assert.Equal(("5101", "partial", false), (answer.Value[2].Id, answer.Value[2].Kind, answer.Value[2].Retryable));
        Assert.Equal("https://acme.huducloud.com/a/printer-offline-5101", answer.Value[2].Link);
        Assert.StartsWith("The article was published as 5101, but 1 of 1 screenshots did not attach", answer.Value[2].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADestinationThatMayHaveLandedComesThroughAsNotRetryable()
    {
        // The contract's two answers for a destination that was sent and never confirmed: the deadline
        // came first, or the PSA's answer could not be read. Each is not ok, has no id, and is not
        // retryable, and its words (which say to check the ticket first) reach the technician unchanged.
        using var handler = new RecordingHandler(Json(JsonDocument.Parse(Contract("response_uncertain")).RootElement));

        var answer = await Gateway(handler).PublishAsync(Wire(), TestContext.Current.CancellationToken);

        Assert.True(answer.Ok);
        Assert.Equal(["unknown", "timed_out"], answer.Value!.Select(r => r.Kind));
        Assert.All(answer.Value!, r =>
        {
            Assert.False(r.Ok);
            Assert.Null(r.Id);
            Assert.False(r.Retryable);
            Assert.Matches("Check .+ before (you publish|publishing)", r.Error);
        });
    }

    [Fact]
    public async Task ASearchAsksWithTheQueryAndMapsTheRows()
    {
        using var handler = new RecordingHandler(Json(new { tickets = new[] { new { id = "48213", summary = "Printer offline", company = "Acme Dental", status = "In Progress" } } }));

        var answer = await Gateway(handler).SearchTicketsAsync("printer", TestContext.Current.CancellationToken);

        Assert.True(answer.Ok);
        Assert.Equal("q=printer", handler.Asked!.Query.TrimStart('?'));
        var row = Assert.Single(answer.Value!);
        Assert.Equal(("48213", "Printer offline", "Acme Dental"), (row.Id, row.Summary, row.Company));
    }

    [Fact]
    public async Task AnEmptyQueryIsAskedTooBecauseItMeansTheRecentTickets()
    {
        using var handler = new RecordingHandler(Json(new { tickets = new[] { new { id = "48213", summary = "Printer offline", company = "Acme Dental", status = "New" } } }));

        var answer = await Gateway(handler).SearchTicketsAsync(string.Empty, TestContext.Current.CancellationToken);

        Assert.True(answer.Ok);
        Assert.Equal("48213", Assert.Single(answer.Value!).Id);
        Assert.Equal("?q=", handler.Asked!.Query);
    }

    [Fact]
    public async Task IntegrationsComeBackAsProvidersWithTheirHints()
    {
        using var handler = new RecordingHandler(Json(new { integrations = new[] { new { provider = "connectwise", siteUrl = "https://na.myconnectwise.net", secret = "••••1234", connectedAt = "2026-09-25T00:00:00Z", lastCheckedAt = (string?)null, lastError = (string?)null } } }));

        var answer = await Gateway(handler).IntegrationsAsync(TestContext.Current.CancellationToken);

        Assert.True(answer.Ok);
        var row = Assert.Single(answer.Value!);
        Assert.Equal("connectwise", row.Provider);
        Assert.Equal("••••1234", row.Secret);
    }

    [Fact]
    public async Task IntegrationDetailsCarryWhenTheyWereCheckedAndWhatWentWrong()
    {
        // Settings → Integrations (ST-082) reads the same list the publish pane does, and the rest of it.
        using var handler = new RecordingHandler(Json(new { integrations = new[] { new { provider = "connectwise", siteUrl = "https://na.myconnectwise.net", secret = "••••1234", connectedAt = "2026-09-25T00:00:00Z", lastCheckedAt = "2026-09-25T10:00:00Z", lastError = "ConnectWise rejected the key. Update it in Settings → Integrations." } } }));

        var answer = await Gateway(handler).IntegrationDetailsAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(answer.Value!);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero), row.LastCheckedAt);
        Assert.StartsWith("ConnectWise rejected", row.LastError, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero), row.ConnectedAt);
    }

    [Fact]
    public async Task StoringCheckingRemovingAndUnmappingHitTheirRoutes()
    {
        using var storing = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var stored = await Gateway(storing).StoreIntegrationAsync("connectwise", "https://na.myconnectwise.net", "acme+PUB:PRIV", TestContext.Current.CancellationToken);
        Assert.True(stored.Ok);
        Assert.Equal(HttpMethod.Put, storing.Method);
        Assert.EndsWith("/v1/integrations/connectwise", storing.Asked!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"siteUrl\":\"https://na.myconnectwise.net\"", storing.Body, StringComparison.Ordinal);
        Assert.Contains("\"secret\":\"acme", storing.Body, StringComparison.Ordinal);
        Assert.Contains("PRIV", storing.Body, StringComparison.Ordinal);

        using var checking = new RecordingHandler(Json(new { ok = false, message = "ConnectWise rejected the key. Update it in Settings → Integrations.", checkedAt = "2026-09-25T10:00:00Z" }));
        var checkAnswer = await Gateway(checking).CheckIntegrationAsync("connectwise", TestContext.Current.CancellationToken);
        Assert.True(checkAnswer.Ok);
        Assert.False(checkAnswer.Value!.Ok);
        Assert.StartsWith("ConnectWise rejected", checkAnswer.Value.Message, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, checking.Method);
        Assert.EndsWith("/v1/integrations/connectwise/check", checking.Asked!.AbsolutePath, StringComparison.Ordinal);

        using var removing = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var removed = await Gateway(removing).RemoveIntegrationAsync("hudu", TestContext.Current.CancellationToken);
        Assert.True(removed.Ok);
        Assert.Equal(HttpMethod.Delete, removing.Method);
        Assert.EndsWith("/v1/integrations/hudu", removing.Asked!.AbsolutePath, StringComparison.Ordinal);

        using var unmapping = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var unmapped = await Gateway(unmapping).UnmapCompanyAsync("Acme Dental", TestContext.Current.CancellationToken);
        Assert.True(unmapped.Ok);
        Assert.Equal(HttpMethod.Delete, unmapping.Method);
        Assert.EndsWith("/v1/integrations/hudu/companies/Acme%20Dental", unmapping.Asked!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompanyMappingsAreListedAndAMappingIsPut()
    {
        // ST-097's endpoints, as the service calls them for the pane's prompt.
        using var listing = new RecordingHandler(Json(new { companies = new[] { new { id = "7", name = "Acme Dental" } }, mappings = new[] { new { psaCompany = "Acme", docCompanyId = "7", docCompanyName = "Acme Dental", confidence = "manual" } } }));
        var companies = await Gateway(listing).CompanyMappingsAsync(TestContext.Current.CancellationToken);

        using var putting = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var mapped = await Gateway(putting).MapCompanyAsync("Acme Dental", "7", TestContext.Current.CancellationToken);

        Assert.True(companies.Ok);
        Assert.Equal("Acme Dental", Assert.Single(companies.Value!.Companies).Name);
        Assert.Equal("manual", Assert.Single(companies.Value.Mappings).Confidence);
        Assert.EndsWith("/v1/integrations/hudu/companies", listing.Asked!.AbsolutePath, StringComparison.Ordinal);
        Assert.True(mapped.Ok);
        Assert.Equal(HttpMethod.Put, putting.Method);
        Assert.Contains("\"psaCompany\":\"Acme Dental\"", putting.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalIsTheBackendsWordsNotAStatusCode()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = JsonContent.Create(new { error = "no_psa", message = "Connect a PSA to publish." }),
        });

        var answer = await Gateway(handler).PublishAsync(Wire(), TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Equal("Connect a PSA to publish.", answer.Refusal);
    }

    [Fact]
    public async Task ABackendThatDoesNotAnswerIsARefusalTheTechnicianCanRead()
    {
        using var handler = new ThrowingHandler();

        var answer = await Gateway(handler).SearchTicketsAsync("printer", TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Equal("The backend did not answer.", answer.Refusal);
    }

    [Fact]
    public async Task WithoutABackendOrATokenNothingIsSent()
    {
        using var handler = new RecordingHandler(Json(new { }));
        var noAddress = new PsaGateway(new HttpClient(handler), () => "a-device-token");
        var noToken = new PsaGateway(new HttpClient(handler) { BaseAddress = new Uri("https://api.screentail.example/") }, () => null);

        var first = await noAddress.IntegrationsAsync(TestContext.Current.CancellationToken);
        var second = await noToken.IntegrationsAsync(TestContext.Current.CancellationToken);

        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task APublishWritesTheBundleSentRowWithItsBytesAndHost(HttpStatusCode status)
    {
        // A publish carries the note and its screenshots off the machine, and the audit export's "bytes
        // sent, and to which host" had a row only for the draft. Accepted or refused, the bytes left.
        using var handler = new RecordingHandler(status == HttpStatusCode.OK
            ? Json(JsonDocument.Parse(Contract("response")).RootElement)
            : new HttpResponseMessage(status));
        var audit = new RecordingAudit();

        _ = await Gateway(handler, audit).PublishAsync(Wire(), TestContext.Current.CancellationToken);

        var row = Assert.Single(audit.Rows);
        Assert.Equal((AuditTypes.BundleSent, "s-0001", "api.screentail.example"), (row.Type, row.SessionId, row.Detail));
        Assert.Equal(Encoding.UTF8.GetByteCount(handler.Body!), row.Count);
    }

    [Fact]
    public async Task AnAuditLogThatFailsDoesNotCostThePublishItsResults()
    {
        // The note is on the ticket by the time the answer is read. The row comes after it, and a row
        // that cannot be written is no reason to tell the technician the publish failed.
        using var handler = new RecordingHandler(Json(JsonDocument.Parse(Contract("response")).RootElement));

        var answer = await Gateway(handler, new ThrowingAudit()).PublishAsync(Wire(), TestContext.Current.CancellationToken);

        Assert.True(answer.Ok);
        Assert.Equal(3, answer.Value!.Count);
    }

    [Fact]
    public async Task NothingIsRecordedAsSentWhenNothingWasSentOrNoSessionWent()
    {
        // No answer means whether the bytes left is unknown, and the audit log does not guess. A search or
        // the integrations list carries no session, so it is not a bundle.
        var audit = new RecordingAudit();
        using var unreachable = new ThrowingHandler();
        using var answering = new RecordingHandler(Json(new { tickets = Array.Empty<object>(), integrations = Array.Empty<object>() }));

        _ = await Gateway(unreachable, audit).PublishAsync(Wire(), TestContext.Current.CancellationToken);
        _ = await Gateway(answering, audit).SearchTicketsAsync("printer", TestContext.Current.CancellationToken);

        Assert.Empty(audit.Rows);
    }

    private static PsaGateway Gateway(HttpMessageHandler handler, IAuditLog? audit = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.screentail.example/") }, () => "a-device-token", audit);

    private static PublishWire Wire() => new()
    {
        SessionId = "s-0001",
        TicketId = "48213",
        Company = "Acme Dental",
        NoteType = "internal",
        Minutes = 30,
        StartedAt = new DateTimeOffset(2026, 9, 24, 14, 2, 0, TimeSpan.Zero),
        Billable = true,
        Reviewer = "A Technician",
        Footer = true,
        Destinations = ["ticket_note", "time_entry"],
        Note = Draft(Step("Checked the spooler service; it was stopped.", StepConfidence.High, "f-0002") with { Confirmed = true, TranscriptRefs = ["t-0003"] }),
        Frames = [new PublishWireFrame("f-0002", "/9j/4AAQSkZJRg==", "image/jpeg")],
    };

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private static List<string> Shape(JsonElement element, string path = "")
    {
        var paths = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    paths.AddRange(Shape(property.Value, $"{path}.{property.Name}"));
                }

                break;
            case JsonValueKind.Array:
                var first = element.EnumerateArray().FirstOrDefault();
                paths.AddRange(first.ValueKind == JsonValueKind.Undefined ? [$"{path}[]"] : Shape(first, $"{path}[]"));
                break;
            default:
                paths.Add(path);
                break;
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static string Contract(string property)
    {
        using var stream = typeof(PsaGatewayTests).Assembly.GetManifestResourceStream("ScreenTail.Tests.publish-request.v1.json")
            ?? throw new InvalidOperationException("The publish contract fixture is not embedded in this assembly.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty(property).GetRawText();
    }

    private static JsonElement Sent(RecordingHandler handler) =>
        JsonDocument.Parse(handler.Body ?? throw new InvalidOperationException("nothing was sent")).RootElement;

    private sealed class RecordingHandler(HttpResponseMessage reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? Body { get; private set; }

        public Uri? Asked { get; private set; }

        public HttpMethod? Method { get; private set; }

        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Asked = request.RequestUri;
            Method = request.Method;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return reply;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reply.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("no route to host");
    }

    /// <summary>Keeps every row it is asked to write.</summary>
    internal sealed class RecordingAudit : IAuditLog
    {
        public List<(string Type, string? SessionId, long? Count, string? Detail)> Rows { get; } = [];

        public Task RecordAsync(string type, string? sessionId = null, long? count = null, string? detail = null, CancellationToken ct = default)
        {
            Rows.Add((type, sessionId, count, detail));
            return Task.CompletedTask;
        }
    }

    /// <summary>Fails the way a locked audit table does.</summary>
    internal sealed class ThrowingAudit : IAuditLog
    {
        public Task RecordAsync(string type, string? sessionId = null, long? count = null, string? detail = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("the audit table is locked");
    }
}
