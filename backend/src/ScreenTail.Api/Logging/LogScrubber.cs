using System.Text.RegularExpressions;

namespace ScreenTail.Api.Logging;

/// <summary>
/// The client's scrubber, on the backend (ST-011, INV-10). The API holds a note's text and a ticket's
/// company in memory for one request (INV-7); a framework message that quotes a value is the way either
/// would reach a log, so every line passes through here first. Kept in step with
/// <c>client/ScreenTail.Shared/Logging/LogScrubber.cs</c> by hand, the way the wire contracts are, with
/// one deliberate difference: this list also carries the web framework's request keys (QueryString, Uri,
/// redirect, Location, SourceValue, and the query, uri and url suffixes). The client hosts no web server
/// and makes no call through IHttpClientFactory, so those lines never reach its logs. Do not drop them
/// in a sync.
/// </summary>
public static partial class LogScrubber
{
    public const string Redacted = "[redacted]";

    // The last line is the framework's, not ours: the hosting layer logs every request line with
    // {QueryString} (a ticket search's typed text) and {Path} (a route value such as a company), the
    // HTTP client logs every PSA call with {Uri} (the tenant's site and a ticket id), and the HTTPS
    // redirect logs the whole URL it sends a request to as {redirect}. Location is the same URL as a
    // header, and a parameter that fails to bind is logged with the text it was given as {SourceValue}.
    // Matched as suffixes too, so RequestUri, BaseUrl and SearchQuery fall under the same rule.
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "title", "text", "ocr", "ocrtext", "transcript", "note", "notes", "body", "clipboard", "summary",
        "query", "secret", "token", "password", "credential", "authorization", "apikey", "privatekey",
        "publickey", "company", "ticket", "email", "path", "filename", "file", "url", "site", "siteurl",
        "querystring", "uri", "redirect", "location", "sourcevalue",
    };

    private static readonly string[] Suffixes = ["title", "text", "transcript", "clipboard", "secret", "password", "credential", "token", "note", "query", "uri", "url"];

    [GeneratedRegex(@"(?<![\w])(?:[A-Za-z]:\\|\\\\)(?:[^\\\s""'<>|]+(?: [^\\\s""'<>|]+)*\\)*[^\\\s""'<>|]*")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<![\w/])/(?:Users|home|root|var|tmp|private|opt|etc|srv|mnt|app)/[^\s""'<>|]+")]
    private static partial Regex PosixPath();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var scrubbed = WindowsPath().Replace(text, "[path]");
        scrubbed = PosixPath().Replace(scrubbed, "[path]");
        return Email().Replace(scrubbed, "[email]");
    }

    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        // A destructuring or stringifying prefix ({@Company}, {$Company}) is part of the key the
        // formatter logs under, not part of the name.
        var lower = key.TrimStart('@', '$').ToLowerInvariant();
        return Words.Contains(lower) || Suffixes.Any(suffix => lower.Length > suffix.Length && lower.EndsWith(suffix, StringComparison.Ordinal));
    }
}
