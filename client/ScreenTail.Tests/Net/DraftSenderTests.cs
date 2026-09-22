using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenTail.Core.Intel;
using ScreenTail.Core.Net;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Net;

/// <summary>
/// The step that has never run: a finished session leaving the machine to be drafted (ST-063, ST-064).
///
/// Everything either side of this has existed for weeks. The bundle is built for real on every session,
/// the backend endpoint drafts and answers, and the outbox queues the work and retries it. Between them
/// was a stub that returned "no summarization provider is configured yet", so no session has ever
/// produced a note.
///
/// <b>The translation is the substance.</b> A <see cref="SessionBundle"/>'s frame carries a path —
/// <c>frames/f-0002.jpg</c> — because that is what a session on disk looks like. The backend needs the
/// bytes. So this reads each selected frame's redacted image out of the store and sends it base64, and
/// the path never crosses the wire at all: it is a filename from a technician's machine, and it is of no
/// use to a model.
///
/// <b>The bundle is rebuilt here, not queued.</b> The outbox holds a marker, so a draft owed since
/// yesterday is selected from the store as it is now — retention may have thinned it, and a queued
/// bundle could otherwise resurrect a frame the tenant's window has already removed (INV-12).
/// </summary>
public sealed class DraftSenderTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Picture = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    [Fact]
    public async Task TheBackendIsSentTheImageAndNotThePathToIt()
    {
        // The whole reason this class exists. A frame's `image` is a filename on the technician's
        // machine; the model needs what is in the file.
        var store = await SessionAsync();
        var handler = new RecordingHandler(Answer(HttpStatusCode.OK, Ok()));

        var outcome = await Sender(store, handler).SendAsync(Item(), TestContext.Current.CancellationToken);

        Assert.True(outcome.State is OutboxState.Done, outcome.Message);
        var frame = Assert.Single(Sent(handler).GetProperty("frames").EnumerateArray());
        Assert.Equal(Convert.ToBase64String(Picture), frame.GetProperty("image").GetString());
        Assert.DoesNotContain("frames/", handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatTheModelIsToldIsWhatTheRedactionWorkerCouldRead()
    {
        // OCR text reaches the model by design; it is what the note is written from. What matters is
        // that it is the redacted text, which is the only text the store will give back (INV-1).
        var store = await SessionAsync();
        var handler = new RecordingHandler(Answer(HttpStatusCode.OK, Ok()));

        _ = await Sender(store, handler).SendAsync(Item(), TestContext.Current.CancellationToken);

        var frame = Assert.Single(Sent(handler).GetProperty("frames").EnumerateArray());
        Assert.Equal("Services [REDACTED] stopped", frame.GetProperty("ocr_text").GetString());
    }

    [Fact]
    public async Task ADraftThatComesBackIsWhatTheSessionShows()
    {
        // The point of the whole path: Review has something to show, and the session says so.
        var store = await SessionAsync();
        var handler = new RecordingHandler(Answer(HttpStatusCode.OK, Ok()));

        var outcome = await Sender(store, handler).SendAsync(Item(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Done, outcome.State);
        var session = (await store.LoadSessionAsync("s1"))!;
        Assert.Equal("Printer offline", session.Draft?.SuggestedTitle);

        // The state lives on the row rather than in the exported session, and History is what reads it.
        var row = Assert.Single(await store.ListSessionsAsync());
        Assert.Equal("draft_ready", row.State);
    }

    [Fact]
    public async Task WithoutATokenNothingLeavesTheMachine()
    {
        // A device that has not been enrolled has nothing to prove who it is, and a bundle is a
        // customer's screen. It waits rather than being sent unauthenticated or thrown away.
        var store = await SessionAsync();
        var handler = new RecordingHandler(Answer(HttpStatusCode.OK, Ok()));

        var outcome = await Sender(store, handler, token: null).SendAsync(Item(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Pending, outcome.State);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task ABundleTheBackendRefusesIsNotAskedAgain()
    {
        // 400 means the bundle is wrong, and the same bundle will be just as wrong in an hour. Retrying
        // it forever is how a queue fills with work that can never succeed.
        var store = await SessionAsync();
        var handler = new RecordingHandler(Answer(HttpStatusCode.BadRequest, """{"status":"rejected","reason":"A session id is required."}"""));

        var outcome = await Sender(store, handler).SendAsync(Item(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Failed, outcome.State);
        Assert.Contains("session id", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // The tenant has spent its drafting budget. It comes back at midnight.
    [InlineData(HttpStatusCode.PaymentRequired, """{"status":"cost_cap_reached","reason":"This tenant has reached its drafting budget for today."}""")]
    // The model did not answer, which is the case the outbox was written for.
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"status":"unavailable","reason":"The drafting model did not answer."}""")]
    // Nobody has configured a provider yet. A deployment problem, not this session's problem.
    [InlineData(HttpStatusCode.NotImplemented, """{"status":"not_configured","reason":"No summarization provider is configured on this deployment."}""")]
    public async Task SomethingThatMayBeTrueLaterIsTriedAgainLater(HttpStatusCode status, string body)
    {
        var store = await SessionAsync();
        var handler = new RecordingHandler(Answer(status, body));

        var outcome = await Sender(store, handler).SendAsync(Item(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Pending, outcome.State);
    }

    [Fact]
    public async Task ABackendNobodyCouldReachIsTriedAgain()
    {
        var store = await SessionAsync();
        var handler = new BrokenHandler();

        var outcome = await Sender(store, handler).SendAsync(Item(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Pending, outcome.State);
    }

    [Fact]
    public async Task ASessionRetentionHasAlreadyTakenIsNotDrafted()
    {
        // The draft was owed since yesterday and the session aged out overnight. Nothing to send, and
        // nothing that asking again would fix.
        var store = await StoreAsync();
        var handler = new RecordingHandler(Answer(HttpStatusCode.OK, Ok()));

        var outcome = await Sender(store, handler).SendAsync(Item(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Failed, outcome.State);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task TheClientWritesTheContractTheBackendReads()
    {
        // The backend must not depend on a Windows-only client, so what binds the two is JSON and the
        // field names are the contract. This one drifted before anything could notice it: while the
        // sender was a stub the shape had never been posted anywhere, and `image` meant a path on this
        // side and base64 bytes on the other.
        //
        // shared/contracts/summarize-request.v1.json is the fixture both ends read. The backend's
        // WireContractTests posts it to the real endpoint; this asserts the client writes it.
        var store = await SessionAsync();
        var handler = new RecordingHandler(Answer(HttpStatusCode.OK, Ok()));

        _ = await Sender(store, handler).SendAsync(Item(), TestContext.Current.CancellationToken);

        var contract = Contract();
        var sent = Sent(handler);
        Assert.Equal(Shape(contract), Shape(sent));
    }

    /// <summary>
    /// The field names and nesting, as a sorted list of paths. Values are deliberately left out: the
    /// fixture describes a different session than the one this test records, and what has to agree is
    /// the shape rather than the contents.
    /// </summary>
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

            // One entry per array, from its first item: a contract is about what an element looks like,
            // not how many there are.
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

    /// <summary>The request out of the shared contract fixture.</summary>
    private static JsonElement Contract()
    {
        using var stream = typeof(DraftSenderTests).Assembly.GetManifestResourceStream("ScreenTail.Tests.summarize-request.v1.json")
            ?? throw new InvalidOperationException("The shared contract fixture is not embedded in this assembly.");
        using var document = JsonDocument.Parse(stream);
        return JsonDocument.Parse(document.RootElement.GetProperty("request").GetRawText()).RootElement;
    }

    /// <summary>The request body, parsed. What the backend would have received.</summary>
    private static JsonElement Sent(RecordingHandler handler) =>
        JsonDocument.Parse(handler.Body ?? throw new InvalidOperationException("nothing was sent")).RootElement;

    private static DraftSender Sender(ISessionStore store, HttpMessageHandler handler, string? token = "a-device-token") =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.screentail.example/") },
            store,
            () => token);

    private static OutboxItem Item() => new(
        "o1", "s1", OutboxKind.Draft, "draft:s1", "{}", OutboxState.Pending, 0, At, At, null, null, null);

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Ok() => """
        {"status":"ok","draft":{"problem":"Nothing printed.","steps":[],"result":"It prints.",
         "follow_ups":[],"suggested_title":"Printer offline","suggested_time_minutes":10,
         "kb_candidate":false,"kb_reason":"n","source":"cloud","prompt_version":"note_v1"}}
        """;

    /// <summary>A finished session with one redacted frame and one thing said.</summary>
    private async Task<SqliteSessionStore> SessionAsync()
    {
        var store = await StoreAsync();
        await store.CreateSessionAsync(new NewSession("s1", At, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await store.StageFrameAsync("s1", new StagedFrame("f1", 1_000, FrameTrigger.Click, 1920, 1080, null, new byte[] { 0x01 }));
        await store.MarkFrameRedactedAsync(
            "f1",
            new RedactionOutcome(Picture, "Services [REDACTED] stopped", [], SensitiveContext: false, At));
        await store.AppendTranscriptAsync(
            "s1",
            new TranscriptSegment { Id = "t1", TsMs = 1_200, EndMs = 2_000, Speaker = Speaker.Tech, Text = "clearing the queue" });
        await store.FinalizeSessionAsync("s1", new FinalizeInfo(60_000, false));
        return store;
    }

    private async Task<SqliteSessionStore> StoreAsync() =>
        _store ??= await SqliteSessionStore.OpenAsync(
            Path.Combine(_dir, "store.db"),
            new FixedKey(RandomNumberGenerator.GetBytes(32)));

    /// <summary>Keeps the request rather than sending it, so a test can read what would have gone.</summary>
    private sealed class RecordingHandler(HttpResponseMessage reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? Body { get; private set; }

        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
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

    /// <summary>Fails the way an unreachable host does.</summary>
    private sealed class BrokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No such host is known.");
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private SqliteSessionStore? _store;

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
