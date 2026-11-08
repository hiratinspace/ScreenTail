using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Shell;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Net;
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
    public async Task APipeThatDropsAfterThePublishWentOutMayHaveReachedTheTicket()
    {
        // The command left and the service went before it answered: the backend may have posted the note.
        // Reported as a plain failure, the pane let the technician publish it a second time.
        var publisher = await PublisherAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _backend.Hold = hold.Task;
        var request = new PublishRequest("s1", new TicketMatch("48213", "Printer offline", "Acme Dental"), NoteType.Internal, 30, new HashSet<Destination> { Destination.TicketNote, Destination.TimeEntry, Destination.KbArticle }, Draft(), ["f1"]);

        var publishing = publisher.PublishAsync(request, TestContext.Current.CancellationToken);
        await _backend.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var stopping = _server!.DisposeAsync().AsTask();
        var results = await publishing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        hold.SetResult();
        await stopping;
        _server = null;

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.MayHaveLanded, r.Error));
        Assert.All(results, r => Assert.Equal(DestinationResult.Unknown, r.Kind));
        Assert.Contains("may already be on the ticket", results.Single(r => r.Destination == Destination.TicketNote).Error, StringComparison.Ordinal);
        Assert.Contains("Check the ticket", results.Single(r => r.Destination == Destination.TimeEntry).Error, StringComparison.Ordinal);
        Assert.Contains("Check the knowledge base", results.Single(r => r.Destination == Destination.KbArticle).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublishedSessionIsRefusedAsMayHaveLandedUntilThePaneSaysItIsPublishingAgain()
    {
        var publisher = await PublisherAsync();
        var request = new PublishRequest("s1", new TicketMatch("48213", "Printer offline", "Acme Dental"), NoteType.Internal, 30, new HashSet<Destination> { Destination.TicketNote }, Draft(), ["f1"]);
        Assert.True(Assert.Single(await publisher.PublishAsync(request, TestContext.Current.CancellationToken)).Ok);

        var refused = Assert.Single(await publisher.PublishAsync(request, TestContext.Current.CancellationToken));

        // The pane reads it as the same "check first" outcome, so the technician is asked, not just told.
        Assert.False(refused.Ok);
        Assert.True(refused.MayHaveLanded);
        Assert.Equal(PublishCommands.AlreadyPublished, refused.Error);
        Assert.Equal(DestinationResult.AlreadyPublished, refused.Kind);
        Assert.Equal(1, _backend.PublishCalls);

        var again = Assert.Single(await publisher.PublishAsync(request with { PublishAgain = true }, TestContext.Current.CancellationToken));

        Assert.True(again.Ok);
        Assert.Equal(2, _backend.PublishCalls);
    }

    [Fact]
    public async Task ARetryBesideALandingDoesNotVouchForWhatAnotherWindowLeftUnknown()
    {
        // MAJOR-1 (re-review of #265 at bde6a99): window A saw the note land and the time entry plainly
        // fail. Window B then sent the time entry, and it came back unknown. A's Retry was unheld and
        // went with publish_again, so the time entry could land twice.
        var publisher = await PublisherAsync();
        _backend.FailTime = true;
        var panel = Panel(publisher);
        await panel.PublishAsync(Draft(), ["f1"], TestContext.Current.CancellationToken);
        Assert.True(panel.PartiallyPublished);
        Assert.Equal(1, _backend.PublishCalls);

        // Window B's attempt, as the store keeps it.
        Assert.True(await _store!.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = "landed", ["time_entry"] = "unknown" }, TestContext.Current.CancellationToken));
        _backend.FailTime = false;

        Assert.True(panel.CanRetry);
        var results = await panel.RetryAsync(Draft(), ["f1"], TestContext.Current.CancellationToken);

        Assert.Equal(1, _backend.PublishCalls);
        Assert.Equal(DestinationResult.SessionUnconfirmed, results.Single(r => r.Destination == Destination.TimeEntry).Kind);
    }

    [Fact]
    public async Task AWholeRequestRefusalAfterAnUnknownKeepsRetryHeld()
    {
        // MAJOR-1, the same window: the note landed and the time entry may have. The technician ticked
        // the box and Retry was refused for the whole request, which says nothing about the ticket. The
        // time entry is still in doubt, so the next Retry waits for the box again.
        var publisher = await PublisherAsync();
        _backend.TimeKind = "unknown";
        var panel = Panel(publisher);
        await panel.PublishAsync(Draft(), ["f1"], TestContext.Current.CancellationToken);
        Assert.Equal(1, _backend.PublishCalls);

        panel.TicketChecked = true;
        _backend.RefuseOnce = "The backend did not answer.";
        _backend.TimeKind = null;
        var refused = await panel.RetryAsync(Draft(), ["f1"], TestContext.Current.CancellationToken);
        Assert.Equal(DestinationResult.Refused, refused.Single(r => r.Destination == Destination.TimeEntry).Kind);
        Assert.Equal(2, _backend.PublishCalls);

        Assert.False(panel.CanRetry);
        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.RetryBlockedBecause);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.RetryAsync(Draft(), ["f1"], TestContext.Current.CancellationToken));
        Assert.Equal(2, _backend.PublishCalls);
    }

    /// <summary>A real pane over the pipe, on the seeded session, with the note and the time entry chosen.</summary>
    private static PublishPanel Panel(PipePublisher publisher)
    {
        var panel = new PublishPanel(
            Session(draft: Draft()) with { SessionId = "s1" },
            ["connectwise"],
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            publisher.PublishAsync);
        panel.KbArticle = false;
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        return panel;
    }

    [Fact]
    public void TheSessionsPublishOutcomesBecomeThePanesEarlierOutcomes()
    {
        var earlier = PipePublisher.Earlier(new Dictionary<string, string> { ["ticket_note"] = "landed", ["time_entry"] = "unknown", ["something_new"] = "landed" });

        Assert.Equal(new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Landed, [Destination.TimeEntry] = EarlierOutcome.Unknown }, earlier);
        Assert.Empty(PipePublisher.Earlier(null));
    }

    [Fact]
    public async Task AnUnconfirmedSessionIsRefusedAsSessionUnconfirmed()
    {
        var publisher = await PublisherAsync();
        Assert.True(await _store!.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = "unknown" }, TestContext.Current.CancellationToken));
        var request = new PublishRequest("s1", new TicketMatch("48213", "Printer offline", "Acme Dental"), NoteType.Internal, 30, new HashSet<Destination> { Destination.TicketNote }, Draft(), ["f1"]);

        var refused = Assert.Single(await publisher.PublishAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal((false, DestinationResult.SessionUnconfirmed, PublishCommands.Unconfirmed), (refused.Ok, refused.Kind, refused.Error));
        Assert.True(refused.MayHaveLanded);
        Assert.Equal(0, _backend.PublishCalls);
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

        // Said of the whole request, not of the destination, so the pane does not read it as "nothing of
        // this is on the ticket" and forget an earlier unknown (m-c).
        Assert.Equal(DestinationResult.Refused, only.Kind);
        Assert.False(only.MayHaveLanded);
    }

    [Fact]
    public async Task APaneThatKnowsOnlyAnotherEarlierOutcomeCannotSendOverAStoredUnknown()
    {
        // MJ1 (re-review of #265): the pane opened knowing the note landed, and the store also holds the
        // time entry as unknown (another window's attempt). The pane offers the time entry with nothing
        // to tick, as a plain failure would be, and the service refuses it for what it holds.
        var publisher = await PublisherAsync();
        Assert.True(await _store!.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = "landed", ["time_entry"] = "unknown" }, TestContext.Current.CancellationToken));
        var panel = new PublishPanel(
            Session(draft: Draft()) with { SessionId = "s1" },
            ["connectwise"],
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            publisher.PublishAsync,
            earlierOutcomes: new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Landed });
        panel.KbArticle = false;
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        Assert.True(panel.TimeEntry);
        Assert.Null(panel.BlockedBecause);

        var results = await panel.PublishAsync(Draft(), ["f1"], TestContext.Current.CancellationToken);

        Assert.Equal(0, _backend.PublishCalls);
        var refused = Assert.Single(results);
        Assert.Equal((Destination.TimeEntry, DestinationResult.SessionUnconfirmed), (refused.Destination, refused.Kind));
    }

    [Fact]
    public async Task ARefusedPublishIsAskedOfTheBackendOnce()
    {
        // S-36: the refusal's words come back on the one exchange. Sending the command again to learn
        // them is a second publish, with a customer's ticket at the end of it.
        var publisher = await PublisherAsync();
        _backend.Refusal = "The backend did not answer.";
        var request = new PublishRequest("s1", new TicketMatch("1", "x", "y"), NoteType.Internal, 30, new HashSet<Destination> { Destination.TicketNote, Destination.TimeEntry }, Draft(), ["f1"]);

        var results = await publisher.PublishAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal((false, "The backend did not answer."), (r.Ok, r.Error)));
        Assert.Equal(1, _backend.PublishCalls);
    }

    [Fact]
    public async Task ARefusalThatClearsOnTheSecondAskIsNotPublishedBehindTheTechniciansBack()
    {
        // The case that posts a customer note nobody clicked for (INV-3): the first answer is a timeout
        // and a second ask would have gone through. One click is one ask; the pane shows the refusal and
        // Retry is the technician's to press.
        var publisher = await PublisherAsync();
        _backend.RefuseOnce = "The backend did not answer.";
        var request = new PublishRequest("s1", new TicketMatch("1", "x", "y"), NoteType.Internal, 30, new HashSet<Destination> { Destination.TicketNote }, Draft(), ["f1"]);

        var results = await publisher.PublishAsync(request, TestContext.Current.CancellationToken);

        var only = Assert.Single(results);
        Assert.False(only.Ok);
        Assert.Equal("The backend did not answer.", only.Error);
        Assert.Equal(1, _backend.PublishCalls);
        Assert.Null(_backend.Received);
    }

    [Fact]
    public async Task WithNoServiceEveryDestinationFailsWithoutAsking()
    {
        await using var nobody = new CaptureConnection(new ShellState(), _ => throw new TimeoutException("nothing is listening"), CaptureConnection.Never);
        var publisher = new PipePublisher(nobody);
        var request = new PublishRequest("s1", new TicketMatch("1", "x", "y"), NoteType.Internal, 30, new HashSet<Destination> { Destination.TicketNote, Destination.KbArticle }, Draft(), []);

        var results = await publisher.PublishAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal((false, "Not connected to the capture service."), (r.Ok, r.Error)));
    }

    [Fact]
    public async Task ALinkThatIsNotHttpsIsNotOfferedToOpen()
    {
        // "Open in ConnectWise" hands the link to the shell, so only a web link the provider gave back
        // is kept; anything else lands without a link rather than with one that opens something else.
        var publisher = await PublisherAsync();
        _backend.Link = "file:///C:/Windows/System32/calc.exe";
        var request = new PublishRequest("s1", new TicketMatch("1", "x", "y"), NoteType.Internal, 30, new HashSet<Destination> { Destination.TicketNote }, Draft(), []);

        var only = Assert.Single(await publisher.PublishAsync(request, TestContext.Current.CancellationToken));

        Assert.True(only.Ok);
        Assert.Null(only.Link);
    }

    [Fact]
    public async Task ARefusedConnectionCheckIsAskedOfTheBackendOnce()
    {
        _ = await PublisherAsync();
        var settings = new PipeIntegrations(_connection!);
        _backend.Refusal = "The backend did not answer.";

        var check = await settings.CheckAsync("hudu", TestContext.Current.CancellationToken);

        Assert.False(check.Ok);
        Assert.Equal("The backend did not answer.", check.Message);
        Assert.Equal(1, _backend.CheckCalls);
    }

    [Fact]
    public async Task AnAcceptedActivationNamesTheTenantAfterOneCall()
    {
        // The single ask also carries the good answer: the card names the tenant, the device keeps its
        // credentials, and the backend saw exactly one activation.
        var backend = new ActivationBackend(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { tenantName = "Acme IT", deviceId = Guid.NewGuid(), refreshToken = "r", accessToken = "a", expiresAt = DateTimeOffset.UtcNow.AddHours(1) }),
        });
        _ = await PublisherAsync(backend.Commands());
        var device = new PipeDevice(_connection!);

        var answer = await device.ActivateAsync("KX7PM-4R2WQ", "TECH", TestContext.Current.CancellationToken);

        Assert.Equal("Acme IT", answer.TenantName);
        Assert.Null(answer.Refusal);
        Assert.Equal(1, backend.Calls);
        Assert.NotNull(backend.Credentials.Load());
    }

    [Fact]
    public async Task ARefusedActivationIsAskedOfTheBackendOnce()
    {
        // S-135: the activation card had the same second ask. A used or expired code costs one call.
        var backend = new ActivationBackend(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { error = "invite_expired", message = "That code has expired. Ask your admin for a new invite." }),
        });
        _ = await PublisherAsync(backend.Commands());
        var device = new PipeDevice(_connection!);

        var answer = await device.ActivateAsync("KX7PM-4R2WQ", "TECH", TestContext.Current.CancellationToken);

        Assert.Null(answer.TenantName);
        Assert.Equal("That code has expired. Ask your admin for a new invite.", answer.Refusal);
        Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task AnActivationRefusedOnceIsNotRetriedBehindTheTechniciansBack()
    {
        // A backend that timed out and would have answered the second time: the card shows the timeout,
        // and the device is not enrolled by a call the technician did not make.
        var backend = new ActivationBackend(call => call == 1
            ? throw new HttpRequestException("timed out")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { tenantName = "Acme IT", deviceId = Guid.NewGuid(), refreshToken = "r", accessToken = "a", expiresAt = DateTimeOffset.UtcNow.AddHours(1) }),
            });
        _ = await PublisherAsync(backend.Commands());
        var device = new PipeDevice(_connection!);

        var answer = await device.ActivateAsync("KX7PM-4R2WQ", "TECH", TestContext.Current.CancellationToken);

        Assert.Null(answer.TenantName);
        Assert.Equal("The backend did not answer.", answer.Refusal);
        Assert.Equal(1, backend.Calls);
        Assert.Null(backend.Credentials.Load());
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

    private async Task<PipePublisher> PublisherAsync(DeviceCommands? device = null)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)));
        await _store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await _store.SaveRedactedFrameAsync("s1", new StagedFrame("f1", 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 }), new RedactionOutcome(new byte[] { 9, 9 }, "t", [], false, DateTimeOffset.UnixEpoch));
        await _store.SaveDraftAsync("s1", Draft());
        await _store.FinalizeSessionAsync("s1", new FinalizeInfo(10_000, false));

        var handler = new PipeController(new PublishCommands(_store, _backend), device);
        _server = new IpcServer(IpcServer.DefaultPipeFactory(_pipeName), _token, new AcceptAll(), handler, _store, "0.0.1-test");
        _server.Start();
        _connection = new CaptureConnection(
            new ShellState(),
            async ct => await IpcClient.ConnectAsync(_pipeName, _token, "test-ui", serverVerifier: null, "0.0.1", ct: ct),
            // Keep trying until the server is listening. IpcServer.Start creates its pipe on a background
            // task, and with CaptureConnection.Never a first attempt that ran ahead of it on a busy runner
            // was the only attempt: both pipe test classes failed together, about one hosted run in ten.
            _ => TimeSpan.FromMilliseconds(50));
        await _connection.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!_connection.IsConnected)
        {
            Assert.True(DateTime.UtcNow < deadline, "the connection did not come up");
            await Task.Delay(10);
        }

        return new PipePublisher(_connection);
    }

    /// <summary>The service's publish commands, and its device commands when a test brings them.</summary>
    private sealed class PipeController(PublishCommands publishing, DeviceCommands? device) : IIpcCommandHandler
    {
        public CaptureStateSnapshot CurrentState => CaptureStateSnapshot.Idle;

        public CapabilitiesReported CurrentCapabilities => throw new NotSupportedException();

        public async Task<CommandResult> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            await publishing.HandleAsync(command, caller, ct)
            ?? (device is null ? null : await device.HandleAsync(command, ct))
            ?? new CommandResult { RequestId = command.RequestId, Ok = false, Error = "Not a publish command." };

        public async Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            await publishing.ReplyToAsync(command, caller, ct)
            ?? (device is null ? null : await device.ReplyToAsync(command, ct));
    }

    /// <summary>The backend's <c>/v1/devices/activate</c>, counting every call that reaches it.</summary>
    private sealed class ActivationBackend(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public DeviceSessionTests.InMemoryCredentials Credentials { get; } = new();

        public DeviceCommands Commands() => new(new DeviceSession(
            new HttpClient(this) { BaseAddress = new Uri("https://api.screentail.example/") },
            Credentials,
            time: new ManualTime(DateTimeOffset.UtcNow)));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(Calls));
        }
    }

    private sealed class ScriptedBackend : IPsaGateway
    {
        public string? Refusal { get; set; }

        /// <summary>Refuses the next publish with these words, then lands every one after it.</summary>
        public string? RefuseOnce { get; set; }

        /// <summary>Every publish that reached the backend, refused or not.</summary>
        public int PublishCalls { get; private set; }

        public int CheckCalls { get; private set; }

        public string Link { get; set; } = "https://cw.example/1";

        public bool FailTime { get; set; }

        /// <summary>When set, the time entry comes back failed with this kind (unknown, timed_out), ahead of <see cref="FailTime"/>.</summary>
        public string? TimeKind { get; set; }

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

        public Task<GatewayAnswer<IntegrationCheckRow>> CheckIntegrationAsync(string provider, CancellationToken ct = default)
        {
            CheckCalls++;
            return Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IntegrationCheckRow>(r) : GatewayAnswer.Of(new IntegrationCheckRow(true, $"Connected to {Details.FirstOrDefault(d => d.Provider == provider)?.SiteUrl}.")));
        }

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

        /// <summary>When set, a publish waits for it before answering, so a test can drop the pipe meanwhile.</summary>
        public Task? Hold { get; set; }

        /// <summary>Set once a publish reaches the backend.</summary>
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default)
        {
            PublishCalls++;
            Reached.TrySetResult();
            if (Hold is { } hold)
            {
                await hold.ConfigureAwait(false);
            }

            if (RefuseOnce is { } once)
            {
                RefuseOnce = null;
                return GatewayAnswer.Refused<IReadOnlyList<PublishOutcomeRow>>(once);
            }

            if (Refusal is { } r)
            {
                return GatewayAnswer.Refused<IReadOnlyList<PublishOutcomeRow>>(r);
            }

            Received = bundle;
            return GatewayAnswer.Of<IReadOnlyList<PublishOutcomeRow>>([.. bundle.Destinations.Select(d =>
                d == "time_entry" && TimeKind is { } kind ? new PublishOutcomeRow(d, false, Error: "ConnectWise may have saved it.", Kind: kind)
                : d == "time_entry" && FailTime ? new PublishOutcomeRow(d, false, Error: "ConnectWise says no.", Kind: "forbidden")
                : new PublishOutcomeRow(d, true, "1", Link))]);
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
