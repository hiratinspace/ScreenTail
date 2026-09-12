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
    /// <summary>Words that follow a password cue in ordinary speech, so "password is incorrect" isn't a secret.</summary>
    private static readonly HashSet<string> NotSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "incorrect", "wrong", "correct", "expired", "reset", "resets", "changed", "change", "changing", "updated",
        "required", "policy", "manager", "field", "prompt", "box", "again", "here", "there", "empty", "blank",
        "the", "a", "an", "it", "that", "this", "his", "her", "their", "my", "your", "our", "not", "never",
        "and", "or", "but", "so", "then", "when", "which", "what", "who", "why", "how", "if", "for", "to",
    };

    public static IEnumerable<PatternMatch> Find(string text, RedactionPolicy policy)
    {
        if (policy.Ssn)
        {
            foreach (Match m in Ssn().Matches(text))
            {
                yield return new PatternMatch(MaskKind.Ssn, m.Index, m.Length, "[SSN]");
            }
        }

        if (policy.Cards)
        {
            foreach (Match m in CardCandidate().Matches(text))
            {
                // Only a Luhn-valid number is a card. Without this, order numbers and asset tags get masked.
                if (PassesLuhn(m.Value))
                {
                    yield return new PatternMatch(MaskKind.Card, m.Index, m.Length, "[CARD]");
                }
            }
        }

        if (policy.ApiKeys)
        {
            foreach (Match m in ApiKey().Matches(text))
            {
                yield return new PatternMatch(MaskKind.ApiKey, m.Index, m.Length, "[SECRET]");
            }
        }

        if (policy.Passwords)
        {
            foreach (Match m in PasswordPair().Matches(text))
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
            foreach (Match m in Email().Matches(text))
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
                // A tenant pattern that can't keep up is skipped for this frame rather than stalling capture.
                continue;
            }

            while (match.Success && match.Length > 0)
            {
                yield return new PatternMatch(MaskKind.CustomPattern, match.Index, match.Length, "[REDACTED]");
                try
                {
                    match = match.NextMatch();
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Luhn check over the digits of a candidate card number.</summary>
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
    [GeneratedRegex(@"\b(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Ssn();

    // Either a contiguous 13–19 digit number or the usual 4-4-4-4 / 4-6-5 groupings — not an unbroken run of
    // digits across a space, which would let a card swallow whatever number follows it. Luhn decides the rest.
    [GeneratedRegex(@"\b(?:\d{13,19}|\d{3,6}(?:[ -]\d{3,6}){2,4})\b", RegexOptions.CultureInvariant, 100)]
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
          | -----BEGIN[ A-Z]*PRIVATE\ KEY-----
          | (?<=(?i:api[ _-]?key|secret|token|bearer)\s{0,3}[:=]?\s{0,3})[A-Za-z0-9+/_-]{24,}={0,2}
        )
        """,
        RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace,
        100)]
    private static partial Regex ApiKey();

    // "password is Winter2026", "pwd: hunter2", "passphrase = abc". The value is masked, the cue stays.
    [GeneratedRegex(
        @"\b(?:password|passphrase|passwd|pwd|pass\s?code|pin(?:\s?number)?)\b[\s:=]*(?:is|was|to|equals|set\s+to)?[\s:=]*(?<value>[^\s,.;!?]{2,})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        100)]
    private static partial Regex PasswordPair();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Email();
}
