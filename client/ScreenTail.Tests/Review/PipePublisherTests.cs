using System.IO.Pipes;
using System.Security.Cryptography;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Settings;
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
        Assert.Equal("forbidden", time.Kind);
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
    public async Task TheMappingPromptsChoicesAndAnswerGoOverThePipe()
    {
        var publisher = await PublisherAsync();
        _backend.Companies = [new CompanyChoiceRow("7", "Acme Dental")];

        var choices = await publisher.CompaniesAsync(TestContext.Current.CancellationToken);
        var mapped = await publisher.MapAsync("Acme Dental", "7", TestContext.Current.CancellationToken);

        Assert.Equal("Acme Dental", Assert.Single(choices).Name);
        Assert.True(mapped);
        Assert.Equal(("Acme Dental", "7"), _backend.Mapped);
    }

    [Fact]
    public async Task TheSettingsScreensCallsGoOverTheSamePipe()
    {
        // ST-082: PipeIntegrations is the Settings screen's gateway, over the connection the publish
        // pane already uses; the service answers both from the same commands class.
        _ = await PublisherAsync();
        var settings = new PipeIntegrations(_connection!);
        _backend.Details = [new IntegrationDetailRow("hudu", "https://acme.huducloud.com", "••••5678", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Hudu rejected the API key. Update it in Settings → Integrations.")];
        _backend.Companies = [new CompanyChoiceRow("7", "Acme Dental")];

        var rows = await settings.ListAsync(TestContext.Current.CancellationToken);
        var stored = await settings.StoreAsync("hudu", "https://acme.huducloud.com", "new-key", TestContext.Current.CancellationToken);
        var check = await settings.CheckAsync("hudu", TestContext.Current.CancellationToken);
        var mappings = await settings.MappingsAsync(TestContext.Current.CancellationToken);
        var unmapped = await settings.UnmapAsync("Acme Dental", TestContext.Current.CancellationToken);
        var removed = await settings.RemoveAsync("hudu", TestContext.Current.CancellationToken);

        var row = Assert.Single(rows!);
        Assert.Equal("hudu", row.Provider);
        Assert.StartsWith("Hudu rejected", row.LastError, StringComparison.Ordinal);
        Assert.Null(stored);
        Assert.Equal(("hudu", "https://acme.huducloud.com", "new-key"), _backend.Stored);
        Assert.True(check.Ok);
        Assert.Equal("Connected to https://acme.huducloud.com.", check.Message);
        Assert.Equal("Acme Dental", Assert.Single(mappings!.Companies).Name);
        Assert.Null(unmapped);
        Assert.Equal("Acme Dental", _backend.Unmapped);
        Assert.Null(removed);
        Assert.Equal("hudu", _backend.Removed);
    }

    [Fact]
    public async Task AServiceThatRefusesASettingsCallSaysWhy()
    {
        _ = await PublisherAsync();
        var settings = new PipeIntegrations(_connection!);
        _backend.Refusal = "This deployment has no vault master key, so credentials cannot be stored.";

        var stored = await settings.StoreAsync("hudu", "https://acme.huducloud.com", "new-key", TestContext.Current.CancellationToken);
        var check = await settings.CheckAsync("hudu", TestContext.Current.CancellationToken);
        var rows = await settings.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_backend.Refusal, stored);
        Assert.False(check.Ok);
        Assert.Equal(_backend.Refusal, check.Message);
        Assert.Null(rows);
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

        public IReadOnlyList<CompanyChoiceRow> Companies { get; set; } = [];

        public IReadOnlyList<IntegrationDetailRow> Details { get; set; } = [];

        public (string Provider, string SiteUrl, string Secret)? Stored { get; private set; }

        public string? Removed { get; private set; }

        public string? Unmapped { get; private set; }

        public Task<GatewayAnswer<IReadOnlyList<IntegrationDetailRow>>> IntegrationDetailsAsync(CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IReadOnlyList<IntegrationDetailRow>>(r) : GatewayAnswer.Of(Details));

        public Task<GatewayAnswer<bool>> StoreIntegrationAsync(string provider, string siteUrl, string secret, CancellationToken ct = default)
        {
            if (Refusal is { } r)
            {
                return Task.FromResult(GatewayAnswer.Refused<bool>(r));
            }

            Stored = (provider, siteUrl, secret);
            return Task.FromResult(GatewayAnswer.Of(true));
        }

        public Task<GatewayAnswer<bool>> RemoveIntegrationAsync(string provider, CancellationToken ct = default)
        {
            Removed = provider;
            return Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));
        }

        public Task<GatewayAnswer<IntegrationCheckRow>> CheckIntegrationAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IntegrationCheckRow>(r) : GatewayAnswer.Of(new IntegrationCheckRow(true, $"Connected to {Details.FirstOrDefault(d => d.Provider == provider)?.SiteUrl}.")));

        public Task<GatewayAnswer<bool>> UnmapCompanyAsync(string psaCompany, CancellationToken ct = default)
        {
            Unmapped = psaCompany;
            return Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));
        }

        public (string Psa, string Doc)? Mapped { get; private set; }

        public Task<GatewayAnswer<CompanyMappingsAnswer>> CompanyMappingsAsync(CancellationToken ct = default) =>
            Task.FromResult(GatewayAnswer.Of(new CompanyMappingsAnswer(Companies, [])));

        public Task<GatewayAnswer<bool>> MapCompanyAsync(string psaCompany, string docCompanyId, CancellationToken ct = default)
        {
            Mapped = (psaCompany, docCompanyId);
            return Task.FromResult(GatewayAnswer.Of(true));
        }

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
