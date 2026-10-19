namespace ScreenTail.Api.Summarize;

/// <summary>
/// The patterns and word lists <see cref="DraftValidator"/> is built from (ST-061, ST-063).
///
/// <c>research/prompts/checks.py</c> reads the same values from <c>research/prompts/shared-rules.json</c>.
/// They are constants here rather than a file the service loads, so a deployment has nothing extra to
/// ship and nothing that can fail to load at startup. <c>SharedRulesTests</c> compares every entry with
/// the file, so the two validators cannot drift apart again the way the 2026-09-19 and 2026-10-12
/// reviews found them: each claiming to mirror the other, and neither doing so.
/// </summary>
internal static class DraftRules
{
    /// <summary>
    /// Deliberate markers the redaction engine leaves behind. They are allowed, and guessing what they
    /// stood for is not.
    /// </summary>
    public static readonly IReadOnlyList<string> RedactionMarkers = ["[REDACTED]", "[CARD]", "[SSN]", "[SECRET]", "[EMAIL]"];

    /// <summary>
    /// Every pair of marks a model might reach for, not only straight doubles.
    ///
    /// The rule is "quotation marks". A fabricated line inside curly quotes or guillemets is the same
    /// fabrication, and a model writing prose reaches for curly ones more often than not, so this whole
    /// class of invented quotation walked past the check until 2026-09-20.
    /// </summary>
    public const string Quoted = "[\"“‘«]([^\"”’»]{4,})[\"”’»]";

    public const string Ssn = @"\b(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}\b";

    /// <summary>
    /// Nine unbroken digits are an order number far more often than a social security number, so this
    /// one needs a cue in front of it. Without it the hyphenated form was the only shape caught.
    /// </summary>
    public const string SsnCued =
        @"\b(?:ssn|social\s?security(?:\s?(?:number|no|#))?)\b\s*(?:is|was)?\s*[:=#]?\s*"
        + @"(?!000|666|9\d\d)\d{3}(?!00)\d{2}(?!0000)\d{4}\b";

    /// <summary>
    /// A credential's cue word, and only that.
    ///
    /// Where the credential sits after the cue is a question about how somebody writes or speaks, and a
    /// single expression answered it by assuming the two are adjacent. Four of the seven phrasings the
    /// client's scrubber had just been fixed for still passed here untouched: "the password is, uh,
    /// Winter2026" matched nothing at all, because the connective run consumed " is" and the value could
    /// not then begin on a comma (2026-09-21).
    /// </summary>
    public const string CredentialCue = @"\b(?:password|passphrase|passwd|pwd|api[ _-]?key|secret|token|bearer)\b";

    /// <summary>
    /// Words that join a cue to the credential, or that somebody says while remembering one.
    ///
    /// Skipped rather than read as the value. Mirrors the client's own list, because a rule that masks
    /// on the device and a rule that refuses on the server disagreeing is how a credential reaches a
    /// customer's ticket.
    /// </summary>
    public static readonly IReadOnlyList<string> Connectives =
    [
        "is", "was", "to", "set", "reset", "changed", "change", "now", "will", "be", "equals", "are",
        "uh", "um", "er", "ah", "like", "just", "actually", "currently", "still",
        "the", "a", "an", "on", "for", "of", "at", "in", "my", "your", "our", "their", "his", "her",
        "its", "new", "old", "that", "this", "it",
    ];

    /// <summary>
    /// What separates a credential from a sentence about one.
    ///
    /// "Outlook prompted for a password repeatedly" and "the password was wrong" are notes a technician
    /// would write; "the password is Summer2024" is the thing the rule forbids. A failed check fails the
    /// whole draft, so a false positive here throws away a correct note: the value has to look like a
    /// secret, not merely follow the word.
    /// </summary>
    public const string SecretShaped = @"^(?=.*[A-Za-z])(?=.*\d)[\x21-\x7e]{6,}$|^[\x21-\x7e]{16,}$";

    /// <summary>
    /// Credentials recognisable without a cue word at all. A model quoting one back needs no
    /// introduction, and neither does a key that survived redaction upstream and was read out of OCR.
    /// </summary>
    public static readonly IReadOnlyList<string> KeyShapes =
    [
        "AKIA[0-9A-Z]{16}",
        "gh[pousr]_[A-Za-z0-9]{30,}",
        "xox[baprs]-[A-Za-z0-9-]{10,}",
        "sk-[A-Za-z0-9_-]{20,}",
        "-----BEGIN[ A-Z]*PRIVATE KEY-----",
    ];

    /// <summary>
    /// A note is a record, not an instruction (prompt rule 5a). A web address, a command line, or a
    /// direction to install, run, download or disable something.
    /// </summary>
    public static readonly IReadOnlyList<string> Directive =
    [
        @"(?:https?://|www\.)\S+",
        @"\b(?:Set-ExecutionPolicy|powershell|cmd\.exe|regedit|curl|wget|iwr|irm|invoke-webrequest"
            + @"|invoke-restmethod|invoke-expression|iex|certutil|bitsadmin|mshta|rundll32)\b",
        @"\b(?:disable|turn\s+off|uninstall|remove)\s+(?:the\s+)?(?:antivirus|defender|firewall|edr|mfa|two-factor)\b",
        @"\b(?:download|install|run|execute|disable|uninstall|delete)\b\s+(?:the\s+|your\s+|windows\s+)?\S+",
    ];

    /// <summary>Every value above, under the name <c>shared-rules.json</c> gives it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AsShared() => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["redaction_markers"] = RedactionMarkers,
        ["quoted"] = [Quoted],
        ["ssn"] = [Ssn],
        ["ssn_cued"] = [SsnCued],
        ["credential_cue"] = [CredentialCue],
        ["connectives"] = Connectives,
        ["secret_shaped"] = [SecretShaped],
        ["key_shapes"] = KeyShapes,
        ["directive"] = Directive,
    };
}
