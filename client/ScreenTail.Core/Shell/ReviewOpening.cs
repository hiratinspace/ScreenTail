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
    /// <param name="sessionLocalOnly">The session was recorded under local-only.</param>
    /// <param name="localOnlyNow">
    /// Asks the service whether local-only is in force now: true, false, or null when it would not say.
    /// Asked only when everything else allows the lookup.
    /// </param>
    public static async Task<bool> MayLookUpSuggestedTicketAsync(
        bool sessionLocalOnly,
        string? suggestedTicket,
        int integrations,
        Func<CancellationToken, Task<bool?>> localOnlyNow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localOnlyNow);
        if (sessionLocalOnly || string.IsNullOrWhiteSpace(suggestedTicket) || integrations <= 0)
        {
            return false;
        }

        // The setting in force now, not only the session's flag: a session recorded before local-only
        // was turned on is opened under it. An answer that did not come is not taken to mean "off".
        return await localOnlyNow(ct).ConfigureAwait(false) == false;
    }
}
