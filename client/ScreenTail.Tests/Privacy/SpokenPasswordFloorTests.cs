using System.Text;
using System.Text.RegularExpressions;
using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// The spoken-password walk may grow and never shrink (INV-1; 2026-10-12 review, S-12).
///
/// The walk was widened to reach a secret said after a long run-up, and twice a widening also stopped
/// masking something the walk had masked before; each time the example rows still passed. So the
/// walk as it stood before the widening is kept here, frozen, as <see cref="Floor"/>, and a generated
/// corpus of cue sentences is put through both. Any character the frozen walk masks and the live one
/// does not fails the test. The frozen copy is the floor: it is never edited to make this pass.
/// </summary>
public sealed class SpokenPasswordFloorTests
{
    private static readonly RedactionPolicy PasswordsOnly = new() { Ssn = false, Cards = false, ApiKeys = false, Passwords = true };

    [Fact]
    public void TheWalkMasksEverythingTheWalkBeforeItMasked()
    {
        var tried = 0;
        var floorMasked = 0;
        var narrower = new List<string>();
        foreach (var text in Corpus())
        {
            tried++;
            var floor = Covered(text, Floor.Find(text));
            var live = Covered(text, PatternLibrary.Find(text, PasswordsOnly, () => { }).Select(m => (m.Start, m.Length)));
            if (Array.IndexOf(floor, true) >= 0)
            {
                floorMasked++;
            }

            if (floor.Where((masked, i) => masked && !live[i]).Any() && narrower.Count < 10)
            {
                narrower.Add(text);
            }
        }

        // The corpus has to exercise the walk for the comparison to mean anything.
        Assert.True(tried > 200_000, $"only {tried} sentences tried");
        Assert.True(floorMasked > tried / 4, $"the frozen walk masked only {floorMasked} of {tried}");
        Assert.True(narrower.Count == 0, "masked before and not now:\n" + string.Join('\n', narrower));
    }

    [Fact]
    public void TheFloorIsTheWalkBeforeTheWidening()
    {
        // Pins the frozen copy itself, so an edit to it shows up here rather than as a silently weaker
        // floor: what it masked and what it left, including what the widening set out to reach.
        Assert.Equal("the password is [X]", Apply("the password is Winter2026"));
        Assert.Equal("pwd: [X]", Apply("pwd: hunter2"));
        Assert.Equal("password is [X]", Apply("password is Winter 2026"));
        Assert.Equal("the passphrase is [X] fun", Apply("the passphrase is have fun"));
        Assert.Equal("the password is uh the new [X]", Apply("the password is uh the new Winter2026"));
        Assert.Equal("the password is uh um the new Winter2026", Apply("the password is uh um the new Winter2026"));
        Assert.Equal("the password on the router is [X]", Apply("the password on the router is sunshine"));
        Assert.Equal("the password is incorrect", Apply("the password is incorrect"));
        Assert.Equal("the password, which is Winter2026", Apply("the password, which is Winter2026"));
        Assert.Equal("the password for the domain admin account is Winter2026", Apply("the password for the domain admin account is Winter2026"));

        static string Apply(string text)
        {
            var builder = new StringBuilder(text);
            foreach (var (start, length) in Floor.Find(text).OrderByDescending(s => s.Start))
            {
                builder.Remove(start, length).Insert(start, "[X]");
            }

            return builder.ToString();
        }
    }

    private static bool[] Covered(string text, IEnumerable<(int Start, int Length)> spans)
    {
        var covered = new bool[text.Length];
        foreach (var (start, length) in spans)
        {
            Array.Fill(covered, true, start, length);
        }

        return covered;
    }

    private static readonly string[] Cues = ["the password", "pwd", "PIN", "Wi-Fi password", "pass"];

    private static readonly string[] FirstSeparators = [" ", ": ", "=", " - ", ", "];

    private static readonly string[] Separators = [" ", ", ", " - ", ". "];

    /// <summary>
    /// One or two of every kind of word the walk treats differently: joining words, assertions, clause
    /// words, words that end a sentence about a password, auxiliaries, references, plain words, words
    /// that look like secrets, and punctuation standing as a word.
    /// </summary>
    private static readonly string[] Alphabet =
    [
        "is", "was", "to", "set", "now", "the", "on", "for", "uh", "new",
        "which", "when", "that",
        "incorrect", "expired", "here", "but", "it",
        "has", "have", "been",
        "ticket", "port",
        "router", "domain", "account", "fun", "since", "probably",
        "Winter2026", "4821", "48213", "P@ss.word1!", "HAS", "BEEN", "sUnshine", "verylongpassphrase",
        "-", "/",
    ];

    private static IEnumerable<string> Corpus()
    {
        // Every two words after every cue, with every separator after the cue and between the words.
        foreach (var cue in Cues)
        {
            foreach (var first in FirstSeparators)
            {
                foreach (var a in Alphabet)
                {
                    yield return cue + first + a;
                    foreach (var gap in Separators)
                    {
                        foreach (var b in Alphabet)
                        {
                            yield return cue + first + a + gap + b;
                        }
                    }
                }
            }
        }

        // Every three words after a spoken and a typed cue, said, colon-separated and dashed.
        foreach (var cue in new[] { "the password", "pwd" })
        {
            foreach (var first in new[] { " ", ": ", " - " })
            {
                foreach (var a in Alphabet)
                {
                    foreach (var b in Alphabet)
                    {
                        foreach (var c in Alphabet)
                        {
                            yield return cue + first + a + " " + b + " " + c;
                        }
                    }
                }
            }
        }

        // And longer sentences, the length the walk now reads, drawn the same way every run.
        var random = new Random(20261025);
        var builder = new StringBuilder();
        for (var n = 0; n < 40_000; n++)
        {
            builder.Clear().Append(Cues[random.Next(Cues.Length)]).Append(FirstSeparators[random.Next(FirstSeparators.Length)]);
            var length = random.Next(1, 14);
            for (var i = 0; i < length; i++)
            {
                if (i > 0)
                {
                    builder.Append(random.Next(4) == 0 ? Separators[random.Next(Separators.Length)] : " ");
                }

                var word = Alphabet[random.Next(Alphabet.Length)];
                builder.Append(random.Next(8) == 0 ? word.ToUpperInvariant() : word);
            }

            yield return builder.ToString();
        }
    }

    /// <summary>
    /// The password cue and the walk after it exactly as <c>PatternLibrary</c> had them before the S-12
    /// widening (main at 1fee251). Frozen: never edit this to make the comparison pass.
    /// </summary>
    private static class Floor
    {
        private static readonly Regex Cue = new(
            @"\b(?:password|passphrase|passwd|pwd|pass\s?code|pass(?=\s{0,3}[:=])|pin(?:\s?number)?)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> NotSecrets = new(StringComparer.OrdinalIgnoreCase)
        {
            "incorrect", "wrong", "correct", "expired", "reset", "resets", "changed", "change", "changing", "updated",
            "required", "policy", "manager", "field", "prompt", "box", "again", "here", "there", "empty", "blank",
            "the", "a", "an", "it", "that", "this", "his", "her", "their", "my", "your", "our", "not", "never",
            "and", "or", "but", "so", "then", "when", "which", "what", "who", "why", "how", "if", "for", "to",
        };

        private static readonly HashSet<string> Connectives = new(StringComparer.OrdinalIgnoreCase)
        {
            "is", "was", "to", "set", "reset", "changed", "change", "now", "will", "be", "equals", "are",
            "uh", "um", "er", "ah", "like", "just", "actually", "currently", "still",
            "the", "a", "an", "on", "for", "of", "at", "in", "my", "your", "our", "their", "his", "her",
            "its", "new", "old",
        };

        private static readonly HashSet<string> Assertive = new(StringComparer.OrdinalIgnoreCase)
        {
            "is", "was", "to", "set", "reset", "changed", "change", "now", "will", "be", "equals", "are",
        };

        private const int MaxConnectives = 4;

        private const int MaxSecretWords = 4;

        public static IEnumerable<(int Start, int Length)> Find(string text)
        {
            foreach (Match m in Cue.Matches(text))
            {
                if (SecretAfter(text, m.Index + m.Length) is { Length: > 0 } secret)
                {
                    yield return secret;
                }
            }
        }

        private static (int Start, int Length)? SecretAfter(string text, int from)
        {
            var at = from;
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

                if (NotSecrets.Contains(value))
                {
                    return null;
                }

                if (!LooksLikeSecret(value) && NextWord(text, start + length) is { } following
                    && Connectives.Contains(text.Substring(following.Start, following.Length)))
                {
                    at = start + length;
                    continue;
                }

                if (!LooksLikeSecret(value) && !asserted)
                {
                    return null;
                }

                return (start, SecretEnd(text, start + length) - start);
            }

            return null;
        }

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

            var start = i;
            while (i < text.Length && IsWordChar(text[i]))
            {
                i++;
            }

            return (start, i - start);
        }

        private static bool IsWordChar(char c) => !char.IsWhiteSpace(c) && c is not (',' or '.' or ';' or '!' or '?' or ':' or '=' or '"' or '\'');
    }
}
