using System.Text.RegularExpressions;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

/// <param name="Kind">What was found; also the <see cref="MaskedRegion"/> kind and the audit counter.</param>
/// <param name="Start">Index in the text the detector ran on.</param>
/// <param name="Replacement">What the text becomes. Never contains any of the matched content (INV-10).</param>
public sealed record PatternMatch(MaskKind Kind, int Start, int Length, string Replacement)
{
    public int End => Start + Length;

    /// <summary>
    /// Where each password cue this match came from starts in the text; empty for any other rule. A
    /// frame's row rule masks the cue's row and every row the match touches. A merge keeps every one,
    /// so a password another rule also claimed keeps its row.
    /// </summary>
    public IReadOnlyList<int> CueStarts { get; init; } = [];
}

/// <summary>
/// The rules that match what the OCR engine makes of a secret rather than the secret as written (ST-042,
/// after #275). All of them run in the product; a test can leave each out to measure what it adds.
/// </summary>
[Flags]
internal enum MisreadRules
{
    None = 0,

    /// <summary>A key's known prefix with its underscore or a letter misread.</summary>
    Prefixes = 1,

    /// <summary>Any run of text that looks random (<see cref="PatternLibrary.RandomRuns"/>).</summary>
    RandomRuns = 2,

    /// <summary>"pass" with its colon misread, and a secret straight after it.</summary>
    BarePass = 4,

    /// <summary>An AWS access key ID with the I of its prefix misread, or a space after the prefix.</summary>
    AwsPrefix = 8,

    /// <summary>A random run's + / and = padding counted toward its length (review of PR #278).</summary>
    Base64Runs = 16,

    /// <summary>Two neighbouring pieces read as one random run where the OCR engine put a space between.</summary>
    SplitRuns = 32,

    /// <summary>"pass" with its colon read as a dash, a bar or an i, and "pw:".</summary>
    PassForms = 64,

    /// <summary>The rules of the round before the review of PR #278, kept to measure the review's against.</summary>
    Round1 = Prefixes | RandomRuns | BarePass,

    All = Round1 | AwsPrefix | Base64Runs | SplitRuns | PassForms,
}

/// <summary>
/// The built-in pattern library (ST-042). Each detector reports spans; <see cref="RedactionEngine"/> decides
/// what to do with them. Detectors never log or return the matched text.
/// </summary>
internal static partial class PatternLibrary
{
    /// <summary>
    /// Match budget for the built-in patterns, in milliseconds. They are linear, so this never fires on
    /// input — only on a machine so loaded that matching stalls. It was 100 ms, which a busy CI agent
    /// tripped; at one second a timeout means something is genuinely wrong.
    /// </summary>
    private const int BuiltInTimeoutMs = 1_000;

    /// <summary>Words that follow a password cue in ordinary speech, so "password is incorrect" isn't a secret.</summary>
    private static readonly HashSet<string> NotSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "incorrect", "wrong", "correct", "expired", "reset", "resets", "changed", "change", "changing", "updated",
        "required", "policy", "manager", "field", "prompt", "box", "again", "here", "there", "empty", "blank",
        "the", "a", "an", "it", "this", "his", "her", "their", "my", "your", "our", "not", "never",
        "and", "or", "but", "so", "then", "what", "who", "why", "how", "if", "for", "to",
    };

    /// <summary>
    /// Words that join a password cue to the password, or that a person says while thinking.
    ///
    /// Skipped rather than masked. The old rule masked the token immediately after the cue, which is
    /// almost never the secret: "the password is, uh, Winter2026" masked "is", "the password on the
    /// router is Winter2026" masked "on", and "the password was reset to Spring2027" masked "reset" and
    /// then dropped it for being an ordinary word. Each of those wrote an audit row saying a redaction
    /// had happened (2026-09-20 review).
    /// </summary>
    private static readonly HashSet<string> Connectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "was", "to", "set", "reset", "changed", "change", "now", "will", "be", "equals", "are",
        "uh", "um", "er", "ah", "like", "just", "actually", "currently", "still",
        "the", "a", "an", "on", "for", "of", "at", "in", "my", "your", "our", "their", "his", "her",
        "its", "new", "old",
    };

    /// <summary>
    /// Words that join a cue to the clause that states its value. "which", "when" and "that" used to end
    /// the walk, so "the password, which is Winter2026" was stored whole (2026-10-12 review, S-12).
    /// Walked past like <see cref="Connectives"/>, but kept apart from them: an ordinary word is only
    /// carried over on a joining word proper, so that "the password is tomato, which they changed" still
    /// masks "tomato".
    ///
    /// Only words the old walk stopped on belong here. "has", "have" and "been" do not: the old walk
    /// read them as ordinary words and masked one reached through an assertion ("the passphrase is have
    /// fun"), so skipping them would store what it masked. Read as ordinary words they still reach a
    /// value further on: the walk carries on past prose ("has been reset to Spring2027"), and a mask
    /// that lands on one runs on to the value (see <see cref="Through"/>).
    /// </summary>
    private static readonly HashSet<string> Clause = new(StringComparer.OrdinalIgnoreCase)
    {
        "which", "when", "that",
    };

    /// <summary>
    /// Words after which a number is a reference, not a credential: "the password for the user on ticket
    /// 48213". Consulted only where the walk is reading past prose that used to end it, with nothing yet
    /// asserting a value; an assertion ("is 48213", "pin: 48213") always wins.
    /// </summary>
    private static readonly HashSet<string> References = new(StringComparer.OrdinalIgnoreCase)
    {
        "ticket", "case", "order", "invoice", "port", "ext", "extension", "build", "version", "line", "id",
        "number", "no", "ref", "reference",
    };

    /// <summary>
    /// How many words past a cue are read before giving up on finding a secret.
    ///
    /// A person says what a password is for before saying what it is: "the password for the domain
    /// admin account is Winter2026" puts the secret seventh. Bounded all the same, because a cue early
    /// in a long paragraph must not put the whole paragraph under suspicion.
    /// </summary>
    private const int MaxWalk = 10;

    /// <summary>
    /// The joining words that assert a value — "is", "was", "set to" — as opposed to the articles and
    /// prepositions that merely continue a sentence. An ordinary word reached only through the latter
    /// is the sentence carrying on ("the PIN for the user", "a new password at first login"), not the
    /// secret; one reached through the former is taken as the secret however plain it looks (ST-115).
    /// </summary>
    private static readonly HashSet<string> Assertive = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "was", "to", "set", "reset", "changed", "change", "now", "will", "be", "equals", "are",
    };

    /// <summary>
    /// How many words of the secret to mask once one is found.
    ///
    /// More than one because a spoken password is often more than one word — "Winter 2026" was masked as
    /// "Winter", which leaves the year in the transcript and the audit log claiming otherwise. Bounded,
    /// because the rest of the sentence is a technician's own account of what they did and masking it
    /// teaches them to turn the rule off. This is the value in the text, which is all a transcript gets.
    /// On a frame, <see cref="RedactionEngine.RedactFrame"/> masks from it to the end of its row on the
    /// screen (owner decision 2026-11-09, #268).
    /// </summary>
    private const int MaxSecretWords = 4;

    /// <summary>
    /// Where the secret is after a password cue, or null when the sentence was about a password rather
    /// than containing one.
    ///
    /// Three things have to be true at once, which is why this is not a regular expression any more.
    /// Joining words are walked past ("was reset to Spring2027"), so are the noises people make while
    /// remembering one ("is, uh, Winter2026"). Words that end a sentence about passwords — "incorrect",
    /// "expired", "blank" — stop it dead, because masking those is what makes a technician turn the rule
    /// off. And a password said as two words ("Winter 2026") has to be masked as two, or the year stays
    /// in the transcript with an audit row claiming otherwise (2026-09-20 review).
    ///
    /// The walk used to give up on the first ordinary word that had no joining word behind it, and after
    /// five words, so "the password for the domain admin account is Winter2026" was stored verbatim with
    /// no audit row (2026-10-12 review, S-12). It reads on now: the words between the cue and the
    /// assertion say what the credential is for, and the secret is the first secret-like word after
    /// them, or a plain word once something has asserted a value. This is the on-device rule (INV-1),
    /// so it only ever grows: every decision the old walk made is made the same way here, and the new
    /// reading starts exactly where the old walk returned null.
    /// </summary>
    private static (int Start, int Length)? SecretAfter(string text, int from)
    {
        var at = from;

        // A colon or an equals sign right after the cue asserts as much as "is" does: "pass: hunter2".
        var asserted = text.AsSpan(from, Math.Min(4, text.Length - from)).IndexOfAny(':', '=') >= 0;

        // Set where the old walk gave up: an ordinary word with nothing asserting a value and no joining
        // word behind it. From there the reading is new, and a number is held to a higher standard.
        var pastProse = false;
        var previous = string.Empty;
        for (var steps = 0; steps < MaxWalk; steps++)
        {
            var word = NextWord(text, at);
            if (word is null)
            {
                return null;
            }

            var (start, length) = word.Value;

            // A colon or an equals sign crossed later in the sentence asserts just the same: "password
            // for the admin account: Winter2026".
            asserted |= text.AsSpan(at, start - at).IndexOfAny(':', '=') >= 0;

            var value = text.Substring(start, length);
            if (Connectives.Contains(value) || Clause.Contains(value))
            {
                asserted |= Assertive.Contains(value);
                at = start + length;
                continue;
            }

            // A sentence about a password, not a password.
            if (NotSecrets.Contains(value))
            {
                return null;
            }

            var secretLike = LooksLikeSecret(value);
            if (!secretLike && FollowedByConnective(text, start + length))
            {
                // An ordinary word with a joining word behind it is still part of the run-up: "the
                // password on the router is Winter2026" reaches "router" here, and the secret is two
                // words further on.
                previous = value;
                at = start + length;
                continue;
            }

            if (secretLike)
            {
                // Taken as the secret at once ("the PIN to 4821 for her" does not walk past 4821 and
                // mask "her"), with one exception: while reading past prose the old walk ended on, with
                // nothing yet asserting a value, a number after "ticket" or "port" is a reference, so
                // "reset the password for the user on ticket 48213" stays as written.
                if (asserted || !pastProse || !References.Contains(previous))
                {
                    // Shouted on a screen, "HAS" and "BEEN" look like secrets too, and so does a dash
                    // standing between words; they are the sentence on its way to one ("PASSWORD HAS
                    // BEEN RESET TO SPRING2027", "password - is Winter2026"), so their mask runs on.
                    var end = SecretEnd(text, start + length);
                    return Auxiliary.Contains(value) || IsPunctuation(value)
                        ? Through(text, start, end, MaxWalk - steps - 1, asserted, takePlain: asserted)
                        : (start, end - start);
                }
            }
            else if (asserted)
            {
                // An ordinary word reached through an assertion is taken as the secret however plain it
                // looks: "Wi-Fi password: sunshine", "the wifi password on the router is sunshine" (ST-115).
                // It may also be the sentence carrying on to the secret ("password: has been reset to
                // Spring2027", "the password is probably Winter2026"), so the mask runs on to a later
                // secret-like word when there is one. It never covers less than the plain word alone.
                return Through(text, start, SecretEnd(text, start + length), MaxWalk - steps - 1, asserted: true, takePlain: Auxiliary.Contains(value));
            }

            // Where the old walk returned null. The sentence is still saying what the credential is for
            // ("the password for the domain admin account is Winter2026" reaches "domain" here), and the
            // walk goes on to the assertion.
            pastProse = true;
            previous = value;
            at = start + length;
        }

        return null;
    }

    /// <summary>
    /// The words that carry a sentence from a cue to its value ("has been reset to") without being part
    /// of either. Read as ordinary words, because the walk has always masked one reached through an
    /// assertion, and a passphrase can start with one ("is have fun"). Where the walk lands on one of
    /// them, its mask runs on to the secret-like word after it instead of stopping short of it.
    /// </summary>
    private static readonly HashSet<string> Auxiliary = new(StringComparer.OrdinalIgnoreCase)
    {
        "has", "have", "had", "been",
    };

    /// <summary>
    /// The span from <paramref name="start"/> to the first secret-like word after
    /// <paramref name="end"/>, within <paramref name="budget"/> words, or the span to
    /// <paramref name="end"/> when there is none. Never shorter than what it was given, so a word the
    /// walk took as the secret stays masked whatever follows it. Stops at a full stop, at a word that
    /// ends a sentence about a password ("but", "expired"), and, with nothing asserting a value,
    /// before a number that follows "ticket" or "port", the same as the walk.
    /// </summary>
    /// <param name="takePlain">
    /// Whether a plain word counts as the end of the secret too, as it does after an asserted
    /// <see cref="Auxiliary"/> word.
    /// </param>
    private static (int Start, int Length) Through(string text, int start, int end, int budget, bool asserted, bool takePlain)
    {
        var at = end;
        var previous = string.Empty;
        for (var steps = 0; steps < budget; steps++)
        {
            if (NextWord(text, at) is not { } word)
            {
                break;
            }

            asserted |= text.AsSpan(at, word.Start - at).IndexOfAny(':', '=') >= 0;
            var value = text.Substring(word.Start, word.Length);
            asserted |= Assertive.Contains(value);
            var joining = Connectives.Contains(value) || Clause.Contains(value) || Auxiliary.Contains(value) || IsPunctuation(value);
            if (!joining && NotSecrets.Contains(value))
            {
                break;
            }

            if (!joining && LooksLikeSecret(value))
            {
                if (!asserted && References.Contains(previous))
                {
                    break;
                }

                return (start, SecretEnd(text, word.Start + word.Length) - start);
            }

            // After an asserted "has", "been" or dash, the first plain word is taken too, the way the
            // walk takes one straight after "is": "pwd=has, tomato", "the passphrase is have fun". With
            // a joining word behind it the sentence is still on its way, and the reading goes on.
            if (!joining && takePlain && !FollowedByJoining(text, word.Start + word.Length))
            {
                return (start, SecretEnd(text, word.Start + word.Length) - start);
            }

            previous = value;
            at = word.Start + word.Length;
        }

        return (start, end - start);
    }

    /// <summary>A word with no letter or digit in it: a dash or a slash standing between words.</summary>
    private static bool IsPunctuation(string value)
    {
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the next word is any of those <see cref="Through"/> reads past: "has since been".</summary>
    private static bool FollowedByJoining(string text, int after)
    {
        if (NextWord(text, after) is not { } following)
        {
            return false;
        }

        var value = text.Substring(following.Start, following.Length);
        return Connectives.Contains(value) || Clause.Contains(value) || Auxiliary.Contains(value) || IsPunctuation(value);
    }

    private static bool FollowedByConnective(string text, int after) =>
        NextWord(text, after) is { } following && Connectives.Contains(text.Substring(following.Start, following.Length));

    /// <summary>
    /// How far the secret runs past its first word.
    ///
    /// Only over things that look like secrets themselves, which is what keeps "Winter 2026" together
    /// and "4821 for her" apart. The rest of the sentence is the technician's own account of what they
    /// did, and masking it is how a privacy feature becomes one that gets switched off.
    /// </summary>
    private static int SecretEnd(string text, int after)
    {
        var end = after;
        for (var words = 1; words < MaxSecretWords; words++)
        {
            if (NextWord(text, end) is not { } next)
            {
                break;
            }

            var value = text.Substring(next.Start, next.Length);
            if (!LooksLikeSecret(value) || Connectives.Contains(value) || NotSecrets.Contains(value))
            {
                break;
            }

            end = next.Start + next.Length;
        }

        return end;
    }

    /// <summary>
    /// Whether a word looks like a credential rather than like English.
    ///
    /// A digit in it, a capital letter somewhere other than the front, a symbol, or simply being longer
    /// than anybody's vocabulary. Deliberately loose: the cost of a false yes is one masked word in a
    /// transcript, and the cost of a false no is a password in a customer's ticket.
    /// </summary>
    private static bool LooksLikeSecret(string value)
    {
        if (value.Length >= 12)
        {
            return true;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsDigit(c) || !char.IsLetter(c) || (i > 0 && char.IsUpper(c)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The next run of word characters, skipping whitespace and the punctuation between words.</summary>
    private static (int Start, int Length)? NextWord(string text, int from)
    {
        var i = from;
        while (i < text.Length && !IsWordChar(text[i]))
        {
            // A full stop ends the sentence, and with it any claim that what follows is the password.
            if (text[i] is '.' or ';' or '!' or '?')
            {
                return null;
            }

            i++;
        }

        if (i >= text.Length)
        {
            return null;
        }

        var start = i;
        while (i < text.Length && IsWordChar(text[i]))
        {
            i++;
        }

        return (start, i - start);
    }

    /// <summary>What counts as part of a word. Punctuation inside a password counts; separators do not.</summary>
    private static bool IsWordChar(char c) => !char.IsWhiteSpace(c) && c is not (',' or '.' or ';' or '!' or '?' or ':' or '=' or '"' or '\'');

    /// <param name="complete">
    /// Set to false when a detector could not finish. The caller must treat the text as unscanned rather
    /// than clean: a frame we failed to search is not a frame we may store (INV-1).
    /// </param>
    public static IEnumerable<PatternMatch> Find(string text, RedactionPolicy policy, Action onIncomplete) =>
        Find(text, policy, onIncomplete, MisreadRules.All);

    /// <param name="misreads">Which of the rules for what the OCR engine misreads run; all of them in the product.</param>
    internal static IEnumerable<PatternMatch> Find(string text, RedactionPolicy policy, Action onIncomplete, MisreadRules misreads)
    {
        if (policy.Ssn)
        {
            foreach (Match m in Safely(Ssn().Matches(text), onIncomplete))
            {
                yield return new PatternMatch(MaskKind.Ssn, m.Index, m.Length, "[SSN]");
            }
        }

        if (policy.Cards)
        {
            foreach (Match m in Safely(CardCandidate().Matches(text), onIncomplete))
            {
                foreach (var (index, length) in CardsIn(m, text))
                {
                    yield return new PatternMatch(MaskKind.Card, index, length, "[CARD]");
                }
            }
        }

        if (policy.ApiKeys)
        {
            foreach (Match m in Safely(ApiKey().Matches(text), onIncomplete))
            {
                yield return new PatternMatch(MaskKind.ApiKey, m.Index, m.Length, "[SECRET]");
            }

            if (misreads.HasFlag(MisreadRules.Prefixes))
            {
                foreach (Match m in Safely(MisreadKey().Matches(text), onIncomplete))
                {
                    yield return new PatternMatch(MaskKind.ApiKey, m.Index, m.Length, "[SECRET]");
                }
            }

            if (misreads.HasFlag(MisreadRules.AwsPrefix))
            {
                foreach (Match m in Safely(MisreadAwsKey().Matches(text), onIncomplete))
                {
                    yield return new PatternMatch(MaskKind.ApiKey, m.Index, m.Length, "[SECRET]");
                }
            }

            if (misreads.HasFlag(MisreadRules.RandomRuns))
            {
                foreach (var (start, length) in RandomRuns(text, misreads))
                {
                    yield return new PatternMatch(MaskKind.ApiKey, start, length, "[SECRET]");
                }
            }
        }

        if (policy.Passwords)
        {
            foreach (Match m in Safely(PasswordCue().Matches(text), onIncomplete))
            {
                // Keep the cue ("the password is") and mask what follows it, so Review still reads sensibly.
                if (SecretAfter(text, m.Index + m.Length) is { Length: > 0 } secret)
                {
                    yield return new PatternMatch(MaskKind.Password, secret.Start, secret.Length, "[REDACTED]") { CueStarts = [m.Index] };
                }
            }

            var forms = misreads.HasFlag(MisreadRules.PassForms);
            foreach (Match m in Safely(misreads.HasFlag(MisreadRules.BarePass) ? BarePassCue().Matches(text) : NeverMatches().Matches(string.Empty), onIncomplete))
            {
                if (SecretRightAfter(text, m.Index + m.Length, forms) is { Length: > 0 } secret)
                {
                    yield return new PatternMatch(MaskKind.Password, secret.Start, secret.Length, "[REDACTED]") { CueStarts = [m.Index] };
                }
            }

            foreach (Match m in Safely(forms ? PassFormCue().Matches(text) : NeverMatches().Matches(string.Empty), onIncomplete))
            {
                if (SecretRightAfter(text, m.Index + m.Length, forms) is { Length: > 0 } secret)
                {
                    yield return new PatternMatch(MaskKind.Password, secret.Start, secret.Length, "[REDACTED]") { CueStarts = [m.Index] };
                }
            }
        }

        if (policy.Emails)
        {
            foreach (Match m in Safely(Email().Matches(text), onIncomplete))
            {
                yield return new PatternMatch(MaskKind.Email, m.Index, m.Length, "[EMAIL]");
            }
        }

        foreach (var pattern in policy.CustomPatterns)
        {
            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RedactionPolicy.MatchTimeout);
            }
            catch (ArgumentException)
            {
                // An administrator types these (ST-047), and a typed regex is a regex with a typo in it
                // sooner or later. Building it threw on the first frame of every session and took the
                // redaction worker's loop with it, so the tenant that configured the pattern was the one
                // that lost capture (2026-09-19 review).
                //
                // Skipped and reported incomplete, like a pattern that ran out of time: a rule that did
                // not run is text that was not searched, and ADR-0004 discards such a frame rather than
                // storing it.
                onIncomplete();
                continue;
            }

            Match match;
            try
            {
                match = regex.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                // Skipped for this frame rather than stalling capture — but the text was then not fully
                // searched, and saying nothing here reported it as clean. The built-ins have always
                // called this; the custom path did not, so a tenant pattern that timed out let every
                // account number on the screen be stored with Complete = true (INV-1).
                onIncomplete();
                continue;
            }

            while (match.Success)
            {
                if (match.Length > 0)
                {
                    yield return new PatternMatch(MaskKind.CustomPattern, match.Index, match.Length, "[REDACTED]");
                }
                else if (match.Index >= text.Length)
                {
                    break;
                }

                try
                {
                    // A zero-length match returns itself from NextMatch for ever, so it used to end the
                    // scan — silently, part-way through, leaving the rest of the text unsearched while
                    // reporting it clean. Stepping past it keeps going.
                    match = match.Length > 0 ? match.NextMatch() : regex.Match(text, match.Index + 1);
                }
                catch (RegexMatchTimeoutException)
                {
                    onIncomplete();
                    break;
                }
            }
        }
    }

    /// <summary>How many letters and digits a run needs before <see cref="RandomRuns"/> reads it as random.</summary>
    internal const int RandomRunLength = 16;

    /// <summary>
    /// The share of neighbouring letters and digits in a run that change class (capital, small letter,
    /// digit) at which <see cref="RandomRuns"/> reads it as random. A random string of the 62 changes at
    /// about 0.62 of its pairs; a name built of words and a number, "Office365ProPlusRetail" or
    /// "Int32.TryParse(value", at 0.35 or less.
    /// </summary>
    internal const double RandomRunChanges = 0.4;

    /// <summary>
    /// How often, as a share of a run's neighbouring pairs of letters and digits, one letter is a capital
    /// and the next letter a small one or the other way, digits between them skipped, at which
    /// <see cref="RandomRuns"/> reads it as random. A random string does at about 0.4 of its pairs, and
    /// "A1b2C3d4E5f6G7h8" at 0.47. A hexadecimal number joined to a word does once or twice: the wrap pass joins the
    /// GUID that ends a table's row to the device name that starts the next, "5b8e0f1a2c3dAccounts-PC23",
    /// and without this the hosted runner's table pages lost 58.6% of their ink to it, against 11.1%.
    /// </summary>
    internal const double RandomRunCaseChanges = 0.15;

    /// <summary>
    /// Every run of text between spaces and separators (<see cref="EndsRun"/>) that looks random, whatever
    /// introduces it (ST-042, after #275):
    /// at least <see cref="RandomRunLength"/> letters and digits, with a capital, a small letter and a
    /// digit among them, changing class at no less than <see cref="RandomRunChanges"/> of its pairs and
    /// between a capital and a small letter at no less than <see cref="RandomRunCaseChanges"/>.
    ///
    /// The shape rules need a prefix or a cue read right, and the OCR engine reads a long random string
    /// worst of all: on the hosted runner's probe it misread the prefix or cue of most of the keys and
    /// tokens it did not mask, while reading their bodies well enough to be seen to be random. This
    /// infers nothing about what the string is for. Measured on random strings, it takes 91% of 16
    /// characters, 98% of 26 and 99% of 36; it takes no word of prose, and names in code only where they change
    /// case and digit as often as random text does. The wrap pass reads a key cut by its row's end as
    /// one run, so a piece too short to be taken alone is taken with the piece below it.
    ///
    /// After the review of PR #278: base64's + and / and its = padding count toward the length
    /// (<see cref="MisreadRules.Base64Runs"/>), and two neighbouring pieces with one space between, a random
    /// run and a short piece or two short pieces (shorter than <see cref="RandomRunLength"/>, with a digit
    /// or two changes of case), are taken as one run where together they are
    /// (<see cref="MisreadRules.SplitRuns"/>): the OCR engine splits a key with a space, and two halves of
    /// a 16 to 31 character key were each too short. The join is in the text; on the screen the words
    /// the match touches are masked, and the token rule takes what touches them, as for any match.
    /// </summary>
    internal static IEnumerable<(int Start, int Length)> RandomRuns(string text, MisreadRules rules = MisreadRules.All)
    {
        var symbols = rules.HasFlag(MisreadRules.Base64Runs);
        var split = rules.HasFlag(MisreadRules.SplitRuns);
        (int Start, int End, bool Random)? held = null;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && EndsRun(text, i, symbols))
            {
                i++;
            }

            var start = i;
            while (i < text.Length && !EndsRun(text, i, symbols))
            {
                i++;
            }

            if (i == start)
            {
                continue;
            }

            var run = Shape(text.AsSpan(start, i - start), symbols);
            if (run.Random)
            {
                yield return (start, i - start);
            }

            // Two pieces with one space between, at least one of them not random alone: a random run and
            // the short piece the engine cut from it, or two short halves. A piece is short and has a
            // digit or changes case twice, so a word of prose, the cue before a key, and a long name that
            // is not random alone are not halves of anything.
            var joins = run.Random || run.Piece;
            if (split && joins && held is var (heldStart, heldEnd, heldRandom) && !(heldRandom && run.Random)
                && start == heldEnd + 1 && text[heldEnd] == ' '
                && Shape(string.Concat(text.AsSpan(heldStart, heldEnd - heldStart), text.AsSpan(start, i - start)), symbols).Random)
            {
                yield return (heldStart, i - heldStart);
            }

            held = joins ? (start, i, run.Random) : null;
        }
    }

    /// <summary>Whether a run looks random, and whether it could be a piece of one: short, with a digit or two changes of case.</summary>
    private static (bool Random, bool Piece) Shape(ReadOnlySpan<char> run, bool symbols)
    {
        var (count, length, changes, cases, upper, lower, digit, previous, letter) = (0, 0, 0, 0, false, false, false, 0, 0);
        foreach (var c in run)
        {
            var kind = char.IsAsciiLetterUpper(c) ? 1 : char.IsAsciiLetterLower(c) ? 2 : char.IsAsciiDigit(c) ? 3 : 0;
            if (kind == 0)
            {
                // Base64's symbols make up its length but say nothing of its letters.
                length += symbols && c is '+' or '/' or '=' ? 1 : 0;
                continue;
            }

            (upper, lower, digit) = (upper || kind == 1, lower || kind == 2, digit || kind == 3);
            changes += previous != 0 && previous != kind ? 1 : 0;
            if (kind != 3)
            {
                // Between letters, with any digits between them skipped: "A1b2C3d4" changes case
                // at every letter, though no two neighbours are a capital and a small letter.
                cases += letter != 0 && letter != kind ? 1 : 0;
                letter = kind;
            }

            previous = kind;
            count++;
            length++;
        }

        var random = length >= RandomRunLength && upper && lower && digit
            && changes >= RandomRunChanges * (count - 1) && cases >= RandomRunCaseChanges * (count - 1);
        return (random, length < RandomRunLength && (digit || cases >= 2));
    }

    /// <summary>
    /// What ends a run for <see cref="RandomRuns"/>: a space, or a separator that stands between a name
    /// and its value, so "api_key=A1b2C3d4E5f6G7h8I9j0K1l2M3n4" keeps its cue readable as the cue rule
    /// does. A full stop does not: a signed token is three runs joined by them.
    /// </summary>
    private static bool EndsRun(char c) => char.IsWhiteSpace(c) || c is '=' or ':' or '"' or '\'' or ',' or ';';

    /// <summary>
    /// <see cref="EndsRun(char)"/> at <paramref name="at"/>, except, with <paramref name="padding"/>, an
    /// = that ends a run, as base64's padding does: after a letter, digit or symbol of the run and before
    /// another = or whatever ends a run. "api_key=..." still parts its cue from its value.
    /// </summary>
    private static bool EndsRun(string text, int at, bool padding)
    {
        if (!padding || text[at] != '=' || at == 0 || EndsRun(text[at - 1]) && text[at - 1] != '=')
        {
            return EndsRun(text[at]);
        }

        var next = at + 1;
        while (next < text.Length && text[next] == '=')
        {
            next++;
        }

        return next < text.Length && !EndsRun(text[next]);
    }

    /// <summary>
    /// The secret straight after a bare "pass" with no colon, or with the colon read as a full stop or
    /// a comma: "pass Tangerine3327Wobble", "pass. Juniper8749Quiver". The OCR engine read "pass:" so on
    /// 6 of 80 pages at 125% and on 11 to 19 of 80 through a viewer at 0.6, 0.8 and 0.9 (hosted runner,
    /// after #275), and the cue rule, which needs the colon to tell "pass" from the verb, never fired.
    ///
    /// Only the very next word is taken, and only when it is at least six characters with a digit or a
    /// symbol in it, so "pass the test", "PASS FAIL" and "pass it on" are left alone. It takes its row
    /// the way any password does.
    ///
    /// With <see cref="MisreadRules.PassForms"/> (review of PR #278) the colon may also have been read as
    /// a dash or a bar, "pass - X" and "pass| X", and two more cues take the same next word: "passi", the
    /// colon read as an i, and "pw:".
    /// </summary>
    private static (int Start, int Length)? SecretRightAfter(string text, int from, bool forms)
    {
        var i = from;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] is '.' or ',' or ';' || forms && text[i] is '-' or '|' or ':' or '='))
        {
            i++;
        }

        var start = i;
        while (i < text.Length && IsWordChar(text[i]))
        {
            i++;
        }

        if (i - start < 6)
        {
            return null;
        }

        var value = text.Substring(start, i - start);
        if (!LooksLikeSecret(value) || NotSecrets.Contains(value) || Connectives.Contains(value) || !value.Any(c => char.IsDigit(c) || !char.IsLetter(c)))
        {
            return null;
        }

        return (start, SecretEnd(text, i) - start);
    }

    /// <summary>
    /// Enumerates matches, stopping quietly if the regex engine runs out of its budget. The caller is told
    /// through <paramref name="onIncomplete"/> so it can fail closed; swallowing it would mean reporting a
    /// frame as clean that was never fully searched.
    /// </summary>
    private static IEnumerable<Match> Safely(MatchCollection matches, Action onIncomplete)
    {
        var enumerator = matches.GetEnumerator();
        while (true)
        {
            Match current;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                current = (Match)enumerator.Current!;
            }
            catch (RegexMatchTimeoutException)
            {
                onIncomplete();
                yield break;
            }

            yield return current;
        }
    }

    /// <summary>Luhn check over the digits of a candidate card number.</summary>
    /// <summary>
    /// The card numbers inside one run of digit groups.
    ///
    /// A run is not a card. OCR joins a page's words with single spaces, so whatever sits beside the
    /// number on a payment form — the CVV, the expiry, a zip code, an order number — arrives in the same
    /// run. Taking the run as the candidate meant the checksum was computed over the card <i>and</i> its
    /// neighbour, failed, and the card went through unmasked with its CVV beside it (2026-09-19 review).
    ///
    /// So when the run as a whole is not a card, every window of three to five whole groups inside it is
    /// tried. Whole groups, because a card does not start halfway through a word.
    ///
    /// <b>Every window that passes is masked, and overlaps are merged.</b> One window in ten passes the
    /// checksum by accident, and choosing between a true window and an accidental one that overlaps it
    /// risks choosing wrong and leaving four digits of a real card on the screen. Masking the union
    /// costs a neighbouring number now and then and never leaves part of a card behind.
    ///
    /// Windows must begin with 2 to 6, the digits payment cards begin with. That is what keeps a
    /// spreadsheet of ordinary numbers from coming back full of holes. It is not applied to a run that
    /// is a card all by itself, which behaves exactly as it always has.
    /// </summary>
    private static List<(int Index, int Length)> CardsIn(Match run, string text)
    {
        // Only a Luhn-valid number is a card. Without this, order numbers and asset tags get masked.
        if (PassesLuhn(run.ValueSpan))
        {
            return [(run.Index, run.Length)];
        }

        var groups = run.Groups["g"].Captures;
        var found = new List<(int Start, int End)>();
        for (var first = 0; first + MinCardGroups <= groups.Count; first++)
        {
            if (text[groups[first].Index] is < '2' or > '6')
            {
                continue;
            }

            var last = Math.Min(groups.Count, first + MaxCardGroups) - 1;
            for (; last >= first + MinCardGroups - 1; last--)
            {
                var start = groups[first].Index;
                var end = groups[last].Index + groups[last].Length;
                if (PassesLuhn(text.AsSpan(start, end - start)))
                {
                    found.Add((start, end));
                }
            }
        }

        // Already ordered by start. Merge anything that touches, so callers never see overlapping masks.
        var merged = new List<(int Index, int Length)>();
        foreach (var (start, end) in found)
        {
            if (merged.Count > 0 && start <= merged[^1].Index + merged[^1].Length)
            {
                var previous = merged[^1];
                merged[^1] = (previous.Index, Math.Max(previous.Index + previous.Length, end) - previous.Index);
            }
            else
            {
                merged.Add((start, end - start));
            }
        }

        return merged;
    }

    /// <summary>4-4-4-4 is four groups, 4-6-5 is three, and nothing in use has more than five.</summary>
    private const int MinCardGroups = 3;

    private const int MaxCardGroups = 5;

    internal static bool PassesLuhn(ReadOnlySpan<char> candidate)
    {
        var sum = 0;
        var digits = 0;
        var doubling = false;
        for (var i = candidate.Length - 1; i >= 0; i--)
        {
            var c = candidate[i];
            if (!char.IsAsciiDigit(c))
            {
                continue;
            }

            var value = c - '0';
            if (doubling)
            {
                value *= 2;
                if (value > 9)
                {
                    value -= 9;
                }
            }

            sum += value;
            digits++;
            doubling = !doubling;
        }

        return digits is >= 13 and <= 19 && sum % 10 == 0;
    }

    // 123-45-6789 and 123 45 6789; the exclusions are the ranges the SSA never issues.
    //
    // A bare 123456789 is included only where a cue says what it is. Nine digits on their own are an order
    // number, a part number or a phone number far more often than a social security number, and ST-042
    // budgets 2% false positives — masking every nine-digit run would spend that many times over on one
    // screen of a customer's order history.
    [GeneratedRegex(
        @"\b(?!000|666|9\d\d)\d{3}[-. ](?!00)\d{2}[-. ](?!0000)\d{4}\b"
        + @"|(?<=(?i:ssn|social\s?security(?:\s?(?:number|no|#))?)\s{0,3}[:=#]?\s{0,3})(?!000|666|9\d\d)\d{3}(?!00)\d{2}(?!0000)\d{4}\b",
        RegexOptions.CultureInvariant,
        BuiltInTimeoutMs)]
    private static partial Regex Ssn();

    // Either a contiguous 13–19 digit number, or a run of digit groups of any length. The run is a place
    // to look, not a candidate: CardsIn finds the 4-4-4-4 and 4-6-5 shapes inside it. It used to stop at
    // five groups and treat what it had as the number, which is how a card swallowed the CVV beside it,
    // failed Luhn as a whole, and was left alone.
    [GeneratedRegex(@"\b(?:\d{13,19}|(?<g>\d{3,6})(?:[ .-](?<g>\d{3,6})){2,})\b", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, BuiltInTimeoutMs)]
    private static partial Regex CardCandidate();

    // Recognisable credential shapes plus any long random-looking string introduced as a key or token.
    [GeneratedRegex(
        """
        (?:
            AKIA[0-9A-Z]{16}
          | gh[pousr]_[A-Za-z0-9]{30,}
          | github_pat_[A-Za-z0-9_]{22,}
          | xox[baprs]-[A-Za-z0-9-]{10,}
          | https://hooks\.slack\.com/services/[A-Za-z0-9/_-]{10,}
          | sk-[A-Za-z0-9_-]{20,}
          | sk_(?:live|test)_[A-Za-z0-9]{16,}
          | npm_[A-Za-z0-9]{30,}
          | AIza[0-9A-Za-z_-]{30,}
          | SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}
          | ya29\.[A-Za-z0-9._-]{20,}
          | eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}
          | -----BEGIN[\ A-Z]*PRIVATE\ KEY(?:\ BLOCK)?-----(?s:.*?)(?:-----END[\ A-Z]*PRIVATE\ KEY(?:\ BLOCK)?-----|$)
          | (?<=(?i:(?:api|access|secret|private|auth|account|client|consumer|signing|encryption|master|app|maps)[ _-]?(?:key|token|secret)?|secret|token|bearer)["']?\s{0,4}[:=]?\s{0,4}["']?)[A-Za-z0-9+/_.-]{24,}={0,2}
        )
        """,
        RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace,
        BuiltInTimeoutMs)]
    private static partial Regex ApiKey();

    // The GitHub, Stripe and npm prefixes as the OCR engine reads them on a screen (ST-042, after
    // #275). On the hosted runner's probe the engine read the underscore of "ghp_" and "sk_live_" as a
    // space, as nothing, or as punctuation, and the l of "live" as 1, I or |, on 165 of the 264 secrets
    // it misread at 100% to 150% display scale; one character wrong in the prefix and ApiKey never
    // fired, however well the rest was read. These shapes are not words in any language.
    [GeneratedRegex(
        @"gh[pousr][ _.,-]?[A-Za-z0-9]{30,}|sk[ _.,-]?(?:[lI1|i]ive|t[e3]st)[ _.,-]?[A-Za-z0-9]{16,}|npm[ _.,-]?[A-Za-z0-9]{30,}",
        RegexOptions.CultureInvariant,
        BuiltInTimeoutMs)]
    private static partial Regex MisreadKey();

    // "password", "pwd", "passphrase". Only the cue: where the secret sits after it is a question about
    // how somebody speaks, and a regex answered it by assuming the very next token (2026-09-20 review).
    [GeneratedRegex(
        @"\b(?:password|passphrase|passwd|pwd|pass\s?code|pass(?=\s{0,3}[:=])|pin(?:\s?number)?)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        BuiltInTimeoutMs)]
    private static partial Regex PasswordCue();

    [GeneratedRegex("(?!)", RegexOptions.CultureInvariant)]
    private static partial Regex NeverMatches();

    // An AWS access key ID as the OCR engine reads it (review of PR #278): the I of AKIA or ASIA read as
    // 1, l or |, or a space after the prefix. ApiKey takes AKIA read right. Sixteen capitals and digits
    // and no more, so "ASIA PACIFIC" and words that start so are left alone.
    [GeneratedRegex(@"\bA[KS][I1l|]A ?[0-9A-Z]{16}\b", RegexOptions.CultureInvariant, BuiltInTimeoutMs)]
    private static partial Regex MisreadAwsKey();

    // "passi", "pass:" with the colon read as an i, and "pw:" (see SecretRightAfter).
    [GeneratedRegex(@"\b(?:passi\b|pw(?=\s{0,3}[:=]))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, BuiltInTimeoutMs)]
    private static partial Regex PassFormCue();

    // "pass" with no colon after it, as the OCR engine reads "pass:" (see SecretRightAfter).
    [GeneratedRegex(@"\bpass\b(?!\s{0,3}[:=])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, BuiltInTimeoutMs)]
    private static partial Regex BarePassCue();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant, BuiltInTimeoutMs)]
    private static partial Regex Email();
}
