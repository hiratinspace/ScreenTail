using System.Globalization;
using System.Text;
using System.Text.Json;
using ScreenTail.Core.Store;

namespace ScreenTail.Core.Audit;

/// <param name="FramesCaptured">Frames staged, whatever triggered them.</param>
/// <param name="FramesPurgedUnredacted">Frames deleted because they could not be checked (INV-1).</param>
/// <param name="SuppressionsByReason">How often capture stopped for each reason, and for how long.</param>
/// <param name="RedactionsByKind">What was masked, counted by kind. Counts only, never the matches.</param>
/// <param name="BytesSent">Bytes that left the machine, by destination host.</param>
public sealed record AuditSummary(
    string? SessionId,
    long FramesCaptured,
    long FramesPurgedUnredacted,
    IReadOnlyDictionary<string, AuditInterval> SuppressionsByReason,
    IReadOnlyDictionary<string, long> RedactionsByKind,
    IReadOnlyDictionary<string, long> BytesSent)
{
    public IReadOnlyCollection<string> Destinations => (IReadOnlyCollection<string>)BytesSent.Keys;
}

/// <param name="Times">How many separate intervals.</param>
/// <param name="TotalMs">How long they came to altogether.</param>
public sealed record AuditInterval(long Times, long TotalMs);

/// <summary>
/// Turns the audit log into something a person can be shown (ST-045, Spec §5 S4 "Export audit log").
///
/// The reason this exists as its own class rather than a method on the store is what it must never do. The
/// export is handed to a customer asking what ScreenTail saw on their machine, and its value rests
/// entirely on it carrying no content — no OCR text, no transcript, no window titles. So it is built only
/// from <see cref="AuditRecord"/>, which has nowhere to put any of those: a type name, a count, a session
/// id, a timestamp, and a detail that <see cref="AuditDetail"/> has already refused anything sentence-like
/// for. There is no path from a frame or a transcript segment into this file, by construction rather than
/// by remembering.
/// </summary>
public static class AuditExport
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string ToJson(IReadOnlyList<AuditRecord> records, AuditVerification verification)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(verification);

        return JsonSerializer.Serialize(
            new
            {
                exported_at = DateTimeOffset.UtcNow,
                integrity = new
                {
                    intact = verification.Intact,
                    rows_checked = verification.Checked,
                    rows_predating_the_chain = verification.Unchained,
                    first_broken_row = verification.BrokenAt,
                    note = verification.Describe(),
                },
                entries = records.Select(r => new
                {
                    id = r.Id,
                    at = r.At,
                    session_id = r.SessionId,
                    type = r.Type,
                    count = r.Count,
                    detail = r.Detail,
                    hash = r.Hash,
                }),
            },
            Indented);
    }

    public static string ToCsv(IReadOnlyList<AuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var csv = new StringBuilder("id,at,session_id,type,count,detail,hash\n");
        foreach (var r in records)
        {
            csv.Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(r.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(r.SessionId ?? string.Empty).Append(',')
                .Append(r.Type).Append(',')
                .Append(r.Count?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(r.Detail ?? string.Empty).Append(',')
                .Append(r.Hash ?? string.Empty).Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>
    /// What the rows add up to: Spec §5 S4's line of the story — frames captured, frames purged
    /// unredacted, suppressed intervals, redactions by type, bytes sent and where to.
    /// </summary>
    public static AuditSummary Summarise(IReadOnlyList<AuditRecord> records, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var rows = sessionId is null ? records : records.Where(r => r.SessionId == sessionId).ToList();

        var suppressions = new Dictionary<string, AuditInterval>(StringComparer.Ordinal);
        var redactions = new Dictionary<string, long>(StringComparer.Ordinal);
        var sent = new Dictionary<string, long>(StringComparer.Ordinal);
        long captured = 0;
        long purged = 0;

        foreach (var row in rows)
        {
            switch (row.Type)
            {
                case AuditTypes.FrameCaptured:
                    captured += row.Count ?? 1;
                    break;
                case AuditTypes.FramesPurgedUnredacted:
                    purged += row.Count ?? 1;
                    break;
                case AuditTypes.CaptureSuppressed when row.Detail is { } reason:
                    var seen = suppressions.GetValueOrDefault(reason, new AuditInterval(0, 0));
                    suppressions[reason] = new AuditInterval(seen.Times + 1, seen.TotalMs + (row.Count ?? 0));
                    break;
                // Seen or heard, a masked card number is a masked card number in the totals. The rows
                // themselves keep the two apart for anyone who wants to know which.
                case AuditTypes.FrameRedacted or AuditTypes.TranscriptRedacted when row.Detail is { } kind:
                    redactions[kind] = redactions.GetValueOrDefault(kind) + (row.Count ?? 0);
                    break;
                case AuditTypes.BundleSent when row.Detail is { } host:
                    sent[host] = sent.GetValueOrDefault(host) + (row.Count ?? 0);
                    break;
                default:
                    break;
            }
        }

        return new AuditSummary(sessionId, captured, purged, suppressions, redactions, sent);
    }
}
