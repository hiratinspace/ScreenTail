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
    private long _reportedAt;

    /// <summary>
    /// How long a window must hold focus before it counts. Alt-tabbing through five windows should report
    /// where the technician landed, not everything they passed through.
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(60);

    public ForegroundWindowInfo? Reported => _reported;

    /// <summary>
    /// Offers an observation. Returns what should be reported, or null for "nothing new". The caller is
    /// expected to offer again after <see cref="Settle"/> when this returns null for a pending window.
    /// </summary>
    public ForegroundWindowInfo? Offer(ForegroundWindowInfo observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.SameAs(_reported))
        {
            return null;
        }

        // The first window we ever see is reported immediately: there is nothing to settle against, and the
        // session needs to know where it started.
        if (_reported is null)
        {
            return Accept(observed);
        }

        return _time.GetElapsedTime(_reportedAt) < Settle ? null : Accept(observed);
    }

    /// <summary>Whatever is in front once things stop moving, reported even if it never settled.</summary>
    public ForegroundWindowInfo? Flush(ForegroundWindowInfo observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        return observed.SameAs(_reported) ? null : Accept(observed);
    }

    private ForegroundWindowInfo Accept(ForegroundWindowInfo observed)
    {
        _reported = observed;
        _reportedAt = _time.GetTimestamp();
        return observed;
    }
}
