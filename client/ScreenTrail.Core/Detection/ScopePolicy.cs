using ScreenTrail.Core.Detection.Registry;
using ScreenTrail.Shared.Schema;

namespace ScreenTrail.Core.Detection;

public sealed record ScopeOptions
{
    /// <summary>
    /// INV-5: the default is remote-tool windows plus the admin-tool allowlist. "All windows" is opt-in and
    /// confirmed in Settings; a technician turns it on knowing their own inbox is now fair game.
    /// </summary>
    public bool CaptureAllWindows { get; init; }

    /// <summary>Processes the technician has excluded outright (ST-043). Beats everything else.</summary>
    public IReadOnlySet<string> ExcludedProcesses { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remote tools the technician switched off in Settings (ST-080), by registry id. A window of one is out of scope.</summary>
    public IReadOnlySet<string> DisabledTools { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The shipped and tenant-synced exclusions: password managers, banking tabs, credential prompts
    /// (ST-043). Null means none are loaded, which is a misconfiguration rather than a policy: the
    /// defaults ship with the product.
    /// </summary>
    public Privacy.ExclusionList? Exclusions { get; init; }
}

/// <param name="Scope">What the timeline records for this window.</param>
/// <param name="ToolId">The registry entry that matched, when one did.</param>
/// <param name="Reason">The HUD's words for why (Spec §5 S2). Carries a process name, never window content.</param>
/// <param name="Window">
/// The window this decision is about. Carried so a capture can prove it is photographing the window that
/// was judged, rather than whatever happens to be in front a moment later (INV-5).
/// </param>
public sealed record ScopeDecision(CaptureScope Scope, string? ToolId, RemoteToolKind? Tool, string Reason, nint Window = 0)
{
    /// <summary>Whether a screenshot may be taken. Clicks are still recorded out of scope, without their position; frames are not.</summary>
    public bool MayCaptureFrames => Scope is CaptureScope.RemoteTool or CaptureScope.AdminTool;

    /// <summary>
    /// Whether what the technician says may be kept while this window is in front: exactly where a
    /// screenshot may be taken.
    ///
    /// INV-6 drops data, not just frames, for an excluded app, an elevated window and anything out of
    /// scope. Speech was the gap: with a password manager in front, frames and keystrokes were dropped
    /// and a credential read aloud off it was transcribed, stored and quotable in the draft (2026-10-12
    /// review). Following the frame rule rather than naming the excluded case alone is the closed choice.
    /// An elevated window is folded into out-of-scope by the policy and has no flag of its own to read,
    /// and a window outside the session can be another customer's (INV-5). What it costs is narration
    /// while the technician has stepped outside the remote tool, which "All windows" gives back to a
    /// tenant that wants it.
    /// </summary>
    public bool MayHearNarration => MayCaptureFrames;

    /// <summary>
    /// Whether this event may be written while the window in front is the one this decision is about.
    ///
    /// INV-6: "excluded app, elevated window, out-of-scope → no frames, <b>no typing events written</b>."
    /// Anything keyboard-derived is dropped out of scope. A burst count, a shortcut and an Enter all say
    /// something about what was typed, and Ctrl+C in a customer's password manager is the case that
    /// matters.
    ///
    /// Clicks are the deliberate exception, and ST-023's own criterion asks for it: "switching to Outlook
    /// logs clicks but captures no frames". A click out of scope records that the technician went
    /// somewhere and did something, with no picture and nothing of what they typed, which is what makes
    /// "clicks logged, no frames" visible in Review instead of a silent gap. That is the line INV-6 draws,
    /// and it is drawn between a keystroke and a mouse button rather than between an event and no event.
    /// A click kept out of scope is kept without its position: see <see cref="Admit"/>.
    /// </summary>
    public bool MayRecord(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        return MayCaptureFrames || sessionEvent is not (TypingBurstEvent or ShortcutEvent or EnterEvent);
    }

    /// <summary>
    /// The event as it may be written while this window is in front: unchanged where frames may be taken,
    /// and a click anywhere else without where it landed.
    ///
    /// INV-6 drops data, not just frames. A click's screen position in a window that may not be
    /// photographed is data about that window: a run of positions over an on-screen keypad or a vault's
    /// list says which keys or entries were pressed as surely as a picture would. The click itself stays
    /// (its time and button), so the timeline can still say "clicks logged, no frames".
    /// </summary>
    public SessionEvent Admit(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        return MayCaptureFrames ? sessionEvent : WithoutPosition(sessionEvent);
    }

    /// <summary>
    /// The stricter of two decisions about one window: the one that forbids frames when only one does
    /// (frames, typing, narration and click positions all follow <see cref="MayCaptureFrames"/>), otherwise
    /// the newer. For a title not reported yet, which may tighten what is in force but not loosen it.
    /// </summary>
    /// <param name="inForce">The decision in force.</param>
    /// <param name="newer">The decision for the newer look at the same window.</param>
    public static ScopeDecision Stricter(ScopeDecision inForce, ScopeDecision newer)
    {
        ArgumentNullException.ThrowIfNull(inForce);
        ArgumentNullException.ThrowIfNull(newer);
        return !inForce.MayCaptureFrames && newer.MayCaptureFrames ? inForce : newer;
    }

    /// <summary>
    /// A click with its position removed; any other event unchanged. For when nothing is known about the
    /// window in front, which is not a reason to keep where the mouse was.
    /// </summary>
    public static SessionEvent WithoutPosition(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        return sessionEvent is ClickEvent click && (click.X is not null || click.Y is not null)
            ? click with { X = null, Y = null }
            : sessionEvent;
    }
}

/// <summary>
/// Decides what may be captured from the window in front (ST-023, INV-5).
///
/// Runs on every foreground change, so it answers from a dictionary where it can and only falls back to
/// patterns when it must. The order matters and is the whole of the invariant: an exclusion wins over
/// everything, a remote tool is in scope, an admin tool is in scope *because* it sits beside a session, and
/// everything else is out of scope (clicks still logged so the timeline stays honest about what the
/// technician did, but no frames).
/// </summary>
public sealed class ScopePolicy(RemoteToolRegistry registry, ScopeOptions? options = null)
{
    private ScopeOptions _options = options ?? new ScopeOptions();

    public ScopeOptions Options => _options;

    /// <summary>The tenant's policy arriving (ST-047): a new set of options for every decision after this one.</summary>
    public void Apply(ScopeOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    public RemoteToolRegistry Registry => registry;

    public ScopeDecision Decide(ForegroundWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.IsNone)
        {
            return new ScopeDecision(CaptureScope.OutOfScope, null, null, "Nothing in the foreground.");
        }

        var process = window.ProcessName ?? "an unknown app";

        // The shipped list first: password managers, banking tabs, credential prompts. Checked before the
        // technician's own additions and before anything else, because an exclusion beats every reason a
        // window might otherwise be in scope: a password manager opened during a support session is
        // still support work, and still must not be photographed (ST-043).
        if (_options.Exclusions?.Match(window) is { } excluded)
        {
            return new ScopeDecision(CaptureScope.Excluded, excluded.Id, null, $"Not capturing: {excluded.DisplayName} is excluded.");
        }

        // A window the technician excluded is never captured, whatever else it looks like.
        if (window.ProcessName is { } name && _options.ExcludedProcesses.Contains(name))
        {
            return new ScopeDecision(CaptureScope.Excluded, null, null, $"Not capturing: {process} is on your excluded list.");
        }

        // An elevated window cannot be captured whatever the policy says: Windows hides it from us. Saying
        // "in scope" here would promise frames that never arrive (Spec §5 S2).
        if (window.IsElevated)
        {
            return new ScopeDecision(CaptureScope.OutOfScope, null, null, "Elevated window: screen not captured.");
        }

        // The desktop, the taskbar and Task View. The desktop's rectangle is every monitor, so a frame of it
        // is a frame of every window in front of it, excluded ones included (2026-10-12 review, S-2). Refused
        // before any match and whatever "all windows" says; clicks are still logged, as for any out-of-scope
        // window. A shell window whose class could not be read is refused too: nothing shows it is not the desktop.
        if (ShellWindows.IsShell(window))
        {
            return NotCapturing(process);
        }

        if (registry.MatchTool(window) is { } tool)
        {
            return _options.DisabledTools.Contains(tool.Id)
                ? new ScopeDecision(CaptureScope.OutOfScope, tool.Id, tool.Kind, $"Not capturing: {tool.DisplayName} is switched off in Settings.")
                : new ScopeDecision(CaptureScope.RemoteTool, tool.Id, tool.Kind, $"Capturing: {tool.DisplayName}", window.Handle);
        }

        if (registry.MatchBrowser(window) is { } browser)
        {
            return _options.DisabledTools.Contains(browser.Id)
                ? new ScopeDecision(CaptureScope.OutOfScope, browser.Id, RemoteToolKind.Browser, $"Not capturing: {browser.DisplayName} is switched off in Settings.")
                : new ScopeDecision(CaptureScope.RemoteTool, browser.Id, RemoteToolKind.Browser, $"Capturing: {browser.DisplayName}", window.Handle);
        }

        if (registry.MatchAdminTool(window) is { } admin)
        {
            return new ScopeDecision(CaptureScope.AdminTool, admin.Id, null, $"Capturing: {admin.DisplayName}", window.Handle);
        }

        if (_options.CaptureAllWindows)
        {
            return new ScopeDecision(CaptureScope.AdminTool, null, null, $"Capturing: {process} (all windows)", window.Handle);
        }

        return NotCapturing(process);
    }

    private static ScopeDecision NotCapturing(string process) =>
        new(CaptureScope.OutOfScope, null, null, $"Not capturing: {process}");
}
