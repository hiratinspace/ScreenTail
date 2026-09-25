using System.Security.Cryptography;
using ScreenTail.Core.Net;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The service's side of publishing over the pipe (ST-093): the UI names the frames, the service reads
/// their bytes from the store, and the backend's per-destination answer comes back as it is.
/// </summary>
public sealed class PublishCommandsTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FakeGateway _gateway = new();
    private SqliteSessionStore? _store;

    [Fact]
    public async Task ThePublishCarriesTheNamedFramesBytesInOrderAndSkipsOneThatIsGone()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1", "f2"]);

        var reply = await commands.ReplyToAsync(Publish(["f2", "gone", "f1"]));

        var published = Assert.IsType<SessionPublished>(reply);
        Assert.True(published.Results[0].Ok);
        var sent = Assert.Single(_gateway.Published);
        Assert.Equal(["f2", "f1"], sent.Frames.Select(f => f.Id));
        Assert.Equal(Convert.ToBase64String(Bytes("f2")), sent.Frames[0].Image);
        Assert.Equal("48213", sent.TicketId);
        Assert.Equal("Acme Dental", sent.Company);
        Assert.Equal(DateTimeOffset.UnixEpoch, sent.StartedAt);
        Assert.Null(sent.Reviewer);
    }

    [Fact]
    public async Task ABackendRefusalIsAFailedResultWithItsWords()
    {
        var commands = await CommandsAsync();
        await SeedAsync([]);
        _gateway.Refusal = "Connect a PSA to publish.";

        var command = Publish([]);
        Assert.Null(await commands.ReplyToAsync(command));
        var result = await commands.HandleAsync(command);

        Assert.False(result!.Ok);
        Assert.Equal("Connect a PSA to publish.", result.Error);
    }

    [Fact]
    public async Task ASessionThatIsGoneCannotBePublished()
    {
        var commands = await CommandsAsync();

        var command = Publish([]) with { SessionId = "nope" };
        Assert.Null(await commands.ReplyToAsync(command));
        var result = await commands.HandleAsync(command);

        Assert.False(result!.Ok);
        Assert.Empty(_gateway.Published);
    }

    [Fact]
    public async Task SearchAndIntegrationsPassThrough()
    {
        var commands = await CommandsAsync();
        _gateway.Tickets = [new TicketRow("48213", "Printer offline", "Acme Dental", "New")];
        _gateway.Integrations = [new IntegrationInfo("connectwise", "https://na", "••••1234")];

        var tickets = Assert.IsType<TicketsFound>(await commands.ReplyToAsync(new SearchTicketsCommand { RequestId = 1, Query = "printer" }));
        var integrations = Assert.IsType<IntegrationsListed>(await commands.ReplyToAsync(new GetIntegrationsCommand { RequestId = 2 }));

        Assert.Equal("48213", Assert.Single(tickets.Tickets).Id);
        Assert.Equal("connectwise", Assert.Single(integrations.Integrations).Provider);
        Assert.Equal("printer", _gateway.LastQuery);
    }

    [Fact]
    public async Task TheMappingQuestionsPassThrough()
    {
        var commands = await CommandsAsync();

        var listed = Assert.IsType<CompanyMappingsListed>(await commands.ReplyToAsync(new GetCompanyMappingsCommand { RequestId = 3 }));
        var mapped = await commands.HandleAsync(new MapCompanyCommand { RequestId = 4, PsaCompany = "Acme Dental", DocCompanyId = "7" });

        Assert.Equal("Acme Dental", Assert.Single(listed.Companies).Name);
        Assert.True(mapped!.Ok);
    }

    [Fact]
    public async Task TheSettingsQuestionsAndActionsPassThrough()
    {
        // ST-082: the Settings screen manages integrations through the same service and gateway.
        var commands = await CommandsAsync();

        var listed = Assert.IsType<IntegrationDetailsListed>(await commands.ReplyToAsync(new ListIntegrationsCommand { RequestId = 5 }));
        var checkedEvent = Assert.IsType<IntegrationChecked>(await commands.ReplyToAsync(new CheckIntegrationCommand { RequestId = 6, Provider = "connectwise" }));
        var stored = await commands.HandleAsync(new StoreIntegrationCommand { RequestId = 7, Provider = "hudu", SiteUrl = "https://acme.huducloud.com", Secret = "key" });
        var removed = await commands.HandleAsync(new RemoveIntegrationCommand { RequestId = 8, Provider = "hudu" });
        var unmapped = await commands.HandleAsync(new UnmapCompanyCommand { RequestId = 9, PsaCompany = "Acme Dental" });

        Assert.Equal("connectwise", Assert.Single(listed.Integrations).Provider);
        Assert.True(checkedEvent.Ok);
        Assert.True(stored!.Ok);
        Assert.True(removed!.Ok);
        Assert.True(unmapped!.Ok);
        Assert.Equal(("hudu", "https://acme.huducloud.com", "key"), _gateway.Stored);
    }

    [Fact]
    public async Task CommandsItDoesNotOwnAreLeftAlone()
    {
        var commands = await CommandsAsync();

        Assert.Null(await commands.ReplyToAsync(new StartCommand { RequestId = 1 }));
        Assert.Null(await commands.HandleAsync(new StartCommand { RequestId = 1 }));
    }

    private static PublishSessionCommand Publish(string[] frameIds) => new()
    {
        RequestId = 7,
        SessionId = "s1",
        TicketId = "48213",
        TicketCompany = "Acme Dental",
        NoteType = "internal",
        Minutes = 30,
        Destinations = ["ticket_note", "time_entry"],
        Note = Draft(),
        FrameIds = frameIds,
    };

    private async Task<PublishCommands> CommandsAsync()
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)));
        return new PublishCommands(_store, _gateway);
    }

    private async Task SeedAsync(string[] frames)
    {
        await _store!.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        foreach (var id in frames)
        {
            await _store.SaveRedactedFrameAsync("s1", new StagedFrame(id, 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 }), new RedactionOutcome(Bytes(id), "t", [], false, DateTimeOffset.UnixEpoch));
        }

        await _store.SaveDraftAsync("s1", Draft());
        await _store.FinalizeSessionAsync("s1", new FinalizeInfo(10_000, false));
    }

    private static byte[] Bytes(string id) => System.Text.Encoding.ASCII.GetBytes("jpeg-" + id);

    private sealed class FakeGateway : IPsaGateway
    {
        public string? Refusal { get; set; }

        public IReadOnlyList<TicketRow> Tickets { get; set; } = [];

        public IReadOnlyList<IntegrationInfo> Integrations { get; set; } = [];

        public List<PublishWire> Published { get; } = [];

        public string? LastQuery { get; private set; }

        public Task<GatewayAnswer<IReadOnlyList<IntegrationInfo>>> IntegrationsAsync(CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IReadOnlyList<IntegrationInfo>>(r) : GatewayAnswer.Of<IReadOnlyList<IntegrationInfo>>(Integrations));

        public Task<GatewayAnswer<IReadOnlyList<TicketRow>>> SearchTicketsAsync(string query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IReadOnlyList<TicketRow>>(r) : GatewayAnswer.Of<IReadOnlyList<TicketRow>>(Tickets));
        }

        public (string Provider, string SiteUrl, string Secret)? Stored { get; private set; }

        public Task<GatewayAnswer<IReadOnlyList<IntegrationDetailRow>>> IntegrationDetailsAsync(CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IReadOnlyList<IntegrationDetailRow>>(r) : GatewayAnswer.Of<IReadOnlyList<IntegrationDetailRow>>([new IntegrationDetailRow("connectwise", "https://na.myconnectwise.net", "••••1234", DateTimeOffset.UnixEpoch, null, null)]));

        public Task<GatewayAnswer<bool>> StoreIntegrationAsync(string provider, string siteUrl, string secret, CancellationToken ct = default)
        {
            Stored = (provider, siteUrl, secret);
            return Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));
        }

        public Task<GatewayAnswer<bool>> RemoveIntegrationAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));

        public Task<GatewayAnswer<IntegrationCheckRow>> CheckIntegrationAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IntegrationCheckRow>(r) : GatewayAnswer.Of(new IntegrationCheckRow(true, "Connected to https://na.myconnectwise.net.")));

        public Task<GatewayAnswer<bool>> UnmapCompanyAsync(string psaCompany, CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));

        public Task<GatewayAnswer<CompanyMappingsAnswer>> CompanyMappingsAsync(CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<CompanyMappingsAnswer>(r) : GatewayAnswer.Of(new CompanyMappingsAnswer([new CompanyChoiceRow("7", "Acme Dental")], [])));

        public Task<GatewayAnswer<bool>> MapCompanyAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));

        public Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default)
        {
            if (Refusal is { } r)
            {
                return Task.FromResult(GatewayAnswer.Refused<IReadOnlyList<PublishOutcomeRow>>(r));
            }

            Published.Add(bundle);
            return Task.FromResult(GatewayAnswer.Of<IReadOnlyList<PublishOutcomeRow>>([.. bundle.Destinations.Select(d => new PublishOutcomeRow(d, true, "1"))]));
        }
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
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
