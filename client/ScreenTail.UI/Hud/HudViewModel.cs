using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;

namespace ScreenTail.UI.Hud;

/// <summary>
/// The recording pill (ST-072, Spec §5 S2).
///
/// Every word and colour comes from <see cref="HudState"/>, which is platform-neutral and tested without
/// WPF (ADR-0002). This binds it, and keeps one rule of its own: nothing here may ever decide that
/// capture has stopped. The service owns that (ADR-0003), and a UI that inferred it from silence would be
/// the silent-capture path INV-4 forbids.
///
/// v0.6.0 (Outbox): Pause, Stop and Mark are on the pill itself, each offered only where the service
/// would accept it (<see cref="HudControls"/>), and the expanded pill lists the session's last three
/// events as kinds and times (<see cref="HudTimeline"/>). The buttons raise events; the shell sends the
/// commands the tray already sends, so what each one does is the service's, unchanged.
/// </summary>
public sealed partial class HudViewModel : ObservableObject
{
    private const string NoSession = "No session running";

    private readonly ShellState _shell;
    private readonly RecordingClock _clock;
    private readonly Lock _refreshing = new();
    private readonly HudTimeline _timeline;
    private bool _hiddenForThisSession;

    /// <param name="shell">The store the pill reads.</param>
    /// <param name="clock">
    /// Keeps the recording clock moving between the service's snapshots, shared with the tray and the shell
    /// so the three never disagree. Required, and owned by whoever made it: a clock the pill made for itself
    /// would be a timer nobody disposes, ticking for as long as the store says recording. Made before the
    /// pill listens to the store, so the clock has a new snapshot as its base by the time the pill draws it.
    /// </param>
    /// <param name="time">The wall clock the expanded pill's events are stamped with. The system's outside tests.</param>
    public HudViewModel(ShellState shell, RecordingClock clock, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(clock);
        _shell = shell;
        _clock = clock;
        _timeline = new HudTimeline(time);
        _shell.Changed += _ => Refresh(observe: true);

        // The clock's tick redraws the number, and only the number moves: the state is re-read from the
        // store each time, never inferred from the tick (ADR-0003, INV-4). A tick is not an event either.
        _clock.Ticked += () => Refresh(observe: false);
        Refresh(observe: true);
    }

    /// <summary>Raised when the technician asks for the current session's Review (double-click, Open in ScreenTail).</summary>
    public event Action? ReviewRequested;

    /// <summary>Raised by the pill's Mark button and the expanded pill's Mark moment.</summary>
    public event Action? MarkRequested;

    /// <summary>Raised by the pill's Pause button while capture runs or a guard holds it.</summary>
    public event Action? PauseRequested;

    /// <summary>Raised by the same button once the technician has paused.</summary>
    public event Action? ResumeRequested;

    /// <summary>Raised by the pill's Stop button: stop and draft, as the tray's item does.</summary>
    public event Action? StopRequested;

    [ObservableProperty]
    private HudSnapshot _snapshot = HudState.For(null);

    /// <summary>What Windows allows. Set by the shell when the service reports it (ST-021).</summary>
    [ObservableProperty]
    private CapabilityReport? _capabilities;

    [ObservableProperty]
    private bool _online = true;

    /// <summary>Spec §5 S2: click the pill, or its chevron, to see the session card.</summary>
    [ObservableProperty]
    private bool _expanded;

    /// <summary>
    /// The last three events, newest first: kinds, counts and times, never content (INV-10). Replaced whole
    /// rather than changed in place, because the store's changes arrive off the UI thread and WPF marshals a
    /// property change but not a collection's.
    /// </summary>
    public IReadOnlyList<HudEventRow> RecentEvents { get; private set; } = [];

    public bool HasRecentEvents => RecentEvents.Count > 0;

    /// <summary>"ScreenConnect session, since 14:02", or plain words between sessions.</summary>
    public string SessionTitle { get; private set; } = NoSession;

    /// <summary>Mark moment, while recording. Null when the service would refuse it.</summary>
    public HudControl? MarkControl { get; private set; }

    /// <summary>Pause, or Resume once paused. Null when neither would be accepted.</summary>
    public HudControl? PauseControl { get; private set; }

    /// <summary>Stop and draft, while a session runs.</summary>
    public HudControl? StopControl { get; private set; }

    /// <summary>
    /// The buttons' words, never null: a hidden button keeps the words it would have, so a binding never
    /// walks through a null control (Spec §5 S2: name and hotkey, "Pause · Ctrl+Alt+P").
    /// </summary>
    public HudControl MarkButton => MarkControl ?? HudControls.Mark;

    /// <inheritdoc cref="MarkButton"/>
    public HudControl PauseButton => PauseControl ?? HudControls.Pause;

    /// <inheritdoc cref="MarkButton"/>
    public HudControl StopButton => StopControl ?? HudControls.Stop;

    public bool ShowMarkControl => MarkControl is not null;

    public bool ShowPauseControl => PauseControl is not null;

    public bool ShowStopControl => StopControl is not null;

    /// <summary>Pause's glyph turns to Resume's (Segoe Fluent Icons: Pause, Play).</summary>
    public string PauseGlyph => PauseControl?.Action == HudAction.Resume ? "" : "";

    /// <summary>The chevron's name says what it will do.</summary>
    public string ExpandName => Expanded ? "Hide recent events" : "Show recent events";

    /// <summary>Chevron up when open, down when closed.</summary>
    public string ExpandGlyph => Expanded ? "" : "";

    public string StateText => Snapshot.State.Text;

    public string StateGlyph => Snapshot.State.Glyph;

    public string? StateTooltip => Snapshot.State.Tooltip;

    /// <summary>The pill as a screen reader says it (Spec §7), clock included.</summary>
    public string SpokenState => Snapshot.Spoken;

    /// <summary>What is announced when it changes: the state without the clock (ST-084).</summary>
    public string Announcement => Snapshot.Announcement;

    public HudTone Tone => Snapshot.State.Tone;

    public bool Visible => Snapshot.Visible;

    /// <summary>The masked-count chip: beside the shield, the number of screenshots still being masked.</summary>
    public string RedactionText => Snapshot.PendingRedactions.ToString(CultureInfo.InvariantCulture);

    /// <summary>The chip in words, for its tooltip and a screen reader.</summary>
    public string RedactionSpoken => Snapshot.PendingRedactions == 1
        ? "1 screenshot still being masked. It is not readable until it is done."
        : string.Create(CultureInfo.InvariantCulture, $"{Snapshot.PendingRedactions} screenshots still being masked. They are not readable until this reaches zero.");

    /// <summary>The shield count is only worth screen space while there is a backlog to report.</summary>
    public bool ShowRedactions => Snapshot.PendingRedactions > 0;

    public bool ShowMic => Snapshot.Mic is not null;

    public string? MicText => Snapshot.Mic?.Text;

    public string? MicTooltip => Snapshot.Mic?.Tooltip;

    public bool ShowCloud => Snapshot.Cloud is not null;

    public string? CloudText => Snapshot.Cloud?.Text;

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
        Refresh(observe: false);
    }

    [RelayCommand]
    private void ToggleExpanded() => Expanded = !Expanded;

    [RelayCommand]
    private void Review() => ReviewRequested?.Invoke();

    [RelayCommand]
    private void Mark() => MarkRequested?.Invoke();

    /// <summary>Pause or Resume, whichever the button shows. The service decides; the pill only asks.</summary>
    [RelayCommand]
    private void PauseOrResume()
    {
        switch (PauseControl?.Action)
        {
            case HudAction.Pause:
                PauseRequested?.Invoke();
                break;
            case HudAction.Resume:
                ResumeRequested?.Invoke();
                break;
            default:
                break;
        }
    }

    [RelayCommand]
    private void Stop() => StopRequested?.Invoke();

    /// <summary>The service accepted a mark the pill sent: it joins the expanded pill's events.</summary>
    public void MomentMarked()
    {
        lock (_refreshing)
        {
            if (_timeline.MomentMarked())
            {
                PublishTimeline();
            }
        }
    }

    /// <summary>A new session makes the pill visible again, whatever was clicked during the last one.</summary>
    public void SessionStarted()
    {
        _hiddenForThisSession = false;
        Refresh(observe: true);
    }

    /// <summary>
    /// Under a lock because the store's changes and the clock's ticks arrive on different threads. Each
    /// refresh reads the store afresh, so whichever finishes last shows the latest state; without the lock
    /// a tick that read the store just before a pause could finish after the pause's refresh and leave the
    /// pill saying "recording" with no timer left to correct it.
    /// </summary>
    /// <param name="observe">
    /// The store changed, so the timeline compares this state with the last. A clock tick only redraws the
    /// number and is not an event.
    /// </param>
    private void Refresh(bool observe)
    {
        lock (_refreshing)
        {
            var capture = _shell.Snapshot.KnownCapture;
            Snapshot = HudState.For(_clock.Show(capture), Capabilities, Online, _hiddenForThisSession);

            var controls = HudControls.For(capture);
            MarkControl = controls.FirstOrDefault(c => c.Action is HudAction.Mark);
            PauseControl = controls.FirstOrDefault(c => c.Action is HudAction.Pause or HudAction.Resume);
            StopControl = controls.FirstOrDefault(c => c.Action is HudAction.Stop);

            if (observe && _timeline.Observe(capture))
            {
                PublishTimeline();
            }

            Notify();
        }
    }

    private void PublishTimeline()
    {
        RecentEvents = [.. _timeline.Recent.Select(HudEventRow.For)];
        SessionTitle = _timeline.SessionTitle ?? NoSession;
        OnPropertyChanged(nameof(RecentEvents));
        OnPropertyChanged(nameof(HasRecentEvents));
        OnPropertyChanged(nameof(SessionTitle));
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(StateTooltip));
        OnPropertyChanged(nameof(SpokenState));
        OnPropertyChanged(nameof(Announcement));
        OnPropertyChanged(nameof(Tone));
        OnPropertyChanged(nameof(Visible));
        OnPropertyChanged(nameof(RedactionText));
        OnPropertyChanged(nameof(RedactionSpoken));
        OnPropertyChanged(nameof(ShowRedactions));
        OnPropertyChanged(nameof(ShowMic));
        OnPropertyChanged(nameof(MicText));
        OnPropertyChanged(nameof(MicTooltip));
        OnPropertyChanged(nameof(ShowCloud));
        OnPropertyChanged(nameof(CloudText));
        OnPropertyChanged(nameof(CloudTooltip));
        OnPropertyChanged(nameof(MarkControl));
        OnPropertyChanged(nameof(PauseControl));
        OnPropertyChanged(nameof(StopControl));
        OnPropertyChanged(nameof(MarkButton));
        OnPropertyChanged(nameof(PauseButton));
        OnPropertyChanged(nameof(StopButton));
        OnPropertyChanged(nameof(ShowMarkControl));
        OnPropertyChanged(nameof(ShowPauseControl));
        OnPropertyChanged(nameof(ShowStopControl));
        OnPropertyChanged(nameof(PauseGlyph));
    }

    partial void OnCapabilitiesChanged(CapabilityReport? value) => Refresh(observe: false);

    partial void OnOnlineChanged(bool value) => Refresh(observe: false);

    partial void OnExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandName));
        OnPropertyChanged(nameof(ExpandGlyph));
    }
}

/// <summary>One line of the expanded pill: an icon for the kind, the kind in words, and the time.</summary>
/// <param name="Glyph">Segoe Fluent Icons; decoration beside the words, hidden from a screen reader.</param>
/// <param name="Text">The kind in words ("Screenshot taken").</param>
/// <param name="Time">HH:mm:ss.</param>
/// <param name="Spoken">The line as a screen reader says it.</param>
public sealed record HudEventRow(string Glyph, string Text, string Time, string Spoken)
{
    public static HudEventRow For(HudEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var glyph = e.Kind switch
        {
            HudEventKind.ScreenshotTaken => "",
            HudEventKind.ScreenshotMasked => "",
            HudEventKind.MomentMarked => "",
            HudEventKind.Paused or HudEventKind.PausedAutomatically => "",
            HudEventKind.Resumed => "",
            HudEventKind.NotCapturing => "",
            _ => "",
        };
        return new HudEventRow(glyph, e.Text, e.Time, e.Spoken);
    }
}
