using System.Text.RegularExpressions;

namespace ScreenTail.Core.Intel;

/// <summary>
/// A ticket number read off what is in front of the technician when a session starts (ST-077).
///
/// The title first — the browser tab's when there is one, because a PSA in a browser puts the ticket in
/// the tab and the browser's own name in the window — and the clipboard only when the title says nothing.
/// What comes out is digits. The title and the clipboard are read here and nowhere else, and neither is
/// kept: INV-10 forbids titles in anything that persists, and a clipboard is whatever the technician last
/// copied, which may be a password.
///
/// A number is a ticket when it is introduced as one (<c>#48213</c>, <c>Ticket 48213</c>, <c>SR 48213</c>)
/// or, on the clipboard alone, when it is the whole of what was copied. A phone number, a card number and
/// an address all have digits, and none of them is a ticket.
/// </summary>
public static partial class TicketHint
{
    private const int LongestClipboard = 4_096;

    [GeneratedRegex(@"(?:#|\b(?:ticket|service ticket|sr|case|request)\s*#?\s*)(\d{3,9})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Introduced();

    [GeneratedRegex(@"^\s*#?(\d{3,9})\s*$")]
    private static partial Regex Bare();

    public static string? From(string? title, string? browserTabTitle, string? clipboard)
    {
        return FromTitle(browserTabTitle) ?? FromTitle(title) ?? FromClipboard(clipboard);
    }

    private static string? FromTitle(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Introduced().Match(text) is { Success: true } match ? match.Groups[1].Value : null;

    private static string? FromClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > LongestClipboard)
        {
            return null;
        }

        if (Bare().Match(text) is { Success: true } bare)
        {
            return bare.Groups[1].Value;
        }

        return Introduced().Match(text) is { Success: true } introduced ? introduced.Groups[1].Value : null;
    }
}
