using System.Text.RegularExpressions;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

/// <param name="Kind">What was found; also the <see cref="MaskedRegion"/> kind and the audit counter.</param>
/// <param name="Start">Index in the text the detector ran on.</param>
/// <param name="Replacement">What the text becomes. Never contains any of the matched content (INV-10).</param>
public sealed record PatternMatch(MaskKind Kind, int Start, int Length, string Replacement)
{
    public int End => Start + Length;
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
        "the", "a", "an", "it", "that", "this", "his", "her", "their", "my", "your", "our", "not", "never",
        "and", "or", "but", "so", "then", "when", "which", "what", "who", "why", "how", "if", "for", "to",
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

    /// <summary>How many joining words to walk past before giving up on finding a secret.</summary>
    private const int MaxConnectives = 4;

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
    /// teaches them to turn the rule off.
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
    /// </summary>
    private static (int Start, int Length)? SecretAfter(string text, int from)
    {
        var at = from;

        // A colon or an equals sign right after the cue asserts as much as "is" does: "pass: hunter2".
        var asserted = text.AsSpan(from, Math.Min(4, text.Length - from)).IndexOfAny(':', '=') >= 0;
        for (var steps = 0; steps <= MaxConnectives; steps++)
        {
            var word = NextWord(text, at);
            if (word is null)
            {
                return null;
            }

            var (start, length) = word.Value;
            var value = text.Substring(start, length);
            if (Connectives.Contains(value))
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

            // An ordinary word with a joining word behind it is still part of the run-up: "the password
            // on the router is Winter2026" reaches "router" here, and the secret is two words further
            // on. A word that looks like a secret is taken as one immediately, so "the PIN to 4821 for
            // her" does not walk past 4821 and mask "her".
            if (!LooksLikeSecret(value) && NextWord(text, start + length) is { } following
                && Connectives.Contains(text.Substring(following.Start, following.Length)))
            {
                at = start + length;
                continue;
            }

            // An ordinary word reached without anything asserting a value is the sentence carrying on.
            if (!LooksLikeSecret(value) && !asserted)
            {
                return null;
            }

            return (start, SecretEnd(text, start + length) - start);
        }

        return null;
    }

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
    public static IEnumerable<PatternMatch> Find(string text, RedactionPolicy policy, Action onIncomplete)
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
        }

        if (policy.Passwords)
        {
            foreach (Match m in Safely(PasswordCue().Matches(text), onIncomplete))
            {
                // Keep the cue ("the password is") and mask what follows it, so Review still reads sensibly.
                if (SecretAfter(text, m.Index + m.Length) is { Length: > 0 } secret)
                {
                    yield return new PatternMatch(MaskKind.Password, secret.Start, secret.Length, "[REDACTED]");
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

    // "password", "pwd", "passphrase". Only the cue: where the secret sits after it is a question about
    // how somebody speaks, and a regex answered it by assuming the very next token (2026-09-20 review).
    [GeneratedRegex(
        @"\b(?:password|passphrase|passwd|pwd|pass\s?code|pass(?=\s{0,3}[:=])|pin(?:\s?number)?)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        BuiltInTimeoutMs)]
    private static partial Regex PasswordCue();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant, BuiltInTimeoutMs)]
    private static partial Regex Email();
}
