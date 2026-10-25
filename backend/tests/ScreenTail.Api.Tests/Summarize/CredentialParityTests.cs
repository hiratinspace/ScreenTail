using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// Everything production refused as a credential, a social security number or a card is still refused
/// (ST-061, the owner's option A of 2026-10-24).
///
/// Option A keeps the rule to credential shapes, as production had it, and adds shapes: so the new rules
/// may refuse more, never less. <see cref="Production"/> is production's secret rules, copied verbatim from
/// <c>backend/src/ScreenTail.Api/Summarize/DraftValidator.cs</c> at main b989436, and every hardening
/// case, every differential case and a generated set are put to both.
/// </summary>
public sealed class CredentialParityTests
{
    [Fact]
    public void WhateverProductionRefusedAsASecretIsStillRefused()
    {
        var compared = 0;
        var added = new List<string>();
        var lost = new List<string>();
        foreach (var text in Corpus().Concat(Generated()))
        {
            compared++;
            var production = Production.Refuses(text);
            var ours = DraftValidator.Secrets("result", text).Any();
            if (production && !ours)
            {
                lost.Add(Show(text));
            }
            else if (ours && !production)
            {
                added.Add(Show(text));
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{compared} sentences compared with production's secret rules; {added.Count} newly refused, such as {string.Join(", ", added.Where((_, i) => i % Math.Max(1, added.Count / 12) == 0).Take(12))}; {lost.Count} refusals lost.");
        Assert.True(lost.Count == 0, $"{lost.Count} sentences production refused as a secret now pass: " + string.Join(", ", lost.Take(5)));
    }

    [Theory]
    [InlineData("Password reset completed for jsmith.")]
    [InlineData("The password was reset successfully.")]
    [InlineData("The password was accepted after the reset.")]
    [InlineData("The password was changed by the user at 10:40.")]
    [InlineData("The PIN pad at reception was replaced.")]
    public void ASentenceAboutAPasswordIsNotOne(string text)
    {
        // Option A: no plain-word walk. Production passed these, and so does this.
        Assert.False(Production.Refuses(text));
        Assert.Empty(DraftValidator.Secrets("result", text));
    }

    private static IEnumerable<string> Generated()
    {
        (string Sentence, string[] Places)[] sentences =
        [
            ("The new password is Winter2026!", ["password", "Winter2026!"]),
            ("SSN 123456789 was showing in the record.", ["SSN", "123456789"]),
            ("The card 4111 1111 1111 1111 was on screen.", ["4111 1111", "1111 was"]),
            ("Her social security number is 123-45-6789 on the form.", ["security", "123-45-6789"]),
        ];

        var units = Enumerable.Range(0, 0x10000).Where(c => c % 3 == 0 || c < 0x3000).Select(c => ((char)c).ToString());
        foreach (var inserted in units)
        {
            foreach (var (sentence, places) in sentences)
            {
                foreach (var place in places)
                {
                    var at = sentence.IndexOf(place, StringComparison.Ordinal);
                    var end = at + place.Length;
                    yield return sentence[..at] + inserted + sentence[at..];
                    yield return sentence[..(at + 1)] + inserted + sentence[(at + 1)..];
                    yield return sentence[..end] + inserted + sentence[end..];
                }
            }
        }
    }

    private static IEnumerable<string> Corpus()
    {
        foreach (var name in new[] { "hardening-cases.json", "differential-cases.json" })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Api.Tests." + name)
                ?? throw new InvalidOperationException($"{name} is not embedded in this test assembly.");
            using var document = JsonDocument.Parse(stream);
            foreach (var one in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                yield return one.TryGetProperty("text", out var text)
                    ? text.GetString()!
                    : string.Concat(one.GetProperty("parts").EnumerateArray().Select(p => p.GetString()));
            }
        }
    }

    private static string Show(string text)
    {
        var shown = new StringBuilder("'");
        foreach (var c in text)
        {
            _ = c is >= ' ' and <= '~' ? shown.Append(c) : shown.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
        }

        return shown.Append('\'').ToString();
    }

    /// <summary>
    /// Production's secret rules before ST-061: copied verbatim, patterns, options, word list and walk,
    /// from DraftValidator.cs at main b989436. Built under the invariant culture, as production runs.
    /// </summary>
    private static class Production
    {
        private static readonly string[] Redactions = ["[REDACTED]", "[CARD]", "[SSN]", "[SECRET]", "[EMAIL]"];

        private static readonly Regex Ssn = Invariant(() => new Regex(
            @"\b(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}\b",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(1)));

        private static readonly Regex SsnCued = Invariant(() => new Regex(
            @"\b(?:ssn|social\s?security(?:\s?(?:number|no|#))?)\b\s*(?:is|was)?\s*[:=#]?\s*"
            + @"(?!000|666|9\d\d)\d{3}(?!00)\d{2}(?!0000)\d{4}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1)));

        private static readonly Regex LongDigits = Invariant(() => new Regex(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)));

        private static readonly Regex CredentialCue = Invariant(() => new Regex(
            @"\b(?:password|passphrase|passwd|pwd|api[ _-]?key|secret|token|bearer)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1)));

        private static readonly HashSet<string> Connectives = new(StringComparer.OrdinalIgnoreCase)
        {
            "is", "was", "to", "set", "reset", "changed", "change", "now", "will", "be", "equals", "are",
            "uh", "um", "er", "ah", "like", "just", "actually", "currently", "still",
            "the", "a", "an", "on", "for", "of", "at", "in", "my", "your", "our", "their", "his", "her",
            "its", "new", "old", "that", "this", "it",
        };

        private const int MaxSteps = 6;

        private static readonly Regex SecretShaped = Invariant(() => new Regex(
            @"^(?=.*[A-Za-z])(?=.*\d)[\x21-\x7e]{6,}$|^[\x21-\x7e]{16,}$",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(1)));

        private static readonly Regex KeyShape = Invariant(() => new Regex(
            @"AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9_-]{20,}"
            + @"|-----BEGIN[ A-Z]*PRIVATE KEY-----",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(1)));

        public static bool Refuses(string text)
        {
            var withoutMarkers = Redactions.Aggregate(text, (current, marker) => current.Replace(marker, " ", StringComparison.Ordinal));
            return CarriesACredential(withoutMarkers)
                || Ssn.IsMatch(withoutMarkers) || SsnCued.IsMatch(withoutMarkers)
                || (LongDigits.IsMatch(withoutMarkers) && Luhn(withoutMarkers));
        }

        private static bool CarriesACredential(string text)
        {
            if (KeyShape.IsMatch(text))
            {
                return true;
            }

            foreach (Match cue in CredentialCue.Matches(text))
            {
                if (CredentialAfter(text, cue.Index + cue.Length))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CredentialAfter(string text, int start)
        {
            var at = start;
            for (var step = 0; step < MaxSteps; step++)
            {
                if (NextWord(text, at) is not { } word)
                {
                    return false;
                }

                var value = text.Substring(word.Start, word.Length);
                if (Connectives.Contains(value))
                {
                    at = word.Start + word.Length;
                    continue;
                }

                if (value.StartsWith('['))
                {
                    return false;
                }

                if (SecretShaped.IsMatch(value))
                {
                    return true;
                }

                if (NextWord(text, word.Start + word.Length) is { } following)
                {
                    var next = text.Substring(following.Start, following.Length);
                    if (SecretShaped.IsMatch(value + next))
                    {
                        return true;
                    }

                    if (Connectives.Contains(next))
                    {
                        at = word.Start + word.Length;
                        continue;
                    }
                }

                return false;
            }

            return false;
        }

        private static (int Start, int Length)? NextWord(string text, int from)
        {
            var i = from;
            while (i < text.Length && !IsWordChar(text[i]))
            {
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

            var begin = i;
            while (i < text.Length && IsWordChar(text[i]))
            {
                i++;
            }

            return (begin, i - begin);
        }

        private static bool IsWordChar(char c) =>
            !char.IsWhiteSpace(c) && c is not (',' or '.' or ';' or '!' or '?' or ':' or '=' or '"' or '\'');

        private static bool Luhn(string text)
        {
            foreach (Match match in LongDigits.Matches(text))
            {
                var digits = match.Value.Where(char.IsDigit).Select(c => c - '0').Reverse().ToArray();
                var total = 0;
                for (var i = 0; i < digits.Length; i++)
                {
                    var digit = digits[i];
                    if (i % 2 == 1)
                    {
                        digit *= 2;
                        if (digit > 9)
                        {
                            digit -= 9;
                        }
                    }

                    total += digit;
                }

                if (digits.Length >= 13 && total % 10 == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static T Invariant<T>(Func<T> build)
        {
            var was = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                return build();
            }
            finally
            {
                CultureInfo.CurrentCulture = was;
            }
        }
    }
}
