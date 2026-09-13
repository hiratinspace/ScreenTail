namespace ScreenTail.Core.Detection;

/// <summary>
/// Which processes are browsers, and how to get the active tab's title out of a window title (ST-022).
/// ST-023 matches on the result to decide whether a browser tab is a remote-tool session.
/// </summary>
public static class BrowserTitles
{
    /// <summary>Process names (no extension, lower case) whose window title carries the active tab.</summary>
    private static readonly Dictionary<string, string[]> Suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = [" - Google Chrome"],
        ["msedge"] = [" - Microsoft​ Edge", " - Microsoft Edge"],
        ["firefox"] = [" — Mozilla Firefox", " - Mozilla Firefox"],
        ["brave"] = [" - Brave"],
        ["opera"] = [" - Opera"],
        ["vivaldi"] = [" - Vivaldi"],
    };

    /// <summary>Noise browsers append when a tab is doing something, which isn't part of the tab's name.</summary>
    private static readonly string[] Prefixes = ["▶ ", "● ", "🔊 "];

    public static bool IsBrowser(string? processName) => processName is not null && Suffixes.ContainsKey(processName);

    /// <summary>
    /// The active tab's title, or null when this isn't a browser or the title tells us nothing. Windows gives
    /// the tab title for free in the window title, and taking it from there costs nothing; reading it through
    /// UI Automation would make Chrome build its accessibility tree, which slows the browser down for the
    /// user we're trying to help. ST-043 needs the URL, and that will need UIA.
    /// </summary>
    public static string? ActiveTab(string? processName, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!IsBrowser(processName) || title.Length == 0)
        {
            return null;
        }

        foreach (var suffix in Suffixes[processName!])
        {
            if (title.EndsWith(suffix, StringComparison.Ordinal))
            {
                var tab = title[..^suffix.Length];
                foreach (var prefix in Prefixes)
                {
                    if (tab.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        tab = tab[prefix.Length..];
                    }
                }

                tab = tab.Trim();

                // A window with no tab open is just the browser's own name; there's no tab title to report.
                return tab.Length == 0 ? null : tab;
            }
        }

        // Some windows (downloads, settings, a popup) don't carry the suffix. The title is still the best
        // description of what's in front of the technician, so it's reported as-is rather than dropped.
        return title.Trim() is { Length: > 0 } trimmed ? trimmed : null;
    }
}
