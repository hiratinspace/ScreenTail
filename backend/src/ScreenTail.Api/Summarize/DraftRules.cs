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
    /// Characters a reader does not see: zero-width spaces and joiners, soft hyphens, direction marks and
    /// overrides, and the byte order mark.
    ///
    /// Removed before the secret and instruction rules, so a rule reads the text as a person does.
    /// "In\u200bstall the agent" is "Install the agent" on screen, and a card with zero-width spaces
    /// between its groups is a card (2026-10-12 review, third round).
    /// </summary>
    public const string Invisible = "[\u00ad\u200b-\u200f\u202a-\u202e\u2060-\u2064\u2066-\u2069\ufeff]";

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
    ///
    /// The reversed guillemets German writes, »like this«, count when the closing « comes within the same
    /// sentence, which no menu path has (third round).
    /// </summary>
    public const string Quoted =
        "[\"\u201c\u201e\u00ab\u300c\u300e]([^\"\u201c\u201d\u00bb\u300d\u300f]{4,})[\"\u201c\u201d\u00bb\u300d\u300f]"
        + "|(?<!\\w)['\u2018\u201a\u2039]([^'\u2018\u2019\u203a]{4,})['\u2018\u2019\u203a](?!\\w)"
        + "|\u00bb([^\u00ab\u00bb\\n.!?]{4,}[.!?]?)\u00ab";

    /// <summary>Separated by hyphens, spaces or dots, as the client's pattern library masks them.</summary>
    public const string Ssn = @"\b(?!000|666|9\d\d)\d{3}[-. ](?!00)\d{2}[-. ](?!0000)\d{4}\b";

    /// <summary>
    /// Nine unbroken digits are an order number far more often than a social security number, so this
    /// one needs a cue in front of it. Without it the hyphenated form was the only shape caught.
    ///
    /// No word boundary around the cue, as the client has none: "Employee_SSN: 123456789" and
    /// "SSN123456789" are masked on the device and were accepted here (2026-10-19 parity check).
    /// </summary>
    public const string SsnCued =
        @"(?:ssn|social\s?security(?:\s?(?:number|no|#))?)\s*(?:is|was)?\s*[:=#]?\s*"
        + @"(?!000|666|9\d\d)\d{3}(?!00)\d{2}(?!0000)\d{4}\b";

    /// <summary>
    /// A place a card number can be: digits, in groups of any length joined by one or two spaces
    /// (non-breaking, figure, thin and narrow ones too), hyphens or dots, and touching anything but
    /// another digit.
    ///
    /// A place to look, not the candidate. The card inside it is found by trying every window of whole
    /// groups, as the client's pattern library does, because a CVV or an expiry beside the card arrives
    /// in the same run and the run as a whole fails the checksum (2026-10-12 review, second round).
    /// ASCII digits only, so both validators and the checksum read the same characters.
    ///
    /// Wider than the client's separators, because the model rewrites what it reads and its output is not
    /// bound to them: a card with non-breaking or doubled spaces, or with letters touching it, went
    /// through (third round). Slashes and underscores are left out, because dates and timestamped file
    /// names would give every run of them a chance at the checksum.
    /// </summary>
    public const string CardRun = "(?<![0-9])[0-9]+(?:[ \u00a0\u2007\u2009\u202f.-]{1,2}[0-9]+)*(?![0-9])";

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

        // The client's cue-and-value rule, with its lookbehind matched instead, because Python's must be
        // fixed width. The cue needs no word boundary and the word "key" is optional, so
        // "access_token=", "client_secret:" and "Auth:" before a long value are masked on the device.
        // The cue walk here missed all three, and stopped at the first dot of a value that had them
        // (2026-10-19 parity check).
        @"(?i:(?:api|access|secret|private|auth|account|client|consumer|signing|encryption|master|app|maps)[ _-]?(?:key|token|secret)?|secret|token|bearer)[""']?\s{0,4}[:=]?\s{0,4}[""']?[A-Za-z0-9+/_.-]{24,}={0,2}",
    ];

    private const string Verbs = "download|install|run|execute|disable|uninstall|delete";

    /// <summary>
    /// Space inside a line, and nothing else.
    ///
    /// <c>\s</c> crossed line breaks, so a record form on one line covered a command on the next: "Had
    /// issues", a newline, "Install the agent" passed (2026-10-12 review, third round). Spelled out
    /// rather than <c>[^\S\r\n]</c>, because .NET and Python disagree about a few characters <c>\s</c>
    /// covers.
    /// </summary>
    private const string Space = "[ \t\u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]+";

    /// <summary>
    /// A word inside a clause: no white space, no sentence or clause mark, no bracket, quote, slash or
    /// dash. An apostrophe counts only between two letters, as in "the user's".
    ///
    /// A dash standing alone used to count as a word, so "Had a problem - install the agent" read as one
    /// record (third round).
    /// </summary>
    private const string Word =
        "(?:[^\\s.!?;:,()\\[\\]{}\"'\u2018\u2019\u201a\u201c\u201d\u201e\u00ab\u00bb\u2039\u203a/\\\\\u2010-\u2015-]"
        + "|(?<=\\w)['\u2019](?=\\w))+";

    /// <summary>
    /// Words that cannot be part of the person a reporting verb is about, because an instruction starts
    /// with them: modals and obligations ("should", "had better"), joining words ("and", "then"),
    /// relative words ("who", "that") and the openers of a reminder ("remember", "make sure").
    /// </summary>
    private const string NotAnObject =
        "should|must|shall|will|would|can|could|may|might|ought|need|needs|better|has|have|had|please|kindly"
        + "|then|and|or|nor|but|so|who|whom|whose|which|that|if|when|once|now|remember|forget|ensure|sure|try"
        + "|go|make|let";

    /// <summary>One to three words naming who was asked, told or helped: "the user", "the front desk".</summary>
    private const string Object = "(?:" + Space + "(?!(?:" + Verbs + "|" + NotAnObject + @")\b)" + Word + "){1,3}";

    /// <summary>
    /// A negated auxiliary, or "never" after one. "Don't", "do not" and a bare "never" are left out:
    /// they are how a prohibition is written.
    /// </summary>
    private const string Negation =
        @"(?:\b(?:did|does|was|were|is|are|has|have|had|would|could|will|can|might|may)" + Space + @"not\b|\bcannot"
        + @"|\b(?:did|does|was|were|is|are|has|have|had|would|could|ca|wo)n['" + "\u2019\u02bc" + @"]t"
        + @"|\b(?:did|does|was|were|is|are|has|have|had|would|could|will|can)" + Space + @"never)"
        + "(?:" + Space + "(?:have|be|been|yet|ever|even)){0,2}"
        + "(?:" + Space + "able" + Space + "to)?";

    private const string Reporting = "asked|told|advised|instructed|reminded|encouraged|prompted|invited|urged|warned|taught";

    /// <summary>
    /// The forms a record takes in front of one of the <see cref="Verbs"/>, and only these. They are the
    /// shapes T-204's records have: a negation ("the backup did not run"), a reporting verb with the
    /// person before "to" ("asked the user to install"), and a failure or an attempt before "to" ("the
    /// update failed to install"), with a past passive of the reporting form ("was asked to install").
    ///
    /// Each one used to be wider and each width let an instruction through (2026-10-12 review, third
    /// round). A verb read as a noun after an article took "Verify the download and install AnyDesk".
    /// A past tense with any three words as its object took "Anyone who had the issue should install".
    /// "will" and "going to" took "The technicians will install AnyDesk on every machine", and "used
    /// to" and "got" took a verb the instruction was about. They are gone, and the records they let
    /// through ("The install failed", "Had the user run") cost a repair, as they did on main.
    ///
    /// Only reporting verbs in the past that are rarely adjectives, and the person is one to three
    /// words that cannot start an instruction (<see cref="NotAnObject"/>). A present passive ("Users
    /// are advised to install") is a notice, not a record, so only "was" and "were" make one.
    /// </summary>
    private const string Record =
        Negation
        + @"|\b(?:failed|unable|refused|tried|attempted|managed|wanted|needed|began|started|continued|chose|decided|forgot|declined|offered|agreed|planned)"
        + Space + "to"
        + @"|\b(?:was|were)" + Space + "able" + Space + "to"
        + @"|\b(?:" + Reporting + "|showed|helped|caused|wanted)" + Object + Space + "to"
        + @"|\b(?:was|were)(?:" + Space + "(?:not|never|also|already|first|then))?" + Space
        + "(?:" + Reporting + "|shown|helped)" + Space + "to";

    /// <summary>
    /// A note is a record, not an instruction (prompt rule 5a). A web address, a command line, a
    /// security control switched off, or a direction addressed to "you".
    ///
    /// A note addressed to the reader at all is not how a record reads, so "you" with up to six words
    /// in the same clause and then one of the verbs is refused whatever the words are: "you should
    /// probably install", "you'll need to install", and a record form addressed to the reader, "Make
    /// sure you asked the user to install the agent". Three words let that last one through, because
    /// the reporting form took the verb (2026-10-19).
    /// </summary>
    public static readonly IReadOnlyList<string> Directive =
    [
        @"(?:https?://|www\.)\S+",
        @"\b(?:Set-ExecutionPolicy|powershell|cmd\.exe|regedit|curl|wget|iwr|irm|invoke-webrequest"
            + @"|invoke-restmethod|invoke-expression|iex|certutil|bitsadmin|mshta|rundll32)\b",
        @"\b(?:disable|turn\s+off|uninstall|remove)\s+(?:the\s+)?(?:antivirus|defender|firewall|edr|mfa|two-factor)\b",
        @"\byou(?:['" + "\u2019\u02bc" + @"](?:ll|d|re|ve))?(?:" + Space + "(?!(?:" + Verbs + @")\b)" + Word + "){0,6}"
            + Space + "(?:" + Verbs + @")\b",
    ];

    /// <summary>
    /// A direction to install, run, download, delete or disable something, which is refused wherever the
    /// verb stands unless it is in one of the forms a record takes (<see cref="Record"/>).
    ///
    /// The first group is a record form together with its verb, and any verbs joined to it with "and",
    /// "or" or "nor" ("did not download or install"). Every record form left has a subject or an
    /// auxiliary that governs the second verb too. A match without that group is the instruction.
    ///
    /// The verb on its own is enough. It needed a word after it, so "Install, then reboot.", "Then
    /// install." and "install" followed by a dash passed (third round). Refusing by default is the
    /// point: an unlisted phrasing of an instruction is refused, and an unlisted phrasing of a record is
    /// what costs a repair.
    /// </summary>
    public const string Imperative =
        "((?:" + Record + ")" + Space + "(?:" + Verbs + @")\b(?:" + Space + "(?:or|and|nor)" + Space + "(?:" + Verbs + @")\b)*)"
        + @"|\b(?:" + Verbs + @")\b";

    /// <summary>
    /// The client's password cues, exactly as <c>PatternLibrary.PasswordCue</c> writes them. After one of
    /// these the client's own walk runs (<see cref="NotSecrets"/>, <see cref="Asserting"/>), so the
    /// server refuses every word the device would have masked.
    /// </summary>
    public const string PasswordCue = @"\b(?:password|passphrase|passwd|pwd|pass\s?code|pass(?=\s{0,3}[:=])|pin(?:\s?number)?)\b";

    /// <summary>
    /// Words that end the client's walk with nothing masked: "the password is incorrect". The client's
    /// <c>NotSecrets</c>, word for word.
    ///
    /// The client masks any other word reached after an asserting word, so "The password is sunshine."
    /// was masked on the device and accepted in a draft (2026-10-19 parity check). It also masks
    /// "successfully" in "The password was reset successfully", and the server refuses that too. A word
    /// added here and to the client's list is how that changes, and <c>test_shared_rules.py</c> fails
    /// until both have it.
    /// </summary>
    public static readonly IReadOnlyList<string> NotSecrets =
    [
        "incorrect", "wrong", "correct", "expired", "reset", "resets", "changed", "change", "changing", "updated",
        "required", "policy", "manager", "field", "prompt", "box", "again", "here", "there", "empty", "blank",
        "the", "a", "an", "it", "that", "this", "his", "her", "their", "my", "your", "our", "not", "never",
        "and", "or", "but", "so", "then", "when", "which", "what", "who", "why", "how", "if", "for", "to",
    ];

    /// <summary>The joining words that assert a value, the client's <c>Assertive</c>: after one, a plain word is the secret.</summary>
    public static readonly IReadOnlyList<string> Asserting =
    [
        "is", "was", "to", "set", "reset", "changed", "change", "now", "will", "be", "equals", "are",
    ];

    /// <summary>How many joining words the client's walk passes, its <c>MaxConnectives</c>.</summary>
    public const int PasswordWalkSteps = 4;

    /// <summary>Every value above, under the name <c>shared-rules.json</c> gives it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AsShared() => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["redaction_markers"] = RedactionMarkers,
        ["invisible"] = [Invisible],
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
        ["imperative"] = [Imperative],
        ["password_cue"] = [PasswordCue],
        ["not_secrets"] = NotSecrets,
        ["asserting"] = Asserting,
        ["password_walk_steps"] = [PasswordWalkSteps.ToString(System.Globalization.CultureInfo.InvariantCulture)],
    };
}
