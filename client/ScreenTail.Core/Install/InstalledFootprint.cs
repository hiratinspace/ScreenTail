using System.Buffers;

namespace ScreenTail.Core.Install;

/// <summary>
/// What an installed ScreenTail puts on a machine outside its own folder, and how the uninstall finds it
/// again (ST-112, ADR-0007).
///
/// The installer is per machine. Its uninstall runs once, as the system account, and clears what each
/// technician's ScreenTail made outside the install folder: their data folder under their local app data
/// and any Run value the UI wrote for "Start UI at login". It cannot ask Windows for "the" user's folders,
/// because there is no one user; it reads every profile and works the paths out itself. Which folders it
/// may delete, and with whose rights, is <see cref="PlanDataRemoval"/>'s decision.
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

    /// <summary>
    /// The service's upgrade mode: when an upgrade removes this version, the installer runs it as the
    /// system account just before the installed files are removed. It stops running copies and waits
    /// until no other program has an installed file open, and touches nothing else: no user's data, no
    /// Run value, no capture.
    /// </summary>
    public const string UpgradeReleaseFlag = "--upgrade-release";

    /// <summary>Under each profile, the folder every ScreenTail process keeps its data in.</summary>
    private const string DataUnderProfile = @"AppData\Local\ScreenTail";

    /// <summary>
    /// The data folder below a profile, one folder at a time, for a removal that opens each relative to
    /// the one before. Windows' default local app data: a technician whose Local AppData is redirected
    /// somewhere else by policy (Windows does not support that, but it happens) is not found here.
    /// </summary>
    public static IReadOnlyList<string> DataSegments { get; } = DataUnderProfile.Split('\\');

    /// <summary>
    /// What is removed from a data folder whose owner is not signed in: Delete everything's own secrets
    /// (LocalDataEraser), the store's key first, then the device's refresh token and the file a save
    /// writes before it, then the pipe token. Without the key, the store that stays cannot be read.
    /// </summary>
    public static IReadOnlyList<string> Secrets { get; } = ["store.key", "device.cred", "device.cred.tmp", "ipc.token"];

    private static readonly SearchValues<char> NotInAPath = SearchValues.Create("\"<>|*?:%");

    /// <summary>
    /// What the uninstall does with each profile's data folder. The uninstall runs as the system account
    /// and every folder under a profile is its owner's to change, so the system account deletes nothing
    /// there on its own account: a folder is deleted only for a user who is signed in, with that user's
    /// token, so whatever the folder turns out to point at, the delete has no more rights than its owner.
    /// A link anywhere from just below the profile down to the data folder leaves it alone, and so does a
    /// profile path that cannot be read plainly. Everything left is reported, never silently skipped.
    /// </summary>
    /// <param name="profiles">Each profile's owner (a security identifier) and folder, as Windows lists them.</param>
    /// <param name="signedIn">The owners who have a session on the machine now, whose tokens the uninstall holds.</param>
    /// <param name="probe">What a folder is, asked without following it.</param>
    public static IReadOnlyList<DataFolderDecision> PlanDataRemoval(
        IEnumerable<KeyValuePair<string, string?>> profiles,
        IReadOnlySet<string> signedIn,
        Func<string, PathState> probe)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(signedIn);
        ArgumentNullException.ThrowIfNull(probe);

        var decisions = new List<DataFolderDecision>();
        foreach (var (sid, path) in profiles)
        {
            if (Normalise(path) is not { } profile)
            {
                decisions.Add(new DataFolderDecision(sid, null, null, DataFolderAction.LeaveUnreadablePath));
                continue;
            }

            var below = BelowProfile(profile);
            var folder = below[^1];
            var states = below.Select(probe).ToList();
            var action = states.Contains(PathState.Link) ? DataFolderAction.LeaveLinked
                : states[^1] == PathState.Missing ? DataFolderAction.Absent
                : !signedIn.Contains(sid) ? DataFolderAction.ShredSecrets
                : DataFolderAction.DeleteAsOwner;
            decisions.Add(new DataFolderDecision(sid, profile, folder, action));
        }

        return decisions;
    }

    /// <summary>
    /// The folders from just below the profile down to the data folder: the ones a user could replace
    /// with a link. The profile folder itself is Windows' own, in a folder only administrators change.
    /// </summary>
    public static IReadOnlyList<string> BelowProfile(string profile)
    {
        var root = Normalise(profile) ?? throw new ArgumentException("Not a plain local folder.", nameof(profile));
        var folders = new List<string>();
        var current = root;
        foreach (var segment in DataUnderProfile.Split('\\'))
        {
            current += @"\" + segment;
            folders.Add(current);
        }

        return folders;
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
