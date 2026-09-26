using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Input;
using ScreenTail.Core.Settings;

namespace ScreenTail.UI.Settings.Capture;

/// <summary>One remote tool with its toggle.</summary>
public sealed partial class ToolRowViewModel(CapturePanel.ToolToggle toggle, Action<string, bool> changed) : ObservableObject
{
    public string Id => toggle.Id;

    public string DisplayName => toggle.DisplayName;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = toggle.Enabled;

    partial void OnEnabledChanged(bool value) => changed(Id, value);
}

/// <summary>One hotkey row: the action, its chord as typed, and the problem beside it.</summary>
public sealed partial class HotkeyRowViewModel(HotkeyAction action, string label, string text, Func<HotkeyAction, string, string?> changed) : ObservableObject
{
    public HotkeyAction Action => action;

    public string Label => label;

    [ObservableProperty]
    public partial string Text { get; set; } = text;

    [ObservableProperty]
    public partial string Problem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility ProblemVisibility { get; set; } = Visibility.Collapsed;

    partial void OnTextChanged(string value)
    {
        Problem = changed(Action, value) ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void Recheck(string? problem)
    {
        Problem = problem ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>
/// Settings → Capture (ST-080, Spec §5 S5), bound over <see cref="CapturePanel"/>, which holds every
/// rule. Start at login is the shell's: it is the UI process that starts, so the UI writes the Run key.
/// </summary>
public sealed partial class CaptureViewModel : ObservableObject
{
    private static readonly (HotkeyAction Action, string Label)[] HotkeyLabels =
    [
        (HotkeyAction.StartCapture, "Start capture"),
        (HotkeyAction.PauseOrResume, "Pause or resume"),
        (HotkeyAction.StopAndDraft, "Stop and draft"),
        (HotkeyAction.MarkMoment, "Mark this moment"),
    ];

    private readonly CapturePanel _panel;
    private readonly Func<bool, Task> _startAtLogin;

    /// <param name="startAtLogin">Registers or removes the UI from the technician's login items; the shell's job.</param>
    public CaptureViewModel(CapturePanel panel, Func<bool, Task> startAtLogin)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _startAtLogin = startAtLogin ?? throw new ArgumentNullException(nameof(startAtLogin));
        Refresh();
    }

    public ObservableCollection<ToolRowViewModel> Tools { get; } = [];

    public ObservableCollection<HotkeyRowViewModel> Hotkeys { get; } = [];

    public IReadOnlyList<string> Sensitivities { get; } = ["low", "medium", "high"];

    public IReadOnlyList<ModelChoice> Models => _panel.Models;

    [ObservableProperty]
    public partial bool RemoteToolsOnly { get; set; } = true;

    [ObservableProperty]
    public partial bool AllWindows { get; set; }

    [ObservableProperty]
    public partial bool ScopeEditable { get; set; } = true;

    [ObservableProperty]
    public partial string ScopeWarning { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility ScopeWarningVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial bool AutoStart { get; set; } = true;

    [ObservableProperty]
    public partial int GraceSeconds { get; set; } = 120;

    [ObservableProperty]
    public partial string Sensitivity { get; set; } = "medium";

    [ObservableProperty]
    public partial string MicrophoneName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ModelChoice? Model { get; set; }

    [ObservableProperty]
    public partial string AppliesAtNextStart { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StartUiAtLogin { get; set; }

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
    private void ConfirmAllWindows()
    {
        _panel.ConfirmAllWindows();
        Field(() => { });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var saved = await _panel.SaveAsync().ConfigureAwait(true);
        if (saved)
        {
            await _startAtLogin(_panel.StartUiAtLogin).ConfigureAwait(true);
        }

        Refresh();
        Saved = saved ? "Saved and applied." : string.Empty;
    }

    partial void OnAllWindowsChanged(bool value) => Field(() => _panel.CaptureAllWindows = value);

    partial void OnRemoteToolsOnlyChanged(bool value)
    {
        if (value)
        {
            Field(() => _panel.CaptureAllWindows = false);
        }
    }

    partial void OnAutoStartChanged(bool value) => Field(() => _panel.AutoStart = value);

    partial void OnGraceSecondsChanged(int value) => Field(() => _panel.GraceSeconds = value);

    partial void OnSensitivityChanged(string value) => Field(() => _panel.Sensitivity = value);

    partial void OnModelChanged(ModelChoice? value) => Field(() => _panel.Model = value?.Name);

    partial void OnStartUiAtLoginChanged(bool value) => Field(() => _panel.StartUiAtLogin = value);

    private void Field(Action set)
    {
        set();
        Saved = string.Empty;
        ScopeWarning = _panel.ScopeWarning ?? string.Empty;
        ScopeWarningVisibility = ScopeWarning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Problem = _panel.Problem ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanSave = _panel.CanSave;
    }

    private string? HotkeyChanged(HotkeyAction action, string text)
    {
        _panel.SetHotkey(action, text);
        foreach (var row in Hotkeys.Where(r => r.Action != action))
        {
            row.Recheck(_panel.HotkeyProblem(row.Action));
        }

        Field(() => { });
        return _panel.HotkeyProblem(action);
    }

    private void Refresh()
    {
        Tools.Clear();
        foreach (var tool in _panel.Tools)
        {
            Tools.Add(new ToolRowViewModel(tool, (id, enabled) => Field(() => _panel.SetTool(id, enabled))));
        }

        Hotkeys.Clear();
        foreach (var (action, label) in HotkeyLabels)
        {
            Hotkeys.Add(new HotkeyRowViewModel(action, label, _panel.HotkeyText(action), HotkeyChanged));
        }

        AllWindows = _panel.CaptureAllWindows;
        RemoteToolsOnly = !_panel.CaptureAllWindows;
        ScopeEditable = !_panel.ScopeLocked;
        AutoStart = _panel.AutoStart;
        GraceSeconds = _panel.GraceSeconds;
        Sensitivity = _panel.Sensitivity;
        MicrophoneName = _panel.Microphones.Count > 0 ? _panel.Microphones[0] : "No microphone found";
        Model = _panel.Models.FirstOrDefault(m => string.Equals(m.Name, _panel.Model, StringComparison.OrdinalIgnoreCase));
        AppliesAtNextStart = _panel.AppliesAtNextStart;
        StartUiAtLogin = _panel.StartUiAtLogin;
        Field(() => { });
    }
}
