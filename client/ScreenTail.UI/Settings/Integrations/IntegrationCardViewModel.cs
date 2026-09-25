using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Settings;

namespace ScreenTail.UI.Settings.Integrations;

/// <summary>
/// One provider's card, bound over <see cref="IntegrationCard"/>, which holds every rule (ST-082,
/// Spec §5 S7). The secret parts pass through here on their way to the card and are cleared with it
/// the moment they are stored.
/// </summary>
public sealed partial class IntegrationCardViewModel : ObservableObject
{
    private readonly IntegrationCard _card;
    private readonly Func<Task> _changed;

    /// <param name="changed">Called after a save or a removal, so the screen can refresh what depends on it.</param>
    internal IntegrationCardViewModel(IntegrationCard card, Func<Task> changed)
    {
        _card = card;
        _changed = changed;
        Refresh();
    }

    public string Title => _card.Title;

    public Visibility ConnectWiseVisibility => _card.IsConnectWise ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HuduVisibility => _card.IsConnectWise ? Visibility.Collapsed : Visibility.Visible;

    public string SitePlaceholder => _card.IsConnectWise ? "https://na.myconnectwise.net" : "https://acme.huducloud.com";

    [ObservableProperty]
    public partial string SiteUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CompanyId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PublicKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PrivateKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SecretHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Problem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckResult { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckMark { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanSave { get; set; }

    [ObservableProperty]
    public partial bool Connected { get; set; }

    [ObservableProperty]
    public partial Visibility FieldsVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility SummaryVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility ProblemVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility CheckVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility ConnectedBadgeVisibility { get; set; } = Visibility.Collapsed;

    [RelayCommand]
    private async Task SaveAsync()
    {
        _ = await _card.SaveAsync().ConfigureAwait(true);
        Refresh();
        await _changed().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        _ = await _card.CheckAsync().ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        _ = await _card.RemoveAsync().ConfigureAwait(true);
        Refresh();
        await _changed().ConfigureAwait(true);
    }

    [RelayCommand]
    private void Change()
    {
        _card.Change();
        Refresh();
    }

    partial void OnSiteUrlChanged(string value) => Field(() => _card.SiteUrl = value);

    partial void OnCompanyIdChanged(string value) => Field(() => _card.CompanyId = value);

    partial void OnPublicKeyChanged(string value) => Field(() => _card.PublicKey = value);

    partial void OnPrivateKeyChanged(string value) => Field(() => _card.PrivateKey = value);

    partial void OnApiKeyChanged(string value) => Field(() => _card.ApiKey = value);

    private void Field(Action set)
    {
        set();
        Problem = _card.Problem ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanSave = _card.CanSave;
    }

    internal void Refresh()
    {
        SiteUrl = _card.SiteUrl;
        CompanyId = _card.CompanyId;
        PublicKey = _card.PublicKey;
        PrivateKey = _card.PrivateKey;
        ApiKey = _card.ApiKey;
        Connected = _card.Connected;
        Status = _card.Status;
        SecretHint = _card.SecretHint;
        Problem = _card.Problem ?? string.Empty;

        // Validation is shown once something has been typed; a fresh card says what to fill in, not
        // that it is wrong.
        ProblemVisibility = _card.Editing && Problem.Length > 0 && (_card.SiteUrl.Length > 0 || _card.Problem == Problem && _card.Connected) ? Visibility.Visible : Visibility.Collapsed;
        CanSave = _card.CanSave;
        FieldsVisibility = _card.Editing ? Visibility.Visible : Visibility.Collapsed;
        SummaryVisibility = _card.Editing ? Visibility.Collapsed : Visibility.Visible;
        ConnectedBadgeVisibility = _card.Connected ? Visibility.Visible : Visibility.Collapsed;
        CheckResult = _card.CheckResult ?? _card.LastError ?? string.Empty;
        CheckMark = _card.CheckOk switch { true => "✓", false => "✗", null => _card.LastError is null ? string.Empty : "✗" };
        CheckVisibility = CheckResult.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
