namespace ScreenTail.Core.Intel;

public enum TimeRounding
{
    /// <summary>Round up to the next increment — the usual MSP billing convention.</summary>
    Up,

    /// <summary>Round to the nearest increment, halves going up.</summary>
    Nearest,
}

/// <summary>
/// Per-tenant time-entry policy (Scope FR-6, Settings S7). ConnectWise shops normally bill in 6- or
/// 15-minute increments with a minimum charge; ST-047 syncs these from the tenant.
/// </summary>
public sealed record TimeEntryOptions
{
    public int IncrementMinutes { get; init; } = 15;

    /// <summary>Shortest billable entry. Applied before rounding, so the result is always a whole number of increments.</summary>
    public int MinimumMinutes { get; init; } = 15;

    public TimeRounding Rounding { get; init; } = TimeRounding.Up;
}

/// <summary>
/// Turns a session's active duration into the minutes suggested in Review's time-entry field (ST-066).
/// Pauses and suppressed stretches are already excluded: the state machine only counts time spent
/// recording, and that is what it stores as the session duration.
/// </summary>
public static class TimeEntryCalculator
{
    /// <param name="activeDuration">Time spent recording, pauses excluded.</param>
    /// <returns>Minutes to suggest; 0 for a session with no active time, which shouldn't be billed by default.</returns>
    public static int SuggestMinutes(TimeSpan activeDuration, TimeEntryOptions? options = null)
    {
        options ??= new TimeEntryOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.IncrementMinutes);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinimumMinutes);

        if (activeDuration <= TimeSpan.Zero)
        {
            return 0;
        }

        var minutes = Math.Max(activeDuration.TotalMinutes, options.MinimumMinutes);
        var increments = options.Rounding switch
        {
            TimeRounding.Up => Math.Ceiling(minutes / options.IncrementMinutes),
            TimeRounding.Nearest => Math.Floor((minutes / options.IncrementMinutes) + 0.5),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Rounding, null),
        };

        return (int)increments * options.IncrementMinutes;
    }

    /// <summary>Convenience for the stored session duration, which is already the active time.</summary>
    public static int SuggestMinutes(long activeMs, TimeEntryOptions? options = null) =>
        SuggestMinutes(TimeSpan.FromMilliseconds(activeMs), options);
}
