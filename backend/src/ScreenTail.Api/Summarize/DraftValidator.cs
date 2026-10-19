using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenTail.Api.Summarize;

/// <summary>
/// What a model may not be allowed to say (ST-063, prompt <c>note_v1</c>).
///
/// The prompt asks for a great deal of restraint: cite only real evidence, quote only what was said,
/// leave redactions alone, never write an instruction. A prompt is a request. <b>This is the part that
/// refuses</b>, and it runs on every draft before one is stored, so a model having an off day produces a
/// rejected draft and a retry rather than a confident, wrong note in a customer's ticket.
///
/// Two of the rules are security rather than quality. A credential that survived redaction must not be
/// written back out, because after this it is in a ticket, an email and somebody's inbox. And a note that
/// reads as an instruction is the prompt-injection landing site: text on a customer's screen can ask a
/// model to emit a command, and a note is where a person downstream would find it and run it.
///
/// The same rules run in <c>research/prompts/checks.py</c> over the eval corpus. They are here as well
/// because that harness runs offline and this runs in front of a technician. Both are built from
/// <c>research/prompts/shared-rules.json</c> (see <see cref="DraftRules"/>), and
/// <c>hardening-cases.json</c> says what both must answer.
/// </summary>
public static class DraftValidator
{
    /// <summary>The prompt these rules were written against. A draft from another has been checked by nothing.</summary>
    public const string PromptVersion = "note_v1";

    /// <summary><c>suggested_time_minutes</c> in <c>schema.json</c>: a day at most, and never negative.</summary>
    private const long MaxMinutes = 1440;

    private static readonly Regex Invisible = Rule(DraftRules.Invisible);

    private static readonly Regex WordApostrophe = Rule(DraftRules.WordApostrophe);

    private static readonly Regex Quoted = Rule(DraftRules.Quoted);

    private static readonly Regex Ssn = Rule(DraftRules.Ssn);

    private static readonly Regex SsnCued = Rule(DraftRules.SsnCued, RegexOptions.IgnoreCase);

    /// <summary>Where a card can be. See <see cref="CarriesACard"/> for how one is found inside it.</summary>
    private static readonly Regex CardRun = Rule(DraftRules.CardRun);

    private static readonly Regex DigitGroup = Rule("[0-9]+");

    /// <summary>How many digits a payment card has.</summary>
    private const int MinCardDigits = 13;

    private const int MaxCardDigits = 19;

    /// <summary><see cref="CarriesACredential"/> walks from these. The same walk is in <c>research/prompts/checks.py</c>.</summary>
    private static readonly Regex CredentialCue = Rule(DraftRules.CredentialCue, RegexOptions.IgnoreCase);

    private static readonly Regex PinCue = Rule(DraftRules.PinCue, RegexOptions.IgnoreCase);

    private static readonly HashSet<string> Connectives = new(DraftRules.Connectives, StringComparer.OrdinalIgnoreCase);

    /// <summary>How far past a cue to look before giving up on finding a credential.</summary>
    private const int MaxSteps = 6;

    private static readonly Regex SecretShaped = Rule(DraftRules.SecretShaped);

    private static readonly Regex PinShaped = Rule(DraftRules.PinShaped);

    private static readonly Regex KeyShape = Rule(string.Join("|", DraftRules.KeyShapes));

    private static readonly Regex Directive = Rule(string.Join("|", DraftRules.Directive), RegexOptions.IgnoreCase);

    /// <summary>See <see cref="DraftRules.Imperative"/>: a match whose first group did not take part is an instruction.</summary>
    private static readonly Regex Imperative = Rule(DraftRules.Imperative, RegexOptions.IgnoreCase);

    private static Regex Rule(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>How much of a model-chosen string is worth repeating. Enough to recognise a citation.</summary>
    private const int EchoedLength = 48;

    /// <summary>
    /// A model-chosen string, made safe to put in a sentence.
    ///
    /// Every reason below quotes something the model wrote, and those reasons go back to the client, into
    /// the stored draft-failed reason, and into the repair prompt the model reads next. The model reads
    /// OCR of a customer's screen, so a screen can suggest what it writes: a "frame id" carrying newlines
    /// forges log lines, and one carrying a thousand characters pushes screen content into places INV-10
    /// keeps it out of. Neither needs an attacker; a model having a bad day writes long nonsense on its
    /// own.
    ///
    /// Kept recognisable rather than removed, because a reason that will not say which citation was
    /// wrong does not help the technician reading it. Every site that repeats the model goes through
    /// here: the 2026-10-12 review found three that did not.
    /// </summary>
    internal static string Echoed(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var safe = new StringBuilder(Math.Min(value.Length, EchoedLength) + 2);
        Span<char> pair = stackalloc char[2];
        foreach (var rune in value.EnumerateRunes())
        {
            if (safe.Length >= EchoedLength)
            {
                return safe.Append('…').ToString();
            }

            // Printable, and nothing that ends a line, reorders the text around it, or closes the
            // quotation it sits inside. Line and paragraph separators end a line as surely as a newline
            // does, and a bidirectional override makes a reason read differently from what it says.
            var shown = Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned
                || rune.Value == '\''
                ? new Rune('.')
                : rune;
            _ = safe.Append(pair[..shown.EncodeToUtf16(pair)]);
        }

        return safe.ToString();
    }

    /// <returns>Why this draft may not be shown. Empty means it is fine to store.</returns>
    public static IReadOnlyList<string> Check(DraftJson draft, SummarizeBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(bundle);

        // Whole before anything else is asked of it. Every rule below walks a list or reads a string, and
        // a model can write null for either: that threw after the call was paid for, a 500 and no repair
        // (2026-10-12 review). A value the client cannot store is the same draft broken another way, and
        // was worse: a 200 the device could not read, retried and billed until the outbox gave up.
        if (Shape(draft) is { Count: > 0 } broken)
        {
            return broken;
        }

        var reasons = new List<string>(OutsideTheSchema(draft));

        var frames = bundle.Frames.ToDictionary(frame => frame.Id, StringComparer.Ordinal);
        var segments = bundle.Transcript.ToDictionary(segment => segment.Id, StringComparer.Ordinal);
        var spoken = Normalise(string.Join(" ", bundle.Transcript.Select(segment => segment.Text)));

        if (!string.Equals(draft.PromptVersion, PromptVersion, StringComparison.Ordinal))
        {
            reasons.Add($"The draft came from prompt '{Echoed(draft.PromptVersion)}', not '{PromptVersion}'.");
        }

        for (var i = 0; i < draft.Steps.Count; i++)
        {
            var step = draft.Steps[i];
            var where = $"Step {i + 1}";

            foreach (var frameId in step.FrameRefs)
            {
                if (!frames.TryGetValue(frameId, out var frame))
                {
                    // Review renders frame references as chips a technician clicks. A dangling one is a
                    // broken promise of evidence, and usually means the step was invented with it.
                    reasons.Add($"{where} cites frame '{Echoed(frameId)}', which is not in this session.");
                }
                else if (frame.Excluded)
                {
                    reasons.Add($"{where} cites frame '{Echoed(frameId)}', which the technician removed.");
                }
            }

            foreach (var segmentId in step.TranscriptRefs.Where(id => !segments.ContainsKey(id)))
            {
                reasons.Add($"{where} cites transcript segment '{Echoed(segmentId)}', which does not exist.");
            }

            // The exact word, as the client reads it. "LOW" used to count as low here.
            if (step.TranscriptRefs.Count == 0 && !string.Equals(step.Confidence, "low", StringComparison.Ordinal))
            {
                reasons.Add($"{where} has no transcript evidence, so its confidence cannot be '{Echoed(step.Confidence)}'.");
            }

            reasons.AddRange(CheckText(where, step.Text, spoken));
        }

        // Every free-text field, not only the steps. These are the ones most likely to be copied into a
        // customer-visible note, and checking steps alone is the mistake the Python rules already made
        // once: a follow-up saying "install the agent from …" passed.
        foreach (var (field, value) in new[]
        {
            ("problem", draft.Problem),
            ("result", draft.Result),
            ("suggested_title", draft.SuggestedTitle),
            ("kb_reason", draft.KbReason),
        })
        {
            reasons.AddRange(CheckText(field, value, spoken));
        }

        foreach (var followUp in draft.FollowUps)
        {
            reasons.AddRange(CheckText("follow_ups", followUp, spoken));
        }

        if (string.IsNullOrWhiteSpace(draft.KbReason))
        {
            reasons.Add("kb_reason is empty; a person has to read it to decide about the article.");
        }

        // The client applies the tenant's rounding, so the model's number is unrounded active time. One
        // longer than the session is a made-up number about something a customer is charged for.
        if (bundle.DurationMs is { } durationMs && durationMs > 0)
        {
            var sessionMinutes = durationMs / 60_000d;
            if (draft.SuggestedTimeMinutes > sessionMinutes + 1)
            {
                reasons.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The suggested time is {draft.SuggestedTimeMinutes} minutes for a session of {sessionMinutes:F0}; it should be the active time, unrounded."));
            }
        }

        return reasons;
    }

    /// <summary>The values a step's confidence may take, as <c>research/prompts/schema.json</c> lists them.</summary>
    private static readonly string[] Confidences = ["high", "low"];

    /// <summary>The values a draft's source may take, as the schema lists them and the client's enum reads them.</summary>
    private static readonly string[] Sources = ["cloud", "local"];

    /// <summary>
    /// Whether the draft is whole: every list the schema requires is a list with no holes in it, every
    /// string it requires is there, and every value the client reads into an enum is one it knows.
    ///
    /// The deserializer cannot say this for us. <c>required</c> asks that a property be present, not that
    /// it be anything but null, and a null inside a list is never checked at all. Each reason names the
    /// field and none quotes the value, which came from a model that read a customer's screen.
    /// </summary>
    private static List<string> Shape(DraftJson draft)
    {
        var broken = new List<string>();

        foreach (var (field, value) in new[]
        {
            ("problem", draft.Problem),
            ("result", draft.Result),
            ("suggested_title", draft.SuggestedTitle),
            ("kb_reason", draft.KbReason),
            ("source", draft.Source),
            ("prompt_version", draft.PromptVersion),
        })
        {
            if (value is null)
            {
                broken.Add($"{field} is missing; the schema requires it.");
            }
        }

        if (draft.Source is not null && !OneOf(draft.Source, Sources))
        {
            broken.Add("source must be 'cloud' or 'local'.");
        }

        if (draft.FollowUps is null)
        {
            broken.Add("follow_ups must be a list, even when it is empty.");
        }
        else if (draft.FollowUps.Any(followUp => followUp is null))
        {
            broken.Add("follow_ups may not hold a null.");
        }

        if (draft.Steps is null)
        {
            broken.Add("steps must be a list, even when it is empty.");
            return broken;
        }

        for (var i = 0; i < draft.Steps.Count; i++)
        {
            var where = $"steps[{i}]";
            if (draft.Steps[i] is not { } step)
            {
                broken.Add($"{where} is null.");
                continue;
            }

            if (step.Text is null)
            {
                broken.Add($"{where}.text is missing; the schema requires it.");
            }

            if (step.Confidence is null || !OneOf(step.Confidence, Confidences))
            {
                broken.Add($"{where}.confidence must be 'high' or 'low'.");
            }

            foreach (var (field, refs) in new[] { ("frame_refs", step.FrameRefs), ("transcript_refs", step.TranscriptRefs) })
            {
                if (refs is null)
                {
                    broken.Add($"{where}.{field} must be a list, even when it is empty.");
                }
                else if (refs.Any(id => id is null))
                {
                    broken.Add($"{where}.{field} may not hold a null.");
                }
            }
        }

        return broken;
    }

    /// <summary>
    /// Exactly as the schema writes it. <c>research/prompts/schema.json</c> lists the values in lower case,
    /// and a validator that let "HIGH" through would disagree with the schema it checks against and with
    /// the Python checks that compare them as written (review round 2 of the 2026-10-12 fixes).
    /// </summary>
    private static bool OneOf(string value, string[] allowed) =>
        Array.Exists(allowed, candidate => string.Equals(candidate, value, StringComparison.Ordinal));

    /// <summary>
    /// A number <c>schema.json</c> does not allow. <see cref="Shape"/> has already held the enums and
    /// the required parts to the schema; this is the one bound it cannot see, because the deserializer
    /// reads any whole number into it.
    /// </summary>
    private static IEnumerable<string> OutsideTheSchema(DraftJson draft)
    {
        if (draft.SuggestedTimeMinutes is < 0 or > MaxMinutes)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"The suggested time is {draft.SuggestedTimeMinutes} minutes; it must be between 0 and {MaxMinutes}.");
        }
    }

    private static IEnumerable<string> CheckText(string where, string? text, string spoken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        // An apostrophe is not a closing quote. Folded first, so "I've" inside a quotation does not end it.
        foreach (Match match in Quoted.Matches(WordApostrophe.Replace(text, "ʼ")))
        {
            // A quoted sentence in a ticket note is a claim about what somebody said, and a customer may
            // read it. Paraphrase is fine; invented speech is not.
            var quoted = match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value;
            if (!spoken.Contains(Normalise(quoted), StringComparison.Ordinal))
            {
                yield return $"{where} quotes '{Echoed(quoted)}', which nobody said.";
            }
        }

        // The markers are deliberate and stay. What must not appear is the thing one of them replaced.
        // Blanked rather than treated as the end of the walk, so "the password is [REDACTED] Winter2026"
        // is still refused. Invisible characters go first, so the rules read what a person sees.
        // checks.py does both the same way.
        var withoutMarkers = DraftRules.RedactionMarkers.Aggregate(
            Invisible.Replace(text, ""),
            (current, marker) => current.Replace(marker, " ", StringComparison.Ordinal));

        if (CarriesACredential(withoutMarkers))
        {
            yield return $"{where} appears to write out a credential.";
        }

        if (Ssn.IsMatch(withoutMarkers) || SsnCued.IsMatch(withoutMarkers))
        {
            yield return $"{where} contains something shaped like a social security number.";
        }

        if (CarriesACard(withoutMarkers))
        {
            yield return $"{where} contains something shaped like a card number.";
        }

        if (Directive.Match(withoutMarkers) is { Success: true } directive)
        {
            yield return $"{where} reads as an instruction rather than a record: '{Echoed(directive.Value.Trim())}'.";
        }
        else if (Imperative.Matches(withoutMarkers).FirstOrDefault(verb => !verb.Groups[1].Success) is { } imperative)
        {
            yield return $"{where} reads as an instruction rather than a record: '{Echoed(imperative.Value.Trim())}'.";
        }
    }

    /// <summary>
    /// Whether this text puts a credential back into the note (prompt rule 5).
    ///
    /// Two ways in: a cue word followed by something secret-shaped, and a key whose shape needs no cue.
    /// The shape test is what keeps "the password was wrong" out of it. A rejected draft costs a
    /// technician a correct note, so the value has to look like a secret rather than merely follow the
    /// word.
    /// </summary>
    private static bool CarriesACredential(string text)
    {
        if (KeyShape.IsMatch(text))
        {
            return true;
        }

        foreach (Match cue in CredentialCue.Matches(text))
        {
            if (CredentialAfter(text, cue.Index + cue.Length, digitsAlone: false))
            {
                return true;
            }
        }

        foreach (Match cue in PinCue.Matches(text))
        {
            if (CredentialAfter(text, cue.Index + cue.Length, digitsAlone: true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a credential follows a cue, looking past the words that join the two.
    ///
    /// The first word that is not a joining word decides. If it looks like a secret, it is one. If it is
    /// an ordinary word with a joining word behind it, the cue was still naming what the credential is
    /// for ("the password on the router is Winter2026") and the walk goes on. A credential written as
    /// two words is judged on the pair, because neither half of "Winter 2026" looks like a secret alone;
    /// only when the first half has a letter in it, or "12 characters" would be a password, and after a
    /// PIN cue only once a joining word has been passed: "the passcode is Winter 2026" is a credential
    /// and "the PIN pad 2041" is a pad with a number on it.
    /// </summary>
    /// <param name="digitsAlone">After a PIN or passcode cue, where four digits are the credential.</param>
    private static bool CredentialAfter(string text, int start, bool digitsAlone)
    {
        var at = start;
        var skipped = false;
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
                skipped = true;
                continue;
            }

            if (SecretShaped.IsMatch(value) || (digitsAlone && PinShaped.IsMatch(value)))
            {
                return true;
            }

            if (NextWord(text, word.Start + word.Length) is { } following)
            {
                var next = text.Substring(following.Start, following.Length);
                if ((skipped || !digitsAlone) && value.Any(char.IsLetter) && SecretShaped.IsMatch(value + next))
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

    /// <summary>
    /// The next word, or null at the end of the sentence. A full stop ends any claim that what follows
    /// is the credential.
    /// </summary>
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

    /// <summary>
    /// Whether a card number is in this text: a run of digit groups that passes the card checksum, or a
    /// window of whole groups inside one that does.
    ///
    /// The checksum is what keeps every order number and serial from being a false positive, and a
    /// validator that cries wolf is one somebody turns off. Windows are what keep a card with its CVV
    /// beside it from being read as one longer number that fails the checksum and goes through. That is
    /// how the client's pattern library reads a card, and the same window rule: whole groups, 13 to 19
    /// digits, starting with 2 to 6, the digits payment cards start with. <c>checks.py</c> does the same.
    /// </summary>
    private static bool CarriesACard(string text)
    {
        foreach (Match run in CardRun.Matches(text))
        {
            if (PassesLuhn(run.ValueSpan))
            {
                return true;
            }

            var groups = DigitGroup.Matches(run.Value);
            for (var first = 0; first < groups.Count; first++)
            {
                if (groups[first].Value[0] is < '2' or > '6')
                {
                    continue;
                }

                var digits = 0;
                for (var last = first; last < groups.Count; last++)
                {
                    digits += groups[last].Length;
                    if (digits > MaxCardDigits)
                    {
                        break;
                    }

                    var end = groups[last].Index + groups[last].Length;
                    if (digits >= MinCardDigits && PassesLuhn(run.ValueSpan[groups[first].Index..end]))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>The card checksum over the digits of a candidate, which has to have a card's length.</summary>
    private static bool PassesLuhn(ReadOnlySpan<char> candidate)
    {
        var total = 0;
        var digits = 0;
        for (var i = candidate.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(candidate[i]))
            {
                continue;
            }

            var digit = candidate[i] - '0';
            if (digits % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            total += digit;
            digits++;
        }

        return digits is >= MinCardDigits and <= MaxCardDigits && total % 10 == 0;
    }

    /// <summary>
    /// Text as it is compared with the transcript: one space between words, lower case, and every kind of
    /// apostrophe the same one, so a quotation typed with a curly apostrophe matches speech transcribed
    /// with a straight one.
    /// </summary>
    private static string Normalise(string text) =>
        Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Replace('’', '\'')
            .Replace('ʼ', '\'')
            .Trim()
            .ToLowerInvariant();
}
