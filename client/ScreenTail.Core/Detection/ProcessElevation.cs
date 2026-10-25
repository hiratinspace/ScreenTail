namespace ScreenTail.Core.Detection;

/// <summary>
/// Whether a window's process counts as elevated, from its token's mandatory integrity level (ST-022, INV-6).
///
/// Elevation used to be inferred from failing to open the process. A process the technician elevated runs
/// as the same user and opens without complaint, so an administrator's regedit, mmc or PowerShell was
/// judged an ordinary admin tool and photographed, while INV-6 and the HUD say an elevated window is not.
/// The integrity level is the answer Windows itself uses to keep a lower process away from a higher one.
/// Kept apart from the Windows plumbing so the rule is testable without a desktop.
/// </summary>
public static class ProcessElevation
{
    /// <summary>The standard user's level (SECURITY_MANDATORY_MEDIUM_RID).</summary>
    public const int Medium = 0x2000;

    /// <summary>An elevated administrator's level (SECURITY_MANDATORY_HIGH_RID). At or above this is elevated.</summary>
    public const int High = 0x3000;

    /// <summary>A service running as the machine (SECURITY_MANDATORY_SYSTEM_RID).</summary>
    public const int System = 0x4000;

    /// <summary>
    /// Whether a window of this process is treated as elevated.
    /// </summary>
    /// <param name="target">The process's integrity level, or null when it could not be read.</param>
    /// <param name="own">This process's integrity level, or null when it could not be read.</param>
    /// <param name="isOwnProcess">The window belongs to this process.</param>
    public static bool IsElevated(int? target, int? own, bool isOwnProcess)
    {
        // This process's windows are the one thing known not to be someone else's elevated tool. The
        // service owns none in production; a test host that runs elevated does, and judging its own
        // windows against themselves would say nothing true.
        if (isOwnProcess)
        {
            return false;
        }

        // A level that cannot be read is not evidence of an ordinary window. Blind is the closed answer.
        if (target is not { } level)
        {
            return true;
        }

        // Above this process is elevated whatever this process is: its input and its pixels are not this
        // process's to take. And an administrator's level is elevated even for a host that is itself
        // elevated, because INV-6 is about the window, not about who is looking at it.
        return level >= High || (own is { } mine && level > mine);
    }
}
