using System.Globalization;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>
/// The words of the note as it will land (Spec v0.6.0 §5 S3, "The note is edited in place as it will
/// land"): each step's time, the screenshot it is published with, what an empty section does on the
/// ticket, and the line under the note with the masking count and the save state.
///
/// The ticket's own form is the backend's <c>NoteFormatter</c>: the four sections in order, steps
/// numbered, an empty section left out, and a step's screenshots called by their place among the ones
/// attached ("(screenshot 2)"). The editor says the same things in the same words, so what the
/// technician reads here is what the customer's ticket shows. Counts and times only (INV-10).
/// </summary>
public static class NoteLanding
{
    /// <summary>The quiet line over the note: what it is, and how to change it.</summary>
    public const string Guide = "This is the note as it will read on the ticket. Click any line to edit it.";

    /// <summary>What an inferred step says beside its glyph (Spec §5 S3, verbatim).</summary>
    public const string Inferred = "Inferred from screen only";

    /// <summary>An empty section: the formatter leaves it off the ticket, so the editor says so.</summary>
    public const string EmptySection = "Empty, so it is left off the ticket.";

    /// <summary>
    /// What a step's screenshot takes from the keyboard, for a screen reader (Spec v0.6.8 §5 S3): Del
    /// works on a step's row as it does in the strip, with the same five-second undo.
    /// </summary>
    public const string FrameHelp = "Enter enlarges it. Space includes or excludes it. Del deletes it, with 5 seconds to undo (Ctrl+Z).";

    /// <summary>The same, as the screenshot's tooltip, with the pointer's way in and where the blur is.</summary>
    public const string FrameTip = "Enter or click to enlarge, B there to blur. Space includes or excludes it. Del deletes it, with 5 seconds to undo (Ctrl+Z).";

    /// <summary>
    /// Whether Ctrl+Z in the note takes back a screenshot's delete, rather than being the focused text
    /// box's own undo. It does while a delete is inside its five seconds, unless the technician has typed
    /// since Del and focus is in a text box: Ctrl+Z undoes the last thing they did, and then that is the typing.
    /// </summary>
    /// <param name="deletePending">A deleted screenshot is still inside its undo window.</param>
    /// <param name="inTextBox">Keyboard focus is in one of the note's text boxes.</param>
    /// <param name="typedSinceDelete">The note's text has changed since Del was pressed.</param>
    public static bool UndoIsTheDelete(bool deletePending, bool inTextBox, bool typedSinceDelete) =>
        deletePending && !(inTextBox && typedSinceDelete);

    /// <summary>
    /// When the step happened, as the session's own clock read it ("14:04"): its first screenshot's time,
    /// else its first transcript line's, else null (a step the technician added has neither).
    /// </summary>
    public static string? StepTime(Session session, IReadOnlyList<string> frameRefs, IReadOnlyList<string> transcriptRefs)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(frameRefs);
        ArgumentNullException.ThrowIfNull(transcriptRefs);

        long? at = frameRefs
            .Select(id => session.Frames.FirstOrDefault(frame => frame.Id == id))
            .FirstOrDefault(frame => frame is not null)?.TsMs;
        at ??= transcriptRefs
            .Select(id => session.Transcript.FirstOrDefault(segment => segment.Id == id))
            .FirstOrDefault(segment => segment is not null)?.TsMs;

        // The session's own offset, never the machine's: the time a technician sees beside a step is the
        // time on the screen they were working on, the same one the transcript lines carry.
        return at is { } ms
            ? session.StartedAt.AddMilliseconds(ms).ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// What the ticket calls this step's screenshots: "screenshot 2", "screenshots 1, 3", by their place
    /// among the frames that are attached, as the formatter numbers them. Null when none of them goes out.
    /// </summary>
    public static string? ScreenshotPlaces(IReadOnlyList<string> frameRefs, IReadOnlyList<string> attached)
    {
        ArgumentNullException.ThrowIfNull(frameRefs);
        ArgumentNullException.ThrowIfNull(attached);

        var places = frameRefs
            .Select(id => IndexOf(attached, id))
            .Where(index => index >= 0)
            .Select(index => index + 1)
            .Distinct()
            .Order()
            .ToList();
        return places.Count switch
        {
            0 => null,
            1 => string.Create(CultureInfo.InvariantCulture, $"screenshot {places[0]}"),
            _ => $"screenshots {string.Join(", ", places.Select(p => p.ToString(CultureInfo.InvariantCulture)))}",
        };
    }

    /// <summary>
    /// The masking count under the note: the items masked on the screenshots that go out, and how many
    /// of those are on a screenshot no step shows, which the technician has not seen on the note.
    /// "2 items masked, none left to review"; "1 item masked, 1 on a screenshot no step shows";
    /// "Nothing masked".
    /// </summary>
    /// <param name="attached">The frames that go out with the note, as the store has them now (a blur adds a region).</param>
    /// <param name="onSteps">The ids of the frames a step row shows.</param>
    public static string Masking(IEnumerable<Frame> attached, IReadOnlySet<string> onSteps)
    {
        ArgumentNullException.ThrowIfNull(attached);
        ArgumentNullException.ThrowIfNull(onSteps);

        var frames = attached.ToList();
        var masked = frames.Sum(frame => frame.MaskedRegions.Count);
        if (masked == 0)
        {
            return "Nothing masked";
        }

        var unseen = frames.Where(frame => !onSteps.Contains(frame.Id) && frame.MaskedRegions.Count > 0).ToList();
        var items = masked == 1 ? "1 item masked" : string.Create(CultureInfo.InvariantCulture, $"{masked} items masked");
        var where = unseen.Count == 1 ? "a screenshot" : "screenshots";
        return unseen.Count == 0
            ? $"{items}, none left to review"
            : string.Create(CultureInfo.InvariantCulture, $"{items}, {unseen.Sum(frame => frame.MaskedRegions.Count)} on {where} no step shows");
    }

    /// <summary>
    /// The step's frame in words, for a screen reader and the line under its thumbnail: "screenshot 2"
    /// when it goes out, "not attached" when it was left out, "withheld" when it never can be.
    /// </summary>
    public static string FrameState(string? place, bool included, bool withheld) =>
        withheld ? "withheld" : included && place is not null ? place : "not attached";

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
