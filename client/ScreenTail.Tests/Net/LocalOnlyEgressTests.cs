using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ScreenTail.Core.Net;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Net;

/// <summary>
/// INV-8 through the real senders, on the deployment's own egress shape.
///
/// The service sends drafts, metrics and publishes to one host, so that host is on the backend list and
/// the publish list both. Under local-only the allowlist cannot tell them apart; the purpose each sender
/// attaches is the only thing standing between a session bundle and the network. These tests build each
/// sender the way the service does, on a client whose handler is the egress guard, and count what reached
/// the far side (2026-10-12 review). A sender that one day asks for the publish purpose fails here.
/// </summary>
public sealed class LocalOnlyEgressTests : IAsyncDisposable
{
    private static readonly Uri Backend = new("https://api.screentail.example/");
    private static readonly DateTimeOffset At = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private SqliteSessionStore? _store;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalOnlyStopsEveryDraftAndMetricEvenWhenTheBackendIsAPublishHost(bool lockedByTheAdmin)
    {
        var policy = new EgressPolicy(EgressSettings.ForBackend(Backend, SpeechModels.Hosts));
        policy.Apply(localOnly: true, enforced: lockedByTheAdmin);
        Assert.Contains(Backend.Host, policy.Settings.PublishHosts);
        Assert.Contains(Backend.Host, policy.Settings.BackendHosts);

        var far = new CountingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var guard = new EgressGuard(policy, far);
        using var http = new HttpClient(guard) { BaseAddress = Backend };
        var store = await FinishedSessionAsync();

        var draft = await new DraftSender(http, store, () => "a-device-token").SendAsync(Item(OutboxKind.Draft), TestContext.Current.CancellationToken);
        var metric = await new MetricsSender(http, () => "a-device-token").SendAsync(Item(OutboxKind.Metric), TestContext.Current.CancellationToken);

        Assert.Equal(0, far.Requests);
        Assert.Equal(2, guard.Blocked);
        Assert.Equal(OutboxState.Failed, draft.State);
        Assert.Contains("Local-only", draft.Message, StringComparison.Ordinal);
        Assert.Equal(OutboxState.Failed, metric.State);
        Assert.Contains("Local-only", metric.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnderLocalOnlyAPublishTheTechnicianAskedForStillReachesTheBackend()
    {
        // The mirror: the same guard, the same host, and the one purpose INV-8 lets through. Without this
        // the test above would pass just as well for a guard that refused everything.
        var policy = new EgressPolicy(EgressSettings.ForBackend(Backend, SpeechModels.Hosts));
        policy.Apply(localOnly: true, enforced: true);
        var far = new CountingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { results = Array.Empty<object>() }) });
        var guard = new EgressGuard(policy, far);
        using var http = new HttpClient(guard) { BaseAddress = Backend };

        var answer = await new PsaGateway(http, () => "a-device-token").PublishAsync(Wire(), TestContext.Current.CancellationToken);

        Assert.True(answer.Ok, answer.Refusal);
        Assert.Equal(1, far.Requests);
        Assert.Equal("/v1/sessions/publish", far.LastPath);
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public void WithNoBackendConfiguredNothingIsAllowedToAnyHostButTheModels()
    {
        // An unset SCREENTAIL_BACKEND is the ordinary case on a fresh machine. Empty lists, not a guess.
        var settings = EgressSettings.ForBackend(null, SpeechModels.Hosts);

        Assert.Empty(settings.BackendHosts);
        Assert.Empty(settings.PublishHosts);
        Assert.Equal(SpeechModels.Hosts, settings.ModelHosts);
        Assert.False(settings.LocalOnly);
    }

    private static OutboxItem Item(OutboxKind kind) => new(
        "o1", "s1", kind, $"{kind}:s1", "{}", OutboxState.Pending, 0, At, At, null, null, null);

    private static ScreenTail.Core.Net.PublishWire Wire() => new()
    {
        SessionId = "s1",
        TicketId = "48213",
        NoteType = "internal",
        Minutes = 30,
        Destinations = ["ticket_note"],
        Note = Draft(),
        Frames = [],
    };

    /// <summary>A finished session with one redacted frame, so a draft has something real to send.</summary>
    private async Task<SqliteSessionStore> FinishedSessionAsync()
    {
        var store = _store ??= await SqliteSessionStore.OpenAsync(Path.Combine(_dir, "store.db"), new FixedKey(RandomNumberGenerator.GetBytes(32)));
        await store.CreateSessionAsync(new NewSession("s1", At, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await store.StageFrameAsync("s1", new StagedFrame("f1", 1_000, FrameTrigger.Click, 1920, 1080, null, new byte[] { 0x01 }));
        await store.MarkFrameRedactedAsync("f1", new RedactionOutcome(new byte[] { 0xFF, 0xD8, 0xFF }, "Services", [], SensitiveContext: false, At));
        await store.FinalizeSessionAsync("s1", new FinalizeInfo(60_000, false));
        return store;
    }

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

    /// <summary>The far side of the guard. A request counted here is a request that left the machine.</summary>
    private sealed class CountingHandler(HttpResponseMessage reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(reply);
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

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
