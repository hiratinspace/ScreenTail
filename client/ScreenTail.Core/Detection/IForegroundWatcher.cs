namespace ScreenTail.Core.Detection;

/// <summary>
/// Reports which window is in front (ST-022). ST-023 turns that into a capture scope, ST-040 into
/// suppression, ST-043 into exclusions. This only says what is there.
/// </summary>
public interface IForegroundWatcher : IAsyncDisposable
{
    /// <summary>Raised when the foreground window changes, or when its title changes. Handlers must not block.</summary>
    event Action<ForegroundWindowInfo>? Changed;

    /// <summary>What is in front right now, without waiting for the next change.</summary>
    ForegroundWindowInfo Current { get; }

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Decides which raw observations are worth telling anyone about (ST-022). Kept apart from the Windows
/// plumbing so the rules are testable without a desktop: a window flicking focus and coming back is not two
/// events, and a repeated observation of the same window is not an event at all.
/// </summary>
public sealed class ForegroundChangeFilter(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ForegroundWindowInfo? _reported;
    private volatile ForegroundWindowInfo? _latest;
    private long _reportedAt;

    /// <summary>
    /// How long the same window must hold a new title before the change counts. Titles are the noisy ones:
    /// a progress percentage or an unsaved-changes marker can rewrite the title several times a second.
    /// A change of window is never held back: the 100 ms budget is measured on exactly that.
    /// </summary>
    public TimeSpan TitleSettle { get; init; } = TimeSpan.FromMilliseconds(250);

    public ForegroundWindowInfo? Reported => _reported;

    /// <summary>
    /// The last observation offered, reported or held. Safe to read from any thread.
    ///
    /// A retitle held for <see cref="TitleSettle"/> is still what is on screen. A scope decision made on
    /// the reported title stays in force for it until the report goes out, and a browser tab that turned
    /// into an excluded page inside that time would be photographed under the old tab's decision (INV-6).
    /// Whoever answers "may this window be photographed now" reads this as well, and decides again when
    /// it shows a title nobody has decided on (<see cref="UndecidedTitle"/>).
    /// </summary>
    public ForegroundWindowInfo? Latest => _latest;

    /// <summary>
    /// Offers an observation. Returns what should be reported, or null for "nothing new". The caller is
    /// expected to offer again after <see cref="TitleSettle"/> when this returns null for a pending window.
    /// </summary>
    public ForegroundWindowInfo? Offer(ForegroundWindowInfo observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        _latest = observed;
        if (observed.SameAs(_reported))
        {
            return null;
        }

        // A different window is a real event and goes out at once. Holding it back to debounce cost 233 ms
        // on the laptop against a 100 ms budget, because the change then waited for the next poll.
        if (_reported is null || observed.Handle != _reported.Handle)
        {
            return Accept(observed);
        }

        // Same window, new title: let it settle, so a title that rewrites itself doesn't become a stream.
        return _time.GetElapsedTime(_reportedAt) < TitleSettle ? null : Accept(observed);
    }

    /// <summary>Whatever is in front once things stop moving, reported even if it never settled.</summary>
    public ForegroundWindowInfo? Flush(ForegroundWindowInfo observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        _latest = observed;
        return observed.SameAs(_reported) ? null : Accept(observed);
    }

    private ForegroundWindowInfo Accept(ForegroundWindowInfo observed)
    {
        _reported = observed;
        _reportedAt = _time.GetTimestamp();
        return observed;
    }
}

/// <summary>
/// Whether the window a scope decision was made about is now showing a title nobody has decided on
/// (INV-5, INV-6).
///
/// <see cref="ForegroundChangeFilter"/> holds a retitle back for its settle time so a title that rewrites
/// itself does not become a stream of reports. The scope decision follows the reports, so for that time
/// it answered for the old title: a browser tab that became a password vault, or a remote tool's window
/// that moved to another customer's machine, could be photographed under the decision made before.
/// Deciding again on the newer look costs a dictionary lookup and a few bounded patterns, which is far
/// cheaper than a frame of the wrong thing.
/// </summary>
public static class UndecidedTitle
{
    /// <summary>
    /// The newer look at the decided window to decide on instead, or null when the decision is still about
    /// what is on screen. Only the same window counts: another window in front is the watcher's to report
    /// at once, and the capturer already refuses to photograph a window that is not the one decided.
    /// </summary>
    /// <param name="decided">The observation the decision in force was made about.</param>
    /// <param name="latest">The last observation of the window in front, reported or held (<see cref="ForegroundChangeFilter.Latest"/>).</param>
    public static ForegroundWindowInfo? Newer(ForegroundWindowInfo decided, ForegroundWindowInfo? latest)
    {
        ArgumentNullException.ThrowIfNull(decided);
        return latest is not null && !decided.IsNone && latest.Handle == decided.Handle && !latest.SameAs(decided)
            ? latest
            : null;
    }
}
