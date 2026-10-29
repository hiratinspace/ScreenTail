using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Update;

namespace ScreenTail.UI.Update;

/// <summary>Settings → Updates (ST-113), bound over <see cref="UpdatesPanel"/>, which holds every rule.</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly UpdatesPanel _panel;
    private bool _refreshing;

    public UpdatesViewModel(UpdatesPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        Refresh();
    }

    [ObservableProperty]
    public partial string Sentence { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Installed { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Stable { get; set; } = true;

    [ObservableProperty]
    public partial bool Pilot { get; set; }

    [ObservableProperty]
    public partial bool ChannelEditable { get; set; }

    [ObservableProperty]
    public partial string LockedNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility LockedVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial bool CanInstall { get; set; }

    [ObservableProperty]
    public partial string Problem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility ProblemVisibility { get; set; } = Visibility.Collapsed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _panel.LoadAsync(ct).ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        _ = await _panel.InstallAsync().ConfigureAwait(true);
        Refresh();
    }

    partial void OnPilotChanged(bool value)
    {
        if (!_refreshing && value)
        {
            _ = ChooseAsync(UpdateChannel.Pilot);
        }
    }

    partial void OnStableChanged(bool value)
    {
        if (!_refreshing && value)
        {
            _ = ChooseAsync(UpdateChannel.Stable);
        }
    }

    private async Task ChooseAsync(UpdateChannel channel)
    {
        _ = await _panel.ChooseAsync(channel).ConfigureAwait(true);
        Refresh();
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            Sentence = _panel.Sentence;
            Installed = _panel.InstalledVersion.Length == 0 ? string.Empty : $"Installed: ScreenTail {_panel.InstalledVersion}";
            Stable = _panel.Channel == UpdateChannel.Stable;
            Pilot = _panel.Channel == UpdateChannel.Pilot;
            ChannelEditable = _panel.CanChooseChannel;
            LockedNote = _panel.ChannelNote ?? string.Empty;
            LockedVisibility = _panel.ChannelNote is null ? Visibility.Collapsed : Visibility.Visible;
            CanInstall = _panel.CanInstall;
            Problem = _panel.Problem ?? string.Empty;
            ProblemVisibility = _panel.Problem is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _refreshing = false;
        }
    }
}
