using ScreenTail.Core.Onboarding;
using ScreenTail.Core.Settings;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Onboarding;

/// <summary>
/// The onboarding wizard's rules (ST-083, Spec §5 S8): seven steps in order, each with what it takes to
/// continue; permissions checked with a fix offered and re-checked (AC3); integrations skippable with
/// drafts still produced and Publish saying "Connect a PSA" (AC2); "Try it" a real test session through
/// the service; Done remembered so it does not run again, and re-runnable from the tray.
/// </summary>
public sealed class OnboardingWizardTests
{
    [Fact]
    public void TheStepsRunInTheSpecsOrderAndTheDotsSayWhere()
    {
        var wizard = Wizard();

        Assert.Equal(OnboardingStep.Welcome, wizard.Step);
        Assert.Equal(7, wizard.StepCount);
        Assert.Equal(1, wizard.StepNumber);
        Assert.False(wizard.CanGoBack);

        wizard.Next();
        Assert.Equal(OnboardingStep.Activate, wizard.Step);
        Assert.True(wizard.CanGoBack);
        wizard.Back();
        Assert.Equal(OnboardingStep.Welcome, wizard.Step);
    }

    [Fact]
    public async Task ActivationWaitsForTheDeviceOrAnExplicitLater()
    {
        var device = new FakeDevice();
        var wizard = Wizard(device: device);
        wizard.Next();

        await wizard.LoadStepAsync();
        Assert.False(wizard.CanContinue);
        Assert.Contains("code", wizard.Blocker, StringComparison.OrdinalIgnoreCase);

        wizard.Activation.Code = "KX7PM-4R2WQ";
        Assert.True(await wizard.Activation.ActivateAsync());
        Assert.True(wizard.CanContinue);
        Assert.Equal("Acme IT", wizard.Activation.TenantName);
    }

    [Fact]
    public async Task ActivationCanBeSkippedBecauseTheStepSaysItCan()
    {
        // The blocker's own words are "or come back to this from Settings later" (ST-010's card), and a
        // device enrolled from the environment (the M1 runbook) has no code to enter. Skip has to be real.
        var wizard = Wizard();
        wizard.Next();
        await wizard.LoadStepAsync();

        Assert.True(wizard.CanSkip);
        wizard.Skip();

        Assert.Equal(OnboardingStep.Permissions, wizard.Step);
    }

    [Fact]
    public async Task ABlockedMicrophoneShowsTheFixAndIsCheckedAgain()
    {
        // AC3: blocked mic → deep link and re-check.
        var capabilities = new FakeCapabilities
        {
            Checks =
            [
                new CapabilityStatus { Capability = "microphone", State = "blocked", Message = "Microphone access is off for desktop apps.", FixHint = "ms-settings:privacy-microphone" },
                new CapabilityStatus { Capability = "screen_capture", State = "ok", Message = "Screen capture works." },
                new CapabilityStatus { Capability = "input_hooks", State = "ok", Message = "Input hooks install." },
            ],
        };
        var wizard = Wizard(capabilities: capabilities);
        wizard.Go(OnboardingStep.Permissions);

        await wizard.LoadStepAsync();

        var mic = wizard.Permissions.Single(p => p.Capability == "microphone");
        Assert.False(mic.Ok);
        Assert.Equal("ms-settings:privacy-microphone", mic.FixLink);
        Assert.True(wizard.CanContinue, "a blocked microphone is a worse session, not a blocked setup");
        Assert.Contains("without narration", wizard.Blocker ?? wizard.Note, StringComparison.OrdinalIgnoreCase);

        capabilities.Checks = [.. capabilities.Checks.Select(c => c.Capability == "microphone" ? c with { State = "ok", Message = "Microphone works.", FixHint = null } : c)];
        await wizard.RecheckAsync();
        Assert.True(wizard.Permissions.Single(p => p.Capability == "microphone").Ok);
        Assert.Equal(2, capabilities.Asked);
    }

    [Fact]
    public async Task ScreenCaptureBlockedBlocksSetup()
    {
        var capabilities = new FakeCapabilities
        {
            Checks = [new CapabilityStatus { Capability = "screen_capture", State = "blocked", Message = "Screen capture is refused by policy.", FixHint = null }],
        };
        var wizard = Wizard(capabilities: capabilities);
        wizard.Go(OnboardingStep.Permissions);

        await wizard.LoadStepAsync();

        Assert.False(wizard.CanContinue);
        Assert.Contains("Screen capture", wizard.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkippingConnectFinishesWithNothingConnected()
    {
        // AC2: skipped integrations → drafts still produced; Publish shows "Connect a PSA" (the
        // publish pane's own rule). The wizard only has to let the technician past.
        var integrations = new FakeIntegrations();
        var wizard = Wizard(integrations: integrations);
        wizard.Go(OnboardingStep.Connect);
        await wizard.LoadStepAsync();

        Assert.True(wizard.CanSkip);
        wizard.Skip();

        Assert.Equal(OnboardingStep.TryIt, wizard.Step);
        Assert.Empty(integrations.Stored);
    }

    [Fact]
    public async Task TryItStartsAndStopsATestSessionThroughTheService()
    {
        var session = new FakeTestSession();
        var wizard = Wizard(session: session);
        wizard.Go(OnboardingStep.TryIt);
        await wizard.LoadStepAsync();
        Assert.False(wizard.CanContinue);

        Assert.True(await wizard.StartTestAsync());
        Assert.True(session.Started);
        Assert.True(wizard.TestRunning);
        Assert.True(await wizard.StopTestAsync());
        Assert.True(session.Stopped);
        Assert.False(wizard.TestRunning);
        Assert.True(wizard.CanContinue);
        Assert.Contains("draft", wizard.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FinishingIsRememberedSoSetupDoesNotRunAgainUnasked()
    {
        DateTimeOffset? finishedAt = null;
        var wizard = Wizard(finished: at => finishedAt = at);
        wizard.Go(OnboardingStep.Done);
        await wizard.LoadStepAsync();

        wizard.Finish();

        Assert.NotNull(finishedAt);
        Assert.True(wizard.Finished);
    }

    private static OnboardingWizard Wizard(FakeDevice? device = null, FakeCapabilities? capabilities = null, FakeIntegrations? integrations = null, FakeTestSession? session = null, Action<DateTimeOffset>? finished = null) =>
        new(
            new ActivationPanel(device ?? new FakeDevice(), "TECH-LAPTOP"),
            capabilities ?? new FakeCapabilities(),
            new CapturePanel(new FakeCapture()),
            new IntegrationsPanel(integrations ?? new FakeIntegrations()),
            session ?? new FakeTestSession(),
            finished ?? (_ => { }));

    private sealed class FakeDevice : IDeviceGateway
    {
        private DeviceStanding _standing = new(false, null, 0, false, false, "Not activated. Enter the code from your invite.");

        public Task<DeviceStanding?> StandingAsync(CancellationToken ct = default) => Task.FromResult<DeviceStanding?>(_standing);

        public Task<ActivationAnswer> ActivateAsync(string code, string deviceName, CancellationToken ct = default)
        {
            _standing = new DeviceStanding(true, "Acme IT", 0, false, false, "Activated with Acme IT.");
            return Task.FromResult(new ActivationAnswer("Acme IT", null));
        }
    }

    private sealed class FakeCapabilities : ICapabilitiesGateway
    {
        public IReadOnlyList<CapabilityStatus> Checks { get; set; } =
        [
            new CapabilityStatus { Capability = "microphone", State = "ok", Message = "Microphone works." },
            new CapabilityStatus { Capability = "screen_capture", State = "ok", Message = "Screen capture works." },
            new CapabilityStatus { Capability = "input_hooks", State = "ok", Message = "Input hooks install." },
        ];

        public int Asked { get; private set; }

        public Task<IReadOnlyList<CapabilityStatus>?> CheckAsync(CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult<IReadOnlyList<CapabilityStatus>?>(Checks);
        }
    }

    private sealed class FakeCapture : ICaptureGateway
    {
        public Task<CaptureSnapshot?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<CaptureSnapshot?>(new CaptureSnapshot(new Shared.Settings.CaptureSettings(), [new ToolChoice("rdp", "Remote Desktop")], [], false, 120));

        public Task<string?> SaveAsync(Shared.Settings.CaptureSettings settings, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeIntegrations : IIntegrationsGateway
    {
        public List<string> Stored { get; } = [];

        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IntegrationDetail>?>([]);

        public Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default)
        {
            Stored.Add(provider);
            return Task.FromResult<string?>(null);
        }

        public Task<string?> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default) => Task.FromResult(new CheckOutcome(true, "Connected."));

        public Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default) => Task.FromResult<CompanyMappingsPage?>(null);

        public Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeTestSession : ITestSession
    {
        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public Task<bool> StartAsync(CancellationToken ct = default)
        {
            Started = true;
            return Task.FromResult(true);
        }

        public Task<bool> StopAsync(CancellationToken ct = default)
        {
            Stopped = true;
            return Task.FromResult(true);
        }
    }
}
