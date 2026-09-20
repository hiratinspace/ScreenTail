using System.Globalization;
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

    private static readonly Regex Quoted = new("\"([^\"]{4,})\"", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex Ssn = new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex LongDigits = new(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex Credential = new(
        @"\b(?:password|passphrase|api[ -]?key|secret|token)\b\s*(?:is|=|:)\s*\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// A note is a record, not an instruction (prompt rule 5a). A web address, a command line, or a
    /// direction to install, run, download or disable something.
    /// </summary>
    private static readonly Regex Directive = new(
        @"(?:https?://|www\.)\S+"
        + @"|\b(?:Set-ExecutionPolicy|Invoke-Expression|Invoke-WebRequest|curl|wget|powershell|cmd\.exe|regedit)\b"
        + @"|\b(?:download|install|run|execute|disable|uninstall|delete)\b\s+(?:the\s+|your\s+|windows\s+)?\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

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
                    reasons.Add($"{where} cites frame '{frameId}', which is not in this session.");
                }
                else if (frame.Excluded)
                {
                    reasons.Add($"{where} cites frame '{frameId}', which the technician removed.");
                }
            }

            foreach (var segmentId in step.TranscriptRefs.Where(id => !segments.ContainsKey(id)))
            {
                reasons.Add($"{where} cites transcript segment '{segmentId}', which does not exist.");
            }

            if (step.TranscriptRefs.Count == 0 && !string.Equals(step.Confidence, "low", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"{where} has no transcript evidence, so its confidence cannot be '{step.Confidence}'.");
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

        if (Credential.IsMatch(withoutMarkers))
        {
            yield return $"{where} appears to write out a credential.";
        }

        if (Ssn.IsMatch(withoutMarkers))
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
