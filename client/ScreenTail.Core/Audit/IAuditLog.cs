namespace ScreenTail.Core.Audit;

/// <summary>
/// Append-only record of what the client did: types, counts and session ids, never content (INV-10).
/// The store implements it, hash-chains each row, and can export and verify the result (ST-045).
/// </summary>
public interface IAuditLog
{
    /// <param name="detail">
    /// A short label saying which — a mask kind, a suppression reason, a destination host. Never content;
    /// <see cref="AuditDetail"/> is what keeps it that way.
    /// </param>
    Task RecordAsync(string type, string? sessionId = null, long? count = null, string? detail = null, CancellationToken ct = default);
}
