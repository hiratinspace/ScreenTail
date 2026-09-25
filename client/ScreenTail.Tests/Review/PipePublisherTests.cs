using System.IO.Pipes;
using System.Security.Cryptography;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Shell;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The publish pane's delegates, end to end over a real pipe (ST-078 meets ST-093): the UI names frames
/// and destinations, the service reads the store and asks the backend, and the pane gets each
/// destination's outcome in the shape <see cref="PublishPanel"/> reasons about.
/// </summary>
public sealed class PipePublisherTests : IAsyncDisposable
{
    private readonly string _pipeName = IpcPipeNames.ForUser("test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _token = IpcToken.Generate();
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly ScriptedBackend _backend = new();
    private SqliteSessionStore? _store;
    private IpcServer? _server;
    private CaptureConnection? _connection;

    [Fact]
    public async Task ThePaneGetsEachDestinationsOutcome()
    {
        var publisher = await PublisherAsync();
        var session = (await _store!.LoadSessionAsync("s1"))!;
        var request = new PublishRequest("s1", new TicketMatch("48213", "Printer offline", "Acme Dental"), NoteType.Internal, 30, new HashSet<Destination> { Destination.TicketNote, Destination.TimeEntry }, session.Draft!, ["f1"]);
        _backend.FailTime = true;

        var results = await publisher.PublishAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.Destination == Destination.TicketNote).Ok);
        var time = results.Single(r => r.Destination == Destination.TimeEntry);
        Assert.False(time.Ok);
        Assert.Equal("ConnectWise says no.", time.Error);
        Assert.Equal(["f1"], _backend.Received!.Frames.Select(f => f.Id));
        Assert.Equal("Acme Dental", _backend.Received.Company);
    }

    [Fact]
    public async Task ARefusalFailsEveryRequestedDestinationWithTheReason()
    {
        var publisher = await PublisherAsync();
        _backend.Refusal = "Connect a PSA to publish.";
        var request = new PublishRequest("s1", new TicketMatch("1", "x", "y"), NoteType.Internal, 30, new HashSet<Destination> { Destination.TicketNote }, Draft(), []);

        var results = await publisher.PublishAsync(request, TestContext.Current.CancellationToken);

        var only = Assert.Single(results);
        Assert.False(only.Ok);
        Assert.Equal("Connect a PSA to publish.", only.Error);
    }

    [Fact]
    public async Task SearchAndIntegrationsArriveAsThePaneWantsThem()
    {
        var publisher = await PublisherAsync();
        _backend.Tickets = [new TicketRow("48213", "Printer offline", "Acme Dental")];
        _backend.Integrations = [new IntegrationInfo("connectwise", "https://na", "••••1234")];

        var matches = await publisher.SearchAsync("printer", TestContext.Current.CancellationToken);
        var integrations = await publisher.IntegrationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("#48213 · Printer offline · Acme Dental", Assert.Single(matches).Label);
        Assert.Equal(["connectwise"], integrations);
    }

    private async Task<PipePublisher> PublisherAsync()
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)));
        await _store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await _store.SaveRedactedFrameAsync("s1", new StagedFrame("f1", 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 }), new RedactionOutcome(new byte[] { 9, 9 }, "t", [], false, DateTimeOffset.UnixEpoch));
        await _store.SaveDraftAsync("s1", Draft());
        await _store.FinalizeSessionAsync("s1", new FinalizeInfo(10_000, false));

        var handler = new PublishOnlyController(new PublishCommands(_store, _backend));
        _server = new IpcServer(IpcServer.DefaultPipeFactory(_pipeName), _token, new AcceptAll(), handler, _store, "0.0.1-test");
        _server.Start();
        _connection = new CaptureConnection(
            new ShellState(),
            async ct => await IpcClient.ConnectAsync(_pipeName, _token, "test-ui", serverVerifier: null, "0.0.1", ct: ct),
            CaptureConnection.Never);
        await _connection.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!_connection.IsConnected)
        {
            Assert.True(DateTime.UtcNow < deadline, "the connection did not come up");
            await Task.Delay(10);
        }

        return new PipePublisher(_connection);
    }

    private sealed class PublishOnlyController(PublishCommands publishing) : IIpcCommandHandler
    {
        public CaptureStateSnapshot CurrentState => CaptureStateSnapshot.Idle;

        public CapabilitiesReported CurrentCapabilities => throw new NotSupportedException();

        public async Task<CommandResult> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            await publishing.HandleAsync(command, ct) ?? new CommandResult { RequestId = command.RequestId, Ok = false, Error = "Not a publish command." };

        public Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default) => publishing.ReplyToAsync(command, ct);
    }

    private sealed class ScriptedBackend : IPsaGateway
    {
        public string? Refusal { get; set; }

        public bool FailTime { get; set; }

        public IReadOnlyList<TicketRow> Tickets { get; set; } = [];

        public IReadOnlyList<IntegrationInfo> Integrations { get; set; } = [];

        public PublishWire? Received { get; private set; }

        public Task<GatewayAnswer<IReadOnlyList<IntegrationInfo>>> IntegrationsAsync(CancellationToken ct = default) =>
            Task.FromResult(GatewayAnswer.Of<IReadOnlyList<IntegrationInfo>>(Integrations));

        public Task<GatewayAnswer<IReadOnlyList<TicketRow>>> SearchTicketsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(GatewayAnswer.Of<IReadOnlyList<TicketRow>>(Tickets));

        public Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default)
        {
            if (Refusal is { } r)
            {
                return Task.FromResult(GatewayAnswer.Refused<IReadOnlyList<PublishOutcomeRow>>(r));
            }

            Received = bundle;
            return Task.FromResult(GatewayAnswer.Of<IReadOnlyList<PublishOutcomeRow>>([.. bundle.Destinations.Select(d => d == "time_entry" && FailTime
                ? new PublishOutcomeRow(d, false, Error: "ConnectWise says no.", Kind: "forbidden")
                : new PublishOutcomeRow(d, true, "1", "https://cw.example/1"))]));
        }
    }

    private sealed class AcceptAll : IClientVerifier
    {
        public ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }
}
