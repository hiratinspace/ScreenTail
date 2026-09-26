using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Onboarding;
using ScreenTail.UI.Settings.Activation;
using ScreenTail.UI.Settings.Integrations;

namespace ScreenTail.UI.Onboarding;

/// <summary>One progress dot.</summary>
public sealed record StepDot(int Number, bool Current, bool Done)
{
    public string Mark => Done ? "●" : Current ? "◉" : "○";
}

/// <summary>One remote tool with its toggle, in the "Your remote tools" step.</summary>
public sealed partial class WizardToolViewModel(string id, string displayName, bool enabled, Action<string, bool> changed) : ObservableObject
{
    public string DisplayName => displayName;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = enabled;

    partial void OnEnabledChanged(bool value) => changed(id, value);
}

/// <summary>
/// The onboarding wizard (ST-083, Spec §5 S8), bound over <see cref="OnboardingWizard"/>, which holds
/// every rule. The Activate and Connect steps reuse Settings' own view models; Permissions, Tools and
/// Try it are the wizard's.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly OnboardingWizard _wizard;
    private readonly Action _opened;

    /// <param name="opened">What Finish does after the wizard closes: open the shell on Review.</param>
    public OnboardingViewModel(OnboardingWizard wizard, Action opened)
    {
        _wizard = wizard ?? throw new ArgumentNullException(nameof(wizard));
        _opened = opened ?? throw new ArgumentNullException(nameof(opened));
        Activation = new ActivationViewModel(wizard.Activation);
        Integrations = new IntegrationsViewModel(wizard.Integrations);
        Refresh();
    }

    public event Action? Closed;

    public ActivationViewModel Activation { get; }

    public IntegrationsViewModel Integrations { get; }

    public ObservableCollection<StepDot> Dots { get; } = [];

    public ObservableCollection<PermissionRow> Permissions { get; } = [];

    public ObservableCollection<WizardToolViewModel> Tools { get; } = [];

    [ObservableProperty]
    public partial OnboardingStep Step { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Blocker { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility BlockerVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility NoteVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial bool CanContinue { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    public partial Visibility SkipVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility ContinueVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility FinishVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial bool TestRunning { get; set; }

    [ObservableProperty]
    public partial bool CanStartTest { get; set; }

    public Visibility WelcomeVisibility => Step == OnboardingStep.Welcome ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActivateVisibility => Step == OnboardingStep.Activate ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PermissionsVisibility => Step == OnboardingStep.Permissions ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ToolsVisibility => Step == OnboardingStep.Tools ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ConnectVisibility => Step == OnboardingStep.Connect ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TryItVisibility => Step == OnboardingStep.TryIt ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DoneVisibility => Step == OnboardingStep.Done ? Visibility.Visible : Visibility.Collapsed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _wizard.LoadStepAsync(ct).ConfigureAwait(true);
        if (_wizard.Step == OnboardingStep.Activate)
        {
            await Activation.LoadAsync(ct).ConfigureAwait(true);
        }

        if (_wizard.Step == OnboardingStep.Connect)
        {
            await Integrations.LoadAsync(ct).ConfigureAwait(true);
        }

        Refresh();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        _wizard.Next();
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        _wizard.Back();
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        _wizard.Skip();
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RecheckAsync()
    {
        await _wizard.RecheckAsync().ConfigureAwait(true);
        Refresh();
    }

    /// <summary>The Windows Settings deep link beside a blocked check, e.g. <c>ms-settings:privacy-microphone</c>.</summary>
    [RelayCommand]
    private static void OpenFix(string? link)
    {
        if (link is { Length: > 0 } && (link.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task SaveToolsAsync()
    {
        _ = await _wizard.Capture.SaveAsync().ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private async Task StartTestAsync()
    {
        _ = await _wizard.StartTestAsync().ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private async Task StopTestAsync()
    {
        _ = await _wizard.StopTestAsync().ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private void Finish()
    {
        _wizard.Finish();
        Closed?.Invoke();
        _opened();
    }

    /// <summary>Refreshed after a child panel acted, so the step's Continue follows the activation's outcome.</summary>
    public void Refresh()
    {
        Step = _wizard.Step;
        Title = Step switch
        {
            OnboardingStep.Welcome => "Welcome to ScreenTail",
            OnboardingStep.Activate => "Activate this device",
            OnboardingStep.Permissions => "What this machine allows",
            OnboardingStep.Tools => "Your remote tools",
            OnboardingStep.Connect => "Connect your PSA and documentation",
            OnboardingStep.TryIt => "Try it",
            _ => "You're set",
        };
        Dots.Clear();
        for (var i = 1; i <= _wizard.StepCount; i++)
        {
            Dots.Add(new StepDot(i, i == _wizard.StepNumber, i < _wizard.StepNumber));
        }

        Permissions.Clear();
        foreach (var permission in _wizard.Permissions)
        {
            Permissions.Add(permission);
        }

        Tools.Clear();
        foreach (var tool in _wizard.Capture.Tools)
        {
            Tools.Add(new WizardToolViewModel(tool.Id, tool.DisplayName, tool.Enabled, (id, enabled) => _wizard.Capture.SetTool(id, enabled)));
        }

        Blocker = _wizard.Blocker ?? string.Empty;
        BlockerVisibility = Blocker.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Note = _wizard.Note ?? string.Empty;
        NoteVisibility = Note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanContinue = _wizard.CanContinue;
        CanGoBack = _wizard.CanGoBack;
        SkipVisibility = _wizard.CanSkip ? Visibility.Visible : Visibility.Collapsed;
        ContinueVisibility = Step == OnboardingStep.Done ? Visibility.Collapsed : Visibility.Visible;
        FinishVisibility = Step == OnboardingStep.Done ? Visibility.Visible : Visibility.Collapsed;
        TestRunning = _wizard.TestRunning;
        CanStartTest = !_wizard.TestRunning;
        OnPropertyChanged(nameof(WelcomeVisibility));
        OnPropertyChanged(nameof(ActivateVisibility));
        OnPropertyChanged(nameof(PermissionsVisibility));
        OnPropertyChanged(nameof(ToolsVisibility));
        OnPropertyChanged(nameof(ConnectVisibility));
        OnPropertyChanged(nameof(TryItVisibility));
        OnPropertyChanged(nameof(DoneVisibility));
    }
}
