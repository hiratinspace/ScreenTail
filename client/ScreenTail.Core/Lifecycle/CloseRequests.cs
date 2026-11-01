namespace ScreenTail.Core.Lifecycle;

/// <summary>Who asked a ScreenTail process to close, read off the message Windows sent.</summary>
public enum CloseKind
{
    /// <summary>The machine is shutting down or restarting (WM_ENDSESSION with no reason flag).</summary>
    SystemShutdown,

    /// <summary>The technician is signing out (ENDSESSION_LOGOFF).</summary>
    SignOut,

    /// <summary>
    /// Restart Manager, for an installer that needs a file this process holds (ENDSESSION_CLOSEAPP):
    /// Windows Installer asks in InstallValidate during an upgrade or an uninstall.
    /// </summary>
    RestartManager,

    /// <summary>A WM_CLOSE: <c>taskkill</c> without <c>/f</c>, or an installer's close-application step.</summary>
    CloseMessage,
}

/// <summary>What the message said: who asked, and whether Windows will end the session whatever the answer.</summary>
public readonly record struct CloseRequest(CloseKind Kind, bool Critical)
{
    // The lParam flags of WM_QUERYENDSESSION and WM_ENDSESSION (winuser.h).
    internal const long EndSessionCloseApp = 0x00000001;
    internal const long EndSessionCritical = 0x40000000;
    internal const long EndSessionLogoff = 0x80000000;

    /// <summary>
    /// Reads the lParam of WM_QUERYENDSESSION or WM_ENDSESSION. Restart Manager's flag wins over the
    /// others, because it is the one that means "only this program", and no flag at all is a shutdown.
    /// </summary>
    public static CloseRequest FromEndSession(long flags)
    {
        // lParam is a pointer-sized value; the flags are its low 32 bits, read unsigned.
        var low = flags & 0xFFFFFFFF;
        var kind = (low & EndSessionCloseApp) != 0 ? CloseKind.RestartManager
            : (low & EndSessionLogoff) != 0 ? CloseKind.SignOut
            : CloseKind.SystemShutdown;
        return new CloseRequest(kind, (low & EndSessionCritical) != 0);
    }

    /// <summary>What the logs call it: a fixed word, nothing from the machine (INV-10).</summary>
    public string ForLog => Kind switch
    {
        CloseKind.SystemShutdown => Critical ? "system-shutdown-critical" : "system-shutdown",
        CloseKind.SignOut => Critical ? "sign-out-critical" : "sign-out",
        CloseKind.RestartManager => "restart-manager",
        _ => "close-message",
    };
}

/// <summary>How a wait for the process to stop ended.</summary>
public enum CloseOutcome
{
    /// <summary>Nothing was asked of the process (WM_ENDSESSION with FALSE: somebody refused, and the session goes on).</summary>
    NotClosing,

    /// <summary>The process finished stopping within the wait.</summary>
    Stopped,

    /// <summary>The wait ran out first. Windows may end the process now, as it did before it could be asked.</summary>
    StillStopping,
}

/// <summary>
/// What a ScreenTail process does when Windows asks it to close: at sign-out, at shutdown, and when an
/// installer's Restart Manager needs a file it holds (ST-112). Platform-neutral so the Mac suite holds the
/// rules; the window that receives the messages is the service's <c>SessionEndWindow</c>.
///
/// The rules, in the order the messages come:
///
/// <list type="bullet">
/// <item><b>WM_QUERYENDSESSION is always answered yes</b>, and nothing stops yet. Capture is not a reason to
/// keep a machine from shutting down or a technician from signing out, and an installer that is refused
/// leaves the files for a restart. Another program may still refuse, and then Windows sends
/// WM_ENDSESSION with FALSE and the session goes on, still recording.</item>
/// <item><b>WM_ENDSESSION with TRUE starts the stop and waits for it, bounded.</b> Windows may end the process
/// as soon as this message returns, so it returns only once the process has stopped, or when
/// <see cref="Budget"/> runs out, inside the five seconds Windows waits for a program before it calls the
/// program unresponsive. Running out is no worse than before this existed: the process is ended, and the
/// next start recovers what it was recording.</item>
/// <item><b>WM_CLOSE starts the stop and does not wait</b>: it is posted, so nobody is waiting on the answer.</item>
/// <item><b>The stop is asked for once</b>, whatever arrives after it, and nothing here throws: these run inside
/// a window procedure, where an exception would end the process uncleanly, which is what this exists to avoid.</item>
/// </list>
///
/// What "stop" means is the host's: the capture service stops the way it always stops (capture first, the
/// microphone, then the store), and a session that was recording stays as it was, to be recovered as partial
/// at the next start (ST-020), exactly as after a crash. This class decides when, never how.
/// </summary>
public sealed class CloseRequests
{
    /// <summary>The longest WM_ENDSESSION is held while the process stops: inside Windows' five seconds.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(4);

    private readonly Action _stop;
    private readonly Task _stopped;
    private int _requested;

    /// <param name="stop">Asks the process to stop. Called at most once; must not block.</param>
    /// <param name="stopped">Completes once the process has stopped: the store closed, capture released.</param>
    /// <param name="budget">How long WM_ENDSESSION waits for <paramref name="stopped"/>. <see cref="DefaultBudget"/> unless a test says otherwise.</param>
    public CloseRequests(Action stop, Task stopped, TimeSpan? budget = null)
    {
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        _stopped = stopped ?? throw new ArgumentNullException(nameof(stopped));
        Budget = budget ?? DefaultBudget;
        if (Budget <= TimeSpan.Zero || Budget > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "The wait must be positive and inside the five seconds Windows allows.");
        }
    }

    /// <summary>Raised once, with the request that started the stop, before the stop is asked for. For the log.</summary>
    public event Action<CloseRequest>? Requested;

    /// <summary>Raised when a wait ran out before the process stopped. For the log.</summary>
    public event Action<CloseRequest>? WaitRanOut;

    public TimeSpan Budget { get; }

    /// <summary>The request that started the stop, or null while none has.</summary>
    public CloseRequest? First { get; private set; }

    /// <summary>How many times the stop was asked for: 0 or 1. For the tests.</summary>
    public int StopsAsked => Volatile.Read(ref _requested);

    /// <summary>WM_QUERYENDSESSION: always yes, and nothing stops until Windows says the session is ending.</summary>
    public static bool MayEnd(CloseRequest request)
    {
        _ = request;
        return true;
    }

    /// <summary>WM_ENDSESSION. Starts the stop when the session is ending and waits for it, at most <see cref="Budget"/>.</summary>
    public CloseOutcome Ended(bool ending, CloseRequest request)
    {
        if (!ending)
        {
            return CloseOutcome.NotClosing;
        }

        Begin(request);
        return Wait(request);
    }

    /// <summary>WM_CLOSE: starts the stop and returns at once.</summary>
    public void Close() => Begin(new CloseRequest(CloseKind.CloseMessage, Critical: false));

    private void Begin(CloseRequest request)
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0)
        {
            return;
        }

        First = request;
        try
        {
            Requested?.Invoke(request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A log that failed does not stop the stop.
        }

        try
        {
            _stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Already stopping, or the host is gone: either way there is nothing more to ask.
        }
    }

    private CloseOutcome Wait(CloseRequest request)
    {
        bool done;
        try
        {
            done = _stopped.Wait(Budget);
        }
        catch (AggregateException)
        {
            // Stopping failed, but it ended: there is nothing left to wait for.
            done = true;
        }

        if (!done)
        {
            try
            {
                WaitRanOut?.Invoke(request);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // As above.
            }
        }

        return done ? CloseOutcome.Stopped : CloseOutcome.StillStopping;
    }
}
