namespace ScreenTail.Core.Shell;

/// <summary>
/// What the shell may ask on the technician's behalf when a Review pane opens (ST-077, ST-085).
///
/// The pane's banner on a local-only session says nothing from the session leaves the device, and the
/// shell used to belie it as the pane opened: it asked the PSA about the suggested ticket at once, which
/// put the number read off the technician's window on the wire, into the backend's log, before anybody
/// had chosen to publish anything (2026-10-12 review, S-44, S-147, S-45). INV-8 allows egress from a
/// local-only session only when the technician initiates a publish, and opening Review is not that.
/// </summary>
public static class ReviewOpening
{
    /// <summary>
    /// Whether the pane may look the suggested ticket up as it opens, so the badge can show its summary
    /// and company. Never under local-only, where the technician's own search in the pane is the first
    /// thing that may reach the PSA; never without a hint or a PSA to ask.
    /// </summary>
    public static bool MayLookUpSuggestedTicket(bool localOnly, string? suggestedTicket, int integrations) =>
        !localOnly && !string.IsNullOrWhiteSpace(suggestedTicket) && integrations > 0;
}
