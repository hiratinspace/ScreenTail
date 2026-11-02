namespace ScreenTail.Core.Lifecycle;

/// <summary>The token's elevation type (TOKEN_ELEVATION_TYPE), or Unknown when it could not be read.</summary>
public enum TokenElevation
{
    /// <summary>Not read. Treated as unfit: blind is the closed answer.</summary>
    Unknown = 0,

    /// <summary>TokenElevationTypeDefault: no split token. A standard user, or any user with UAC off.</summary>
    Default = 1,

    /// <summary>TokenElevationTypeFull: the elevated half of an administrator's split token ("Run as administrator").</summary>
    Full = 2,

    /// <summary>TokenElevationTypeLimited: the ordinary half of an administrator's split token, as the Run key starts it.</summary>
    Limited = 3,
}

/// <summary>What the service knows about itself at start, read from its token and its session.</summary>
/// <param name="Elevation">The token's elevation type.</param>
/// <param name="SessionId">The Terminal Services session the process runs in.</param>
/// <param name="User">Whether the token's user is the one signed in to that session (<see cref="SessionUser"/>).</param>
public readonly record struct StartFacts(TokenElevation Elevation, uint SessionId, UserMatch User);

/// <summary>Why the capture service will not run as this process.</summary>
public enum StartRefusal
{
    /// <summary>Fit to run.</summary>
    None,

    /// <summary>Session 0: no desktop, and not a technician's session.</summary>
    SessionZero,

    /// <summary>An administrator's elevated token, or one whose elevation could not be read.</summary>
    Elevated,

    /// <summary>Definitely not the user signed in to this session.</summary>
    NotTheSessionsUser,
}

/// <summary>
/// Whether this process may be the capture service (ST-112, INV-6). Checked at every start, right after the
/// single-instance name is taken and before anything is built; a refusal is logged and the process exits 0.
///
/// The service judges a window elevated by comparing its integrity level with its own
/// (<c>ProcessElevation</c>): above itself is elevated. Run from an administrator's elevated token, the
/// service itself is High, so regedit, mmc or an elevated PowerShell are no longer above it and would be
/// photographed, with the same user's UI attached and an automatic session recording under the pill. That
/// happens if somebody runs the service as administrator, and when Restart Manager restarts it from another
/// program's elevated installer, because a restarted program gets the token of the process that restarts
/// it. So:
///
/// <list type="bullet">
/// <item>an elevated split token (<see cref="TokenElevation.Full"/>) is refused, and one that cannot be read;</item>
/// <item><see cref="TokenElevation.Default"/> is not: that is a standard user, or UAC switched off, where every
/// process of the technician's runs at the same level and <c>ProcessElevation</c> already judges windows
/// against that;</item>
/// <item>a process whose user is definitely not the one signed in to its session is refused (an installer's
/// own account, another user's credentials). One that cannot tell runs: a check that could not read the
/// session would otherwise refuse the service at every sign-in (<see cref="SessionUser"/>);</item>
/// <item>session 0 is refused: it has no desktop and no technician.</item>
/// </list>
///
/// The next sign-in starts the service from the Run key, as the technician, and it runs then.
/// </summary>
public static class StartGuard
{
    public static StartRefusal Decide(StartFacts facts)
    {
        if (facts.SessionId == 0)
        {
            return StartRefusal.SessionZero;
        }

        if (facts.Elevation is TokenElevation.Full or TokenElevation.Unknown)
        {
            return StartRefusal.Elevated;
        }

        if (facts.User == UserMatch.Different)
        {
            return StartRefusal.NotTheSessionsUser;
        }

        return StartRefusal.None;
    }

    /// <summary>The fixed sentence the service logs for a refusal: no user, no SID, no path (INV-10).</summary>
    public static string Explain(StartRefusal refusal) => refusal switch
    {
        StartRefusal.SessionZero => "The capture service does not run in session 0; it starts at the technician's sign-in. Exiting.",
        StartRefusal.Elevated => "The capture service does not run elevated (as administrator, or restarted by an elevated installer): elevated windows would no longer be told apart. It starts at the next sign-in. Exiting.",
        StartRefusal.NotTheSessionsUser => "The capture service runs only as the user signed in to its session. It starts at that user's sign-in. Exiting.",
        _ => string.Empty,
    };

    /// <summary>The UI's sentence for a refusal, as fixed as the service's.</summary>
    public static string ExplainForUi(StartRefusal refusal) => refusal switch
    {
        StartRefusal.SessionZero => "ScreenTail's window does not run in session 0. Exiting.",
        StartRefusal.Elevated => "ScreenTail's window does not run elevated (as administrator, or restarted by an elevated installer). Start it from the Start menu. Exiting.",
        StartRefusal.NotTheSessionsUser => "ScreenTail's window runs only as the user signed in to its session. Exiting.",
        _ => string.Empty,
    };

    /// <summary>
    /// The Windows Application log's event id for a refusal, so one that happened where nobody reads standard
    /// error (an installed build) can be found: 1100 and up for the service, 1200 and up for the UI.
    /// </summary>
    public static int EventId(StartRefusal refusal, bool ui) => (ui ? 1200 : 1100) + (int)refusal;
}
