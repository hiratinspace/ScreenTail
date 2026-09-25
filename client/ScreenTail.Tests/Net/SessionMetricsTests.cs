using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ScreenTail.Core.Net;
using ScreenTail.Core.Outbox;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Net;

/// <summary>
/// ST-098: one metric per session, with nothing in it that is content (INV-10), posted only when
/// telemetry is on, through the outbox so an offline afternoon queues rather than loses it. The edit
/// ratio is the share of the draft's steps the technician changed before publishing.
/// </summary>
public sealed class SessionMetricsTests
{
    [Fact]
    public async Task ClientWritesTheMetricContract()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var sender = new MetricsSender(new HttpClient(handler) { BaseAddress = new Uri("https://api.screentail.example/") }, () => "a-device-token");

        var outcome = await sender.SendAsync(Item(SessionMetricWire.From(Session(draft: Draft(Step("one"), Step("two"))), published: true, editRatio: 0.5)), TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Done, outcome.State);
        Assert.Equal(Shape(JsonDocument.Parse(Contract("request")).RootElement), Shape(JsonDocument.Parse(handler.Body!).RootElement));
        Assert.EndsWith("/v1/metrics/sessions", handler.Asked!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("a-device-token", handler.Authorization?.Parameter);
    }

    [Fact]
    public void TheMetricHasNoContentFieldAtAll()
    {
        // The wire type is the field list Settings shows. A string field other than the id would be the
        // first place a title or a company could sneak in, so there is none.
        var strings = typeof(SessionMetricWire).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name);

        Assert.Equal(["SessionId"], strings);
    }

    [Fact]
    public void TheMetricIsCountsAndTimesReadOffTheSession()
    {
        var session = Session(draft: Draft(Step("one"), Step("two")));

        var metric = SessionMetricWire.From(session, published: false, editRatio: null);

        Assert.Equal(session.SessionId, metric.SessionId);
        Assert.Equal(session.StartedAt, metric.StartedAt);
        Assert.Equal(session.Frames.Count, metric.Frames);
        Assert.Equal(session.Transcript.Count, metric.TranscriptSegments);
        Assert.False(metric.Published);
        Assert.Null(metric.EditRatio);
    }

    [Theory]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "a", "b", "c", "d" }, 0.0)]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "a", "B!", "c", "d" }, 0.25)]
    [InlineData(new[] { "a", "b" }, new[] { "a", "b", "c" }, 0.3333333333333333)]
    [InlineData(new[] { "a", "b", "c" }, new[] { "a" }, 0.6666666666666666)]
    [InlineData(new string[0], new[] { "a" }, 1.0)]
    public void TheEditRatioIsTheShareOfStepsChangedAddedOrRemoved(string[] original, string[] published, double expected)
    {
        Assert.Equal(expected, EditRate.Of(Draft([.. original.Select(t => Step(t))]), Draft([.. published.Select(t => Step(t))])), 10);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, OutboxState.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, OutboxState.Pending)]
    [InlineData(HttpStatusCode.BadRequest, OutboxState.Failed)]
    public async Task ABackendThatIsAwayIsRetriedAndOneThatRefusesIsNot(HttpStatusCode status, OutboxState expected)
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(status));
        var sender = new MetricsSender(new HttpClient(handler) { BaseAddress = new Uri("https://api.screentail.example/") }, () => "a-device-token");

        var outcome = await sender.SendAsync(Item(SessionMetricWire.From(Session(draft: Draft(Step("one"))), published: false, editRatio: null)), TestContext.Current.CancellationToken);

        Assert.Equal(expected, outcome.State);
    }

    [Fact]
    public async Task TheReporterQueuesAMetricOnlyWhenTelemetryIsOnAndMeasuresTheEditsAgainstTheOriginal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
        await using var store = await ScreenTail.Core.Store.SqliteSessionStore.OpenAsync(Path.Combine(dir, "store.db"), new FixedKey());
        await store.CreateSessionAsync(new ScreenTail.Core.Store.NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await store.SaveDraftAsync("s1", Draft(Step("one"), Step("two")));
        await store.SaveDraftAsync("s1", Draft(Step("one"), Step("two, edited")));
        await store.FinalizeSessionAsync("s1", new ScreenTail.Core.Store.FinalizeInfo(10_000, false));
        var sent = new List<OutboxItem>();
        var telemetry = false;
        var outbox = new ScreenTail.Core.Outbox.Outbox(store, (item, _) => { sent.Add(item); return Task.FromResult(SendOutcome.Done(null)); });
        var reporter = new MetricsReporter(store, outbox, () => telemetry);

        await reporter.ReportAsync("s1", published: true, TestContext.Current.CancellationToken);
        _ = await outbox.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Empty(sent);

        telemetry = true;
        await reporter.ReportAsync("s1", published: true, TestContext.Current.CancellationToken);
        Assert.True(await outbox.DrainAsync(TestContext.Current.CancellationToken));

        var item = Assert.Single(sent);
        Assert.Equal(OutboxKind.Metric, item.Kind);
        var metric = JsonSerializer.Deserialize<SessionMetricWire>(item.Payload, MetricsSender.Json)!;
        Assert.Equal(0.5, metric.EditRatio);
        Assert.True(metric.Published);
        Assert.DoesNotContain("edited", item.Payload, StringComparison.Ordinal);
        Directory.Delete(dir, recursive: true);
    }

    private sealed class FixedKey : ScreenTail.Core.Store.IStoreKeyProvider
    {
        private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        public byte[] GetKey() => (byte[])_key.Clone();
    }

    private static OutboxItem Item(SessionMetricWire metric) =>
        new("1", metric.SessionId, OutboxKind.Metric, $"metric:{metric.SessionId}", JsonSerializer.Serialize(metric, MetricsSender.Json), OutboxState.Pending, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null);

    private static string Contract(string property)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Tests.session-metric.v1.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty(property).GetRawText();
    }

    private static string Shape(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + ":" + Shape(p.Value))) + "}",
        JsonValueKind.Array => "[" + (element.GetArrayLength() > 0 ? Shape(element[0]) : string.Empty) + "]",
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => element.ValueKind.ToString().ToLowerInvariant(),
    };

    private sealed class RecordingHandler(HttpResponseMessage reply) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public Uri? Asked { get; private set; }

        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return reply;
        }
    }
}
