using ScreenTail.Core.Detection.Registry;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Detection;

public sealed record ScopeOptions
{
    /// <summary>
    /// INV-5: the default is remote-tool windows plus the admin-tool allowlist. "All windows" is opt-in and
    /// confirmed in Settings — a technician turns it on knowing their own inbox is now fair game.
    /// </summary>
    public bool CaptureAllWindows { get; init; }

    /// <summary>Processes the technician has excluded outright (ST-043). Beats everything else.</summary>
    public IReadOnlySet<string> ExcludedProcesses { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The shipped and tenant-synced exclusions: password managers, banking tabs, credential prompts
    /// (ST-043). Null means none are loaded, which is a misconfiguration rather than a policy — the
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
    /// <summary>Whether a screenshot may be taken. Clicks are still recorded out of scope; frames are not.</summary>
    public bool MayCaptureFrames => Scope is CaptureScope.RemoteTool or CaptureScope.AdminTool;

    /// <summary>
    /// Whether this event may be written while the window in front is the one this decision is about.
    ///
    /// INV-6: "excluded app, elevated window, out-of-scope → no frames, <b>no typing events written</b>."
    /// Anything keyboard-derived is dropped out of scope — a burst count, a shortcut and an Enter all say
    /// something about what was typed, and Ctrl+C in a customer's password manager is the case that
    /// matters.
    ///
    /// Clicks are the deliberate exception, and ST-023's own criterion asks for it: "switching to Outlook
    /// logs clicks but captures no frames". A click out of scope records that the technician went
    /// somewhere and did something, with no picture and nothing of what they typed, which is what makes
    /// "clicks logged, no frames" visible in Review instead of a silent gap. That is the line INV-6 draws,
    /// and it is drawn between a keystroke and a mouse button rather than between an event and no event.
    /// </summary>
    public bool MayRecord(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        return MayCaptureFrames || sessionEvent is not (TypingBurstEvent or ShortcutEvent or EnterEvent);
    }
}

/// <summary>
/// Decides what may be captured from the window in front (ST-023, INV-5).
///
/// Runs on every foreground change, so it answers from a dictionary where it can and only falls back to
/// patterns when it must. The order matters and is the whole of the invariant: an exclusion wins over
/// everything, a remote tool is in scope, an admin tool is in scope *because* it sits beside a session, and
/// everything else is out of scope — clicks still logged so the timeline stays honest about what the
/// technician did, but no frames.
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
        // window might otherwise be in scope — a password manager opened during a support session is
        // still support work, and still must not be photographed (ST-043).
        if (_options.Exclusions?.Match(window) is { } excluded)
        {
            return new ScopeDecision(CaptureScope.Excluded, excluded.Id, null, $"Not capturing — {excluded.DisplayName} is excluded.");
        }

        // A window the technician excluded is never captured, whatever else it looks like.
        if (window.ProcessName is { } name && _options.ExcludedProcesses.Contains(name))
        {
            return new ScopeDecision(CaptureScope.Excluded, null, null, $"Not capturing — {process} is on your excluded list.");
        }

        // An elevated window cannot be captured whatever the policy says: Windows hides it from us. Saying
        // "in scope" here would promise frames that never arrive (Spec §5 S2).
        if (window.IsElevated)
        {
            return new ScopeDecision(CaptureScope.OutOfScope, null, null, "Elevated window — screen not captured.");
        }

        if (registry.MatchTool(window) is { } tool)
        {
            return new ScopeDecision(CaptureScope.RemoteTool, tool.Id, tool.Kind, $"Capturing — {tool.DisplayName}", window.Handle);
        }

        if (registry.MatchBrowser(window) is { } browser)
        {
            return new ScopeDecision(CaptureScope.RemoteTool, browser.Id, RemoteToolKind.Browser, $"Capturing — {browser.DisplayName}", window.Handle);
        }

        if (registry.MatchAdminTool(window) is { } admin)
        {
            return new ScopeDecision(CaptureScope.AdminTool, admin.Id, null, $"Capturing — {admin.DisplayName}", window.Handle);
        }

        if (_options.CaptureAllWindows)
        {
            return new ScopeDecision(CaptureScope.AdminTool, null, null, $"Capturing — {process} (all windows)", window.Handle);
        }

        return new ScopeDecision(CaptureScope.OutOfScope, null, null, $"Not capturing — {process}");
    }
}
