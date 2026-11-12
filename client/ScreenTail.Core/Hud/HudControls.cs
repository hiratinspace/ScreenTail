using ScreenTail.Core.Input;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Hud;

/// <summary>What a control on the pill asks the service to do. Each is a command the service already has.</summary>
public enum HudAction
{
    /// <summary><c>mark_moment</c>.</summary>
    Mark,

    /// <summary><c>pause</c>.</summary>
    Pause,

    /// <summary><c>resume</c>.</summary>
    Resume,

    /// <summary><c>stop</c>: stop and draft.</summary>
    Stop,
}

/// <param name="Action">What it asks for.</param>
/// <param name="Name">The action in words, as the tray and the shortcut sheet say it.</param>
/// <param name="Hotkey">The default chord that does the same thing (Spec §5 S1).</param>
public sealed record HudControl(HudAction Action, string Name, string Hotkey)
{
    /// <summary>Spec §5 S2: on hover or focus, the name and the hotkey ("Pause · Ctrl+Alt+P").</summary>
    public string Tooltip => $"{Name} · {Hotkey}";

    /// <summary>What a screen reader says for the button: the same words, with a comma for the dot.</summary>
    public string Spoken => $"{Name}, {Hotkey}";
}

/// <summary>
/// The pill's Pause, Stop and Mark (Spec §5 S2, v0.6.0).
///
/// Offered only where the session machine would accept them, so a click is never one the service
/// silently refuses: pause from recording or suppressed, resume from paused, stop from any of the three,
/// and mark only while recording (<c>SessionMachine</c>). The pill decides nothing more than that; what
/// each one does is the service's, unchanged.
/// </summary>
public static class HudControls
{
    public static readonly HudControl Mark = new(HudAction.Mark, "Mark moment", Chord(HotkeyAction.MarkMoment));

    public static readonly HudControl Pause = new(HudAction.Pause, "Pause", Chord(HotkeyAction.PauseOrResume));

    public static readonly HudControl Resume = new(HudAction.Resume, "Resume", Chord(HotkeyAction.PauseOrResume));

    public static readonly HudControl Stop = new(HudAction.Stop, "Stop and draft", Chord(HotkeyAction.StopAndDraft));

    /// <summary>The controls for this state, in the order the pill draws them: Mark, Pause or Resume, Stop.</summary>
    /// <param name="capture">The last thing the service said; null when the UI has not heard from it.</param>
    public static IReadOnlyList<HudControl> For(CaptureStateSnapshot? capture) => capture?.State switch
    {
        CaptureStates.Recording => [Mark, Pause, Stop],
        CaptureStates.Paused => [Resume, Stop],
        CaptureStates.Suppressed => [Pause, Stop],

        // Not heard from, between sessions, finishing up, or a state this build does not know: a command
        // sent now would be refused or would act on something the technician cannot see.
        _ => [],
    };

    private static string Chord(HotkeyAction action) => HotkeyBindings.Defaults[action].ToString();
}
