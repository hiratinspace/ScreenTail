using System.IO.Pipes;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Onboarding;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Shell;
using ScreenTail.Core.Speech;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// The Settings screens' and the wizard's pipe gateways, over a real pipe to a scripted service
/// (2026-10-12 review, T-143). The panel tests run against fakes of these, so a gateway that swapped two
/// flags of the same type, or two dates, would compile and pass every panel test while the screen showed
/// a lock the admin never set. Each value here is chosen so that a swap with its neighbour reads
/// differently, and each acting command is counted, because a second ask is a second backend call.
/// </summary>
public sealed class PipeSettingsGatewaysTests : IAsyncDisposable
{
    private readonly string _pipeName = IpcPipeNames.ForUser("test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _token = IpcToken.Generate();
    private readonly ScriptedService _service = new();
    private IpcServer? _server;
    private CaptureConnection? _connection;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ThePrivacyGatewayKeepsEachLockOnTheFieldTheAdminLocked(bool localOnlyLocked, bool retentionLocked)
    {
        // The technician's own document beside what is in force: local-only off and seven days of their
        // own, local-only on and thirty days in force. A gateway that read one for the other would show
        // the technician a setting they never made.
        var own = new ClientSettings { LocalOnly = false, RetentionDays = 7, Emails = true };
        _service.Reply = command => command is GetSettingsCommand
            ? new SettingsReported
            {
                Settings = own,
                LocalOnlyLocked = localOnlyLocked,
                RetentionLocked = retentionLocked,
                PolicyVersion = "p-42",
                EffectiveLocalOnly = true,
                EffectiveRetentionDays = 30,
            }
            : null;
        var privacy = new PipePrivacy(await ConnectAsync());

        var snapshot = await privacy.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Equal(localOnlyLocked, snapshot.LocalOnlyLocked);
        Assert.Equal(retentionLocked, snapshot.RetentionLocked);
        Assert.Equal("p-42", snapshot.PolicyVersion);
        Assert.True(snapshot.EffectiveLocalOnly);
        Assert.Equal(30, snapshot.EffectiveRetentionDays);
        Assert.True(snapshot.Settings.Emails);
        Assert.False(snapshot.Settings.LocalOnly);
        Assert.Equal(7, snapshot.Settings.RetentionDays);
    }

    [Fact]
    public async Task ThePrivacyGatewaySendsTheDocumentAndBringsBackTheRefusalsWords()
    {
        _service.Result = command => command is SetSettingsCommand set && set.Settings.RetentionDays == 99
            ? new CommandResult { RequestId = command.RequestId, Ok = false, Error = "Retention is between 1 and 30 days." }
            : new CommandResult { RequestId = command.RequestId, Ok = true };
        var privacy = new PipePrivacy(await ConnectAsync());

        var saved = await privacy.SaveAsync(new ClientSettings { RetentionDays = 14, Telemetry = true }, TestContext.Current.CancellationToken);
        var refused = await privacy.SaveAsync(new ClientSettings { RetentionDays = 99 }, TestContext.Current.CancellationToken);

        Assert.Null(saved);
        Assert.Equal("Retention is between 1 and 30 days.", refused);
        var sent = _service.Received.OfType<SetSettingsCommand>().ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(14, sent[0].Settings.RetentionDays);
        Assert.True(sent[0].Settings.Telemetry);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheCaptureGatewayCarriesTheForcedScopeApartFromTheTechniciansOwn(bool forced)
    {
        _service.Reply = command => command is GetCaptureSettingsCommand
            ? new CaptureSettingsReported
            {
                Settings = new CaptureSettings { CaptureAllWindows = !forced, GraceSeconds = 45, DisabledTools = ["anydesk"] },
                Tools = [new ToolRow("rdp", "Remote Desktop"), new ToolRow("anydesk", "AnyDesk")],
                Microphones = [new MicrophoneRow("{0.0.1.00000000}.{headset}", "Headset", IsDefault: false), new MicrophoneRow("{0.0.1.00000000}.{built-in}", "Built-in microphone", IsDefault: true)],
                PolicyForcesAllWindows = forced,
                DefaultGraceSeconds = 120,
            }
            : null;
        var capture = new PipeCapture(await ConnectAsync());

        var snapshot = await capture.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Equal(forced, snapshot.PolicyForcesAllWindows);
        Assert.Equal(!forced, snapshot.Settings.CaptureAllWindows);
        Assert.Equal(45, snapshot.Settings.GraceSeconds);
        Assert.Equal(120, snapshot.DefaultGraceSeconds);
        Assert.Equal(["anydesk"], snapshot.Settings.DisabledTools);
        Assert.Equal([new ToolChoice("rdp", "Remote Desktop"), new ToolChoice("anydesk", "AnyDesk")], snapshot.Tools);
        Assert.Equal([new MicrophoneDevice("{0.0.1.00000000}.{headset}", "Headset", IsDefault: false), new MicrophoneDevice("{0.0.1.00000000}.{built-in}", "Built-in microphone", IsDefault: true)], snapshot.Microphones);
    }

    [Fact]
    public async Task TheCaptureGatewaySavesOnceAndBringsBackTheRefusalsWords()
    {
        _service.Result = command => new CommandResult { RequestId = command.RequestId, Ok = false, Error = "The settings could not be written to disk." };
        var capture = new PipeCapture(await ConnectAsync());

        var refused = await capture.SaveAsync(new CaptureSettings { AutoStart = false }, TestContext.Current.CancellationToken);

        Assert.Equal("The settings could not be written to disk.", refused);
        Assert.False(Assert.Single(_service.Received.OfType<SetCaptureSettingsCommand>()).Settings.AutoStart);
    }

    [Fact]
    public async Task TheLevelGatewayAsksForTheDeviceInThePickerAndClosesWhenTold()
    {
        // ST-080 AC3. The device id goes out as chosen, the state, level and device listened to come back
        // each in its own place, and the close is one message.
        _service.Reply = command => command is GetMicrophoneLevelCommand
            ? new MicrophoneLevelReported { State = MicrophoneLevelStates.Listening, Level = 0.25, DeviceId = "{0.0.1.00000000}.{built-in}" }
            : null;
        _service.Result = command => new CommandResult { RequestId = command.RequestId, Ok = true };
        var levels = new PipeCapture(await ConnectAsync());

        var reading = await levels.ReadLevelAsync("{0.0.1.00000000}.{left-at-home}", TestContext.Current.CancellationToken);
        await levels.StopLevelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new MeterReading(MicrophoneLevelStates.Listening, 0.25, "{0.0.1.00000000}.{built-in}"), reading);
        Assert.Equal("{0.0.1.00000000}.{left-at-home}", Assert.Single(_service.Received.OfType<GetMicrophoneLevelCommand>()).DeviceId);
        _ = Assert.Single(_service.Received.OfType<StopMicrophoneLevelCommand>());
    }

    [Fact]
    public async Task TheDeviceGatewayKeepsEachStandingFieldWhereTheServiceSaidIt()
    {
        // Three flags and a count beside each other: activated, past the grace period, not revoked.
        _service.Reply = command => command is GetDeviceCommand
            ? new DeviceReported
            {
                Activated = true,
                TenantName = "Acme IT",
                DaysOffline = 9,
                BeyondGrace = true,
                Revoked = false,
                Standing = "offline_beyond_grace",
            }
            : null;
        var device = new PipeDevice(await ConnectAsync());

        var standing = await device.StandingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DeviceStanding(true, "Acme IT", 9, true, false, "offline_beyond_grace"), standing);
    }

    [Fact]
    public async Task ARefusedActivationIsSentToTheServiceOnce()
    {
        // The service's refusal carries its words on the one reply. A gateway that asked again to learn
        // them would make a second activation call to the backend, and one that went through after a
        // timeout would enrol the device while the card said it had not (INV-11).
        _service.Result = command => command is ActivateDeviceCommand
            ? new CommandResult { RequestId = command.RequestId, Ok = false, Error = "That code has expired. Ask your admin for a new invite." }
            : null;
        var device = new PipeDevice(await ConnectAsync());

        var answer = await device.ActivateAsync("KX7PM-4R2WQ", "TECH", TestContext.Current.CancellationToken);

        Assert.Null(answer.TenantName);
        Assert.Equal("That code has expired. Ask your admin for a new invite.", answer.Refusal);
        var sent = Assert.Single(_service.Received.OfType<ActivateDeviceCommand>());
        Assert.Equal(("KX7PM-4R2WQ", "TECH"), (sent.Code, sent.DeviceName));
    }

    [Fact]
    public async Task AnAcceptedActivationNamesTheTenantAfterOneAsk()
    {
        _service.Reply = command => command is ActivateDeviceCommand
            ? new DeviceActivated { TenantName = "Acme IT", DeviceId = Guid.NewGuid() }
            : null;
        var device = new PipeDevice(await ConnectAsync());

        var answer = await device.ActivateAsync("KX7PM-4R2WQ", "TECH", TestContext.Current.CancellationToken);

        Assert.Equal(new ActivationAnswer("Acme IT", null), answer);
        _ = Assert.Single(_service.Received.OfType<ActivateDeviceCommand>());
    }

    [Fact]
    public async Task TheIntegrationsGatewayKeepsEachDateAndEachMappingFieldApart()
    {
        var connected = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var checkedAt = new DateTimeOffset(2026, 10, 20, 16, 30, 0, TimeSpan.Zero);
        _service.Reply = command => command switch
        {
            ListIntegrationsCommand => new IntegrationDetailsListed
            {
                Integrations = [new IntegrationDetailRow("hudu", "https://acme.huducloud.com", "••••5678", connected, checkedAt, "Hudu rejected the API key.")],
            },
            GetCompanyMappingsCommand => new CompanyMappingsListed
            {
                Companies = [new CompanyChoiceRow("7", "Acme Dental (Hudu)")],
                Mappings = [new CompanyMappingRow("Acme Dental", "7", "Acme Dental (Hudu)", "exact")],
            },
            _ => null,
        };
        var integrations = new PipeIntegrations(await ConnectAsync());

        var rows = await integrations.ListAsync(TestContext.Current.CancellationToken);
        var page = await integrations.MappingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new IntegrationDetail("hudu", "https://acme.huducloud.com", "••••5678", connected, checkedAt, "Hudu rejected the API key."), Assert.Single(rows!));
        Assert.NotNull(page);
        Assert.Equal(new CompanyChoice("7", "Acme Dental (Hudu)"), Assert.Single(page.Companies));
        Assert.Equal(new CompanyMappingEntry("Acme Dental", "7", "Acme Dental (Hudu)", "exact"), Assert.Single(page.Mappings));
    }

    [Fact]
    public async Task ARefusedConnectionCheckIsSentToTheServiceOnce()
    {
        _service.Result = command => command is CheckIntegrationCommand
            ? new CommandResult { RequestId = command.RequestId, Ok = false, Error = "The backend did not answer." }
            : null;
        var integrations = new PipeIntegrations(await ConnectAsync());

        var check = await integrations.CheckAsync("hudu", TestContext.Current.CancellationToken);

        Assert.Equal(new CheckOutcome(false, "The backend did not answer."), check);
        Assert.Equal("hudu", Assert.Single(_service.Received.OfType<CheckIntegrationCommand>()).Provider);
    }

    [Fact]
    public async Task TheCapabilitiesGatewayHandsOnEveryCheckWithItsFixLink()
    {
        // The wizard's Fix button opens FixLink, so the gateway must bring it across as the service sent it.
        var blocked = CapabilityCopy.MicrophoneBlocked().ToWire();
        _service.Capabilities = new CapabilitiesReported { CheckedAt = DateTimeOffset.UnixEpoch, CanCapture = true, Checks = [blocked] };
        var capabilities = new PipeCapabilities(await ConnectAsync());

        var checks = await capabilities.CheckAsync(TestContext.Current.CancellationToken);

        var only = Assert.Single(checks!);
        Assert.Equal(blocked.Capability, only.Capability);
        Assert.Equal(blocked.State, only.State);
        Assert.Equal(blocked.FixLink, only.FixLink);
        Assert.Equal(blocked.FixHint, only.FixHint);
    }

    [Fact]
    public async Task WithNoServiceEveryGatewaySaysSoWithoutThrowing()
    {
        await using var nobody = new CaptureConnection(new ShellState(), _ => throw new TimeoutException("nothing is listening"), CaptureConnection.Never);
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await new PipePrivacy(nobody).LoadAsync(ct));
        Assert.NotNull(await new PipePrivacy(nobody).SaveAsync(new ClientSettings(), ct));
        Assert.Null(await new PipeCapture(nobody).LoadAsync(ct));
        Assert.NotNull(await new PipeCapture(nobody).SaveAsync(new CaptureSettings(), ct));
        Assert.Null(await new PipeDevice(nobody).StandingAsync(ct));
        Assert.Null((await new PipeDevice(nobody).ActivateAsync("KX7PM-4R2WQ", "TECH", ct)).TenantName);
        Assert.Null(await new PipeIntegrations(nobody).ListAsync(ct));
        Assert.False((await new PipeIntegrations(nobody).CheckAsync("hudu", ct)).Ok);
        Assert.Null(await new PipeCapabilities(nobody).CheckAsync(ct));
    }

    private async Task<CaptureConnection> ConnectAsync()
    {
        _server = new IpcServer(IpcServer.DefaultPipeFactory(_pipeName), _token, new AnyClient(), _service, new NoAudit(), "0.0.1-test");
        _server.Start();
        _connection = new CaptureConnection(
            new ShellState(),
            async ct => await IpcClient.ConnectAsync(_pipeName, _token, "test-ui", serverVerifier: null, "0.0.1", ct: ct),
            // Keep trying until the server is listening: IpcServer.Start creates its pipe on a background
            // task, as PipePublisherTests explains.
            _ => TimeSpan.FromMilliseconds(50));
        await _connection.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!_connection.IsConnected)
        {
            Assert.True(DateTime.UtcNow < deadline, "the connection did not come up");
            await Task.Delay(10);
        }

        return _connection;
    }

    /// <summary>
    /// A service that answers as scripted and keeps every command it was asked. Recorded on the way in,
    /// before either answer, so a command the server passes from one to the other is counted once.
    /// </summary>
    private sealed class ScriptedService : IIpcCommandHandler
    {
        private readonly Lock _gate = new();
        private readonly List<IpcCommand> _received = [];

        public Func<IpcCommand, IpcEvent?> Reply { get; set; } = _ => null;

        public Func<IpcCommand, CommandResult?> Result { get; set; } = _ => null;

        public CapabilitiesReported Capabilities { get; set; } = new() { CheckedAt = DateTimeOffset.UnixEpoch, CanCapture = true, Checks = [] };

        public IReadOnlyList<IpcCommand> Received
        {
            get
            {
                lock (_gate)
                {
                    return [.. _received];
                }
            }
        }

        public CaptureStateSnapshot CurrentState => CaptureStateSnapshot.Idle;

        public CapabilitiesReported CurrentCapabilities => Capabilities;

        public Task<CommandResult> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            Task.FromResult(Result(command) ?? new CommandResult { RequestId = command.RequestId, Ok = false, Error = "Not scripted." });

        public Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _received.Add(command);
            }

            return Task.FromResult(Reply(command));
        }
    }

    private sealed class AnyClient : IClientVerifier
    {
        public ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    }

    private sealed class NoAudit : IAuditLog
    {
        public Task RecordAsync(string type, string? sessionId = null, long? count = null, string? detail = null, CancellationToken ct = default) => Task.CompletedTask;
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
    }
}
