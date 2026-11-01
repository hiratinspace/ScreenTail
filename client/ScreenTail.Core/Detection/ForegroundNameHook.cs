namespace ScreenTail.Core.Detection;

/// <summary>
/// Keeps the foreground watcher's <c>EVENT_OBJECT_NAMECHANGE</c> hook on the one thread whose renames can
/// matter: the thread that owns the window in front (ST-031, ST-022).
///
/// A rename matters to the watcher only when the window in front retitles itself; every other rename is
/// thrown away in the callback (<see cref="ForegroundEvents"/>). Hooked for every thread on the desktop,
/// though, each of those renames still woke the watcher's thread first, and a desktop has plenty: a
/// progress bar, a clock, a list refreshing in the background. On the laptop that was one thread waking
/// about thirty times a second with no session, and the idle service's cost moved with whatever else
/// happened to be open. <c>SetWinEventHook</c> can be told a thread, and Windows then does the filtering
/// before anything is delivered, so this hooks the foreground window's thread and moves the hook when the
/// foreground moves.
///
/// <b>Never less than before.</b> A window's own title change is raised on the thread that owns it, which
/// is the thread this follows. Should Windows refuse a hook for that thread, the hook goes back to every
/// thread, the old subscription, rather than leave the title to the two-second poll; it tries the scoped
/// hook again at the next foreground change.
///
/// Platform-neutral so the rule is tested on any machine; the Windows watcher supplies the two calls and
/// makes every one of them on its own pump thread, which is where a WinEvent hook has to live.
/// </summary>
public sealed class ForegroundNameHook : IDisposable
{
    /// <summary>The thread value that means every thread on the desktop.</summary>
    public const uint EveryThread = 0;

    private readonly Func<uint, nint> _hook;
    private readonly Action<nint> _unhook;
    private nint _handle;

    /// <param name="hook">Installs the name-change hook for one thread (<see cref="EveryThread"/> for all) and returns its handle, or zero when Windows refuses.</param>
    /// <param name="unhook">Removes a hook <paramref name="hook"/> returned.</param>
    public ForegroundNameHook(Func<uint, nint> hook, Action<nint> unhook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(unhook);
        _hook = hook;
        _unhook = unhook;
    }

    /// <summary>Whether a hook is in at all.</summary>
    public bool Installed => _handle != 0;

    /// <summary>The thread the hook listens to, or <see cref="EveryThread"/>. Meaningless when not <see cref="Installed"/>.</summary>
    public uint Thread { get; private set; }

    /// <summary>Whether the hook is narrowed to one thread, as it should be, rather than the fallback over every thread.</summary>
    public bool Scoped => Installed && Thread != EveryThread;

    /// <summary>
    /// Points the hook at the thread that owns the window now in front, or takes it out when nothing is in
    /// front (thread zero). The new hook goes in before the old one comes out, so a rename on the new
    /// window is never missed for want of a hook; an event the two both deliver is the same rename twice,
    /// which the watcher's filter already reports once.
    /// </summary>
    /// <returns>True when the hook changed.</returns>
    public bool Follow(uint foregroundThread)
    {
        if (foregroundThread == EveryThread)
        {
            if (!Installed)
            {
                return false;
            }

            Remove();
            return true;
        }

        if (Scoped && Thread == foregroundThread)
        {
            return false;
        }

        var scoped = _hook(foregroundThread);
        if (scoped != 0)
        {
            Replace(scoped, foregroundThread);
            return true;
        }

        // Refused for this thread. The old subscription, every thread, keeps the title news coming.
        if (Installed && Thread == EveryThread)
        {
            return false;
        }

        var everywhere = _hook(EveryThread);
        if (everywhere != 0)
        {
            Replace(everywhere, EveryThread);
            return true;
        }

        // Refused outright: the poll is all there is, as it is when the foreground hook itself fails.
        if (Installed)
        {
            Remove();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (Installed)
        {
            Remove();
        }
    }

    private void Replace(nint handle, uint thread)
    {
        var old = _handle;
        _handle = handle;
        Thread = thread;
        if (old != 0)
        {
            _unhook(old);
        }
    }

    private void Remove()
    {
        var old = _handle;
        _handle = 0;
        Thread = EveryThread;
        _unhook(old);
    }
}
