namespace ScreenTail.Core.Detection;

/// <summary>
/// The name a process is reported under, from the full path of its image (ST-022).
///
/// Usually the file name without its extension. The exception is WSLg, which shows Linux GUI apps through
/// its own copy of the Remote Desktop client: an msrdc.exe installed with WSL, drawing RAIL_WINDOW windows
/// exactly as a RemoteApp does. Reported as msrdc it was a remote-desktop session, so opening a Linux
/// terminal started one, and no exclusion could name it without also naming Azure Virtual Desktop and
/// Windows 365, which use the msrdc.exe that ships with the Remote Desktop client. Reported as
/// <see cref="Wslg"/>, it is an ordinary local app: out of scope unless the technician says otherwise,
/// and excludable by that name. Kept apart from the Windows plumbing so the rule is testable without a desktop.
/// </summary>
public static class ProcessImage
{
    /// <summary>The name WSLg's copy of msrdc is reported under.</summary>
    public const string Wslg = "wslg";

    /// <summary>
    /// Where WSL keeps its own files: the Store package's install folder, its package name under
    /// WindowsApps, and the inbox component's folder. Compared without regard to case, as Windows does.
    /// </summary>
    private static readonly string[] WslFolders =
    [
        @"\Program Files\WSL\",
        @"\MicrosoftCorporationII.WindowsSubsystemForLinux",
        @"\lxss\",
    ];

    public static string NameFor(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);

        // Split by hand rather than with Path, whose separators are the host's: this runs against Windows
        // paths, and the rule is tested on machines where a backslash is just a character.
        var file = imagePath[(imagePath.LastIndexOfAny(['\\', '/']) + 1)..];
        var dot = file.LastIndexOf('.');
        var name = dot > 0 ? file[..dot] : file;

        return string.Equals(name, "msrdc", StringComparison.OrdinalIgnoreCase) && IsUnderWsl(imagePath)
            ? Wslg
            : name;
    }

    private static bool IsUnderWsl(string imagePath)
    {
        var path = imagePath.Replace('/', '\\');
        foreach (var folder in WslFolders)
        {
            if (path.Contains(folder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
