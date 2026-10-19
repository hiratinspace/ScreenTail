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
/// because that harness runs offline and this runs in front of a technician. When one changes, both
/// change; <c>DraftValidatorTests</c> and <c>test_prompt_hardening.py</c> are the pair that notice.
/// </summary>
public static class DraftValidator
{
    /// <summary>The prompt these rules were written against. A draft from another has been checked by nothing.</summary>
    public const string PromptVersion = "note_v1";

    private static readonly Regex Quoted = Rule(DraftRules.Quoted);

    private static readonly Regex Ssn = Rule(DraftRules.Ssn);

    private static readonly Regex SsnCued = Rule(DraftRules.SsnCued, RegexOptions.IgnoreCase);

    private static readonly Regex LongDigits = Rule(@"\b(?:\d[ -]?){13,19}\b");

    /// <summary><see cref="CarriesACredential"/> walks from here. The same walk is in <c>research/prompts/checks.py</c>.</summary>
    private static readonly Regex CredentialCue = Rule(DraftRules.CredentialCue, RegexOptions.IgnoreCase);

    private static readonly HashSet<string> Connectives = new(DraftRules.Connectives, StringComparer.OrdinalIgnoreCase);

    /// <summary>How far past a cue to look before giving up on finding a credential.</summary>
    private const int MaxSteps = 6;

    private static readonly Regex SecretShaped = Rule(DraftRules.SecretShaped);

    private static readonly Regex KeyShape = Rule(string.Join("|", DraftRules.KeyShapes));

    private static readonly Regex Directive = Rule(string.Join("|", DraftRules.Directive), RegexOptions.IgnoreCase);

    private static Regex Rule(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>How much of a model-chosen string is worth repeating. Enough to recognise a citation.</summary>
    private const int EchoedLength = 48;

    /// <summary>
    /// A model-chosen string, made safe to put in a sentence.
    ///
    /// Every reason below quotes something the model wrote, and those reasons go back to the client and
    /// into the stored draft-failed reason. The model reads OCR of a customer's screen, so a screen can
    /// suggest what it writes: a "frame id" carrying newlines forges log lines, and one carrying a
    /// thousand characters pushes screen content into places INV-10 keeps it out of. Neither needs an
    /// attacker — a model having a bad day writes long nonsense on its own.
    ///
    /// Kept recognisable rather than removed, because a reason that will not say which citation was
    /// wrong does not help the technician reading it.
    /// </summary>
    private static string Echoed(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var safe = new StringBuilder(Math.Min(value.Length, EchoedLength));
        foreach (var c in value)
        {
            if (safe.Length == EchoedLength)
            {
                return safe.Append('…').ToString();
            }

            // Printable, and nothing that ends a line or closes the quotation it sits inside.
            _ = safe.Append(char.IsControl(c) || c == '\'' ? '.' : c);
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

        var reasons = new List<string>();
        var frames = bundle.Frames.ToDictionary(frame => frame.Id, StringComparer.Ordinal);
        var segments = bundle.Transcript.ToDictionary(segment => segment.Id, StringComparer.Ordinal);
        var spoken = Normalise(string.Join(" ", bundle.Transcript.Select(segment => segment.Text)));

        if (!string.Equals(draft.PromptVersion, PromptVersion, StringComparison.Ordinal))
        {
            reasons.Add($"The draft came from prompt '{draft.PromptVersion}', not '{PromptVersion}'.");
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

    private static IEnumerable<string> CheckText(string where, string? text, string spoken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in Quoted.Matches(text))
        {
            // A quoted sentence in a ticket note is a claim about what somebody said, and a customer may
            // read it. Paraphrase is fine; invented speech is not.
            if (!spoken.Contains(Normalise(match.Groups[1].Value), StringComparison.Ordinal))
            {
                yield return $"{where} quotes \"{match.Groups[1].Value}\", which nobody said.";
            }
        }

        // The markers are deliberate and stay. What must not appear is the thing one of them replaced.
        var withoutMarkers = DraftRules.RedactionMarkers.Aggregate(text, (current, marker) => current.Replace(marker, " ", StringComparison.Ordinal));

        if (CarriesACredential(withoutMarkers))
        {
            yield return $"{where} appears to write out a credential.";
        }

        if (Ssn.IsMatch(withoutMarkers) || SsnCued.IsMatch(withoutMarkers))
        {
            yield return $"{where} contains something shaped like a social security number.";
        }

        if (LongDigits.IsMatch(withoutMarkers) && Luhn(withoutMarkers))
        {
            yield return $"{where} contains something shaped like a card number.";
        }

        if (Directive.Match(withoutMarkers) is { Success: true } directive)
        {
            yield return $"{where} reads as an instruction rather than a record: '{directive.Value}'.";
        }
    }

    /// <summary>
    /// Whether any long run of digits passes the card checksum.
    ///
    /// Without it every order number and serial in a session is a false positive, and a validator that
    /// cries wolf is one somebody turns off.
    /// </summary>
    /// <summary>
    /// Whether this text puts a credential back into the note (prompt rule 5).
    ///
    /// Two ways in: a cue word followed by something secret-shaped, and a key whose shape needs no cue.
    /// The shape test is what keeps "the password was wrong" out of it — a rejected draft costs a
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
            if (CredentialAfter(text, cue.Index + cue.Length))
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
    /// for — "the password on the router is Winter2026" — and the walk goes on. A credential written as
    /// two words is judged on the pair, because neither half of "Winter 2026" looks like a secret alone.
    /// </summary>
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

            // The redaction engine's own marker. Finding one means the rules upstream worked.
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

    private static string Normalise(string text) =>
        Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim().ToLowerInvariant();
}
