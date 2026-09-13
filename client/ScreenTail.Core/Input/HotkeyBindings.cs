using System.Collections.ObjectModel;

namespace ScreenTail.Core.Input;

/// <param name="Action">What could not be bound.</param>
/// <param name="Hotkey">The chord that failed.</param>
/// <param name="Reason">Plain language, for the Settings warning. No content, no error codes.</param>
/// <param name="Suggestion">A chord that is free, so the warning can offer a way out rather than a problem.</param>
public sealed record HotkeyConflict(HotkeyAction Action, Hotkey Hotkey, string Reason, Hotkey? Suggestion);

/// <summary>
/// Which chord does what, and what to say when one cannot be had (ST-029, Spec §5 S1).
///
/// Conflicts come in two kinds and both end in the same place. Two actions bound to the same chord is ours
/// to catch, and this catches it before anything is registered. Another application already owning the
/// chord is Windows' to report, and only at registration time — <c>RegisterHotKey</c> simply fails, with no
/// way to ask who holds it. Either way Settings needs to say which action has no chord and offer one that
/// is free, because a hotkey that silently does nothing is worse than one that was never offered.
/// </summary>
public sealed class HotkeyBindings
{
    /// <summary>
    /// Spec §5 S1. Discard has no default on purpose: it is irreversible, Spec §3 requires a typed
    /// confirmation for it, and a chord next to the others is a chord someone will hit by accident. It
    /// stays bindable for a tenant that wants it, and still goes through the confirmation.
    /// </summary>
    public static readonly ReadOnlyDictionary<HotkeyAction, Hotkey> Defaults =
        new(new Dictionary<HotkeyAction, Hotkey>
        {
            [HotkeyAction.StartCapture] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "R"),
            [HotkeyAction.PauseOrResume] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "P"),
            [HotkeyAction.StopAndDraft] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "S"),
            [HotkeyAction.MarkMoment] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "M"),
        });

    /// <summary>
    /// Keys tried when suggesting a way out, in the order they are offered. Letters that mean something
    /// for the action they replace, then ones that are simply unlikely to be taken.
    /// </summary>
    private static readonly string[] Alternatives =
        ["F9", "F10", "F11", "F8", "F7", "J", "K", "Y", "U", "B", "N", "G", "H", "L", "Q"];

    private readonly Dictionary<HotkeyAction, Hotkey> _bound;

    public HotkeyBindings(IReadOnlyDictionary<HotkeyAction, Hotkey>? bindings = null) =>
        _bound = bindings is null
            ? new Dictionary<HotkeyAction, Hotkey>(Defaults)
            : new Dictionary<HotkeyAction, Hotkey>(bindings);

    public IReadOnlyDictionary<HotkeyAction, Hotkey> All => _bound;

    public Hotkey? For(HotkeyAction action) => _bound.TryGetValue(action, out var hotkey) ? hotkey : null;

    /// <summary>
    /// Every problem this can see without asking Windows: a chord bound twice, or one Windows would accept
    /// and then make unusable. Reported before registration so Settings can show them together rather than
    /// one per failed attempt.
    /// </summary>
    public IReadOnlyList<HotkeyConflict> Validate()
    {
        var conflicts = new List<HotkeyConflict>();
        var seen = new Dictionary<Hotkey, HotkeyAction>();

        foreach (var (action, hotkey) in _bound.OrderBy(b => b.Key))
        {
            if (!hotkey.IsUsable)
            {
                conflicts.Add(new HotkeyConflict(
                    action,
                    hotkey,
                    $"{hotkey} needs Ctrl, Alt or Win — a chord without one would take that key away from every other application.",
                    Suggest()));
                continue;
            }

            if (hotkey.VirtualKey is null)
            {
                conflicts.Add(new HotkeyConflict(
                    action,
                    hotkey,
                    $"Windows has no key called \"{hotkey.Key}\".",
                    Suggest()));
                continue;
            }

            if (seen.TryGetValue(hotkey, out var already))
            {
                conflicts.Add(new HotkeyConflict(
                    action,
                    hotkey,
                    $"{hotkey} is already {Describe(already)}.",
                    Suggest()));
                continue;
            }

            seen[hotkey] = action;
        }

        return conflicts;
    }

    /// <summary>
    /// Windows refused this chord: another application registered it first, and there is no way to ask
    /// which. Turns that into something Settings can show, with a chord that is at least free of ours.
    /// </summary>
    public HotkeyConflict Refused(HotkeyAction action, Hotkey hotkey) =>
        new(
            action,
            hotkey,
            $"Another application is already using {hotkey}, so {Describe(action)} has no shortcut.",
            Suggest());

    /// <summary>Binds an action, replacing whatever it had. Returns the conflicts the change leaves behind.</summary>
    public IReadOnlyList<HotkeyConflict> Bind(HotkeyAction action, Hotkey hotkey)
    {
        _bound[action] = hotkey;
        return Validate();
    }

    /// <summary>
    /// A chord none of ours is using. It cannot promise Windows will accept it — nothing can, short of
    /// trying — but it will not collide with a shortcut this application already has.
    /// </summary>
    public Hotkey? Suggest()
    {
        const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;
        const HotkeyModifiers CtrlAltShift = CtrlAlt | HotkeyModifiers.Shift;

        foreach (var modifiers in new[] { CtrlAlt, CtrlAltShift })
        {
            foreach (var key in Alternatives)
            {
                var candidate = new Hotkey(modifiers, key);
                if (!_bound.ContainsValue(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.StartCapture => "Start capture",
        HotkeyAction.PauseOrResume => "Pause",
        HotkeyAction.StopAndDraft => "Stop and draft",
        HotkeyAction.MarkMoment => "Mark moment",
        HotkeyAction.DiscardSession => "Discard session",
        _ => action.ToString(),
    };
}
