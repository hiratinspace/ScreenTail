using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScreenTail.Core.Net;
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
        Assert.Equal(2, answer.Value!.Count);
        Assert.True(answer.Value[0].Ok);
        Assert.Equal("90001", answer.Value[0].Id);
        Assert.False(answer.Value[1].Ok);
        Assert.Equal("forbidden", answer.Value[1].Kind);
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

    private static PsaGateway Gateway(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.screentail.example/") }, () => "a-device-token");

    private static PublishWire Wire() => new()
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

        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Asked = request.RequestUri;
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
}
