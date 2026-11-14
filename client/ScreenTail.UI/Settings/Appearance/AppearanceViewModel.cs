using CommunityToolkit.Mvvm.ComponentModel;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Shell;

namespace ScreenTail.UI.Settings.Appearance;

/// <summary>
/// Settings, Appearance (ST-070), bound over <see cref="AppearanceSection"/>, which holds every rule. The
/// choice waits for the page's one Save bar, as every other section's does, and the theme changes when it
/// is saved.
/// </summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly Func<string> _windowsMode;
    private bool _refreshing;

    /// <param name="windowsMode">How Windows' app mode is set now, "Light" or "Dark".</param>
    public AppearanceViewModel(AppearanceSection section, Func<string> windowsMode)
    {
        Section = section ?? throw new ArgumentNullException(nameof(section));
        _windowsMode = windowsMode ?? throw new ArgumentNullException(nameof(windowsMode));
        Refresh();
    }

    /// <summary>This section on the Settings page's one Save bar: the choice made, until Save.</summary>
    public AppearanceSection Section { get; }

    /// <summary>The "Changed" mark beside the Theme card's heading.</summary>
    public ChangeMarks Changed { get; } = new();

    /// <summary>A choice was made, so the page's Save bar counts again.</summary>
    public event EventHandler? Edited;

    [ObservableProperty]
    public partial bool UseWindows { get; set; } = true;

    [ObservableProperty]
    public partial bool Light { get; set; }

    [ObservableProperty]
    public partial bool Dark { get; set; }

    /// <summary>"Windows is set to Dark now.": what "Use my Windows setting" shows, said before it is chosen.</summary>
    [ObservableProperty]
    public partial string WindowsNow { get; set; } = string.Empty;

    partial void OnUseWindowsChanged(bool value) => Chosen(value, ThemeChoice.System);

    partial void OnLightChanged(bool value) => Chosen(value, ThemeChoice.Light);

    partial void OnDarkChanged(bool value) => Chosen(value, ThemeChoice.Dark);

    private void Chosen(bool value, ThemeChoice choice)
    {
        if (!_refreshing && value)
        {
            Section.Choose(choice);
            Refresh();
        }
    }

    /// <summary>Shows the card as it is now: after a choice, and after the page saved or undid its changes.</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            UseWindows = Section.Shown == ThemeChoice.System;
            Light = Section.Shown == ThemeChoice.Light;
            Dark = Section.Shown == ThemeChoice.Dark;
            WindowsNow = $"Windows is set to {_windowsMode()} now.";
        }
        finally
        {
            _refreshing = false;
        }

        Changed.Set(Section.Changes);
        Edited?.Invoke(this, EventArgs.Empty);
    }
}
