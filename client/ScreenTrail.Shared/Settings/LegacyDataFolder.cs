namespace ScreenTrail.Shared.Settings;

/// <summary>What <see cref="LegacyDataFolder.Migrate"/> did. A fact for the log: no path, no file name.</summary>
public enum LegacyDataMigration
{
    /// <summary>There was no folder under the old name.</summary>
    NothingToMove,

    /// <summary>The old folder became the new one, whole, in one rename.</summary>
    Moved,

    /// <summary>Both existed: what the new folder lacked was moved in from the old one.</summary>
    Merged,

    /// <summary>Something under the old name could not be moved. It is left where it was, never deleted.</summary>
    LeftBehind,

    /// <summary>The old folder is a link (a junction or a symbolic link), so it is not followed or moved.</summary>
    LeftLinked,
}

/// <summary>
/// The product was called ScreenTail until 2026-11-17, and kept its data under
/// <c>%LOCALAPPDATA%\ScreenTail</c>: the encrypted store and its key, the device's credential, the pipe
/// token, the speech models, shell.json, crash reports and audit exports. It now keeps them under
/// <c>%LOCALAPPDATA%\ScreenTrail</c>. Both the service and the window call this first thing, before
/// either opens anything there, so whichever starts first after the upgrade brings the data across.
///
/// The store's key and the device's credential are protected with DPAPI for the Windows user, not for a
/// path, so they open the same after the move. The move is a rename within one folder of the user's own
/// profile, made with the user's own rights. No file is ever deleted: anything that cannot be moved
/// stays under the old name (only an old folder left empty is removed), and uninstall removes both.
/// </summary>
public static class LegacyDataFolder
{
    public const string OldName = "ScreenTail";

    public const string NewName = "ScreenTrail";

    /// <summary>Moves the old folder under the user's local app data to the new name, if there is one.</summary>
    public static LegacyDataMigration Migrate() =>
        Migrate(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <param name="localAppData">The folder both names live in: the user's local app data.</param>
    public static LegacyDataMigration Migrate(string localAppData)
    {
        if (string.IsNullOrEmpty(localAppData))
        {
            return LegacyDataMigration.NothingToMove;
        }

        var old = new DirectoryInfo(Path.Combine(localAppData, OldName));
        var current = Path.Combine(localAppData, NewName);
        try
        {
            if (!old.Exists)
            {
                return LegacyDataMigration.NothingToMove;
            }

            if (old.LinkTarget is not null || old.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return LegacyDataMigration.LeftLinked;
            }

            if (!Directory.Exists(current))
            {
                old.MoveTo(current);
                return LegacyDataMigration.Moved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process may have moved it a moment ago; if the old folder is gone, that is done.
            return Directory.Exists(old.FullName) ? LegacyDataMigration.LeftBehind : LegacyDataMigration.Moved;
        }

        return Merge(old, current);
    }

    /// <summary>
    /// The new folder already exists, made by whichever process started first or by a version that ran
    /// before this one moved anything. What it already has is newer and stays; the rest comes across.
    /// </summary>
    private static LegacyDataMigration Merge(DirectoryInfo old, string current)
    {
        var leftBehind = false;
        FileSystemInfo[] entries;
        try
        {
            entries = old.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LegacyDataMigration.LeftBehind;
        }

        foreach (var entry in entries)
        {
            var target = Path.Combine(current, entry.Name);
            try
            {
                if (File.Exists(target) || Directory.Exists(target) || entry.LinkTarget is not null)
                {
                    leftBehind = true;
                    continue;
                }

                if (entry is DirectoryInfo directory)
                {
                    directory.MoveTo(target);
                }
                else
                {
                    ((FileInfo)entry).MoveTo(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftBehind = true;
            }
        }

        if (!leftBehind)
        {
            try
            {
                old.Delete(recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftBehind = true;
            }
        }

        return leftBehind ? LegacyDataMigration.LeftBehind : LegacyDataMigration.Merged;
    }
}
