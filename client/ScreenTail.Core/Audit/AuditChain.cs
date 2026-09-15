using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ScreenTail.Core.Audit;

/// <summary>
/// Links audit rows together so the log can show it has not been edited (ST-045).
///
/// Each row's hash covers its own fields and the hash of the row before it, so changing a row, removing
/// one, or reordering two breaks every hash from that point forward. That is the whole claim: not that the
/// log cannot be tampered with — anyone holding the store key can write to the table — but that tampering
/// cannot be made to look like it never happened.
///
/// <b>This is not a signature.</b> Someone with the key can rewrite a row and recompute the rest of the
/// chain. Defeating that needs a key the client does not hold, which is ST-114's evidence pack signing it
/// on the way out, not this. What the chain gives is integrity against edits after the fact — a file
/// recovered from a backup, a row deleted by hand, a log truncated to hide a purge — which is what the
/// export is asked to stand behind.
/// </summary>
public static class AuditChain
{
    /// <summary>What a chain starts from. A row with no predecessor hashes against this.</summary>
    public const string Genesis = "";

    /// <summary>
    /// ASCII unit separator. Fields are joined with a character none of them can contain, so two different
    /// rows cannot produce the same string to hash — "a" + "bc" and "ab" + "c" would otherwise collide.
    /// </summary>
    private const char Separator = '\u001f';

    /// <summary>
    /// The hash of one row. Every field that means anything goes in, separated by a character that cannot
    /// appear in any of them, so two different rows cannot produce the same string to hash — "a" + "bc"
    /// and "ab" + "c" would otherwise collide.
    /// </summary>
    public static string Hash(string? previous, DateTimeOffset at, string? sessionId, string type, long? count, string? detail)
    {
        ArgumentNullException.ThrowIfNull(type);
        var canonical = new StringBuilder()
            .Append(previous ?? Genesis).Append(Separator)
            .Append(at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(Separator)
            .Append(sessionId ?? string.Empty).Append(Separator)
            .Append(type).Append(Separator)
            .Append(count?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(Separator)
            .Append(detail ?? string.Empty)
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Hash(AuditRecord record, string? previous) =>
        Hash(previous, record.At, record.SessionId, record.Type, record.Count, record.Detail);
}

/// <param name="Detail">A short label, never content: a mask kind, a suppression reason, a destination host.</param>
/// <param name="PreviousHash">Null for rows written before the chain existed (schema 4).</param>
/// <param name="Hash">Null for the same reason.</param>
public sealed record AuditRecord(
    long Id,
    DateTimeOffset At,
    string? SessionId,
    string Type,
    long? Count,
    string? Detail,
    string? PreviousHash,
    string? Hash);

/// <param name="Intact">True when every hashed row follows from the one before it.</param>
/// <param name="Checked">How many rows the chain covers.</param>
/// <param name="Unchained">
/// Rows written before the chain existed. Reported rather than counted as failures: they are genuinely
/// unattested, and calling them broken would make every store that predates schema 4 look tampered with.
/// </param>
/// <param name="BrokenAt">The id of the first row that does not follow, or null when none does.</param>
public sealed record AuditVerification(bool Intact, int Checked, int Unchained, long? BrokenAt)
{
    public string Describe() => Intact
        ? Unchained == 0
            ? $"All {Checked} audit rows verify."
            : $"{Checked} audit rows verify; {Unchained} earlier rows predate the hash chain and are not covered."
        : $"The audit log does not verify: row {BrokenAt} does not follow the row before it.";
}
