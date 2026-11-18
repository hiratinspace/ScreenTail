using ScreenTrail.Core.History;

namespace ScreenTrail.Core.Store;

/// <summary>
/// What the coverage banner counts (ST-079, spec v0.7.3 S4): the remote sessions the service saw and did
/// not capture (store migration 0016), and the captured sessions that were only brief pass-throughs.
/// Tool kinds, times and a reason word; never a window title or anything on screen (INV-10).
///
/// Its own interface beside <see cref="ISessionStore"/>, as <see cref="Audit.IAuditLog"/> is, so the store's
/// test decorators need not grow with it.
/// </summary>
public interface ICoverageStore
{
    /// <summary>Keeps a remote session the service saw and did not capture.</summary>
    Task RecordMissedVisitAsync(MissedVisit visit, CancellationToken ct = default);

    /// <summary>
    /// The session ended with nothing in it and was discarded rather than drafted (<see cref="Sessions.EmptySession"/>):
    /// it is left out of the captured count, as a missed visit that brief is left out of the missed one.
    /// The row stays a discarded row in History. False for no such session.
    /// </summary>
    Task<bool> MarkPassThroughAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// The captured sessions and the missed visits that started at or after <paramref name="from"/>, by tool
    /// (and missed ones by reason). Pass-throughs are in neither.
    /// </summary>
    Task<CoverageCounts> CountCoverageAsync(DateTimeOffset from, CancellationToken ct = default);

    /// <summary>
    /// When this store began counting missed visits: the moment migration 0016 was applied. Before it the
    /// device counted no misses, so no coverage before it can be honest.
    /// </summary>
    Task<DateTimeOffset> CoverageCountedSinceAsync(CancellationToken ct = default);

    /// <summary>Deletes the missed visits that ended before the cutoff, with the raw data (INV-12).</summary>
    /// <returns>How many went.</returns>
    Task<int> PurgeMissedVisitsBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

/// <summary>Counts by tool kind (the sessions' vocabulary), for the banner to sum over the tool filter.</summary>
/// <param name="Captured">Sessions captured, pass-throughs left out, by tool kind.</param>
/// <param name="Missed">Visits not captured, by tool kind and reason.</param>
public sealed record CoverageCounts(
    IReadOnlyDictionary<string, int> Captured,
    IReadOnlyDictionary<(string Tool, MissReason Reason), int> Missed);
