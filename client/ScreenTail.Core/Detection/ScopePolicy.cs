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
}

/// <param name="Scope">What the timeline records for this window.</param>
/// <param name="ToolId">The registry entry that matched, when one did.</param>
/// <param name="Reason">The HUD's words for why (Spec §5 S2). Carries a process name, never window content.</param>
public sealed record ScopeDecision(CaptureScope Scope, string? ToolId, RemoteToolKind? Tool, string Reason)
{
    /// <summary>Whether a screenshot may be taken. Clicks are still recorded out of scope; frames are not.</summary>
    public bool MayCaptureFrames => Scope is CaptureScope.RemoteTool or CaptureScope.AdminTool;
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
    private readonly ScopeOptions _options = options ?? new ScopeOptions();

    public RemoteToolRegistry Registry => registry;

    public ScopeDecision Decide(ForegroundWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.IsNone)
        {
            return new ScopeDecision(CaptureScope.OutOfScope, null, null, "Nothing in the foreground.");
        }

        var process = window.ProcessName ?? "an unknown app";

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
            return new ScopeDecision(CaptureScope.RemoteTool, tool.Id, tool.Kind, $"Capturing — {tool.DisplayName}");
        }

        if (registry.MatchBrowser(window) is { } browser)
        {
            return new ScopeDecision(CaptureScope.RemoteTool, browser.Id, RemoteToolKind.Browser, $"Capturing — {browser.DisplayName}");
        }

        if (registry.MatchAdminTool(window) is { } admin)
        {
            return new ScopeDecision(CaptureScope.AdminTool, admin.Id, null, $"Capturing — {admin.DisplayName}");
        }

        if (_options.CaptureAllWindows)
        {
            return new ScopeDecision(CaptureScope.AdminTool, null, null, $"Capturing — {process} (all windows)");
        }

        return new ScopeDecision(CaptureScope.OutOfScope, null, null, $"Not capturing — {process}");
    }
}
