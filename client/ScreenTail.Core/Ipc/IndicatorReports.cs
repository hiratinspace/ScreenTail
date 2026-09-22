namespace ScreenTail.Core.Ipc;

/// <summary>
/// What the service knows about whether anything on screen says capture is happening (INV-4, ST-085).
///
/// It used to know one thing: how many clients were connected to the pipe. <b>A connection is not a
/// pill.</b> The window could be positioned on a monitor that had since been unplugged, the tray icon
/// could be in Windows 11's overflow flyout, and the UI could have frozen while still holding the socket
/// open — and in all three the service went on recording a customer's screen while counting itself
/// indicated (2026-09-20 review).
///
/// A report is the UI saying "I am showing the pill", repeated while it is true. Reports expire, so the
/// interesting case — a UI that has stopped painting but not stopped running — stops counting on its own.
///
/// <b>What this is not.</b> The service cannot verify the claim, and a process that would lie about it
/// is a process that could just as well connect and say nothing, which is exactly what used to be
/// enough. This is not a defence against a hostile client and nothing here should be read as one. It is
/// a defence against the accidents: a crash, a freeze, a monitor unplugged, an icon nobody can see.
/// </summary>
public sealed class IndicatorReports(TimeProvider? time = null)
{
    /// <summary>
    /// How long a report counts for.
    ///
    /// Longer than the UI's reporting interval by enough that one missed message is not a suppressed
    /// session, and short enough that a frozen UI is noticed within a few seconds rather than a few
    /// minutes. The guard's own grace period runs after this, so the two add up.
    /// </summary>
    public static readonly TimeSpan GoodFor = TimeSpan.FromSeconds(6);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _reports = [];

    /// <summary>Whether anything is currently claiming to show the indicator.</summary>
    public bool Showing
    {
        get
        {
            lock (_gate)
            {
                Forget();
                return _reports.Count > 0;
            }
        }
    }

    /// <summary>How many reports are being remembered. For the test that says they do not accumulate.</summary>
    public int Tracked
    {
        get
        {
            lock (_gate)
            {
                return _reports.Count;
            }
        }
    }

    /// <summary>One window saying it is showing the indicator, now.</summary>
    public void Report(Guid window)
    {
        lock (_gate)
        {
            Forget();
            _reports[window] = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// That window has gone.
    ///
    /// The pipe notices a disconnect sooner than the report would have expired, and a window that has
    /// closed is not showing anything. Waiting for the expiry instead would leave a few seconds in which
    /// the service believed a pill that is not there.
    /// </summary>
    public void Forget(Guid window)
    {
        lock (_gate)
        {
            _ = _reports.Remove(window);
        }
    }

    /// <summary>
    /// Drops what has expired.
    ///
    /// On every use rather than on a timer: there are at most a handful of entries, and a client that
    /// reported once and vanished without a clean disconnect would otherwise be remembered for the life
    /// of the service. Always under the lock.
    /// </summary>
    private void Forget()
    {
        if (_reports.Count == 0)
        {
            return;
        }

        var stale = _time.GetUtcNow() - GoodFor;
        foreach (var window in _reports.Where(entry => entry.Value <= stale).Select(entry => entry.Key).ToList())
        {
            _ = _reports.Remove(window);
        }
    }
}
