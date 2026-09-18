namespace ScreenTail.Api.Providers;

/// <param name="Id">The provider's own identifier, as a string: ConnectWise uses integers, others do not.</param>
/// <param name="Summary">The ticket's one-line summary. Shown in the picker.</param>
/// <param name="Company">Who it is for. Shown so a technician cannot publish to the right number at the wrong client.</param>
public sealed record TicketRef(string Id, string Summary, string Company, string? Status = null);

/// <param name="Internal">
/// True for an internal note, false for one the customer sees. Spec v0.4.1 Q2 makes Internal the default:
/// a first draft should not default to customer-facing.
/// </param>
public sealed record TicketNote(string TicketId, string Body, bool Internal, IReadOnlyList<NoteAttachment> Attachments);

/// <param name="Bytes">The image. Redacted before it ever reached the backend (INV-1).</param>
public sealed record NoteAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Bytes);

/// <param name="Minutes">Unrounded active minutes. The client applies the tenant's rounding, so rounding here would round twice.</param>
public sealed record TimeEntry(string TicketId, DateTimeOffset StartedAt, int Minutes, string Notes, bool Billable);

/// <param name="Id">What the provider gave it, so a retry can tell "already published" from "not yet".</param>
public sealed record PublishedNote(string Id, Uri? Url);

/// <summary>
/// A professional services automation platform: ConnectWise Manage in v1, HaloPSA in v1.1 (ST-121).
///
/// One interface for all of them, and the client never sees a concrete one — it asks the backend to
/// publish and the backend decides which provider that means. A client that knew about ConnectWise would
/// need a ConnectWise release to support Halo, and would hold a second set of credentials on a
/// technician's laptop.
///
/// Every method returns <see cref="ProviderResult{T}"/> rather than throwing, because a PSA being down,
/// a key being wrong and a ticket being closed are ordinary outcomes of a publish with different answers
/// for the technician (Spec §4).
/// </summary>
public interface IPsaProvider
{
    /// <summary>Which provider this is, for logs and the integrations table. A name, never a credential.</summary>
    string Name { get; }

    /// <summary>
    /// Cheapest call that proves the credentials work, for Settings → Integrations to show a state
    /// rather than "unknown until you try to publish".
    /// </summary>
    Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default);

    /// <param name="query">What the technician typed. Three characters or more (Spec §5 S3).</param>
    Task<ProviderResult<IReadOnlyList<TicketRef>>> SearchTicketsAsync(string query, CancellationToken ct = default);

    /// <summary>Publishes the note. INV-3: only ever called because a technician pressed Publish.</summary>
    Task<ProviderResult<PublishedNote>> AddNoteAsync(TicketNote note, CancellationToken ct = default);

    Task<ProviderResult<PublishedNote>> AddTimeEntryAsync(TimeEntry entry, CancellationToken ct = default);
}
