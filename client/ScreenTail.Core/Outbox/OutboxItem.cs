namespace ScreenTail.Core.Outbox;

/// <summary>What a queued piece of work is for. The wording a technician sees differs per kind.</summary>
public enum OutboxKind
{
    /// <summary>Ask the backend to draft a note from a session's bundle (ST-063).</summary>
    Draft,

    /// <summary>Add the note to a PSA ticket (ST-093).</summary>
    PublishNote,

    /// <summary>Add the time entry (ST-094).</summary>
    PublishTimeEntry,

    /// <summary>Publish the knowledge-base article (ST-096).</summary>
    PublishArticle,
}

/// <summary>Where a piece of work has got to.</summary>
public enum OutboxState
{
    /// <summary>Waiting. Either the network is down or its backoff has not elapsed.</summary>
    Pending,

    /// <summary>
    /// The request went and no answer came back, so whether it happened is unknown.
    ///
    /// <b>Not the same as pending, and deliberately not retried.</b> A publish that may already have
    /// landed cannot be repeated on the chance it did not: the second copy of a note appears in a
    /// customer's ticket and the technician is the last to find out. It leaves this state only by asking
    /// the provider whether the thing exists.
    /// </summary>
    Uncertain,

    Done,

    /// <summary>Refused, or retried until the ceiling. A technician is told; nothing tries again.</summary>
    Failed,
}

/// <param name="SessionId">Which session the work belongs to. Retention uses it (INV-12).</param>
/// <param name="IdempotencyKey">
/// What makes this piece of work the same piece of work. Finalize can run twice — a crash between
/// drafting and recording it, a recovered session — and this is what stops that becoming two drafts.
/// </param>
/// <param name="Payload">
/// JSON for the sender. It never holds anything the session does not already hold, and it is deleted with
/// the session's raw data rather than outliving it.
/// </param>
public sealed record NewOutboxItem(string SessionId, OutboxKind Kind, string IdempotencyKey, string Payload);

/// <param name="Attempts">How many times it has been sent. The backoff and the ceiling read this.</param>
/// <param name="DueAt">Not before this. A drain that runs early leaves it alone.</param>
/// <param name="RemoteId">What the far end called it, once it exists. Null until then.</param>
public sealed record OutboxItem(
    string Id,
    string SessionId,
    OutboxKind Kind,
    string IdempotencyKey,
    string Payload,
    OutboxState State,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    string? RemoteId);

/// <summary>How a send ended. Four outcomes, because three of them are wrong for at least one caller.</summary>
public readonly record struct SendOutcome
{
    private SendOutcome(OutboxState state, string? remoteId, string? message)
    {
        State = state;
        RemoteId = remoteId;
        Message = message;
    }

    public OutboxState State { get; }

    public string? RemoteId { get; }

    /// <summary>Plain language for the technician. Spec §4's first half; never a stack trace.</summary>
    public string? Message { get; }

    /// <summary>It happened, and the far end called it this.</summary>
    public static SendOutcome Done(string? remoteId) => new(OutboxState.Done, remoteId, null);

    /// <summary>It did not happen and could next time: no network, a timeout before sending, a rate limit.</summary>
    public static SendOutcome Retry(string message) => new(OutboxState.Pending, null, message);

    /// <summary>It will not happen: a missing ticket, a rejected payload, credentials that are wrong.</summary>
    public static SendOutcome Failed(string message) => new(OutboxState.Failed, null, message);

    /// <summary>
    /// The request went and the answer did not come back.
    ///
    /// The one outcome that must not be guessed at. Treating it as a retry duplicates work that may have
    /// landed; treating it as done loses work that may not have.
    /// </summary>
    public static SendOutcome Unknown(string message) => new(OutboxState.Uncertain, null, message);
}

/// <param name="Drafts">Sessions waiting to be drafted. Spec §5 S3's "Draft pending — offline".</param>
/// <param name="Uncertain">Work somebody has to look at, because nothing will resolve it on its own.</param>
public readonly record struct OutboxWaiting(int Drafts, int Publishes, int Uncertain)
{
    public int Total => Drafts + Publishes + Uncertain;
}

/// <summary>Where the queue is kept. The encrypted session store implements it (ST-005).</summary>
public interface IOutboxStore
{
    /// <returns>False when an item with the same idempotency key is already queued.</returns>
    Task<bool> EnqueueAsync(OutboxItem item, CancellationToken ct = default);

    /// <summary>The oldest piece of work that is due, or null. Uncertain items are not due; they are asked about.</summary>
    Task<OutboxItem?> TakeDueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The oldest uncertain item, for the confirmation pass.</summary>
    Task<OutboxItem?> TakeUncertainAsync(CancellationToken ct = default);

    Task UpdateAsync(OutboxItem item, CancellationToken ct = default);

    Task<OutboxWaiting> CountWaitingAsync(CancellationToken ct = default);
}
