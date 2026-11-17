using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTrail.Core.Settings;
using ScreenTrail.UI.Settings.Activation;
using ScreenTrail.UI.Settings.Appearance;
using ScreenTrail.UI.Settings.Capture;
using ScreenTrail.UI.Settings.Integrations;
using ScreenTrail.UI.Settings.Privacy;
using ScreenTrail.UI.Update;

namespace ScreenTrail.UI.Settings;

/// <summary>One entry in the Settings nav, with the count of its changes not yet saved beside it.</summary>
public sealed partial class SettingsNavItem(string key, string label) : ObservableObject
{
    /// <summary>The section's name on the page's Save bar, and the tag of its group on the page.</summary>
    public string Key { get; } = key;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial string PendingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility PendingVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>"Capture, 2 changes not saved": the label first, as WCAG 2.5.3 asks.</summary>
    [ObservableProperty]
    public partial string AutomationName { get; set; } = label;

    internal void Count(int pending)
    {
        PendingText = pending == 0 ? string.Empty : pending.ToString(System.Globalization.CultureInfo.CurrentCulture);
        PendingVisibility = pending == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationName = pending switch
        {
            0 => Label,
            1 => $"{Label}, 1 change not saved",
            _ => $"{Label}, {pending} changes not saved",
        };
    }
}

/// <summary>
/// The Settings screen (Spec §5 S5 to S7, and the Settings layout, v0.6.0): a nav of its sections beside
/// one scrolling page, and one Save bar for the whole page (owner decision). This device's activation
/// (ST-010), Capture (ST-080), Privacy &amp; Redaction (ST-081), Integrations (ST-082),
/// Appearance (ST-070) and Updates (ST-113).
///
/// The rules of the bar are <see cref="SettingsPage"/>'s; this binds them. Each section's view model says
/// when a field changed, and the bar counts again. Save writes every changed section through its own save
/// path and then shows each section as it now is; Undo puts every section back. Leaving Settings keeps the
/// changes pending: the shell keeps this view model and does not read a panel that holds one.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(ActivationViewModel activation, CaptureViewModel capture, PrivacyViewModel privacy, IntegrationsViewModel integrations, AppearanceViewModel appearance, UpdatesViewModel updates)
    {
        Activation = activation ?? throw new ArgumentNullException(nameof(activation));
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        Integrations = integrations ?? throw new ArgumentNullException(nameof(integrations));
        Appearance = appearance ?? throw new ArgumentNullException(nameof(appearance));
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
        // Integrations is not on the Save bar: its cards are read only (the owner manages them in the web
        // admin), and Test connection and the company mapping act at once.
        Page = new SettingsPage([capture.Section, privacy.Section, appearance.Section, updates.Section]);
        Sections =
        [
            new SettingsNavItem("Activation", "Activation"),
            new SettingsNavItem(capture.Section.Name, "Capture"),
            new SettingsNavItem(privacy.Section.Name, "Privacy"),
            new SettingsNavItem("Integrations", "Integrations"),
            new SettingsNavItem(appearance.Section.Name, "Appearance"),
            new SettingsNavItem(updates.Section.Name, "Updates"),
        ];
        SelectedSection = Sections[0];
        capture.Edited += (_, _) => Show();
        privacy.Edited += (_, _) => Show();
        appearance.Edited += (_, _) => Show();
        updates.Edited += (_, _) => Show();
        Show();
    }

    public ActivationViewModel Activation { get; }

    public CaptureViewModel Capture { get; }

    public PrivacyViewModel Privacy { get; }

    public IntegrationsViewModel Integrations { get; }

    public AppearanceViewModel Appearance { get; }

    public UpdatesViewModel Updates { get; }

    /// <summary>The page's one Save bar, its rules.</summary>
    public SettingsPage Page { get; }

    /// <summary>The nav, in the page's order.</summary>
    public ObservableCollection<SettingsNavItem> Sections { get; }

    /// <summary>The section the nav shows as current: chosen in the nav, or scrolled to.</summary>
    [ObservableProperty]
    public partial SettingsNavItem? SelectedSection { get; set; }

    [ObservableProperty]
    public partial Visibility BarVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>"2 changes not saved:", or "Saving 2 changes" while a save is on its way.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    /// <summary>The section the bar's link jumps to: the first with a change, or the first that failed.</summary>
    [ObservableProperty]
    public partial string LinkSection { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LinkName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility LinkVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>Pending: the iris dot beside the words. Failed: the danger cross.</summary>
    [ObservableProperty]
    public partial Visibility PendingMarkVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility FailedMarkVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string FailureMessage { get; set; } = string.Empty;

    /// <summary>Every change by name and section, the Save button's help text for a screen reader: "Unsaved changes: Capture scope (Capture), Retention (Privacy)".</summary>
    [ObservableProperty]
    public partial string BarName { get; set; } = string.Empty;

    /// <summary>"All changes saved and applied." after a save that wrote everything; cleared by the next change.</summary>
    [ObservableProperty]
    public partial string SavedNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility SavedVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>Asks the view to bring a section into view and move focus into it, by its nav key.</summary>
    public event EventHandler<string>? JumpRequested;

    /// <summary>Ctrl+S and the bar's Save: every changed section, each through its own save path.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var saving = Page.SaveAsync();
        Show();
        var saved = await saving.ConfigureAwait(true);
        await ShowSectionsAsync().ConfigureAwait(true);
        Show();
        SavedNote = saved ? "All changes saved and applied." : string.Empty;
        SavedVisibility = saved ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool CanSave() => Page.CanSave;

    /// <summary>The bar's Undo changes: every section back to what was last saved. The service is asked nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        Page.Undo();
        await ShowSectionsAsync().ConfigureAwait(true);
        Show();
    }

    private bool CanUndo() => Page.CanUndo;

    /// <summary>
    /// Esc on the bar. Undo throws away what was typed and cannot itself be undone, so Esc undoes only
    /// when keyboard focus is on the bar (the view sends it only from there) and nothing is being saved.
    /// Esc anywhere else on the page closes what it always closed and leaves the changes alone.
    /// </summary>
    public bool UndoFromEscape()
    {
        if (!Page.CanUndo)
        {
            return false;
        }

        UndoCommand.Execute(null);
        return true;
    }

    /// <summary>A section asked for from elsewhere, kept until the page is on screen to bring it into view.</summary>
    public string? PendingSection { get; private set; }

    /// <summary>
    /// Opens the page at a section (Review's "See integrations" asks for Integrations). The pane brings it
    /// into view now if it is on screen, or when it loads.
    /// </summary>
    public void Show(string key)
    {
        if (Sections.FirstOrDefault(s => s.Key == key) is not { } item)
        {
            return;
        }

        SelectedSection = item;
        PendingSection = key;
        JumpRequested?.Invoke(this, key);
    }

    /// <summary>The section <see cref="Show"/> asked for, once.</summary>
    public string? TakePendingSection()
    {
        var key = PendingSection;
        PendingSection = null;
        return key;
    }

    /// <summary>The bar's link: the section it names, brought into view with focus in it.</summary>
    [RelayCommand]
    private void GoTo(string? key)
    {
        if (Sections.FirstOrDefault(s => s.Key == key) is not { } item)
        {
            return;
        }

        SelectedSection = item;
        JumpRequested?.Invoke(this, item.Key);
    }

    private Task ShowSectionsAsync()
    {
        Capture.Refresh();
        Privacy.Refresh();
        Appearance.Refresh();
        Updates.Refresh();
        return Task.CompletedTask;
    }

    private void Show()
    {
        var state = Page.State;
        var count = Page.PendingCount;
        BarVisibility = state == SaveBarState.Hidden ? Visibility.Collapsed : Visibility.Visible;
        Summary = state == SaveBarState.Saving
            ? $"Saving {count} change{(count == 1 ? string.Empty : "s")}"
            : Page.Summary;

        var link = state == SaveBarState.Failed ? Page.Failures[0].Section : Page.FirstChanged?.Name;
        LinkSection = link ?? string.Empty;
        LinkName = link is null ? string.Empty : $"Go to {link}";
        LinkVisibility = link is not null && state != SaveBarState.Saving ? Visibility.Visible : Visibility.Collapsed;
        PendingMarkVisibility = state is SaveBarState.Pending or SaveBarState.Saving ? Visibility.Visible : Visibility.Collapsed;
        FailedMarkVisibility = state == SaveBarState.Failed ? Visibility.Visible : Visibility.Collapsed;
        FailureMessage = state == SaveBarState.Failed ? Page.FailureMessage ?? string.Empty : string.Empty;
        BarName = count == 0 ? string.Empty : "Unsaved changes: " + string.Join(", ", Page.AllChanges.Select(c => $"{c.Change} ({c.Section})"));
        foreach (var item in Sections)
        {
            item.Count(Page.PendingIn(item.Key));
        }

        if (count > 0)
        {
            SavedNote = string.Empty;
            SavedVisibility = Visibility.Collapsed;
        }

        SaveCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }
}
