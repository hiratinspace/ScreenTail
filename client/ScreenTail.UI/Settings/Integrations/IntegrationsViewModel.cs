using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Settings;

namespace ScreenTail.UI.Settings.Integrations;

/// <summary>One row of the company mapping table, worded.</summary>
public sealed record MappingRow(string PsaCompany, string DocCompanyName, string Confidence)
{
    public string ConfidenceLabel => Confidence switch
    {
        "exact" => "Exact",
        "likely" => "Likely",
        "manual" => "Chosen by you",
        _ => Confidence,
    };
}

/// <summary>
/// Settings → Integrations (ST-082, Spec §5 S7): the two cards and the company mapping table, bound
/// over <see cref="IntegrationsPanel"/>. Loaded when the screen opens and again after anything changes.
/// </summary>
public sealed partial class IntegrationsViewModel : ObservableObject
{
    private readonly IntegrationsPanel _panel;

    public IntegrationsViewModel(IntegrationsPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        ConnectWise = new IntegrationCardViewModel(panel.ConnectWise, RefreshMappingsAsync);
        Hudu = new IntegrationCardViewModel(panel.Hudu, RefreshMappingsAsync);
    }

    public IntegrationCardViewModel ConnectWise { get; }

    public IntegrationCardViewModel Hudu { get; }

    public ObservableCollection<MappingRow> Mappings { get; } = [];

    public ObservableCollection<CompanyChoice> Companies { get; } = [];

    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility NoticeVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility MappingsVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility NoMappingsVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string NewPsaCompany { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CompanyChoice? NewDocCompany { get; set; }

    [ObservableProperty]
    public partial bool CanAddMapping { get; set; }

    [ObservableProperty]
    public partial string MappingProblem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility MappingProblemVisibility { get; set; } = Visibility.Collapsed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _panel.LoadAsync(ct).ConfigureAwait(true);
        ConnectWise.Refresh();
        Hudu.Refresh();
        ShowMappings();
    }

    [RelayCommand]
    private async Task AddMappingAsync()
    {
        if (!CanAddMapping || NewDocCompany is not { } choice)
        {
            return;
        }

        var refusal = await _panel.MapAsync(NewPsaCompany.Trim(), choice.Id).ConfigureAwait(true);
        MappingProblem = refusal ?? string.Empty;
        MappingProblemVisibility = refusal is null ? Visibility.Collapsed : Visibility.Visible;
        if (refusal is null)
        {
            NewPsaCompany = string.Empty;
            NewDocCompany = null;
        }

        ShowMappings();
    }

    [RelayCommand]
    private async Task UnmapAsync(MappingRow? row)
    {
        if (row is null)
        {
            return;
        }

        var refusal = await _panel.UnmapAsync(row.PsaCompany).ConfigureAwait(true);
        MappingProblem = refusal ?? string.Empty;
        MappingProblemVisibility = refusal is null ? Visibility.Collapsed : Visibility.Visible;
        ShowMappings();
    }

    partial void OnNewPsaCompanyChanged(string value) => CanAddMapping = value.Trim().Length > 0 && NewDocCompany is not null;

    partial void OnNewDocCompanyChanged(CompanyChoice? value) => CanAddMapping = NewPsaCompany.Trim().Length > 0 && value is not null;

    private async Task RefreshMappingsAsync()
    {
        await _panel.RefreshMappingsAsync().ConfigureAwait(true);
        ShowMappings();
    }

    private void ShowMappings()
    {
        Notice = _panel.Notice ?? string.Empty;
        NoticeVisibility = Notice.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        MappingsVisibility = _panel.CanMapCompanies ? Visibility.Visible : Visibility.Collapsed;
        Mappings.Clear();
        foreach (var mapping in _panel.Mappings)
        {
            Mappings.Add(new MappingRow(mapping.PsaCompany, mapping.DocCompanyName, mapping.Confidence));
        }

        Companies.Clear();
        foreach (var company in _panel.Companies)
        {
            Companies.Add(company);
        }

        NoMappingsVisibility = _panel.CanMapCompanies && Mappings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CanAddMapping = NewPsaCompany.Trim().Length > 0 && NewDocCompany is not null;
    }
}
