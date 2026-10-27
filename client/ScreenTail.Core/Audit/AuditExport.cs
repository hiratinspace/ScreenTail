using System.Globalization;
using System.Text;
using System.Text.Json;
using ScreenTail.Core.Store;

namespace ScreenTail.Core.Audit;

/// <param name="FramesCaptured">Frames accepted for redaction, whatever triggered them.</param>
/// <param name="FramesPurgedUnredacted">
/// Frames lost before they could be checked (INV-1): deleted unread, or refused before they were accepted
/// because the queue of frames waiting to be read was full. A refused frame is counted here and never in
/// <paramref name="FramesCaptured"/>, so for a session that lost frames that way this can be the larger.
/// It is not a share of the frames captured.
/// </param>
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
    /// <summary>
    /// The export as a string, through the same writer the service streams to a file with. For tests and
    /// for a log small enough to hold; the service never builds the whole export in memory.
    /// </summary>
    public static string ToJson(IReadOnlyList<AuditRecord> records, AuditVerification verification)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(verification);

        using var buffer = new MemoryStream();
        using (var writer = new AuditJsonWriter(buffer, DateTimeOffset.UtcNow))
        {
            foreach (var record in records)
            {
                writer.Write(record);
            }

            writer.Complete(verification);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
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

/// <summary>
/// Writes the audit export one row at a time (2026-10-12 review, E-2).
///
/// The log is never pruned, so the export has to be written in memory that does not grow with it: each
/// row goes to the stream as it is read, and the writer's buffer is flushed every few kilobytes. The
/// verification comes last, after the rows, because it is only known once the last row has been read;
/// a JSON reader does not care about the order of an object's members. Compact rather than indented: the
/// file is evidence for a tool to check, and a year of rows indented is several times the size.
///
/// Built only from <see cref="AuditRecord"/>, for the reason <see cref="AuditExport"/> gives.
/// </summary>
public sealed class AuditJsonWriter : IDisposable, IAsyncDisposable
{
    /// <summary>How much the writer holds before it hands it to the stream.</summary>
    private const int FlushAt = 32 * 1024;

    private readonly Utf8JsonWriter _json;

    public AuditJsonWriter(Stream destination, DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _json = new Utf8JsonWriter(destination);
        _json.WriteStartObject();
        _json.WriteString("exported_at", exportedAt);
        _json.WriteStartArray("entries");
    }

    /// <summary>How many rows have been written.</summary>
    public long Rows { get; private set; }

    public void Write(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _json.WriteStartObject();
        _json.WriteNumber("id", record.Id);
        _json.WriteString("at", record.At);
        WriteText("session_id", record.SessionId);
        _json.WriteString("type", record.Type);
        if (record.Count is { } count)
        {
            _json.WriteNumber("count", count);
        }
        else
        {
            _json.WriteNull("count");
        }

        WriteText("detail", record.Detail);
        WriteText("hash", record.Hash);
        _json.WriteEndObject();
        Rows++;
    }

    /// <summary>Writes a row, and passes what has built up to the stream once there is enough of it.</summary>
    public ValueTask WriteAsync(AuditRecord record, CancellationToken ct)
    {
        Write(record);
        return _json.BytesPending >= FlushAt ? new ValueTask(_json.FlushAsync(ct)) : ValueTask.CompletedTask;
    }

    /// <summary>Closes the rows and says whether they verify.</summary>
    public void Complete(AuditVerification verification)
    {
        End(verification);
        _json.Flush();
    }

    public Task CompleteAsync(AuditVerification verification, CancellationToken ct)
    {
        End(verification);
        return _json.FlushAsync(ct);
    }

    public void Dispose() => _json.Dispose();

    public ValueTask DisposeAsync() => _json.DisposeAsync();

    private void End(AuditVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        _json.WriteEndArray();
        _json.WriteStartObject("integrity");
        _json.WriteBoolean("intact", verification.Intact);
        _json.WriteNumber("rows_checked", verification.Checked);
        _json.WriteNumber("rows_predating_the_chain", verification.Unchained);
        if (verification.BrokenAt is { } broken)
        {
            _json.WriteNumber("first_broken_row", broken);
        }
        else
        {
            _json.WriteNull("first_broken_row");
        }

        _json.WriteString("note", verification.Describe());
        _json.WriteEndObject();
        _json.WriteEndObject();
    }

    private void WriteText(string name, string? value)
    {
        if (value is null)
        {
            _json.WriteNull(name);
        }
        else
        {
            _json.WriteString(name, value);
        }
    }
}
