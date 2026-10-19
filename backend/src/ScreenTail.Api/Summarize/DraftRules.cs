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
    ///
    /// The low-9 quotes German and Polish write, single guillemets and corner brackets count as well
    /// (2026-10-12 review, second round). A closing guillemet does not open a quotation, so a menu path
    /// written "File » Save As » PDF" is not one.
    /// </summary>
    public const string Quoted =
        "[\"\u201c\u201e\u00ab\u300c\u300e]([^\"\u201c\u201d\u00bb\u300d\u300f]{4,})[\"\u201c\u201d\u00bb\u300d\u300f]"
        + "|(?<!\\w)['\u2018\u201a\u2039]([^'\u2018\u2019\u203a]{4,})['\u2018\u2019\u203a](?!\\w)";

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
    /// A place a card number can be: digits, in groups of any length joined by a space, a hyphen or a dot.
    ///
    /// A place to look, not the candidate. The card inside it is found by trying every window of whole
    /// groups, as the client's pattern library does, because a CVV or an expiry beside the card arrives
    /// in the same run and the run as a whole fails the checksum (2026-10-12 review, second round).
    /// ASCII digits only, so both validators and the checksum read the same characters.
    /// </summary>
    public const string CardRun = @"\b[0-9]+(?:[ .-][0-9]+)*\b";

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
    /// Words that put an imperative off by a word or two without making it anything else, and short
    /// openers that often stand before one with no comma ("Once done run ...", "If prompted disable ...").
    /// </summary>
    private const string Introducing =
        @"please|then|now|next|first|second|third|finally|lastly|also|just|so|and|simply|afterwards"
        + @"|after\s+that|make\s+sure\s+to|be\s+sure\s+to|remember\s+to|try\s+to|go\s+ahead\s+and"
        + @"|once\s+done|when\s+done|if\s+(?:needed|necessary|prompted|so|not)|when\s+prompted|otherwise|instead|immediately";

    /// <summary>
    /// A note is a record, not an instruction (prompt rule 5a). A web address, a command line, or a
    /// direction to install, run, download or disable something.
    ///
    /// A direction is a verb in the imperative. The verb anywhere at all used to count, so "the backup
    /// did not run overnight" and "asked the user to install the driver" were refused as instructions
    /// (2026-10-12 review). What counts now is the verb where an imperative stands: at the start of a
    /// sentence, a clause (after a comma, a colon or a bracket) or a bulleted, numbered or lettered
    /// item, perhaps after an introducing word with or without a comma ("Next, install"); or addressed
    /// to "you", with or without a modal ("you will need to run", "you'll need to install").
    ///
    /// Clauses and introducing words are there because the first version of this rule counted only a
    /// sentence's first word and let "If it fails, disable the service" through, which main refused.
    /// This is a security rule, so where a phrasing could be either, it is read as an instruction: a
    /// list such as "checked the logs, run history and services" is refused and costs a repair.
    /// </summary>
    public static readonly IReadOnlyList<string> Directive =
    [
        @"(?:https?://|www\.)\S+",
        @"\b(?:Set-ExecutionPolicy|powershell|cmd\.exe|regedit|curl|wget|iwr|irm|invoke-webrequest"
            + @"|invoke-restmethod|invoke-expression|iex|certutil|bitsadmin|mshta|rundll32)\b",
        @"\b(?:disable|turn\s+off|uninstall|remove)\s+(?:the\s+)?(?:antivirus|defender|firewall|edr|mfa|two-factor)\b",
        @"(?:^|(?<=[.!?;:,(\n]))\s*(?:(?:[-*" + "\u2022" + @"]|\d{1,3}[.)]|[a-z]\))\s*)?(?:(?:" + Introducing + @")\b,?\s+)*"
            + "(?:" + Verbs + @")\b\s+\S+",
        @"\byou(?:['" + "\u2019\u02bc" + @"](?:ll|d)|\s+(?:should|must|will|would|may|might|can|could|ought\s+to))?"
            + @"(?:\s+(?:need|have|want)\s+to)?\s+(?:(?:just|also|then|now|first)\s+)?(?:" + Verbs + @")\b",
    ];

    /// <summary>Every value above, under the name <c>shared-rules.json</c> gives it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AsShared() => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["redaction_markers"] = RedactionMarkers,
        ["word_apostrophe"] = [WordApostrophe],
        ["quoted"] = [Quoted],
        ["ssn"] = [Ssn],
        ["ssn_cued"] = [SsnCued],
        ["card_run"] = [CardRun],
        ["credential_cue"] = [CredentialCue],
        ["pin_cue"] = [PinCue],
        ["connectives"] = Connectives,
        ["secret_shaped"] = [SecretShaped],
        ["pin_shaped"] = [PinShaped],
        ["key_shapes"] = KeyShapes,
        ["directive"] = Directive,
    };
}
