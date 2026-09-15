using System.Text.RegularExpressions;

namespace ScreenTail.Core.Audit;

/// <summary>
/// Guards the one field in an audit row that is not a number or a fixed name (ST-045, INV-10).
///
/// <c>detail</c> exists so a row can say <i>which</i> — which mask kind, which suppression reason, which
/// destination — without squeezing it into the type name. That makes it the only place in the log where
/// free text could arrive, and therefore the only place a line of OCR or a customer's name could end up in
/// a file the export promises carries no content.
///
/// So nothing free-form reaches it. Callers pass enums, which cannot be anything else, or a destination
/// host, which is checked against what a host may look like. Anything else is refused at the call rather
/// than written and discovered later in an export.
/// </summary>
public static partial class AuditDetail
{
    /// <summary>Longer than any real label and shorter than any sentence worth hiding something in.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// What a detail may contain: letters, digits, and the punctuation that appears in host names, enum
    /// names and versions. No spaces, which is what keeps a sentence out.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Allowed { get; }

    public static bool IsValid(string? detail) => detail is null || Allowed.IsMatch(detail);

    /// <summary>
    /// A destination for <see cref="AuditTypes.BundleSent"/>: the host a bundle went to, never a URL with
    /// a path or a query, which could carry a ticket subject or a customer name.
    /// </summary>
    public static string Destination(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Require(destination.Host);
    }

    /// <summary>Turns an enum value into a detail. Safe by construction: an enum cannot be a sentence.</summary>
    public static string Of<T>(T value)
        where T : struct, Enum => Require(value.ToString()!);

    /// <summary>
    /// Refuses anything that is not a label. This is a guard against a mistake in our own code, not against
    /// an attacker — someone who can call this can also write to the table — so it throws rather than
    /// quietly dropping the row: a silent audit row is worse than a loud bug.
    /// </summary>
    public static string Require(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (!Allowed.IsMatch(detail))
        {
            throw new ArgumentException(
                $"An audit detail is a short label, not content: up to {MaxLength} characters of letters, "
                + "digits, dot, colon, underscore or hyphen (INV-10).",
                nameof(detail));
        }

        return detail;
    }
}
