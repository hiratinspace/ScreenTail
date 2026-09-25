using System.Text.RegularExpressions;

namespace ScreenTail.Shared.Logging;

/// <summary>
/// What may not reach a log, removed before the line is written (ST-011, INV-10).
///
/// The messages are content-free by construction: the service logs a title's <i>length</i>, never the
/// title, and the diagnostics record has no field for one. This is the belt to that brace, for the
/// value that reaches a log by mistake — a framework's exception message that quotes a path, a template
/// somebody adds in a hurry. A path names the user and often the customer; an address names a person;
/// and a value logged under a key called <c>Title</c> is a title whatever the message around it says.
/// </summary>
public static partial class LogScrubber
{
    public const string Redacted = "[redacted]";

    // Names a value is logged under when it is content. Matched whole or as a suffix (WindowTitle,
    // OcrText), lower-cased. "Hotkey" and "TitleLength" are not here on purpose: a chord and a count
    // say nothing about what was on the screen.
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "title", "text", "ocr", "ocrtext", "transcript", "note", "notes", "body", "clipboard", "summary",
        "query", "secret", "token", "password", "credential", "authorization", "apikey", "privatekey",
        "publickey", "company", "ticket", "email", "path", "filename", "file", "url", "site", "siteurl",
    };

    private static readonly string[] Suffixes = ["title", "text", "transcript", "clipboard", "secret", "password", "credential", "token", "note"];

    [GeneratedRegex(@"(?<![\w])(?:[A-Za-z]:\\|\\\\)(?:[^\\\s""'<>|]+(?: [^\\\s""'<>|]+)*\\)*[^\\\s""'<>|]*")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<![\w/])/(?:Users|home|root|var|tmp|private|opt|etc|srv|mnt)/[^\s""'<>|]+")]
    private static partial Regex PosixPath();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    /// <summary>Paths become <c>[path]</c> and addresses <c>[email]</c>; everything else is left as it was.</summary>
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

    /// <summary>Whether a value logged under this key is content, and so is written as <see cref="Redacted"/>.</summary>
    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var lower = key.ToLowerInvariant();
        if (Words.Contains(lower))
        {
            return true;
        }

        foreach (var suffix in Suffixes)
        {
            if (lower.Length > suffix.Length && lower.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
