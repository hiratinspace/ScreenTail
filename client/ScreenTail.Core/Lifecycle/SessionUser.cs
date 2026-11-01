namespace ScreenTail.Core.Lifecycle;

/// <summary>Whether a process's user is the one signed in to its session.</summary>
public enum UserMatch
{
    /// <summary>Nothing could be read to compare. Not a refusal: only a definite mismatch is.</summary>
    Unknown,

    /// <summary>The same user.</summary>
    Same,

    /// <summary>Definitely another user.</summary>
    Different,
}

/// <summary>A user and domain as Windows names them locally (WTSUserName/WTSDomainName, or the logon session's).</summary>
public readonly record struct AccountName(string User, string Domain);

/// <summary>
/// Whether this process runs as the user signed in to its session (ST-112, INV-6), compared without a name
/// lookup. Translating the session's user name to a SID asks LSA, and for a domain account away from its
/// domain controller, or an Entra ID account, that can fail or hang, which would refuse the service at every
/// sign-in. So the comparison uses only what is already on the machine:
///
/// <list type="number">
/// <item>the SID of the session's shell (the desktop's own process, explorer) against this token's SID;</item>
/// <item>failing that, the user and domain of this token's logon session against the session's user and
/// domain as Windows reports them, compared as text.</item>
/// </list>
///
/// The shell may not be up yet at sign-in, so it is looked for a few times, a short while apart, before the
/// names are used. Only a definite mismatch refuses: a process that cannot tell runs, and says so.
/// </summary>
public static class SessionUser
{
    /// <summary>How many times the shell is looked for, and how long apart: about three seconds at most.</summary>
    public const int ShellAttempts = 6;

    public static readonly TimeSpan ShellRetry = TimeSpan.FromMilliseconds(500);

    /// <summary>Compares what could be read. SIDs win when both are there; then names; otherwise unknown.</summary>
    public static UserMatch Compare(string? tokenSid, string? shellSid, AccountName? logonSession, AccountName? signedIn)
    {
        if (tokenSid is { Length: > 0 } mine && shellSid is { Length: > 0 } shell)
        {
            return string.Equals(mine, shell, StringComparison.OrdinalIgnoreCase) ? UserMatch.Same : UserMatch.Different;
        }

        if (logonSession is { } logon && signedIn is { } session
            && logon.User.Length > 0 && session.User.Length > 0)
        {
            return string.Equals(logon.User, session.User, StringComparison.OrdinalIgnoreCase)
                && string.Equals(logon.Domain, session.Domain, StringComparison.OrdinalIgnoreCase)
                ? UserMatch.Same
                : UserMatch.Different;
        }

        return UserMatch.Unknown;
    }

    /// <summary>
    /// Looks for the shell up to <paramref name="attempts"/> times, calling <paramref name="wait"/> between
    /// tries, then compares with whatever was found. Bounded: never more than the attempts given.
    /// </summary>
    public static UserMatch Resolve(
        string? tokenSid,
        Func<string?> shellSid,
        Func<AccountName?> logonSession,
        Func<AccountName?> signedIn,
        int attempts = ShellAttempts,
        Action? wait = null)
    {
        ArgumentNullException.ThrowIfNull(shellSid);
        ArgumentNullException.ThrowIfNull(logonSession);
        ArgumentNullException.ThrowIfNull(signedIn);
        string? shell = null;
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            if (attempt > 0)
            {
                (wait ?? (() => Thread.Sleep(ShellRetry)))();
            }

            shell = shellSid();
            if (shell is { Length: > 0 })
            {
                break;
            }
        }

        return Compare(tokenSid, shell, shell is { Length: > 0 } ? null : logonSession(), shell is { Length: > 0 } ? null : signedIn());
    }
}
