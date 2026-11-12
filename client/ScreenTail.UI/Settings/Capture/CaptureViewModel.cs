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
///
/// The microphone's live level is <see cref="MicrophoneLevelWatch"/>'s (AC3). The pane says when the
/// card is being looked at and this passes it on; the watch decides nothing else and holds no audio.
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
    private readonly MicrophoneLevelWatch? _level;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private bool _refreshing;

    /// <param name="startAtLogin">Registers or removes the UI from the technician's login items; the shell's job.</param>
    /// <param name="level">The live level meter, or null where there is no service to ask (the meter then says so and never moves).</param>
    public CaptureViewModel(CapturePanel panel, Func<bool, Task> startAtLogin, MicrophoneLevelWatch? level = null)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        ArgumentNullException.ThrowIfNull(startAtLogin);
        Section = new CaptureSection(panel, startAtLogin);
        _level = level;
        if (_level is not null)
        {
            _level.Changed += LevelChanged;
        }

        Refresh();
    }

    public ObservableCollection<MicrophoneOption> MicrophoneOptions { get; } = [];

    [ObservableProperty]
    public partial MicrophoneOption? SelectedMicrophone { get; set; }

    [ObservableProperty]
    public partial string MicrophoneNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility MicrophoneNoteVisibility { get; set; } = Visibility.Collapsed;

    public string MicrophoneApplies { get; } = CapturePanel.MicrophoneApplies;

    /// <summary>The meter's fill, 0 to 100.</summary>
    [ObservableProperty]
    public partial double MicrophoneLevel { get; set; }

    [ObservableProperty]
    public partial string MicrophoneLevelStatus { get; set; } = MicrophoneLevelWatch.NotWatching;

    /// <summary>
    /// The pane's word on whether the microphone card is being looked at: on screen, scrolled into view,
    /// the window in front and not minimised. The level is asked for exactly while this is true.
    /// </summary>
    public Task SetMicrophoneCardWatchedAsync(bool watched) => _level?.SetWatchingAsync(watched) ?? Task.CompletedTask;

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
    public partial ModelChoice? Model { get; set; }

    [ObservableProperty]
    public partial string AppliesAtNextStart { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StartUiAtLogin { get; set; }

    [ObservableProperty]
    public partial string Problem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility ProblemVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>This section on the Settings page's one Save bar: its changes, its save path (the panel, then the Run key) and Undo.</summary>
    public CaptureSection Section { get; }

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
    private void ConfirmAllWindows()
    {
        _panel.ConfirmAllWindows();
        Field(() => { });
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

    partial void OnSelectedMicrophoneChanged(MicrophoneOption? value)
    {
        if (_refreshing || value is null)
        {
            return;
        }

        // The meter follows the picker before anything is saved, so a device can be tried first.
        if (_level is not null)
        {
            _level.DeviceId = value.Id;
        }

        Field(() => _panel.MicrophoneId = value.Id);
        ShowMicrophoneNote();
    }

    private void LevelChanged()
    {
        if (_ui is not null && SynchronizationContext.Current != _ui)
        {
            _ui.Post(_ => ShowLevel(), null);
            return;
        }

        ShowLevel();
    }

    private void ShowLevel()
    {
        if (_level is null)
        {
            return;
        }

        MicrophoneLevel = _level.Level * 100;
        MicrophoneLevelStatus = _level.Status;
    }

    private void ShowMicrophoneNote()
    {
        MicrophoneNote = _panel.MicrophoneNote ?? string.Empty;
        MicrophoneNoteVisibility = MicrophoneNote.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Field(Action set)
    {
        set();
        ScopeWarning = _panel.ScopeWarning ?? string.Empty;
        ScopeWarningVisibility = ScopeWarning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Problem = _panel.Problem ?? string.Empty;
        ProblemVisibility = Problem.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Changed.Set(Section.Changes);
        Edited?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Shows the panel as it is now: after a load, and after the page saved or undid its changes.</summary>
    public void Refresh()
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
        _refreshing = true;
        MicrophoneOptions.Clear();
        foreach (var option in _panel.MicrophoneOptions)
        {
            MicrophoneOptions.Add(option);
        }

        SelectedMicrophone = MicrophoneOptions.FirstOrDefault(o => o == _panel.SelectedMicrophone);
        _refreshing = false;
        if (_level is not null)
        {
            _level.DeviceId = _panel.MicrophoneId;
        }

        ShowMicrophoneNote();
        Model = _panel.Models.FirstOrDefault(m => string.Equals(m.Name, _panel.Model, StringComparison.OrdinalIgnoreCase));
        AppliesAtNextStart = _panel.AppliesAtNextStart;
        StartUiAtLogin = _panel.StartUiAtLogin;
        Field(() => { });
    }
}
