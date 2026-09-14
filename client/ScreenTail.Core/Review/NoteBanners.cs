using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

public enum BannerKind
{
    /// <summary>Something about the note is worth knowing but nothing is wrong.</summary>
    Info,

    /// <summary>The note is missing something, or was drafted with less than the usual evidence.</summary>
    Warning,
}

/// <param name="Kind">Decides the colour. Never the only signal: Spec §7 forbids colour alone, so the
/// pane also shows the glyph and the text, and the text says what happened without needing either.</param>
public sealed record Banner(BannerKind Kind, string Text);

/// <summary>
/// What the note pane says across the top (Spec §5 S3, ST-074).
///
/// Derived, never stored. A banner is a claim about the session — "three screenshots were removed" — and
/// the only thing entitled to make it is the session record itself. A flag set at draft time would go on
/// saying it after the cause was gone, and worse, would say nothing after a cause that appeared later.
///
/// Wording is Spec §5 S3 verbatim, because these sentences are the whole of what a technician is told
/// before they publish something to a customer's ticket.
/// </summary>
public static class NoteBanners
{
    /// <param name="session">The loaded session. Its own fields are the evidence.</param>
    /// <param name="offline">No connectivity right now, so a cloud draft cannot be requested or retried.</param>
    public static IReadOnlyList<Banner> For(Session session, bool offline = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var banners = new List<Banner>();

        // Ordered worst-first: a technician who reads only the top line should read the one that most
        // changes whether the note is safe to publish.
        if (session.PartialCapture)
        {
            banners.Add(new Banner(
                BannerKind.Warning,
                "Capture started late or was interrupted. Some steps may be missing."));
        }

        if (session.FramesPurgedUnredacted > 0)
        {
            // Singular matters: "1 screenshots were removed" reads as a bug and invites the reader to
            // distrust the count, which is the one number on this screen that must be believed.
            var count = session.FramesPurgedUnredacted;
            var noun = count == 1 ? "screenshot was" : "screenshots were";
            banners.Add(new Banner(
                BannerKind.Warning,
                $"{count} {noun} removed because they could not be redacted in time."));
        }

        if (session.Draft is { Source: DraftSource.Local })
        {
            banners.Add(new Banner(
                BannerKind.Info,
                "Drafted on this device only — quality may be lower."));
        }

        // Local-only mode is why a local draft happened, so saying both would be saying it twice. Said on
        // its own it is still worth saying: the technician chose it, or policy did, and it explains the
        // absence of everything cloud drafting would have added.
        else if (session.LocalOnly)
        {
            banners.Add(new Banner(
                BannerKind.Info,
                "Local-only mode — nothing from this session leaves the device."));
        }

        if (session.Draft is null && offline)
        {
            banners.Add(new Banner(BannerKind.Info, "Draft pending — offline"));
        }

        return banners;
    }

    /// <summary>
    /// Why Publish is disabled, or null when it isn't. Spec §5 S3 puts this in the disabled button's
    /// tooltip; ST-015 moved it to a line beside the button, because a tooltip on a disabled control is
    /// the one place a keyboard user can never reach it. Amendment v0.4.3.
    /// </summary>
    /// <param name="ticketChosen">A ticket has been picked in the right pane.</param>
    public static string? PublishBlockedBecause(Session session, bool ticketChosen, bool offline = false)
    {
        ArgumentNullException.ThrowIfNull(session);

        // v0.4.1 Q4: writing a note by hand after a failed draft is v1.1, so there is nothing to publish.
        if (session.Draft is null)
        {
            // Spec §5 S3 gives one sentence here, ending "Retry the draft first". Offline, that is an
            // instruction the technician cannot carry out - a cloud draft retried with no connection fails
            // again - and §6's own copy rule says always offer the next step. Amendment v0.4.3.
            return offline
                ? "There is no note to publish yet. It will be drafted when you are back online."
                : "There is no note to publish yet. Retry the draft first.";
        }

        if (offline)
        {
            return "You are offline. Publishing needs a connection.";
        }

        return ticketChosen ? null : "Choose a ticket first";
    }
}
