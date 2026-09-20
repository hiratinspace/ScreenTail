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
            foreach (Match m in Safely(PasswordPair().Matches(text), onIncomplete))
            {
                // Keep the cue ("the password is") and mask only the value, so Review still reads sensibly.
                var value = m.Groups["value"];
                if (value.Success && !NotSecrets.Contains(value.Value) && !value.Value.All(char.IsPunctuation))
                {
                    yield return new PatternMatch(MaskKind.Password, value.Index, value.Length, "[REDACTED]");
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
            var regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RedactionPolicy.MatchTimeout);
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
        @"\b(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}\b"
        + @"|(?<=(?i:ssn|social\s?security(?:\s?(?:number|no|#))?)\s{0,3}[:=#]?\s{0,3})(?!000|666|9\d\d)\d{3}(?!00)\d{2}(?!0000)\d{4}\b",
        RegexOptions.CultureInvariant,
        BuiltInTimeoutMs)]
    private static partial Regex Ssn();

    // Either a contiguous 13–19 digit number, or a run of digit groups of any length. The run is a place
    // to look, not a candidate: CardsIn finds the 4-4-4-4 and 4-6-5 shapes inside it. It used to stop at
    // five groups and treat what it had as the number, which is how a card swallowed the CVV beside it,
    // failed Luhn as a whole, and was left alone.
    [GeneratedRegex(@"\b(?:\d{13,19}|(?<g>\d{3,6})(?:[ -](?<g>\d{3,6})){2,})\b", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, BuiltInTimeoutMs)]
    private static partial Regex CardCandidate();

    // Recognisable credential shapes plus any long random-looking string introduced as a key or token.
    [GeneratedRegex(
        """
        (?:
            AKIA[0-9A-Z]{16}
          | gh[pousr]_[A-Za-z0-9]{30,}
          | xox[baprs]-[A-Za-z0-9-]{10,}
          | sk-[A-Za-z0-9_-]{20,}
          | eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}
          | -----BEGIN[\ A-Z]*PRIVATE\ KEY-----(?s:.*?)(?:-----END[\ A-Z]*PRIVATE\ KEY-----|$)
          | (?<=(?i:(?:api|access|secret|private|auth)[ _-]?(?:key|token|secret)?|secret|token|bearer)["']?\s{0,4}[:=]?\s{0,4}["']?)[A-Za-z0-9+/_-]{24,}={0,2}
        )
        """,
        RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace,
        BuiltInTimeoutMs)]
    private static partial Regex ApiKey();

    // "password is Winter2026", "pwd: hunter2", "passphrase = abc". The value is masked, the cue stays.
    [GeneratedRegex(
        @"\b(?:password|passphrase|passwd|pwd|pass\s?code|pin(?:\s?number)?)\b[\s:=]*(?:is|was|to|equals|set\s+to)?[\s:=]*(?<value>[^\s,.;!?]{2,})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        BuiltInTimeoutMs)]
    private static partial Regex PasswordPair();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant, BuiltInTimeoutMs)]
    private static partial Regex Email();
}
