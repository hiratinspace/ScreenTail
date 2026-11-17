using System.Buffers.Text;
using System.Text.RegularExpressions;
using ScreenTrail.Api.Summarize;

namespace ScreenTrail.Api.Publish;

/// <summary>
/// Everything a publish could be wrong about, before a provider is asked. Same ceilings as the bundle
/// where they overlap.
///
/// The note's own bounds (2026-11-15 review) come from what the client can produce, with room to spare,
/// so that no publish a technician could make is refused. The model's schema
/// (<c>research/prompts/schema.json</c>) allows at most 30 steps of 400 characters, 10 follow-ups of 400,
/// a 600-character problem and result, a 70-character title and a 400-character reason: about 17,700
/// characters in all. The device's own draft (<c>FallbackDraft.MaxSteps</c>) has at most 40 steps, one
/// per thing said. The Review editor caps neither the number of steps nor their length, so a technician
/// can add steps and paste text; the golden sessions in <c>research/fixtures</c> are about a thousand
/// characters each. The bounds below are five times the most steps either draft makes and more than ten
/// times the longest note the model writes, so a refusal means a client gone wrong, not a long session.
/// What they stop is a note of tens of megabytes being rendered, copied into a time entry and an
/// article, and sent on to the PSA for each destination.
/// </summary>
public static partial class PublishLimits
{
    public const int MaxFrames = BundleLimits.MaxFrames;
    public const int MaxFrameChars = 1_000_000;
    public const int MaxTicketIdLength = 64;
    public const int MaxMinutes = 24 * 60;

    /// <summary>Five times the device's own draft (40) and more than six times the model's (30).</summary>
    public const int MaxSteps = 200;

    /// <summary>Twenty times the model's ten; the device's draft writes none.</summary>
    public const int MaxFollowUps = 200;

    /// <summary>
    /// Screenshots and transcript lines one step may point at, together. The model names at most ten of
    /// each, the device's draft one, and a publish carries at most <see cref="MaxFrames"/> screenshots.
    /// </summary>
    public const int MaxRefsPerStep = 200;

    /// <summary>
    /// Every character of the note added up: its sections, steps, follow-ups, title, reason and the ids it
    /// refers to. More than ten times the longest note the model can write (about 17,700); a technician
    /// would have to paste some fifty pages of text into Review to reach it.
    /// </summary>
    public const int MaxNoteChars = 200_000;

    /// <summary>The PSA's company name. The mapping table takes the same 200 (CompanyMappingEndpoint); ConnectWise's own field is 50.</summary>
    public const int MaxCompanyLength = 200;

    /// <summary>A display name. The footer is written from the token's user and this is ignored, but what is read is still bounded.</summary>
    public const int MaxReviewerLength = 200;

    private static readonly string[] Images = ["image/jpeg", "image/png"];
    private static readonly string[] NoteTypes = ["internal", "discussion"];
    private static readonly string TooLong = $"A note may be at most {MaxNoteChars} characters.";

    /// <summary>
    /// <c>\A</c> and <c>\z</c>, not <c>^</c> and <c>$</c>: <c>$</c> also matches before a final newline, so
    /// "48213" with a newline after it passed. A ticket id becomes a path segment on the PSA (ConnectWise's
    /// note route), so "..", a segment of its own, is refused in <see cref="Check"/> as well.
    /// </summary>
    [GeneratedRegex(@"\A[A-Za-z0-9._:-]+\z")]
    private static partial Regex Identifier();

    public static string? Check(PublishBundle? bundle)
    {
        if (bundle is null)
        {
            return "The request had no publish in it.";
        }

        if (string.IsNullOrWhiteSpace(bundle.SessionId) || bundle.SessionId.Length > BundleLimits.MaxSessionIdLength)
        {
            return "A session id is required.";
        }

        if (string.IsNullOrWhiteSpace(bundle.TicketId)
            || bundle.TicketId.Length > MaxTicketIdLength
            || !Identifier().IsMatch(bundle.TicketId)
            || bundle.TicketId.Contains("..", StringComparison.Ordinal))
        {
            return "A ticket id is required.";
        }

        if (bundle.Company is { Length: > MaxCompanyLength })
        {
            return $"The company name may be at most {MaxCompanyLength} characters.";
        }

        if (bundle.Reviewer is { Length: > MaxReviewerLength })
        {
            return $"The reviewer's name may be at most {MaxReviewerLength} characters.";
        }

        if (!NoteTypes.Contains(bundle.NoteType, StringComparer.Ordinal))
        {
            return "The note type must be internal or discussion.";
        }

        if (bundle.Minutes is < 0 or > MaxMinutes)
        {
            return $"The time entry must be between 0 and {MaxMinutes} minutes.";
        }

        if (bundle.Destinations is null || bundle.Destinations.Count == 0)
        {
            return "At least one destination is required.";
        }

        if (bundle.Destinations.Any(d => !Destinations.Known.Contains(d)))
        {
            return "A destination must be ticket_note, time_entry or kb_article.";
        }

        if (bundle.Destinations.Contains(Destinations.TimeEntry, StringComparer.Ordinal) && bundle.Minutes == 0)
        {
            return "A time entry of no minutes cannot be logged.";
        }

        if (bundle.Note is null)
        {
            return "The note is required.";
        }

        if (NoteProblem(bundle.Note) is { } note)
        {
            return note;
        }

        if (bundle.Frames is null)
        {
            return "The frames must be present, even when empty.";
        }

        if (bundle.Frames.Count > MaxFrames)
        {
            return $"A publish may carry at most {MaxFrames} frames.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in bundle.Frames)
        {
            if (string.IsNullOrWhiteSpace(frame.Id) || !ids.Add(frame.Id))
            {
                return "Every frame needs its own id.";
            }

            if (!Images.Contains(frame.MediaType, StringComparer.Ordinal))
            {
                return "A frame must be a JPEG or a PNG.";
            }

            if (string.IsNullOrEmpty(frame.Image) || frame.Image.Length > MaxFrameChars || !Base64.IsValid(frame.Image))
            {
                return "A frame's image must be base64 and under a megabyte.";
            }
        }

        return null;
    }

    /// <summary>
    /// The note's shape and size. A list or a step the JSON gave as null is refused here rather than found
    /// by the formatter as a 500, and the characters are counted step by step, so a note far over the
    /// limit costs no more to refuse than one just over it.
    /// </summary>
    private static string? NoteProblem(DraftJson note)
    {
        if (note.Steps is null || note.FollowUps is null)
        {
            return "The note's steps and follow-ups must be present, even when empty.";
        }

        if (note.Steps.Count > MaxSteps)
        {
            return $"A note may have at most {MaxSteps} steps.";
        }

        if (note.FollowUps.Count > MaxFollowUps)
        {
            return $"A note may have at most {MaxFollowUps} follow-ups.";
        }

        long chars = Length(note.Problem) + Length(note.Result) + Length(note.SuggestedTitle) + Length(note.KbReason) + Length(note.Source) + Length(note.PromptVersion);
        foreach (var step in note.Steps)
        {
            if (step?.FrameRefs is null || step.TranscriptRefs is null)
            {
                return "Every step needs its references, even when empty.";
            }

            if (step.FrameRefs.Count + step.TranscriptRefs.Count > MaxRefsPerStep)
            {
                return $"A step may point at no more than {MaxRefsPerStep} screenshots and transcript lines.";
            }

            chars += Length(step.Text) + Length(step.Confidence);
            foreach (var reference in step.FrameRefs.Concat(step.TranscriptRefs))
            {
                chars += Length(reference);
            }

            if (chars > MaxNoteChars)
            {
                return TooLong;
            }
        }

        foreach (var item in note.FollowUps)
        {
            chars += Length(item);
        }

        return chars > MaxNoteChars ? TooLong : null;
    }

    private static int Length(string? text) => text?.Length ?? 0;
}
