using System.Buffers.Text;
using System.Text.RegularExpressions;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Publish;

/// <summary>Everything a publish could be wrong about, before a provider is asked. Same ceilings as the bundle where they overlap.</summary>
public static partial class PublishLimits
{
    public const int MaxFrames = BundleLimits.MaxFrames;
    public const int MaxFrameChars = 1_000_000;
    public const int MaxTicketIdLength = 64;
    public const int MaxMinutes = 24 * 60;

    private static readonly string[] Images = ["image/jpeg", "image/png"];
    private static readonly string[] NoteTypes = ["internal", "discussion"];

    [GeneratedRegex("^[A-Za-z0-9._:-]+$")]
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

        if (string.IsNullOrWhiteSpace(bundle.TicketId) || bundle.TicketId.Length > MaxTicketIdLength || !Identifier().IsMatch(bundle.TicketId))
        {
            return "A ticket id is required.";
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
}
