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

    /// <summary>
    /// Deliberate markers the redaction engine leaves behind. They are allowed, and guessing what they
    /// stood for is not.
    /// </summary>
    private static readonly string[] Redactions = ["[REDACTED]", "[CARD]", "[SSN]", "[SECRET]", "[EMAIL]"];

    /// <summary>
    /// Every pair of marks a model might reach for, not only straight doubles.
    ///
    /// The rule is "quotation marks". A fabricated line inside curly quotes or guillemets is the same
    /// fabrication, and a model writing prose reaches for curly ones more often than not — so this whole
    /// class of invented quotation walked past the check until 2026-09-20.
    /// </summary>
    private static readonly Regex Quoted = new(
        "[\"\u201c\u2018\u00ab]([^\"\u201d\u2019\u00bb]{4,})[\"\u201d\u2019\u00bb]",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Ssn = new(
        @"\b(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Nine unbroken digits are an order number far more often than a social security number, so this
    /// one needs a cue in front of it. Without it the hyphenated form was the only shape caught.
    /// </summary>
    private static readonly Regex SsnCued = new(
        @"\b(?:ssn|social\s?security(?:\s?(?:number|no|#))?)\b\s*(?:is|was)?\s*[:=#]?\s*"
        + @"(?!000|666|9\d\d)\d{3}(?!00)\d{2}(?!0000)\d{4}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    private static readonly Regex LongDigits = new(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A credential introduced by a cue word.
    ///
    /// The connective run is what took getting right. "The password was reset to Winter2026" is how a
    /// technician actually writes it, and an alternation of whole phrases only caught the phrasings
    /// somebody had thought of: "was" matched, the value became "reset", and the credential after it
    /// went into the note. Up to three linking words, then whatever follows.
    /// </summary>
    private static readonly Regex Credential = new(
        @"\b(?:password|passphrase|passwd|pwd|api[ _-]?key|secret|token|bearer)\b"
        + @"(?:\s+(?:is|was|to|set|reset|changed|now|will|be)){0,3}"
        + @"\s*[:=#]?\s*(?<value>[^\s,.;!?]{3,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// What separates a credential from a sentence about one.
    ///
    /// "Outlook prompted for a password repeatedly" and "the password was wrong" are notes a technician
    /// would write; "the password is Summer2024" is the thing the rule forbids. A failed check fails the
    /// whole draft, so a false positive here throws away a correct note: the value has to look like a
    /// secret, not merely follow the word.
    /// </summary>
    private static readonly Regex SecretShaped = new(
        @"^(?=.*[A-Za-z])(?=.*\d)[\x21-\x7e]{6,}$|^[\x21-\x7e]{16,}$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Credentials recognisable without a cue word at all. A model quoting one back needs no
    /// introduction, and neither does a key that survived redaction upstream and was read out of OCR.
    /// </summary>
    private static readonly Regex KeyShape = new(
        @"AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9_-]{20,}"
        + @"|-----BEGIN[ A-Z]*PRIVATE KEY-----",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// A note is a record, not an instruction (prompt rule 5a). A web address, a command line, or a
    /// direction to install, run, download or disable something.
    /// </summary>
    private static readonly Regex Directive = new(
        @"(?:https?://|www\.)\S+"
        + @"|\b(?:Set-ExecutionPolicy|powershell|cmd\.exe|regedit|curl|wget|iwr|irm|invoke-webrequest"
        + @"|invoke-restmethod|invoke-expression|iex|certutil|bitsadmin|mshta|rundll32)\b"
        + @"|\b(?:disable|turn\s+off|uninstall|remove)\s+(?:the\s+)?(?:antivirus|defender|firewall|edr|mfa|two-factor)\b"
        + @"|\b(?:download|install|run|execute|disable|uninstall|delete)\b\s+(?:the\s+|your\s+|windows\s+)?\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

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

            if (step.TranscriptRefs.Count == 0 && !string.Equals(step.Confidence, "low", StringComparison.OrdinalIgnoreCase))
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
        var withoutMarkers = Redactions.Aggregate(text, (current, marker) => current.Replace(marker, " ", StringComparison.Ordinal));

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

        foreach (Match match in Credential.Matches(text))
        {
            if (SecretShaped.IsMatch(match.Groups["value"].Value))
            {
                return true;
            }
        }

        return false;
    }

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
