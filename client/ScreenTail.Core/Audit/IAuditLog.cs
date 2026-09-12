namespace ScreenTail.Core.Audit;

/// <summary>
/// Append-only record of what the client did: types, counts and session ids, never content (INV-10).
/// The store implements it; ST-045 adds the hash chain and export.
/// </summary>
public interface IAuditLog
{
    Task RecordAsync(string type, string? sessionId = null, long? count = null, CancellationToken ct = default);
}
