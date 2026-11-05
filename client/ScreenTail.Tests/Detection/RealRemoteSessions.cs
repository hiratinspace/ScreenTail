using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;

namespace ScreenTail.Tests;

/// <summary>
/// Whether a machine is showing a real remote-support session right now, read off its visible top-level
/// windows (ST-112). The Windows stop-time test runs the real capture service, recording, and must not do
/// that on a laptop where a real session could come to the front, so it asks this first and refuses.
///
/// Shared by both test projects: the Mac suite holds the rule (RealRemoteSessionsTests), and the Windows
/// suite links this file and feeds it the desktop's windows.
///
/// Stricter than the service on purpose. A window counts when the registry takes it for a remote tool (its
/// process or its window class), and a browser counts when its title matches a browser entry's title
/// pattern alone: the service also wants the tab's address, which nothing reads yet, but a check that
/// keeps a test away from real sessions should err towards refusing. A background agent with no visible
/// window (a remote tool's service, WSL's msrdc with no app open) does not count. The test's own process
/// is left out, since its synthetic window is made to look like a remote tool.
/// </summary>
internal static class RealRemoteSessions
{
    /// <summary>How many of <paramref name="windows"/> look like a real remote-support session. A count, never a title.</summary>
    public static int Count(RemoteToolRegistry registry, IEnumerable<ForegroundWindowInfo> windows, int ownProcessId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(windows);
        return windows.Count(window =>
            !window.IsNone
            && window.ProcessId != ownProcessId
            && (registry.MatchTool(window) is not null || BrowserTitleMatches(registry, window)));
    }

    private static bool BrowserTitleMatches(RemoteToolRegistry registry, ForegroundWindowInfo window)
    {
        if (!BrowserTitles.IsBrowser(window.ProcessName))
        {
            return false;
        }

        var title = window.BrowserTabTitle ?? window.Title;
        return title.Length > 0 && registry.BrowserPatterns.Any(pattern =>
        {
            try
            {
                return pattern.CompiledTitle.IsMatch(title);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // A title that takes the pattern this long to read is not one to take a chance on.
                return true;
            }
        });
    }
}
