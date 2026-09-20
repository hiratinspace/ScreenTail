using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.UI.Hud;

/// <summary>
/// The recording pill (ST-072, Spec §5 S2).
///
/// Every word and colour comes from <see cref="HudState"/>, which is platform-neutral and tested without
/// WPF (ADR-0002). This binds it, and keeps one rule of its own: nothing here may ever decide that
/// capture has stopped. The service owns that (ADR-0003), and a UI that inferred it from silence would be
/// the silent-capture path INV-4 forbids.
/// </summary>
public sealed partial class HudViewModel : ObservableObject
{
    private readonly ShellState _shell;
    private bool _hiddenForThisSession;

    public HudViewModel(ShellState shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _shell.Changed += _ => Refresh();
        Refresh();
    }

    /// <summary>Raised when the technician asks for the current session's Review (double-click).</summary>
    public event Action? ReviewRequested;

    /// <summary>Raised on Ctrl+Alt+M from the expanded pill, and on the Mark moment button.</summary>
    public event Action? MarkRequested;

    [ObservableProperty]
    private HudSnapshot _snapshot = HudState.For(null);

    /// <summary>What Windows allows. Set by the shell when the service reports it (ST-021).</summary>
    [ObservableProperty]
    private CapabilityReport? _capabilities;

    [ObservableProperty]
    private bool _online = true;

    /// <summary>Spec §5 S2: click the pill to see the last three events and Mark moment.</summary>
    [ObservableProperty]
    private bool _expanded;

    /// <summary>The last three timeline events, newest last. Counts and kinds only — never content (INV-10).</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RecentEvents { get; } = [];

    public string StateText => Snapshot.State.Text;

    public string StateGlyph => Snapshot.State.Glyph;

    public string? StateTooltip => Snapshot.State.Tooltip;

    public HudTone Tone => Snapshot.State.Tone;

    public bool Visible => Snapshot.Visible;

    public string RedactionText => Snapshot.PendingRedactions == 1
        ? "1 redacting"
        : $"{Snapshot.PendingRedactions} redacting";

    /// <summary>The shield count is only worth screen space while there is a backlog to report.</summary>
    public bool ShowRedactions => Snapshot.PendingRedactions > 0;

    public bool ShowMic => Snapshot.Mic is not null;

    public string? MicTooltip => Snapshot.Mic?.Tooltip;

    public bool ShowCloud => Snapshot.Cloud is not null;

    public string? CloudTooltip => Snapshot.Cloud?.Tooltip;

    /// <summary>
    /// Right-click hides the pill for the rest of this session and no longer (Spec §5 S2).
    ///
    /// It deliberately does nothing while capture is running, paused or suppressed: INV-4 permits a
    /// tray-only session only where the technician explicitly chose one, and a right-click mid-recording
    /// is a slip as often as a choice. <see cref="HudState"/> enforces that; this only records the wish.
    /// </summary>
    [RelayCommand]
    private void Hide()
    {
        _hiddenForThisSession = true;
        Refresh();
    }

    [RelayCommand]
    private void ToggleExpanded() => Expanded = !Expanded;

    [RelayCommand]
    private void Review() => ReviewRequested?.Invoke();

    [RelayCommand]
    private void Mark() => MarkRequested?.Invoke();

    /// <summary>A new session makes the pill visible again, whatever was clicked during the last one.</summary>
    public void SessionStarted()
    {
        _hiddenForThisSession = false;
        RecentEvents.Clear();
        Refresh();
    }

    private void Refresh()
    {
        Snapshot = HudState.For(_shell.Snapshot.KnownCapture, Capabilities, Online, _hiddenForThisSession);
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(StateTooltip));
        OnPropertyChanged(nameof(Tone));
        OnPropertyChanged(nameof(Visible));
        OnPropertyChanged(nameof(RedactionText));
        OnPropertyChanged(nameof(ShowRedactions));
        OnPropertyChanged(nameof(ShowMic));
        OnPropertyChanged(nameof(MicTooltip));
        OnPropertyChanged(nameof(ShowCloud));
        OnPropertyChanged(nameof(CloudTooltip));
    }

    partial void OnCapabilitiesChanged(CapabilityReport? value) => Refresh();

    partial void OnOnlineChanged(bool value) => Refresh();
}
