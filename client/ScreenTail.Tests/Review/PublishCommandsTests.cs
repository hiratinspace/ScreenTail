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
    public async Task ASensitiveFrameIdIsNotSentEvenWhenNamed()
    {
        // session.v1: a sensitive_context frame is never sent or attached. The UI no longer names one, and
        // the service does not take the UI's word for it either.
        var commands = await CommandsAsync();
        await SeedAsync(["f1", "f2"]);
        await AddFrameAsync("s1", "login", 2000, sensitive: true);

        await commands.ReplyToAsync(Publish(["f1", "login", "f2"]));

        Assert.Equal(["f1", "f2"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AFrameFromAnotherSessionIsNotSentEvenWhenNamed()
    {
        // The ids are global in the store. A frame of some other customer's session must not ride along on
        // this ticket because its id was in the list.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        await _store!.CreateSessionAsync(new NewSession("s2", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await AddFrameAsync("s2", "other", 1000);

        await commands.ReplyToAsync(Publish(["f1", "other"]));

        Assert.Equal(["f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AFrameTheTechnicianExcludedIsNotSentEvenWhenNamed()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1", "f2"]);
        await _store!.SetFrameExcludedAsync("f2", excluded: true);

        await commands.ReplyToAsync(Publish(["f1", "f2"]));

        Assert.Equal(["f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AFrameTakenWhileCaptureWasOffIsNotSentEvenWhenNamed()
    {
        // The same rule the drafting bundle applies (INV-6), so the ticket never carries a frame the model
        // was not allowed to see.
        var commands = await CommandsAsync();
        await _store!.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await _store.AppendEventAsync("s1", new CaptureStateEvent { TsMs = 2000, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField });
        await _store.AppendEventAsync("s1", new CaptureStateEvent { TsMs = 4000, State = CaptureState.Recording });
        await AddFrameAsync("s1", "before", 1000);
        await AddFrameAsync("s1", "during", 3000);
        await AddFrameAsync("s1", "after", 5000);

        await commands.ReplyToAsync(Publish(["before", "during", "after"]));

        Assert.Equal(["before", "after"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task APendingFrameIsNotSentEvenWhenNamed()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        await _store!.StageFrameAsync("s1", new StagedFrame("raw", 2000, FrameTrigger.Click, 1600, 900, null, new byte[] { 9 }));

        await commands.ReplyToAsync(Publish(["raw", "f1"]));

        Assert.Equal(["f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AFrameNamedTwiceIsAttachedOnce()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1", "f2"]);

        await commands.ReplyToAsync(Publish(["f2", "f1", "f2"]));

        Assert.Equal(["f2", "f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AnEmptyOrMissingIdIsIgnored()
    {
        // The list comes off the pipe, where a null in a string array is valid JSON.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);

        await commands.ReplyToAsync(Publish([null!, string.Empty, "f1"]));

        Assert.Equal(["f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task APublishWithMoreFramesThanTheBackendTakesIsRefusedBeforeAnyBytesAreRead()
    {
        // E-18. The backend refuses more than 25 frames with a 400, and the client used to find that out
        // after reading, encoding and uploading all of them. The service refuses first, says how many are
        // over, and reads nothing.
        var commands = await CommandsAsync();
        var ids = Enumerable.Range(1, PublishCommands.MaxFrames + 1).Select(i => $"f{i}").ToArray();
        await SeedAsync(ids);

        var command = Publish(ids);
        Assert.Null(await commands.ReplyToAsync(command));
        var result = await commands.HandleAsync(command);

        Assert.False(result!.Ok);
        Assert.Contains($"{PublishCommands.MaxFrames}", result.Error, StringComparison.Ordinal);
        Assert.Contains("26", result.Error, StringComparison.Ordinal);
        Assert.Empty(_gateway.Published);
    }

    [Fact]
    public async Task ExactlyTheCeilingIsSentAndIneligibleNamesDoNotCountAgainstIt()
    {
        // The ceiling is on what would be attached, not on what was typed: an excluded frame or a stray id
        // in the list is dropped before the count, so a strip showing 25 included publishes.
        var commands = await CommandsAsync();
        var ids = Enumerable.Range(1, PublishCommands.MaxFrames).Select(i => $"f{i}").ToArray();
        await SeedAsync(ids);
        await AddFrameAsync("s1", "out", 2000);
        await _store!.SetFrameExcludedAsync("out", excluded: true);

        var reply = await commands.ReplyToAsync(Publish([.. ids, "out", "gone", "f1"]));

        Assert.IsType<SessionPublished>(reply);
        Assert.Equal(PublishCommands.MaxFrames, Assert.Single(_gateway.Published).Frames.Count);
    }

    [Fact]
    public void ThePublishCeilingIsTheBundlesCeiling()
    {
        // One number: the backend's PublishLimits.MaxFrames is BundleLimits.MaxFrames, which is the
        // client's BundleOptions.MaxFrames. A drift here would make the service refuse what the backend
        // takes, or the reverse.
        Assert.Equal(new Intel.BundleOptions().MaxFrames, PublishCommands.MaxFrames);
    }

    [Fact]
    public async Task ASessionCountsAsPublishedOnlyWhenSomethingLanded()
    {
        // T-128 (ST-098, ST-067). The metric and the style signals are about a note that reached the PSA.
        // Every destination failing is not that, and reporting it would count a session as published that
        // the technician is about to retry.
        var metrics = new RecordingBookkeeping();
        var commands = await CommandsAsync(metrics);
        await SeedAsync(["f1"]);
        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, false, Error: "PSA down"))];

        var nothing = Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));
        Assert.All(nothing.Results, r => Assert.False(r.Ok));
        Assert.Empty(metrics.Reported);
        Assert.Empty(metrics.Learned);

        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, d == "ticket_note", d == "ticket_note" ? "1" : null, Error: d == "ticket_note" ? null : "PSA down"))];

        var partly = Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));
        Assert.Contains(partly.Results, r => r.Ok);
        Assert.Equal([("s1", true)], metrics.Reported);
        Assert.Equal(["s1"], metrics.Learned);
    }

    [Fact]
    public async Task AStoreFaultAfterALandedPublishStillReturnsTheResults()
    {
        // S-37. The note is on the ticket by the time the bookkeeping runs. A metrics or style store that
        // throws then must not turn into a dropped pipe and a result list that says every destination
        // failed, which is what the technician would read as "publish again".
        var metrics = new RecordingBookkeeping { Throws = true };
        var commands = await CommandsAsync(metrics);
        await SeedAsync(["f1"]);

        var reply = await commands.ReplyToAsync(Publish(["f1"]));

        var published = Assert.IsType<SessionPublished>(reply);
        Assert.Equal(2, published.Results.Count);
        Assert.All(published.Results, r => Assert.True(r.Ok));
        Assert.Equal([("s1", true)], metrics.Reported);
        Assert.Equal(["s1"], metrics.Learned);
        Assert.NotNull(commands.LastBookkeepingError);
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

    private async Task<PublishCommands> CommandsAsync(RecordingBookkeeping? bookkeeping = null)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)));
        return bookkeeping is null
            ? new PublishCommands(_store, _gateway)
            : new PublishCommands(_store, _gateway, bookkeeping, bookkeeping);
    }

    /// <summary>The metrics reporter and the style learner, remembering what they were told; optionally failing like a store that is down.</summary>
    private sealed class RecordingBookkeeping : IMetricsReporter, Intel.IStyleLearner
    {
        public bool Throws { get; init; }

        public List<(string SessionId, bool Published)> Reported { get; } = [];

        public List<string> Learned { get; } = [];

        public Task ReportAsync(string sessionId, bool published, CancellationToken ct = default)
        {
            Reported.Add((sessionId, published));
            return Throws ? Task.FromException(new InvalidOperationException("the metrics store is locked")) : Task.CompletedTask;
        }

        public Task LearnAsync(string sessionId, CancellationToken ct = default)
        {
            Learned.Add(sessionId);
            return Throws ? Task.FromException(new InvalidOperationException("the style store is locked")) : Task.CompletedTask;
        }
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

    private Task AddFrameAsync(string sessionId, string id, long tsMs, bool sensitive = false) =>
        _store!.SaveRedactedFrameAsync(sessionId, new StagedFrame(id, tsMs, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 }), new RedactionOutcome(Bytes(id), "t", [], sensitive, DateTimeOffset.UnixEpoch));

    private static byte[] Bytes(string id) => System.Text.Encoding.ASCII.GetBytes("jpeg-" + id);

    private sealed class FakeGateway : IPsaGateway
    {
        public string? Refusal { get; set; }

        public IReadOnlyList<TicketRow> Tickets { get; set; } = [];

        public IReadOnlyList<IntegrationInfo> Integrations { get; set; } = [];

        public List<PublishWire> Published { get; } = [];

        /// <summary>How each destination went, by its wire name. Null means everything landed.</summary>
        public Func<IReadOnlyList<string>, IReadOnlyList<PublishOutcomeRow>>? Outcomes { get; set; }

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
            return Task.FromResult(GatewayAnswer.Of<IReadOnlyList<PublishOutcomeRow>>(
                Outcomes?.Invoke(bundle.Destinations) ?? [.. bundle.Destinations.Select(d => new PublishOutcomeRow(d, true, "1"))]));
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
