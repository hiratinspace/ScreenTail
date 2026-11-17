using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTrail.Core.Settings;
using ScreenTrail.Core.Shell;

namespace ScreenTrail.UI.Settings.Integrations;

/// <summary>
/// One provider's card, bound over <see cref="IntegrationCard"/>, which holds every rule (ST-082,
/// Spec §5 S7). Read only: integration credentials can only be changed by the tenant owner, in the web
/// admin, so the card shows what is connected and offers Test connection, and nothing else.
/// </summary>
public sealed partial class IntegrationCardViewModel : ObservableObject
{
    private readonly IntegrationCard _card;
    private readonly OneAtATime _checking = new();

    internal IntegrationCardViewModel(IntegrationCard card)
    {
        _card = card;
        Refresh();
    }

    public string Title => _card.Title;

    [ObservableProperty]
    public partial string SiteUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>Who changes the connection, and where (<see cref="IntegrationCard.OwnerLine"/>).</summary>
    [ObservableProperty]
    public partial string OwnerLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SecretHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckResult { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckMark { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Connected { get; set; }

    /// <summary>The status line: shown once connected. Not connected, the owner line says so itself.</summary>
    [ObservableProperty]
    public partial Visibility StatusVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>The site and the credential's last four: shown once connected.</summary>
    [ObservableProperty]
    public partial Visibility SummaryVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility CheckVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility ConnectedBadgeVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>
    /// Test connection: checks the credential the backend holds. Off with nothing connected. One check at
    /// a time (<see cref="OneAtATime"/>): the command's Execute does not look at whether it is running, so
    /// a second invoke under the first would ask the provider again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => _checking.RunAsync(CheckOnceAsync, CheckCommand.NotifyCanExecuteChanged);

    private bool CanCheck() => _card.Connected && !_checking.Busy;

    private async Task CheckOnceAsync()
    {
        _ = await _card.CheckAsync().ConfigureAwait(true);
        Refresh();
    }

    internal void Refresh()
    {
        SiteUrl = _card.SiteUrl;
        Connected = _card.Connected;
        Status = _card.Status;
        OwnerLine = _card.OwnerLine;
        SecretHint = _card.SecretHint;
        StatusVisibility = _card.Connected ? Visibility.Visible : Visibility.Collapsed;
        SummaryVisibility = _card.Connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectedBadgeVisibility = _card.Connected ? Visibility.Visible : Visibility.Collapsed;
        CheckResult = _card.CheckResult ?? _card.LastError ?? string.Empty;
        CheckMark = _card.CheckOk switch { true => "✓", false => "✗", null => _card.LastError is null ? string.Empty : "✗" };
        CheckVisibility = CheckResult.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CheckCommand.NotifyCanExecuteChanged();
    }
}
