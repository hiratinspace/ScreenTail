using ScreenTail.Core.Settings;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Onboarding;

/// <summary>Spec §5 S8's seven steps, in its order.</summary>
public enum OnboardingStep
{
    Welcome,
    Activate,
    Permissions,
    Tools,
    Connect,
    TryIt,
    Done,
}

/// <summary>The service's capability checks, for the Permissions step. Over the pipe in the running application.</summary>
public interface ICapabilitiesGateway
{
    /// <summary>Null when the service did not answer.</summary>
    Task<IReadOnlyList<CapabilityStatus>?> CheckAsync(CancellationToken ct = default);
}

/// <summary>The sixty-second test session of the "Try it" step: a real session through the service, drafted the real way.</summary>
public interface ITestSession
{
    Task<bool> StartAsync(CancellationToken ct = default);

    Task<bool> StopAsync(CancellationToken ct = default);
}

/// <param name="FixLink">A Windows Settings deep link when there is one, so the fix is one click.</param>
public sealed record PermissionRow(string Capability, string Label, bool Ok, string Message, string? FixLink)
{
    public string Mark => Ok ? "✓" : "✗";
}

/// <summary>
/// The onboarding wizard's rules (ST-083, Spec §5 S8), composed from the panels Settings already has:
/// activation, the tools, the integrations. Each step says what it takes to continue and why not. A
/// blocked microphone is a worse session rather than a blocked setup, so it is shown with its fix and
/// re-checked but does not stop the technician; blocked screen capture does, because there is nothing
/// to capture with. Finishing is remembered so the wizard does not run again unasked; the tray runs it
/// again on request.
/// </summary>
public sealed class OnboardingWizard(
    ActivationPanel activation,
    ICapabilitiesGateway capabilities,
    CapturePanel capture,
    IntegrationsPanel integrations,
    ITestSession testSession,
    Action<DateTimeOffset> finished)
{
    private static readonly (string Capability, string Label)[] Labels =
    [
        ("microphone", "Microphone"),
        ("screen_capture", "Screen capture"),
        ("input_hooks", "Keyboard and mouse hooks"),
    ];

    private readonly ICapabilitiesGateway _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly ITestSession _testSession = testSession ?? throw new ArgumentNullException(nameof(testSession));
    private readonly Action<DateTimeOffset> _finished = finished ?? throw new ArgumentNullException(nameof(finished));
    private bool _testDone;

    public ActivationPanel Activation { get; } = activation ?? throw new ArgumentNullException(nameof(activation));

    public CapturePanel Capture { get; } = capture ?? throw new ArgumentNullException(nameof(capture));

    public IntegrationsPanel Integrations { get; } = integrations ?? throw new ArgumentNullException(nameof(integrations));

    public OnboardingStep Step { get; private set; } = OnboardingStep.Welcome;

    public int StepCount { get; } = Enum.GetValues<OnboardingStep>().Length;

    public int StepNumber => (int)Step + 1;

    public IReadOnlyList<PermissionRow> Permissions { get; private set; } = [];

    public bool PermissionsKnown { get; private set; }

    public bool TestRunning { get; private set; }

    public bool Finished { get; private set; }

    public bool CanGoBack => Step > OnboardingStep.Welcome && Step < OnboardingStep.Done;

    /// <summary>
    /// Two steps can be skipped. Connect, because publishing can wait and capture cannot (Spec §5 S8
    /// step 5); Activate, because its own words offer Settings later, and a device enrolled from the
    /// environment (the M1 runbook) has no code to enter.
    /// </summary>
    public bool CanSkip => Step is OnboardingStep.Connect or OnboardingStep.Activate;

    /// <summary>Why the technician cannot continue yet, or null.</summary>
    public string? Blocker => Step switch
    {
        OnboardingStep.Activate when !Activation.Activated => "Enter the code from your invite, or come back to this from Settings later.",
        OnboardingStep.Permissions when !PermissionsKnown => "Checking what this machine allows…",
        OnboardingStep.Permissions when Permissions.Any(p => !p.Ok && p.Capability == "screen_capture") =>
            "Screen capture is blocked, so there is nothing to capture with. Fix that first.",
        OnboardingStep.TryIt when !_testDone => TestRunning
            ? "Recording. Open any window, click around, say one sentence — then stop."
            : "Start the test session, do a few things, and stop it.",
        _ => null,
    };

    /// <summary>What the step says beside its content, or null: the microphone's consequence, the test's outcome.</summary>
    public string? Note => Step switch
    {
        OnboardingStep.Permissions when Permissions.Any(p => !p.Ok && p.Capability == "microphone") =>
            "Without the microphone, sessions are drafted without narration, from screenshots alone. Fix it now or later in Windows Settings.",
        OnboardingStep.TryIt when _testDone => "The draft is on its way. It opens in Review when it is ready — usually within a minute.",
        OnboardingStep.Done => "ScreenTail starts when you open a remote session. Look for the pill.",
        _ => null,
    };

    public bool CanContinue => Blocker is null && Step < OnboardingStep.Done;

    /// <summary>Asks the service for what the step needs. Called on arrival at each step.</summary>
    public async Task LoadStepAsync(CancellationToken ct = default)
    {
        switch (Step)
        {
            case OnboardingStep.Activate:
                await Activation.LoadAsync(ct).ConfigureAwait(false);
                break;
            case OnboardingStep.Permissions:
                await RecheckAsync(ct).ConfigureAwait(false);
                break;
            case OnboardingStep.Tools:
                await Capture.LoadAsync(ct).ConfigureAwait(false);
                break;
            case OnboardingStep.Connect:
                await Integrations.LoadAsync(ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Runs the checks again — after a fix in Windows Settings, the button says "Check again" (AC3).</summary>
    public async Task RecheckAsync(CancellationToken ct = default)
    {
        var checks = await _capabilities.CheckAsync(ct).ConfigureAwait(false);
        PermissionsKnown = checks is not null;
        Permissions = checks is null
            ? []
            : [.. Labels.Select(l =>
            {
                var check = checks.FirstOrDefault(c => c.Capability == l.Capability);
                return check is null
                    ? new PermissionRow(l.Capability, l.Label, false, "Not checked.", null)
                    : new PermissionRow(l.Capability, l.Label, check.State == "ok", check.Message, check.FixHint);
            })];
    }

    public void Next()
    {
        if (CanContinue)
        {
            Step += 1;
        }
    }

    public void Back()
    {
        if (CanGoBack)
        {
            Step -= 1;
        }
    }

    public void Skip()
    {
        if (CanSkip)
        {
            Step += 1;
        }
    }

    /// <summary>Jumps to a step: the tray's "Set up again" lands on Welcome, Settings' "check permissions" on Permissions.</summary>
    public void Go(OnboardingStep step) => Step = step;

    public async Task<bool> StartTestAsync(CancellationToken ct = default)
    {
        if (TestRunning || !await _testSession.StartAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        TestRunning = true;
        return true;
    }

    public async Task<bool> StopTestAsync(CancellationToken ct = default)
    {
        if (!TestRunning || !await _testSession.StopAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        TestRunning = false;
        _testDone = true;
        return true;
    }

    public void Finish()
    {
        Finished = true;
        _finished(DateTimeOffset.UtcNow);
    }
}

/// <summary>The Permissions step's gateway, over the pipe.</summary>
public sealed class PipeCapabilities(CaptureConnection connection) : ICapabilitiesGateway
{
    private readonly CaptureConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<IReadOnlyList<CapabilityStatus>?> CheckAsync(CancellationToken ct = default)
    {
        var reported = await _connection.RequestAsync<CapabilitiesReported>(id => new GetCapabilitiesCommand { RequestId = id }, ct).ConfigureAwait(false);
        return reported?.Checks;
    }
}

/// <summary>The "Try it" step's session, over the pipe: the same start and stop the hotkeys send.</summary>
public sealed class PipeTestSession(CaptureConnection connection) : ITestSession
{
    private readonly CaptureConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<bool> StartAsync(CancellationToken ct = default) =>
        (await _connection.SendAsync(id => new StartCommand { RequestId = id }, ct).ConfigureAwait(false)).Ok;

    public async Task<bool> StopAsync(CancellationToken ct = default) =>
        (await _connection.SendAsync(id => new StopCommand { RequestId = id }, ct).ConfigureAwait(false)).Ok;
}
