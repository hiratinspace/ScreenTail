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
/// The same rules run in <c>research/prompts/checks.py</c> over the eval corpus. Both are built from
/// <c>research/prompts/shared-rules.json</c> (here through <see cref="DraftRules"/>, which
/// <c>SharedRulesTests</c> holds to the file), and both read text the same way (<see cref="DraftReading"/>).
///
/// Every rule but the quotation rule reads a text twice: as written, and as read (normalised). Either
/// refusing refuses the draft, so the normalisation can only add refusals. It refuses "In&lt;ZWSP&gt;stall
/// the agent", which production let through; and "curl²", which NFKC reads as "curl2", is still refused
/// because of how it was written.
///
/// A text that grows past the reading's bound when it is read (<see cref="DraftReading"/>) is refused
/// unread, and a transcript that does is one no quotation can be shown to come from.
/// </summary>
public static class DraftValidator
{
    /// <summary>The prompt these rules were written against. A draft from another has been checked by nothing.</summary>
    public const string PromptVersion = "note_v1";

    private static readonly Regex Quoted = DraftRules.Compile("quoted", DraftRules.Quoted);
    private static readonly Regex Apostrophe = DraftRules.Compile("apostrophe", [DraftRules.Apostrophe]);
    private static readonly Regex Ssn = DraftRules.Compile("ssn", [DraftRules.Ssn]);
    private static readonly Regex SsnCued = DraftRules.Compile("ssn_cued", [DraftRules.SsnCued]);
    private static readonly Regex Card = DraftRules.Compile("card", [DraftRules.Card]);
    private static readonly Regex CredentialCue = DraftRules.Compile("credential_cue", [DraftRules.CredentialCue]);
    private static readonly Regex SecretShaped = DraftRules.Compile("secret_shaped", [DraftRules.SecretShaped]);
    private static readonly Regex KeyShape = DraftRules.Compile("key_shapes", DraftRules.KeyShapes);
    private static readonly Regex KeyCueValue = DraftRules.Compile("key_cue_value", [DraftRules.KeyCueValue]);
    private static readonly Regex Directive = DraftRules.Compile("directive", DraftRules.Directive);

    private static readonly HashSet<string> Connectives = new(DraftRules.Connectives, StringComparer.Ordinal);
    private static readonly int WalkSteps = int.Parse(DraftRules.WalkSteps, CultureInfo.InvariantCulture);
    private static readonly HashSet<char> ValueBreaks = [.. DraftReading.NetSpace, .. DraftRules.ValueBreaks];

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
    /// here: the 2026-10-12 review found three that did not (S-62).
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

    /// <summary>
    /// The text as every rule but the quotation rule reads it the second time, as <c>checks.read</c> does,
    /// or null when it grows past the reading's bound.
    /// </summary>
    public static string? Read(string text) => DraftReading.Read(text);

    /// <summary>
    /// What <see cref="Instruction"/> answers for a text that grows past the reading's bound. Not null,
    /// because a text nobody could read is not one anybody showed was a record.
    /// </summary>
    public const string Unreadable = "(text that grows too long to read)";

    /// <summary>
    /// What makes this text read as an instruction, or null: the first match of production's rule over
    /// the text as written, then as read, and <see cref="Unreadable"/> for a text that cannot be read.
    /// <c>checks.instruction</c> answers the same.
    /// </summary>
    public static string? Instruction(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return InstructionIn(Readings(text));
    }

    private static string? InstructionIn(string[]? readings)
    {
        if (readings is null)
        {
            return Unreadable;
        }

        foreach (var reading in readings)
        {
            if (Directive.Match(reading) is { Success: true } found)
            {
                return found.Value;
            }
        }

        return null;
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
        var spoken = SpokenForm(string.Join(" ", bundle.Transcript.Select(segment => segment.Text ?? "")));

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

    private static IEnumerable<string> CheckText(string where, string? text, string? spoken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        // Read once, and refused unread when it cannot be: no rule can check what was never read.
        if (DraftReading.Read(text) is not { } read)
        {
            yield return TooLong(where);
            yield break;
        }

        var readings = Both(text, read);
        foreach (var quoted in Quotations(read))
        {
            // A quoted sentence in a ticket note is a claim about what somebody said, and a customer may
            // read it. Paraphrase is fine; invented speech is not. A transcript that could not be read is
            // one nobody can be shown to have said anything in.
            if (spoken is null || !spoken.Contains(AsciiLower(quoted).Trim(' '), StringComparison.Ordinal))
            {
                yield return $"{where} quotes \"{Echoed(quoted)}\", which nobody said.";
            }
        }

        foreach (var secret in SecretsIn(where, readings))
        {
            yield return secret;
        }

        if (InstructionIn(readings) is { } directive)
        {
            yield return $"{where} reads as an instruction rather than a record: '{Echoed(directive)}'.";
        }
    }

    private static string TooLong(string where) =>
        $"{where} grows too long when it is read to be checked, so it may not be shown.";

    /// <summary>
    /// Why this text puts a secret back into the note: a credential, a social security number or a card,
    /// over the text as written and as read. Empty when it does not.
    /// </summary>
    internal static IEnumerable<string> Secrets(string where, string text) =>
        Readings(text) is { } readings ? SecretsIn(where, readings) : [TooLong(where)];

    private static IEnumerable<string> SecretsIn(string where, string[] both)
    {
        if (both.Any(CarriesACredential))
        {
            yield return $"{where} appears to write out a credential.";
        }

        if (both.Any(reading => Ssn.IsMatch(reading) || SsnCued.IsMatch(reading)))
        {
            yield return $"{where} contains something shaped like a social security number.";
        }

        if (both.Any(reading => Card.Matches(reading).Any(match => Luhn(match.Value))))
        {
            yield return $"{where} contains something shaped like a card number.";
        }
    }

    /// <summary>
    /// The text as written and as read, with the redaction engine's markers blanked in both, or null when
    /// it grows past the reading's bound.
    /// </summary>
    private static string[]? Readings(string text) => DraftReading.Read(text) is { } read ? Both(text, read) : null;

    private static string[] Both(string text, string read) => [Blanked(DraftReading.Written(text)), Blanked(read)];

    /// <summary>The markers are deliberate and stay. What must not appear is the thing one of them replaced.</summary>
    private static string Blanked(string text) =>
        DraftRules.RedactionMarkers.Aggregate(text, (current, marker) => current.Replace(marker, " ", StringComparison.Ordinal));

    /// <summary>
    /// What the text puts in quotation marks, as read, with apostrophes inside words made one character
    /// first. A straight or curly apostrophe is the same character as a closing single quote, so a curly
    /// quotation with "I've" in it ended after one letter (T-203), and two possessives in one sentence
    /// read as a quotation nobody said (T-230).
    /// </summary>
    private static IEnumerable<string> Quotations(string read)
    {
        var folded = Apostrophe.Replace(read, DraftRules.ApostropheAs);
        foreach (Match match in Quoted.Matches(folded))
        {
            yield return match.Groups.Values.Skip(1).First(group => group.Success).Value;
        }
    }

    /// <summary>
    /// Text as a quotation is compared with the transcript: read, apostrophes inside words made one kind,
    /// ASCII letters in lower case. Only ASCII, because .NET and Python lower-case the rest differently.
    /// Null when the transcript grows past the reading's bound.
    /// </summary>
    private static string? SpokenForm(string text) =>
        DraftReading.Read(text) is { } read ? AsciiLower(Apostrophe.Replace(read, DraftRules.ApostropheAs)).Trim(' ') : null;

    private static string AsciiLower(string text) =>
        string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
            }
        });

    /// <summary>
    /// Whether this text puts a credential back into the note (prompt rule 5).
    ///
    /// By shape only (the owner's option A, 2026-10-24): a key whose shape needs no cue, a long value
    /// after a named key, or a cue word followed by something secret-shaped. The shape test is what keeps
    /// "Password reset completed for jsmith." out of it: a rejected draft costs a technician a correct
    /// note, so the value has to look like a secret rather than merely follow the word.
    /// </summary>
    private static bool CarriesACredential(string text)
    {
        if (KeyShape.IsMatch(text) || KeyCueValue.IsMatch(text))
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
    /// for ("the password on the router is Winter2026") and the walk goes on. A credential written as two
    /// words is judged on the pair, because neither half of "Winter 2026" looks like a secret alone.
    ///
    /// A word in brackets is judged like any other. Only the redaction engine's exact markers are skipped,
    /// and those are blanked before the walk: a bare "[" let "[Winter2026!]" through (S-63).
    /// </summary>
    private static bool CredentialAfter(string text, int start)
    {
        var at = start;
        for (var step = 0; step < WalkSteps; step++)
        {
            if (NextWord(text, at) is not { } word)
            {
                return false;
            }

            var value = text.Substring(word.Start, word.Length);
            if (IsConnective(value))
            {
                at = word.Start + word.Length;
                continue;
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

                if (IsConnective(next))
                {
                    at = word.Start + word.Length;
                    continue;
                }
            }

            return false;
        }

        return false;
    }

    private static bool IsConnective(string word) => Connectives.Contains(AsciiLower(word));

    /// <summary>
    /// The next word, or null at the end of the sentence. A full stop ends any claim that what follows
    /// is the credential.
    /// </summary>
    private static (int Start, int Length)? NextWord(string text, int from)
    {
        var i = from;
        while (i < text.Length && ValueBreaks.Contains(text[i]))
        {
            if (DraftRules.SentenceEnds.Contains(text[i], StringComparison.Ordinal))
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
        while (i < text.Length && !ValueBreaks.Contains(text[i]))
        {
            i++;
        }

        return (begin, i - begin);
    }

    /// <summary>
    /// Whether a run of digits passes the card checksum. Without it every order number and serial in a
    /// session is a false positive, and a validator that cries wolf is one somebody turns off.
    ///
    /// Tried twice: with each digit's own value, and with the value production gave a digit, its code
    /// minus '0', which for a digit outside ASCII is a large number. Production refused some runs only
    /// because of that, and CredentialParityTests holds this validator to refusing everything production
    /// refused. For ASCII digits the two are the same.
    /// </summary>
    private static bool Luhn(string run)
    {
        var digits = run.Where(c => DigitValue(c) >= 0).Reverse().ToArray();
        return digits.Length is >= 13 and <= 19
            && (Checksum(digits.Select(DigitValue)) || Checksum(digits.Select(c => c - '0')));
    }

    private static bool Checksum(IEnumerable<int> reversed)
    {
        var total = 0;
        var i = 0;
        foreach (var value in reversed)
        {
            var digit = value;
            if (i++ % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            total += digit;
        }

        return total % 10 == 0;
    }

    private static int DigitValue(char c)
    {
        foreach (var zero in DraftReading.DigitZeros)
        {
            if (c >= zero && c <= zero + 9)
            {
                return c - zero;
            }
        }

        return -1;
    }
}
