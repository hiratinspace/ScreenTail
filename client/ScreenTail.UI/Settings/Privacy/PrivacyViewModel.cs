using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Shell;

namespace ScreenTail.UI.Settings.Privacy;

/// <summary>
/// Settings → Privacy &amp; Redaction (ST-081, Spec §5 S6), bound over <see cref="PrivacyPanel"/>, which
/// holds every rule. Export and Delete everything are the shell's: the first needs a file dialog, the
/// second the typed confirmation the service issues a token for, and neither belongs in a view model.
/// </summary>
public sealed partial class PrivacyViewModel : ObservableObject
{
    private readonly PrivacyPanel _panel;
    private readonly Func<string?> _chooseFile;
    private readonly Func<Task<ConfirmedResult>> _eraseEverything;
    private readonly OneAtATime _exporting = new();
    private readonly OneAtATime _erasing = new();

    /// <param name="chooseFile">Asks the technician where to keep the audit log; null when they cancelled.</param>
    /// <param name="eraseEverything">
    /// The shell's typed-confirmation flow for INV-12 (<see cref="DeleteEverything"/>), which asks nothing
    /// while a session is being captured. Its answer, when it is not done or declined, is said beside the
    /// button.
    /// </param>
    public PrivacyViewModel(PrivacyPanel panel, Func<string?> chooseFile, Func<Task<ConfirmedResult>> eraseEverything)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        Section = new PrivacySection(panel);
        _chooseFile = chooseFile ?? throw new ArgumentNullException(nameof(chooseFile));
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

    /// <summary>The slider's top, so an admin's retention beyond 30 days is shown whole rather than clamped.</summary>
    [ObservableProperty]
    public partial int RetentionMaximum { get; set; } = Shared.Settings.ClientSettings.MaxRetentionDays;

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

    /// <summary>This section on the Settings page's one Save bar. Your data's actions are never on it: they act at once.</summary>
    public PrivacySection Section { get; }

    /// <summary>The "Changed" mark beside each card with a change not yet saved.</summary>
    public ChangeMarks Changed { get; } = new();

    /// <summary>A field changed, so the page's Save bar counts again.</summary>
    public event EventHandler? Edited;

    /// <summary>The panel holds a change the technician has not saved, so the shell does not read it again on a reconnect.</summary>
    public bool HasUnsavedEdits => _panel.HasUnsavedEdits;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _panel.LoadAsync(ct).ConfigureAwait(true);
        Refresh();
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

    /// <summary>One file dialog at a time (<see cref="OneAtATime"/>): it is shown before the first await, in a nested message loop where a queued invoke can arrive.</summary>
    [RelayCommand(CanExecute = nameof(CanExportAudit))]
    private Task ExportAuditAsync() => _exporting.RunAsync(ExportAuditOnceAsync, ExportAuditCommand.NotifyCanExecuteChanged);

    private bool CanExportAudit() => !_exporting.Busy;

    private async Task ExportAuditOnceAsync()
    {
        // Where first, so a cancelled dialog costs the service nothing and leaves no file behind.
        if (_chooseFile() is not { } destination)
        {
            return;
        }

        var saved = await _panel.ExportAuditAsync(destination).ConfigureAwait(true);
        if (saved.Saved && saved.Intact)
        {
            // A clean save clears an earlier export's problem, leaving only what the panel itself says.
            Problem = _panel.Problem ?? string.Empty;
            ProblemVisibility = Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        // Not saved, or saved from a log that does not verify: either is something to say out loud.
        Problem = saved.Message;
        ProblemVisibility = Visibility.Visible;
    }

    /// <summary>Why the last "Delete everything" did not happen, or empty. Shown beside the button, never as a dialog.</summary>
    [ObservableProperty]
    public partial string EraseProblem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility EraseProblemVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>One "Delete everything" question at a time (<see cref="OneAtATime"/>): a second invoke opened a second typed dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanEraseEverything))]
    private Task EraseEverythingAsync() => _erasing.RunAsync(EraseEverythingOnceAsync, EraseEverythingCommand.NotifyCanExecuteChanged);

    private async Task EraseEverythingOnceAsync()
    {
        var result = await _eraseEverything().ConfigureAwait(true);
        EraseProblem = result.Outcome is ConfirmedOutcome.Refused or ConfirmedOutcome.NotAvailable
            ? result.Error ?? ConfirmedAction.RefusedWithoutReason
            : string.Empty;
        EraseProblemVisibility = EraseProblem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool CanEraseEverything() => !_erasing.Busy;

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
        Problem = _panel.Problem ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Counted();
    }

    private void Counted()
    {
        Changed.Set(Section.Changes);
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void Tried()
    {
        NewPatternProblem = _panel.NewPattern.Length == 0 ? string.Empty : _panel.NewPatternProblem ?? string.Empty;
        NewPatternProblemVisibility = NewPatternProblem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanAddPattern = _panel.CanAddPattern;
        SampleMasked = _panel.SampleMasked;
    }

    /// <summary>Shows the panel as it is now: after a load, an add or a removal, and after the page saved or undid its changes.</summary>
    public void Refresh()
    {
        LocalOnly = _panel.LocalOnly;
        LocalOnlyEditable = !_panel.LocalOnlyLocked;

        // The slider clamps its value to its top and pushes the clamped value back here. Read the panel
        // first, widen the top to cover both the old value and the new, move the value, then settle the
        // top: the value is never above the top, whether the top grows or shrinks between loads.
        var days = _panel.RetentionDays;
        var maximum = _panel.RetentionMaximum;
        RetentionMaximum = Math.Max(RetentionMaximum, maximum);
        RetentionDays = days;
        RetentionMaximum = maximum;
        RetentionEditable = !_panel.RetentionLocked;
        RetentionLabel = Describe(days);
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
        CanAddProcess = _panel.CanAddProcess;
        Counted();
    }

    /// <summary>Spec §5 S6: the slider says what is deleted and what is kept.</summary>
    private static string Describe(int days) =>
        $"Screenshots, transcript and notes of sessions older than {days} day{(days == 1 ? string.Empty : "s")} are deleted; the audit log is kept.";
}
