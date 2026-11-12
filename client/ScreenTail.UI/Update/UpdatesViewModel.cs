using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Update;

namespace ScreenTail.UI.Update;

/// <summary>
/// Settings → Updates (ST-113), bound over <see cref="UpdatesPanel"/>, which holds every rule. The channel
/// is held as a change for the Settings page's one Save bar (<see cref="UpdatesSection"/>); Install acts
/// at once, as it did.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly UpdatesPanel _panel;
    private bool _refreshing;

    public UpdatesViewModel(UpdatesPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        Section = new UpdatesSection(panel);
        Refresh();
    }

    /// <summary>This section on the Settings page's one Save bar: the channel chosen, until Save.</summary>
    public UpdatesSection Section { get; }

    /// <summary>The "Changed" mark beside the Channel card's heading.</summary>
    public Settings.ChangeMarks Changed { get; } = new();

    /// <summary>The channel was chosen, so the page's Save bar counts again.</summary>
    public event EventHandler? Edited;

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
            Choose(UpdateChannel.Pilot);
        }
    }

    partial void OnStableChanged(bool value)
    {
        if (!_refreshing && value)
        {
            Choose(UpdateChannel.Stable);
        }
    }

    private void Choose(UpdateChannel channel)
    {
        Section.Choose(channel);
        Refresh();
    }

    /// <summary>Shows the card as it is now: after a load or an install, a choice, and after the page saved or undid its changes.</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            Sentence = _panel.Sentence;
            Installed = _panel.InstalledVersion.Length == 0 ? string.Empty : $"Installed: ScreenTail {_panel.InstalledVersion}";
            Stable = Section.Shown == UpdateChannel.Stable;
            Pilot = Section.Shown == UpdateChannel.Pilot;
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

        Changed.Set(Section.Changes);
        Edited?.Invoke(this, EventArgs.Empty);
    }
}
