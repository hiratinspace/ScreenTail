namespace ScreenTail.Core.Install;

/// <summary>What a folder on the way to a data folder turned out to be, asked without following it.</summary>
public enum PathState
{
    Missing,
    Folder,

    /// <summary>A junction, a symbolic link or any other reparse point.</summary>
    Link,
}

/// <summary>What the uninstall does with one profile's ScreenTail data folder (ADR-0007).</summary>
public enum DataFolderAction
{
    /// <summary>Deleted, with the rights of the signed-in user who owns the profile and nobody else's.</summary>
    DeleteAsOwner,

    /// <summary>There is nothing to delete.</summary>
    Absent,

    /// <summary>
    /// The owner is not signed in, so there is no token to delete the folder with. The system account
    /// removes only <see cref="InstalledFootprint.Secrets"/>, each through a handle that refuses links,
    /// which leaves the rest of the store unreadable; the folder and the rest stay.
    /// </summary>
    ShredSecrets,

    /// <summary>Left: a folder between the profile and the data folder, or the data folder, is a link.</summary>
    LeaveLinked,

    /// <summary>Left: the profile path is not a plain local folder, so nothing is worked out from it.</summary>
    LeaveUnreadablePath,
}

/// <param name="Sid">The profile owner's security identifier, as Windows lists the profile.</param>
/// <param name="Profile">The profile folder, or null when its path could not be read plainly.</param>
/// <param name="Folder">The data folder under it, or null with the profile.</param>
public sealed record DataFolderDecision(string Sid, string? Profile, string? Folder, DataFolderAction Action);
