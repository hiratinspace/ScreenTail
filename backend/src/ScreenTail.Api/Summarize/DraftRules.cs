using System.Text.RegularExpressions;

namespace ScreenTail.Api.Summarize;

/// <summary>
/// The patterns and word lists <see cref="DraftValidator"/> is built from (ST-061, ST-063).
///
/// <c>research/prompts/checks.py</c> reads the same values from <c>research/prompts/shared-rules.json</c>.
/// They are constants here so a deployment ships no extra file of rules, and <c>SharedRulesTests</c>
/// compares every entry with the file, so the two validators cannot drift apart again the way the
/// 2026-09-19 and 2026-10-12 reviews found them: each claiming to mirror the other, and neither doing so.
///
/// The same pattern is not the same rule unless both engines read it the same way, and .NET's Regex and
/// Python's re do not. So no pattern here uses <c>\b</c>, <c>\d</c>, <c>\s</c>, <c>\w</c> or another
/// shorthand the two read differently. <c>%word%</c>, <c>%digit%</c>, <c>%space%</c> and
/// <c>%nonspace%</c> are classes written out from <c>unicode-tables.json</c> by <see cref="DraftReading"/>,
/// and they are what .NET's Regex itself reads as <c>\b</c>'s word characters, <c>\d</c>, <c>\s</c> and
/// <c>\S</c>, so a rule written with them means here what it meant before they were written out.
/// </summary>
internal static class DraftRules
{
    /// <summary>
    /// Deliberate markers the redaction engine leaves behind. They are allowed, and guessing what they
    /// stood for is not. Blanked, exactly as written, before the secret and instruction rules run.
    /// </summary>
    public static readonly IReadOnlyList<string> RedactionMarkers = ["[REDACTED]", "[CARD]", "[SSN]", "[SECRET]", "[EMAIL]"];

    /// <summary>Where a word starts, where one ends, and either: <c>\b</c> spelled out.</summary>
    public static readonly IReadOnlyDictionary<string, string> Placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["start"] = "(?<!%word%)",
        ["end"] = "(?!%word%)",
        ["boundary"] = "(?:(?<=%word%)(?!%word%)|(?<!%word%)(?=%word%))",
    };

    /// <summary>
    /// The rules that ignore case: IgnoreCase with CultureInvariant here, re.IGNORECASE with re.ASCII in
    /// Python. The two agree on every character once U+212A KELVIN SIGN is read as K, which both
    /// validators do; Python's IGNORECASE without re.ASCII folds the dotted and dotless i and the long s
    /// into ASCII, and .NET does not.
    /// </summary>
    public static readonly IReadOnlyList<string> IgnoreCase = ["ssn_cued", "credential_cue", "key_cue_value", "directive"];

    /// <summary>
    /// An apostrophe inside a word: "don't", "the user's", "I've". Made one character, U+02BC, before
    /// quotations are looked for, because a straight or curly apostrophe is the same character as a
    /// closing single quote. Unfolded, a curly quotation with "I've" in it ended after one letter and the
    /// fabrication went through (2026-10-12 review, T-203).
    /// </summary>
    public const string Apostrophe = "(?<=%word%)['\u2019](?=%word%)";

    /// <summary>What an apostrophe inside a word is made before quotations are looked for.</summary>
    public const string ApostropheAs = "\u02BC";

    /// <summary>
    /// Every pair of marks a model might reach for, not only straight doubles. A fabricated line inside
    /// curly quotes, guillemets, low-9 quotes, corner brackets or single quotes is the same fabrication.
    /// A single quote opens only where no word ends and closes only where no word begins, which keeps
    /// "the users' printers and the admins' laptops" from reading as a quotation (T-230). A closing
    /// guillemet does not open one, so "File » Save As" is not a quotation; German's reversed »...« is,
    /// when it closes in the same sentence.
    /// </summary>
    public static readonly IReadOnlyList<string> Quoted =
    [
        "[\"\u201C\u201E\u00AB\u300C\u300E]([^\"\u201C\u201D\u00BB\u300D\u300F]{4,})[\"\u201C\u201D\u00BB\u300D\u300F]",
        "%start%['\u2018\u201A\u2039]([^'\u2018\u2019\u203A]{4,})['\u2018\u2019\u203A]%end%",
        "\u00BB([^\u00AB\u00BB.!?]{4,}[.!?]?)\u00AB",
    ];

    /// <summary>A social security number written with hyphens or spaces.</summary>
    public const string Ssn = "%start%(?!000|666|9%digit%%digit%)%digit%{3}[- ](?!00)%digit%{2}[- ](?!0000)%digit%{4}%end%";

    /// <summary>
    /// Nine unbroken digits are an order number far more often than a social security number, so this
    /// one needs a cue in front of it. No word boundary around the cue, as the client has none:
    /// "Employee_SSN: 123456789" and "SSN123456789" are masked on the device and were accepted here
    /// (2026-10-19 parity check).
    /// </summary>
    public const string SsnCued =
        "(?:ssn|social%space%?security(?:%space%?(?:number|no|#))?)%space%*(?:is|was)?%space%*[:=#]?%space%*"
        + "(?!000|666|9%digit%%digit%)%digit%{3}(?!00)%digit%{2}(?!0000)%digit%{4}%end%";

    /// <summary>A run of 13 to 19 digits, with a space or hyphen allowed after each, that may be a card.</summary>
    public const string Card = "%start%(?:%digit%[ -]?){13,19}%boundary%";

    /// <summary>
    /// A credential's cue word, and only that. <see cref="DraftValidator"/> walks from here past the words
    /// that join a cue to its value. "passcode" is the client's cue word too (2026-10-12 review).
    /// </summary>
    public const string CredentialCue = "%start%(?:password|passphrase|passwd|passcode|pwd|api[ _-]?key|secret|token|bearer)%end%";

    /// <summary>
    /// Words that join a cue to the credential, or that somebody says while remembering one. Skipped
    /// rather than read as the value, and compared in ASCII lower case in both validators. "has", "have"
    /// and "been" let "the password has been reset to Winter2026!" reach its value.
    /// </summary>
    public static readonly IReadOnlyList<string> Connectives =
    [
        "is", "was", "to", "set", "reset", "changed", "change", "now", "will", "be", "equals", "are",
        "uh", "um", "er", "ah", "like", "just", "actually", "currently", "still",
        "the", "a", "an", "on", "for", "of", "at", "in", "my", "your", "our", "their", "his", "her",
        "its", "new", "old", "that", "this", "it", "has", "have", "been",
    ];

    /// <summary>How many words past a cue the walk reads before giving up on finding a credential.</summary>
    public const string WalkSteps = "6";

    /// <summary>
    /// How far a text may grow when it is read: its decomposition may hold this many code points for each
    /// of its own, and <see cref="ReadSlack"/> more. Past that it is refused unread (<see cref="DraftReading"/>).
    /// </summary>
    public const string ReadGrowth = "4";

    /// <summary>What a text's decomposition may hold beyond <see cref="ReadGrowth"/>, so a short field may hold a few ligatures.</summary>
    public const string ReadSlack = "64";

    /// <summary>What ends a sentence, and with it any claim that what follows is the credential.</summary>
    public const string SentenceEnds = ".;!?";

    /// <summary>What ends a word in the walk, besides white space: the marks that end a clause or join a value.</summary>
    public const string ValueBreaks = ",.;!?:=\"'";

    /// <summary>
    /// What separates a credential from a sentence about one: letters and digits, or a long run.
    /// "Outlook prompted for a password repeatedly" and "Password reset completed for jsmith." are notes
    /// a technician would write; "the password is Summer2024" is the thing the rule forbids. A plain word
    /// after a cue is not refused (the owner's option A, 2026-10-24).
    /// </summary>
    public const string SecretShaped = @"^(?=.*[A-Za-z])(?=.*[0-9])[\x21-\x7e]{6,}$|^[\x21-\x7e]{16,}$";

    /// <summary>
    /// Credentials recognisable without a cue word at all, and at least every shape the client's
    /// pattern library masks. A GitHub fine-grained token, a Google key and a JWT were masked on the
    /// device and accepted in a draft (2026-10-12 review). <c>research/tests/test_shared_rules.py</c>
    /// reads the client's list to notice the next one.
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

    /// <summary>
    /// A long value after a named key, as the client masks it: "master key: ...", "client_key=...". The
    /// value is 24 characters or more of what keys are written with.
    /// </summary>
    public const string KeyCueValue =
        "(?:api|access|secret|private|auth|account|client|consumer|signing|encryption|master|app|maps)[ _-]?key"
        + "[\"']?%space%{0,4}[:=]?%space%{0,4}[\"']?[A-Za-z0-9+/_.-]{24,}";

    /// <summary>
    /// A note is a record, not an instruction (prompt rule 5a): a web address, a command line, a
    /// direction to turn off a security control, or to install, run, download or disable something.
    ///
    /// Production's rule from before ST-061 (DraftValidator.cs at main b989436), with each <c>\b</c>,
    /// <c>\s</c> and <c>\S</c> written out and nothing else changed. Which instruction-shaped sentences
    /// are refused is a separate redesign (T-204), not this rule set's business.
    /// </summary>
    public static readonly IReadOnlyList<string> Directive =
    [
        @"(?:https?://|www\.)%nonspace%+",
        @"%start%(?:Set-ExecutionPolicy|powershell|cmd\.exe|regedit|curl|wget|iwr|irm|invoke-webrequest|invoke-restmethod|invoke-expression|iex|certutil|bitsadmin|mshta|rundll32)%end%",
        "%start%(?:disable|turn%space%+off|uninstall|remove)%space%+(?:the%space%+)?(?:antivirus|defender|firewall|edr|mfa|two-factor)%end%",
        "%start%(?:download|install|run|execute|disable|uninstall|delete)%end%%space%+(?:the%space%+|your%space%+|windows%space%+)?%nonspace%+",
    ];

    /// <summary>Every value above, under the name <c>shared-rules.json</c> gives it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AsShared() => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["redaction_markers"] = RedactionMarkers,
        ["placeholders"] = [.. Placeholders.Select(p => $"{p.Key}={p.Value}")],
        ["ignore_case"] = IgnoreCase,
        ["apostrophe"] = [Apostrophe],
        ["apostrophe_as"] = [ApostropheAs],
        ["quoted"] = Quoted,
        ["ssn"] = [Ssn],
        ["ssn_cued"] = [SsnCued],
        ["card"] = [Card],
        ["credential_cue"] = [CredentialCue],
        ["connectives"] = Connectives,
        ["walk_steps"] = [WalkSteps],
        ["read_growth"] = [ReadGrowth],
        ["read_slack"] = [ReadSlack],
        ["sentence_ends"] = [SentenceEnds],
        ["value_breaks"] = [ValueBreaks],
        ["secret_shaped"] = [SecretShaped],
        ["key_shapes"] = KeyShapes,
        ["key_cue_value"] = [KeyCueValue],
        ["directive"] = Directive,
    };

    /// <summary>A pattern with its placeholders written out, as <c>checks.expand</c> writes them.</summary>
    public static string Expand(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        foreach (var (name, value) in Placeholders)
        {
            pattern = pattern.Replace($"%{name}%", value, StringComparison.Ordinal);
        }

        foreach (var (name, value) in DraftReading.Classes)
        {
            pattern = pattern.Replace($"%{name}%", value, StringComparison.Ordinal);
        }

        return pattern.Contains('%', StringComparison.Ordinal)
            ? throw new InvalidOperationException($"An unknown placeholder is left in '{pattern[..Math.Min(80, pattern.Length)]}'.")
            : pattern;
    }

    /// <summary>One of the rules above, compiled as both validators compile it.</summary>
    public static Regex Compile(string name, IReadOnlyList<string> alternatives)
    {
        ArgumentNullException.ThrowIfNull(alternatives);
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant
            | (IgnoreCase.Contains(name) ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(Expand(string.Join('|', alternatives)), options, TimeSpan.FromSeconds(1));
    }
}
