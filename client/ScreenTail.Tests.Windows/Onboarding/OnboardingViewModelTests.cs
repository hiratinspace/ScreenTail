using System.Runtime.Versioning;
using ScreenTail.Core.Onboarding;
using ScreenTail.Core.Settings;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;
using ScreenTail.UI.Onboarding;

namespace ScreenTail.Tests.Windows.Onboarding;

/// <summary>
/// The onboarding window's view model over the wizard's rules (ST-083). The rules are tested in Core;
/// this is the binding between them, where the activation card is Settings' own view model acting on
/// the wizard's panel by itself. No window and no Application: the view model is plain properties, so
/// this runs on the hosted runner without a dispatcher.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OnboardingViewModelTests
{
    [Fact]
    public async Task ActivatingInTheWizardTurnsOnContinue()
    {
        // 2026-10-12 review (T-166). The card activated the device and the step's Continue stayed off,
        // because nothing refreshed the wizard's view model after the card acted.
        var screen = new OnboardingViewModel(Wizard(), () => { });
        await screen.NextCommand.ExecuteAsync(null);
        Assert.Equal(OnboardingStep.Activate, screen.Step);
        Assert.False(screen.CanContinue);
        Assert.NotEmpty(screen.Blocker);

        screen.Activation.Code = InviteCodes.Example;
        await screen.Activation.ActivateCommand.ExecuteAsync(null);

        Assert.True(screen.CanContinue);
        Assert.Empty(screen.Blocker);

        await screen.NextCommand.ExecuteAsync(null);
        Assert.Equal(OnboardingStep.Permissions, screen.Step);
    }

    [Fact]
    public async Task ARefusedActivationLeavesContinueOff()
    {
        var screen = new OnboardingViewModel(Wizard(new FakeDevice { Refusal = "That code has expired. Ask your admin for a new invite." }), () => { });
        await screen.NextCommand.ExecuteAsync(null);

        screen.Activation.Code = InviteCodes.Example;
        await screen.Activation.ActivateCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingStep.Activate, screen.Step);
        Assert.False(screen.CanContinue);
    }

    [Fact]
    public async Task TheStepListFollowsTheWizard()
    {
        // Spec §5 S8, as built v0.6.7: the list on the frame names every step and marks the one in hand.
        var screen = new OnboardingViewModel(Wizard(), () => { });
        Assert.Equal(7, screen.Steps.Count);
        Assert.Equal("Welcome", screen.Steps.Single(s => s.IsCurrent).Name);
        Assert.Equal("Step 1 of 7", screen.Progress);
        Assert.DoesNotContain(screen.Steps, s => s.IsDone);

        await screen.NextCommand.ExecuteAsync(null);

        Assert.Equal("Activate", screen.Steps.Single(s => s.IsCurrent).Name);
        Assert.Equal("Welcome", screen.Steps.Single(s => s.IsDone).Name);
        Assert.Equal("Step 2 of 7", screen.Progress);
        Assert.Equal("Step 2 of 7: Activate, this step", screen.Steps[1].Spoken);

        await screen.BackCommand.ExecuteAsync(null);
        Assert.Equal("Welcome", screen.Steps.Single(s => s.IsCurrent).Name);
    }

    [Fact]
    public void DonesNoteIsThePagesAndNotRepeatedInTheFoot()
    {
        // The page's first line on Done is the wizard's note, bound and announced there; the foot's copy
        // of the same sentence under it is hidden. On every other step the foot shows the note.
        var wizard = Wizard();
        var screen = new OnboardingViewModel(wizard, () => { });

        wizard.Go(OnboardingStep.Done);
        screen.Refresh();

        Assert.Equal("ScreenTail starts when you open a remote session. Look for the pill.", screen.Note);
        Assert.Equal(System.Windows.Visibility.Collapsed, screen.NoteVisibility);
        Assert.Equal(System.Windows.Visibility.Visible, screen.FinishVisibility);
        Assert.Equal("Done", screen.Steps.Single(s => s.IsCurrent).Name);
        Assert.Equal(6, screen.Steps.Count(s => s.IsDone));
    }

    private static OnboardingWizard Wizard(FakeDevice? device = null) =>
        new(
            new ActivationPanel(device ?? new FakeDevice(), "TECH-LAPTOP"),
            new FakeCapabilities(),
            new CapturePanel(new FakeCapture()),
            new IntegrationsPanel(new FakeIntegrations()),
            new FakeTestSession(),
            _ => { });

    private sealed class FakeDevice : IDeviceGateway
    {
        private DeviceStanding _standing = new(false, null, 0, false, false, "Not activated. Enter the code from your invite.");

        public string? Refusal { get; init; }

        public Task<DeviceStanding?> StandingAsync(CancellationToken ct = default) => Task.FromResult<DeviceStanding?>(_standing);

        public Task<ActivationAnswer> ActivateAsync(string code, string deviceName, CancellationToken ct = default)
        {
            if (Refusal is { } refusal)
            {
                return Task.FromResult(new ActivationAnswer(null, refusal));
            }

            _standing = new DeviceStanding(true, "Acme IT", 0, false, false, "Activated with Acme IT.");
            return Task.FromResult(new ActivationAnswer("Acme IT", null));
        }
    }

    private sealed class FakeCapabilities : ICapabilitiesGateway
    {
        public Task<IReadOnlyList<CapabilityStatus>?> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CapabilityStatus>?>([]);
    }

    private sealed class FakeCapture : ICaptureGateway
    {
        public Task<CaptureSnapshot?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<CaptureSnapshot?>(new CaptureSnapshot(new CaptureSettings(), [], [], false, 120));

        public Task<string?> SaveAsync(CaptureSettings settings, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeIntegrations : IIntegrationsGateway
    {
        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IntegrationDetail>?>([]);

        public Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default) => Task.FromResult(new CheckOutcome(true, "Connected."));

        public Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default) => Task.FromResult<CompanyMappingsPage?>(null);

        public Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeTestSession : ITestSession
    {
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> StopAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
