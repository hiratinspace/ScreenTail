using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Settings;

namespace ScreenTail.UI.Settings.Privacy;

/// <summary>
/// Settings → Privacy &amp; Redaction (ST-081, Spec §5 S6), bound over <see cref="PrivacyPanel"/>, which
/// holds every rule. Export and Delete everything are the shell's: the first needs a file dialog, the
/// second the typed confirmation the service issues a token for, and neither belongs in a view model.
/// </summary>
public sealed partial class PrivacyViewModel : ObservableObject
{
    private readonly PrivacyPanel _panel;
    private readonly Func<string, Task> _export;
    private readonly Func<Task> _eraseEverything;

    /// <param name="export">Hands the audit log's JSON to the shell to save.</param>
    /// <param name="eraseEverything">The shell's typed-confirmation flow for INV-12.</param>
    public PrivacyViewModel(PrivacyPanel panel, Func<string, Task> export, Func<Task> eraseEverything)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _eraseEverything = eraseEverything ?? throw new ArgumentNullException(nameof(eraseEverything));
        Refresh();
    }

    public IReadOnlyList<string> TelemetryFields { get; } = PrivacyPanel.TelemetryFields;

    public string TelemetryFieldList { get; } = string.Join(", ", PrivacyPanel.TelemetryFields);

    public ObservableCollection<string> CustomPatterns { get; } = [];

    public ObservableCollection<string> ExcludedProcesses { get; } = [];

    [ObservableProperty]
    public partial bool LocalOnly { get; set; }

    [ObservableProperty]
    public partial bool LocalOnlyEditable { get; set; } = true;

    [ObservableProperty]
    public partial int RetentionDays { get; set; } = 7;

    [ObservableProperty]
    public partial bool RetentionEditable { get; set; } = true;

    [ObservableProperty]
    public partial string RetentionLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LockedBecause { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility LocalOnlyLockVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility RetentionLockVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial bool Telemetry { get; set; }

    [ObservableProperty]
    public partial bool Ssn { get; set; } = true;

    [ObservableProperty]
    public partial bool Cards { get; set; } = true;

    [ObservableProperty]
    public partial bool ApiKeys { get; set; } = true;

    [ObservableProperty]
    public partial bool Passwords { get; set; } = true;

    [ObservableProperty]
    public partial bool Emails { get; set; }

    [ObservableProperty]
    public partial string NewPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPatternProblem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility NewPatternProblemVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial bool CanAddPattern { get; set; }

    [ObservableProperty]
    public partial string Sample { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SampleMasked { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewProcess { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewProcessProblem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility NewProcessProblemVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial bool CanAddProcess { get; set; }

    [ObservableProperty]
    public partial string Problem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility ProblemVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial bool CanSave { get; set; }

    [ObservableProperty]
    public partial string Saved { get; set; } = string.Empty;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _panel.LoadAsync(ct).ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var saved = await _panel.SaveAsync().ConfigureAwait(true);
        Refresh();
        Saved = saved ? "Saved and applied." : string.Empty;
    }

    [RelayCommand]
    private void AddPattern()
    {
        _ = _panel.AddPattern();
        Refresh();
    }

    [RelayCommand]
    private void RemovePattern(string? pattern)
    {
        if (pattern is not null)
        {
            _panel.RemovePattern(pattern);
            Refresh();
        }
    }

    [RelayCommand]
    private void AddProcess()
    {
        _ = _panel.AddProcess();
        Refresh();
    }

    [RelayCommand]
    private void RemoveProcess(string? process)
    {
        if (process is not null)
        {
            _panel.RemoveProcess(process);
            Refresh();
        }
    }

    [RelayCommand]
    private async Task ExportAuditAsync()
    {
        var json = await _panel.ExportAuditAsync().ConfigureAwait(true);
        if (json is null)
        {
            Problem = "The capture service did not answer, so the audit log could not be exported.";
            ProblemVisibility = Visibility.Visible;
            return;
        }

        await _export(json).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task EraseEverythingAsync() => _eraseEverything();

    partial void OnLocalOnlyChanged(bool value) => Field(() => _panel.LocalOnly = value);

    partial void OnRetentionDaysChanged(int value) => Field(() =>
    {
        _panel.RetentionDays = value;
        RetentionLabel = Describe(value);
    });

    partial void OnTelemetryChanged(bool value) => Field(() => _panel.Telemetry = value);

    partial void OnSsnChanged(bool value) => Field(() => _panel.Ssn = value);

    partial void OnCardsChanged(bool value) => Field(() => _panel.Cards = value);

    partial void OnApiKeysChanged(bool value) => Field(() => _panel.ApiKeys = value);

    partial void OnPasswordsChanged(bool value) => Field(() => _panel.Passwords = value);

    partial void OnEmailsChanged(bool value) => Field(() => _panel.Emails = value);

    partial void OnNewPatternChanged(string value)
    {
        _panel.NewPattern = value;
        Tried();
    }

    partial void OnSampleChanged(string value)
    {
        _panel.Sample = value;
        SampleMasked = _panel.SampleMasked;
    }

    partial void OnNewProcessChanged(string value)
    {
        _panel.NewProcess = value;
        NewProcessProblem = value.Length == 0 ? string.Empty : _panel.NewProcessProblem ?? string.Empty;
        NewProcessProblemVisibility = NewProcessProblem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanAddProcess = _panel.CanAddProcess;
    }

    private void Field(Action set)
    {
        set();
        Saved = string.Empty;
        Problem = _panel.Problem ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanSave = _panel.CanSave;
    }

    private void Tried()
    {
        NewPatternProblem = _panel.NewPattern.Length == 0 ? string.Empty : _panel.NewPatternProblem ?? string.Empty;
        NewPatternProblemVisibility = NewPatternProblem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanAddPattern = _panel.CanAddPattern;
        SampleMasked = _panel.SampleMasked;
    }

    private void Refresh()
    {
        LocalOnly = _panel.LocalOnly;
        LocalOnlyEditable = !_panel.LocalOnlyLocked;
        RetentionDays = _panel.RetentionDays;
        RetentionEditable = !_panel.RetentionLocked;
        RetentionLabel = Describe(_panel.RetentionDays);
        LockedBecause = _panel.LockedBecause;
        LocalOnlyLockVisibility = _panel.LocalOnlyLocked ? Visibility.Visible : Visibility.Collapsed;
        RetentionLockVisibility = _panel.RetentionLocked ? Visibility.Visible : Visibility.Collapsed;
        Telemetry = _panel.Telemetry;
        Ssn = _panel.Ssn;
        Cards = _panel.Cards;
        ApiKeys = _panel.ApiKeys;
        Passwords = _panel.Passwords;
        Emails = _panel.Emails;
        NewPattern = _panel.NewPattern;
        NewProcess = _panel.NewProcess;
        CustomPatterns.Clear();
        foreach (var pattern in _panel.CustomPatterns)
        {
            CustomPatterns.Add(pattern);
        }

        ExcludedProcesses.Clear();
        foreach (var process in _panel.ExcludedProcesses)
        {
            ExcludedProcesses.Add(process);
        }

        Tried();
        Problem = _panel.Problem ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanSave = _panel.CanSave;
        CanAddProcess = _panel.CanAddProcess;
    }

    /// <summary>Spec §5 S6: the slider says what is deleted and what is kept.</summary>
    private static string Describe(int days) =>
        $"Screenshots, transcript and notes of sessions older than {days} day{(days == 1 ? string.Empty : "s")} are deleted; the audit log is kept.";
}
