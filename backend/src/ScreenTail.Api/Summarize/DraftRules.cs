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
    /// An apostrophe inside a word: "don't", "the user's", "I've".
    ///
    /// Folded to a letter apostrophe before quotations are looked for, because a straight or curly
    /// apostrophe is the same character as a closing single quote. Unfolded, a curly-quoted line with
    /// "I've" in it ended at the apostrophe after one letter and the fabrication went through
    /// (2026-10-12 review), and two possessives in one sentence read as a quotation nobody said.
    /// </summary>
    public const string WordApostrophe = "(?<=\\w)['\u2019](?=\\w)";

    /// <summary>
    /// Every pair of marks a model might reach for, not only straight doubles.
    ///
    /// The rule is "quotation marks". A fabricated line inside curly quotes, guillemets or single quotes
    /// is the same fabrication, and a model writing prose reaches for curly ones more often than not.
    /// A single quote opens only where no word ends and closes only where no word begins, which is what
    /// keeps "the users' printers and the admins' laptops" from reading as a quotation.
    /// </summary>
    public const string Quoted =
        "[\"\u201c\u00ab]([^\"\u201d\u00bb]{4,})[\"\u201d\u00bb]"
        + "|(?<!\\w)['\u2018]([^'\u2019]{4,})['\u2019](?!\\w)";

    /// <summary>Separated by hyphens, spaces or dots, as the client's pattern library masks them.</summary>
    public const string Ssn = @"\b(?!000|666|9\d\d)\d{3}[-. ](?!00)\d{2}[-. ](?!0000)\d{4}\b";

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
    ///
    /// At least the client's cue words. The 2026-10-12 review found "pass:" and the named kinds of key
    /// ("master key", "client key") masked on the device and accepted in a draft.
    /// </summary>
    public const string CredentialCue =
        @"\b(?:password|passphrase|passwd|pwd|pass(?=\s{0,3}[:=])"
        + @"|(?:api|access|secret|private|auth|account|client|consumer|signing|encryption|master|app|maps)[ _-]?key"
        + @"|secret|token|bearer)\b";

    /// <summary>
    /// Cues for a credential that is usually only digits. After one of these, <see cref="PinShaped"/>
    /// counts as well as <see cref="SecretShaped"/>, because "their passcode is 0791" has no letter in it.
    /// </summary>
    public const string PinCue = @"\b(?:pass\s?code|pin(?:\s?number)?)\b";

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
    ///
    /// Letters and digits in any script. It was ASCII only, so one accented letter, or a password in
    /// Cyrillic, made a credential "not a secret" (2026-10-12 review).
    /// </summary>
    public const string SecretShaped = @"^(?=.*[^\W\d_])(?=.*\d)\S{6,}$|^\S{16,}$";

    /// <summary>A PIN or a passcode: four digits or more, and only after a <see cref="PinCue"/>.</summary>
    public const string PinShaped = @"^\d{4,}$";

    /// <summary>
    /// Credentials recognisable without a cue word at all. A model quoting one back needs no
    /// introduction, and neither does a key that survived redaction upstream and was read out of OCR.
    ///
    /// At least every shape the client's pattern library masks. A GitHub fine-grained token, a Google
    /// key and a JWT were masked on the device and accepted in a draft (2026-10-12 review).
    /// <c>research/tests/test_shared_rules.py</c> reads the client's list to notice the next one.
    /// </summary>
    public static readonly IReadOnlyList<string> KeyShapes =
    [
        "AKIA[0-9A-Z]{16}",
        "gh[pousr]_[A-Za-z0-9]{30,}",
        "github_pat_[A-Za-z0-9_]{22,}",
        "xox[baprs]-[A-Za-z0-9-]{10,}",
        @"https://hooks\.slack\.com/services/[A-Za-z0-9/_-]{10,}",
        "sk-[A-Za-z0-9_-]{20,}",
        "sk_(?:live|test)_[A-Za-z0-9]{16,}",
        "npm_[A-Za-z0-9]{30,}",
        "AIza[0-9A-Za-z_-]{30,}",
        @"SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}",
        @"ya29\.[A-Za-z0-9._-]{20,}",
        @"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
        "-----BEGIN[ A-Z]*PRIVATE KEY(?: BLOCK)?-----",
    ];

    private const string Verbs = "download|install|run|execute|disable|uninstall|delete";

    /// <summary>
    /// A note is a record, not an instruction (prompt rule 5a). A web address, a command line, or a
    /// direction to install, run, download or disable something.
    ///
    /// A direction is a verb in the imperative: at the start of a sentence or a bullet, or addressed to
    /// "you". The verb anywhere at all used to count, so "the backup did not run overnight" and "asked
    /// the user to install the driver" were refused as instructions (2026-10-12 review).
    /// </summary>
    public static readonly IReadOnlyList<string> Directive =
    [
        @"(?:https?://|www\.)\S+",
        @"\b(?:Set-ExecutionPolicy|powershell|cmd\.exe|regedit|curl|wget|iwr|irm|invoke-webrequest"
            + @"|invoke-restmethod|invoke-expression|iex|certutil|bitsadmin|mshta|rundll32)\b",
        @"\b(?:disable|turn\s+off|uninstall|remove)\s+(?:the\s+)?(?:antivirus|defender|firewall|edr|mfa|two-factor)\b",
        @"(?:^|(?<=[.!?;:\n]))\s*(?:[-*" + "\u2022" + @"]\s*)?(?:(?:please|then|now|next|first|finally|also)\s+)?"
            + "(?:" + Verbs + @")\b\s+\S+",
        @"\byou\s+(?:should|must|need\s+to|have\s+to|can)\s+(?:" + Verbs + @")\b",
    ];

    /// <summary>Every value above, under the name <c>shared-rules.json</c> gives it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AsShared() => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["redaction_markers"] = RedactionMarkers,
        ["word_apostrophe"] = [WordApostrophe],
        ["quoted"] = [Quoted],
        ["ssn"] = [Ssn],
        ["ssn_cued"] = [SsnCued],
        ["credential_cue"] = [CredentialCue],
        ["pin_cue"] = [PinCue],
        ["connectives"] = Connectives,
        ["secret_shaped"] = [SecretShaped],
        ["pin_shaped"] = [PinShaped],
        ["key_shapes"] = KeyShapes,
        ["directive"] = Directive,
    };
}
