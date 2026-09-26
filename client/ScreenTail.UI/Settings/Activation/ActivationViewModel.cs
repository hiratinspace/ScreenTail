using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Settings;

namespace ScreenTail.UI.Settings.Activation;

/// <summary>The activation card, bound over <see cref="ActivationPanel"/>, which holds every rule (ST-010).</summary>
public sealed partial class ActivationViewModel : ObservableObject
{
    private readonly ActivationPanel _panel;

    public ActivationViewModel(ActivationPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        Refresh();
    }

    [ObservableProperty]
    public partial string Code { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Standing { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Problem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanActivate { get; set; }

    [ObservableProperty]
    public partial Visibility FormVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility ActivatedVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility ProblemVisibility { get; set; } = Visibility.Collapsed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _panel.LoadAsync(ct).ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private async Task ActivateAsync()
    {
        _ = await _panel.ActivateAsync().ConfigureAwait(true);
        Refresh();
    }

    partial void OnCodeChanged(string value)
    {
        _panel.Code = value;
        Problem = _panel.Problem ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 || _panel.Code.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanActivate = _panel.CanActivate;
    }

    private void Refresh()
    {
        Code = _panel.Code;
        Standing = _panel.Standing;
        Problem = _panel.Problem ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 || _panel.Code.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanActivate = _panel.CanActivate;
        FormVisibility = _panel.Activated ? Visibility.Collapsed : Visibility.Visible;
        ActivatedVisibility = _panel.Activated ? Visibility.Visible : Visibility.Collapsed;
    }
}
