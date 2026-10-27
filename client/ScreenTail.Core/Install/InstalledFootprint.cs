using System.Buffers;

namespace ScreenTail.Core.Install;

/// <summary>
/// What an installed ScreenTail puts on a machine outside its own folder, and how the uninstall finds it
/// again (ST-112, ADR-0007).
///
/// The installer is per machine. Its uninstall runs once, as the system account, and must leave nothing
/// behind (INV-12): every technician's data folder under their local app data, and every Run value the UI
/// wrote for "Start UI at login". It cannot ask Windows for "the" user's folders, because there is no
/// one user; it reads every profile and works the paths out itself.
///
/// The paths are Windows paths handled as text, so the rules hold on any machine the tests run on. And
/// the rule for anything the uninstall will delete is that it is never a guess: a path this cannot read
/// plainly (unexpanded, relative, on another machine, climbing out with "..") is skipped, not resolved.
/// </summary>
public static class InstalledFootprint
{
    public const string ServiceExecutable = "ScreenTail.Service.exe";

    public const string UiExecutable = "ScreenTail.UI.exe";

    /// <summary>The Run key, under HKLM for the installed service and under HKCU for the UI's own setting.</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// The machine Run value that starts the service at sign-in. The same name as the development helper
    /// (LoginStartup.ServiceValueName), so a developer's copy and an installed one are never two entries
    /// that look unrelated.
    /// </summary>
    public const string ServiceRunValue = "ScreenTail.Service";

    /// <summary>
    /// The service's uninstall mode. Run by the installer as the system account, before the installed
    /// files are removed, and only for an uninstall: never during an upgrade.
    /// </summary>
    public const string UninstallFlag = "--uninstall-cleanup";

    /// <summary>Under each profile, the folder every ScreenTail process keeps its data in.</summary>
    private const string DataUnderProfile = @"AppData\Local\ScreenTail";

    private static readonly SearchValues<char> NotInAPath = SearchValues.Create("\"<>|*?:%");

    /// <summary>
    /// Each profile's ScreenTail data folder, once each, for the profile folders Windows lists. A profile
    /// path that is not a plain local folder is skipped.
    /// </summary>
    public static IReadOnlyList<string> DataDirectories(IEnumerable<string?> profileDirectories)
    {
        ArgumentNullException.ThrowIfNull(profileDirectories);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        foreach (var profile in profileDirectories)
        {
            if (Normalise(profile) is { } folder && seen.Add(folder))
            {
                directories.Add(folder + @"\" + DataUnderProfile);
            }
        }

        return directories;
    }

    /// <summary>
    /// The executable a Run value starts: the quoted path when there is one, otherwise everything up to
    /// and including ".exe", otherwise the first word. Null when there is nothing to read.
    /// </summary>
    public static string? ExecutableOf(string? command)
    {
        var text = command?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }

        // Unquoted, which Windows allows and reads left to right until something exists. ".exe" followed
        // by the end or a space is where a Run value's executable ends in practice.
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (exe >= 0)
        {
            var end = exe + ".exe".Length;
            if (end == text.Length || char.IsWhiteSpace(text[end]))
            {
                return text[..end];
            }

            exe = text.IndexOf(".exe", end, StringComparison.OrdinalIgnoreCase);
        }

        var space = text.AsSpan().IndexOfAny(' ', '\t');
        return space < 0 ? text : text[..space];
    }

    /// <summary>
    /// Whether <paramref name="path"/> is strictly inside <paramref name="installDirectory"/>, the way
    /// Windows compares paths. The folder itself is not inside itself, and a sibling whose name starts the
    /// same way (ScreenTail.Dev beside ScreenTail) is somebody else's.
    /// </summary>
    public static bool IsInside(string? path, string installDirectory)
    {
        if (Normalise(path) is not { } candidate || Normalise(installDirectory) is not { } folder)
        {
            return false;
        }

        return candidate.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The names of the Run values that start something from this installation, whatever they are called.
    /// Values pointing anywhere else, including a developer's build of ScreenTail, are not ours to remove.
    /// </summary>
    public static IReadOnlyList<string> RunValuesToRemove(IEnumerable<KeyValuePair<string, string?>> values, string installDirectory)
    {
        ArgumentNullException.ThrowIfNull(values);

        return
        [
            .. values
                .Where(value => IsInside(ExecutableOf(value.Value), installDirectory))
                .Select(value => value.Key),
        ];
    }

    /// <summary>
    /// A drive-rooted local path with at least one folder below the drive, with separators made uniform
    /// and no trailing one; or null when the path is anything else. Nothing here resolves a path: a "." or
    /// ".." segment, a colon past the drive (an alternate data stream), an environment variable or a
    /// quote makes the whole path unusable rather than something to interpret.
    /// </summary>
    private static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var text = path.Trim().Replace('/', '\\');
        if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\')
        {
            return null;
        }

        var segments = text[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.AsSpan().IndexOfAny(NotInAPath) >= 0 || segment.Any(char.IsControl))
            {
                return null;
            }
        }

        return text[..2] + @"\" + string.Join('\\', segments);
    }
}
