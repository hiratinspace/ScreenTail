using System.Globalization;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Input;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>What a row of the tray's quick menu does when it is chosen. Each is something the shell already does.</summary>
public enum TrayCommand
{
    /// <summary>Not a command: the state line, the start hint and the separators.</summary>
    None,

    /// <summary><c>pause</c>, the same as the pill's Pause and Ctrl+Alt+P.</summary>
    Pause,

    /// <summary><c>resume</c>, the same as the pill's Resume and Ctrl+Alt+P.</summary>
    Resume,

    /// <summary><c>stop</c>: stop and draft.</summary>
    Stop,

    /// <summary>The typed-confirmation discard of the session the service is on (Spec §3, v0.4.1 Q3).</summary>
    Discard,

    /// <summary>The shell, on Review of the newest draft, or on Review when there is none.</summary>
    OpenLastDraft,

    /// <summary>The shell, on All sessions.</summary>
    History,

    /// <summary>The "What's being captured right now?" panel (ST-071).</summary>
    Diagnostics,

    /// <summary>Put the pill away until the next session starts (INV-4: only between sessions).</summary>
    HidePill,

    /// <summary>Bring the pill back after it was put away.</summary>
    ShowPill,

    /// <summary>The shell, on Settings.</summary>
    Settings,

    /// <summary>The setup wizard, again (ST-083).</summary>
    Setup,

    /// <summary>Quit, finishing a session first if one is being captured.</summary>
    Quit,
}

/// <summary>What kind of row it is, which is how the host draws it.</summary>
public enum TrayRowKind
{
    /// <summary>The first row: what capture is doing, in words, with the tray's own glyph. Never interactive.</summary>
    State,

    /// <summary>A line that says how to do something the menu cannot do. Never interactive.</summary>
    Hint,

    /// <summary>Something to choose. Disabled when the service would refuse it now.</summary>
    Item,

    Separator,
}

/// <param name="Text">The words, with <c>&amp;</c> before the access key; an <c>&amp;</c> that is text is not used.</param>
/// <param name="Hotkey">The default global chord that does the same, shown on the right ("Ctrl+Alt+P"), or null.</param>
public sealed record TrayRow(TrayRowKind Kind, TrayCommand Command, string Text, string? Hotkey, bool Enabled)
{
    /// <summary>The words without the access key's marker: what a screen reader says and a test compares.</summary>
    public string Name => Text.Replace("&", string.Empty, StringComparison.Ordinal);

    /// <summary>The access key, upper case, or null for a row that has none.</summary>
    public char? AccessKey => Text.IndexOf('&', StringComparison.Ordinal) is var at and >= 0 && at + 1 < Text.Length
        ? char.ToUpperInvariant(Text[at + 1])
        : null;

    internal static TrayRow Line(TrayRowKind kind, string text) => new(kind, TrayCommand.None, text, null, false);

    internal static readonly TrayRow Separator = new(TrayRowKind.Separator, TrayCommand.None, string.Empty, null, false);
}

/// <summary>
/// The tray's quick menu (Spec §5 S1), row by row, for the state the service last reported.
///
/// The host draws exactly these rows in this order and does nothing else with the state, so the order,
/// the words, what is disabled and the INV-4 rule for the pill are all held here, where the Mac suite
/// tests them. What each command does is the shell's, unchanged: the tray offers only what the pill,
/// the hotkeys and the shell already do.
/// </summary>
public static class TrayMenu
{
    /// <summary>
    /// Between sessions the spec's "Start capture Ctrl+Alt+R" is this line. The UI has no command that
    /// starts a session the way the hotkey does (the service reads what is in front and attributes the
    /// session to its tool), so the menu says how to start rather than offering a button that cannot.
    /// </summary>
    public static readonly string StartHint = $"Start capture: press {Chord(HotkeyAction.StartCapture)} in the remote session";

    public const string DiscardText = "&Discard session…";

    public const string HistoryText = "Session &history";

    public const string DiagnosticsText = "&What's being captured right now?";

    public const string HidePillText = "Hide the recording pi&ll until the next session";

    /// <summary>The same row while a session may be under way, when the pill stays (INV-4).</summary>
    public const string HidePillNotNowText = "Hide the recording pi&ll (only between sessions)";

    public const string ShowPillText = "Show the recording pi&ll";

    public const string SettingsText = "S&ettings";

    public const string SetupText = "Set &up ScreenTail…";

    public const string QuitText = "&Quit ScreenTail";

    public const string OpenText = "&Open ScreenTail";

    /// <summary>"Open last draft (2 ready)", or Open ScreenTail when no draft is known to be ready.</summary>
    public static string OpenLastDraftText(int ready) => ready > 0
        ? string.Create(CultureInfo.InvariantCulture, $"&Open last draft ({ready} ready)")
        : OpenText;

    /// <param name="snapshot">What the shell last heard.</param>
    /// <param name="pillHidden">Whether the technician has put the pill away between sessions.</param>
    public static IReadOnlyList<TrayRow> For(ShellSnapshot snapshot, bool pillHidden)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var presence = TrayPresence.From(snapshot);
        var connected = snapshot.Connection == ServiceConnection.Connected;
        var capture = connected ? snapshot.Capture : null;
        var rows = new List<TrayRow> { TrayRow.Line(TrayRowKind.State, presence.StateLine) };

        // The first block. While a session is under way, or the UI does not know whether one is: Pause or
        // Resume, Stop and draft, Discard, each enabled only where the service accepts it now (the pill's
        // own rule). Between sessions it is the start line, with Discard while there is a session to
        // discard (a draft finishing, ready or failed).
        var resting = capture?.State is CaptureStates.Idle or CaptureStates.DraftPending or CaptureStates.DraftReady or CaptureStates.DraftFailed;
        if (resting)
        {
            rows.Add(TrayRow.Line(TrayRowKind.Hint, StartHint));
            if (capture!.State != CaptureStates.Idle)
            {
                rows.Add(new TrayRow(TrayRowKind.Item, TrayCommand.Discard, DiscardText, null, Enabled: true));
            }
        }
        else
        {
            var offered = HudControls.For(capture);
            var resume = offered.Contains(HudControls.Resume);
            rows.Add(resume
                ? new TrayRow(TrayRowKind.Item, TrayCommand.Resume, "&Resume", HudControls.Resume.Hotkey, Enabled: true)
                : new TrayRow(TrayRowKind.Item, TrayCommand.Pause, "&Pause", HudControls.Pause.Hotkey, offered.Contains(HudControls.Pause)));
            rows.Add(new TrayRow(TrayRowKind.Item, TrayCommand.Stop, "&Stop and draft", HudControls.Stop.Hotkey, offered.Contains(HudControls.Stop)));

            // The service discards from any state with a session, finishing up included, and the UI can only
            // ask while it is connected.
            rows.Add(new TrayRow(TrayRowKind.Item, TrayCommand.Discard, DiscardText, null, capture is not null && capture.State != CaptureStates.Idle));
        }

        rows.Add(TrayRow.Separator);
        rows.Add(new TrayRow(TrayRowKind.Item, TrayCommand.OpenLastDraft, OpenLastDraftText(presence.DraftsReady), null, Enabled: true));
        rows.Add(new TrayRow(TrayRowKind.Item, TrayCommand.History, HistoryText, null, Enabled: true));
        rows.Add(new TrayRow(TrayRowKind.Item, TrayCommand.Diagnostics, DiagnosticsText, null, Enabled: true));

        // INV-4 (owner decision 2026-10-25): the pill may be put away only while capture is known to be off,
        // and comes back by itself when the next session starts. While a session may be under way the row
        // stays, disabled, and says when it can be used, rather than taking a wish the shell would ignore.
        var knownIdle = connected && HudState.KnownIdle(snapshot.KnownCapture);
        rows.Add(knownIdle && pillHidden
            ? new TrayRow(TrayRowKind.Item, TrayCommand.ShowPill, ShowPillText, null, Enabled: true)
            : new TrayRow(TrayRowKind.Item, TrayCommand.HidePill, knownIdle ? HidePillText : HidePillNotNowText, null, knownIdle));

        rows.Add(TrayRow.Separator);
        rows.Add(new TrayRow(TrayRowKind.Item, TrayCommand.Settings, SettingsText, null, Enabled: true));
        rows.Add(new TrayRow(TrayRowKind.Item, TrayCommand.Setup, SetupText, null, Enabled: true));
        rows.Add(new TrayRow(TrayRowKind.Item, TrayCommand.Quit, QuitText, null, Enabled: true));
        return rows;
    }

    /// <summary>
    /// Whether the tray's Discard has to stop capture before it asks (Spec v0.4.1 Q3): while a session is
    /// being captured no modal may appear (Spec §4), so the session is stopped first and moves to finishing
    /// up, then the typed confirmation is shown.
    /// </summary>
    public static bool DiscardStopsFirst(CaptureStateSnapshot? capture) => HudState.SessionUnderWay(capture) is not null;

    private static string Chord(HotkeyAction action) => HotkeyBindings.Defaults[action].ToString();
}
