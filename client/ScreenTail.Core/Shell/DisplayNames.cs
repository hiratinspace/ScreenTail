using System.Globalization;

namespace ScreenTail.Core.Shell;

/// <summary>
/// The remote tool a session was captured in, by the name a technician knows it by (Spec §4, plain
/// language). The service sends the registry's kind (<c>screenconnect</c>, <c>rdp</c>); History, the
/// shell's status line and the tray show "ScreenConnect" and "Remote Desktop". The names are the
/// registry's own <c>display_name</c>s, held to it by a test, so a tool is never called two things.
/// </summary>
public static class ToolNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["screenconnect"] = "ScreenConnect",
        ["rdp"] = "Remote Desktop",
        ["splashtop"] = "Splashtop",
        ["teamviewer"] = "TeamViewer",
        ["anydesk"] = "AnyDesk",
        ["browser"] = "Browser",

        // A session started by hand over a window no registry entry knows (Ctrl+Alt+R).
        ["other"] = "Other window",
    };

    /// <summary>The tool's name, or its id in title case for one this version does not know, or "Unknown".</summary>
    public static string Display(string? tool) =>
        string.IsNullOrWhiteSpace(tool) ? "Unknown"
        : Known.TryGetValue(tool, out var name) ? name
        : Names.TitleCase(tool);
}

/// <summary>
/// A process by the name its product goes by: "Outlook" for <c>OUTLOOK</c>. Still the process name and
/// nothing more (INV-10: never a window title); only the spelling changes. Common Office and Windows
/// programs have their product's name; anything else is title-cased when it is all one case, and kept as
/// it is when it has a spelling of its own (<c>SRManager</c>).
///
/// No product name reaches the UI: the capture state carries the process name alone
/// (<c>scope_process</c>), which is what INV-10 allows on the pill, so the name is made from it here.
/// </summary>
public static class ProcessNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["outlook"] = "Outlook",
        ["olk"] = "Outlook",
        ["winword"] = "Word",
        ["excel"] = "Excel",
        ["powerpnt"] = "PowerPoint",
        ["onenote"] = "OneNote",
        ["msedge"] = "Microsoft Edge",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["ms-teams"] = "Teams",
        ["teams"] = "Teams",
        ["explorer"] = "File Explorer",
        ["mmc"] = "Management Console",
        ["cmd"] = "Command Prompt",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell",
        ["windowsterminal"] = "Windows Terminal",
        ["taskmgr"] = "Task Manager",
        ["regedit"] = "Registry Editor",
        ["notepad"] = "Notepad",
    };

    public static string Friendly(string process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var name = process.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (name.Length == 0)
        {
            return process;
        }

        if (Known.TryGetValue(name, out var known))
        {
            return known;
        }

        var oneCase = !name.Any(char.IsUpper) || !name.Any(char.IsLower);
        return oneCase ? Names.TitleCase(name) : name;
    }
}

internal static class Names
{
    private static readonly char[] Separators = ['-', '_', '.', ' '];

    /// <summary>"vnc-viewer" → "Vnc Viewer": each word capitalised, separators as spaces.</summary>
    public static string TitleCase(string text) =>
        string.Join(' ', text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..].ToLower(CultureInfo.InvariantCulture)));
}
