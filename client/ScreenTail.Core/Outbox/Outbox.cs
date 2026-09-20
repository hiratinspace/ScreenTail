namespace ScreenTail.Core.Outbox;

/// <summary>
/// Work that has to reach the network, kept until it does (ST-064).
///
/// A technician finishes a job on a train and closes the laptop. The draft has to survive that, and the
/// publish that follows it has to survive a PSA that is having a bad afternoon. So both go through here:
/// queued durably, attempted when there is a network, and backed off when there is not.
///
/// <b>The hard part is not the queue, it is the attempt nobody knows the outcome of.</b> A request that
/// reaches a PSA and whose answer never comes back may or may not have added the note. Retrying it puts a
/// second copy in a customer's ticket, and the technician is the last to find out; dropping it loses a
/// note they think they published. So that outcome has a state of its own, nothing retries it, and it
/// leaves only by asking the provider whether the thing exists.
///
/// Platform-neutral (ADR-0002): the store and the network are both behind interfaces, so the rules can be
/// argued about without a machine, a provider or a connection.
/// </summary>
public sealed class Outbox(
    IOutboxStore store,
    Func<OutboxItem, CancellationToken, Task<SendOutcome>> send,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// How many times a retryable failure is tried before a technician is told instead.
    ///
    /// Eight attempts over the schedule below is about a day. Past that the thing is not coming back on
    /// its own, and a queue that retries forever is a battery drain and a log nobody reads.
    /// </summary>
    public const int MaxAttempts = 8;

    /// <summary>
    /// Asks the provider whether an uncertain piece of work actually landed.
    ///
    /// Returns the remote identifier if it did, null if it did not, and throws if it could not be
    /// established — in which case the item stays uncertain, which is the honest answer.
    ///
    /// Null leaves items parked, and that is the right default: a duplicate note in a customer's ticket
    /// is worse than one a technician can see has not been sent.
    /// </summary>
    public Func<OutboxItem, CancellationToken, Task<string?>>? Confirm { get; init; }

    /// <summary>
    /// Queues work, unless the same work is already queued.
    ///
    /// <returns>False when the idempotency key is already there, which is not a failure.</returns>
    /// </summary>
    public async Task<bool> EnqueueAsync(NewOutboxItem work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var now = _time.GetUtcNow();
        return await store.EnqueueAsync(
            new OutboxItem(
                Guid.NewGuid().ToString("N")[..16],
                work.SessionId,
                work.Kind,
                work.IdempotencyKey,
                work.Payload,
                OutboxState.Pending,
                Attempts: 0,
                CreatedAt: now,
                DueAt: now,
                LastAttemptAt: null,
                LastError: null,
                RemoteId: null),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Does one piece of work: resolves an uncertain item if there is one, otherwise attempts the oldest
    /// due item.
    /// </summary>
    /// <returns>False when there was nothing to do, so a caller can idle rather than spin.</returns>
    public async Task<bool> DrainAsync(CancellationToken ct = default)
    {
        // Resolution first. An uncertain item blocks nothing, but leaving it while newer work goes past
        // means the one thing a person has to look at is also the one thing nothing looks at.
        //
        // "Blocks nothing" was not true: a Confirm that could not answer returned here, so one PSA that
        // would not answer a lookup stopped the whole queue for good (2026-09-19 review). Falling
        // through is what makes the comment true.
        if (Confirm is not null
            && await store.TakeUncertainAsync(ct).ConfigureAwait(false) is { } uncertain
            && await ResolveAsync(uncertain, ct).ConfigureAwait(false))
        {
            return true;
        }

        var item = await store.TakeDueAsync(_time.GetUtcNow(), ct).ConfigureAwait(false);
        if (item is null)
        {
            return false;
        }

        return await AttemptAsync(item, ct).ConfigureAwait(false);
    }

    /// <summary>What is waiting, for the HUD's offline pill and Review's banner (Spec §5 S2, S3).</summary>
    public Task<OutboxWaiting> WaitingAsync(CancellationToken ct = default) => store.CountWaitingAsync(ct);

    /// <summary>
    /// How long before attempt <paramref name="attempts"/> + 1.
    ///
    /// Thirty seconds doubling to an hour. Quick at the start because the common case is a network that
    /// came back a moment ago, and capped because the other common case is a provider that is down for
    /// the afternoon and does not want to be asked every thirty seconds until it returns.
    /// </summary>
    public static TimeSpan RetryAfter(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30L * (1L << Math.Min(Math.Max(attempts, 1) - 1, 20))));

    private async Task<bool> AttemptAsync(OutboxItem item, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var attempts = item.Attempts + 1;

        SendOutcome outcome;
        try
        {
            outcome = await send(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Being asked to stop, not a failure. The item is left exactly as it was for the next run.
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or HttpRequestException
            or OperationCanceledException)
        {
            // The transport threw rather than answering. Whether it reached the far end is exactly what
            // Unknown is for: a thrown timeout is not evidence that nothing happened.
            //
            // OperationCanceledException is in that list because HttpClient's own timeout throws
            // TaskCanceledException, not TimeoutException. It used to fall straight out of here, the
            // host's loop read it as "we are shutting down" and ended for the life of the service, and
            // the item was left Pending with its attempt count untouched — so the next run sent it
            // again. A duplicate publish is exactly what this class exists to prevent (2026-09-19
            // review). The clause above is what keeps a real shutdown distinguishable.
            outcome = ex is IOException or HttpRequestException
                ? SendOutcome.Retry("The request could not be sent.")
                : SendOutcome.Unknown("The request was sent and no answer came back.");
        }
#pragma warning disable CA1031 // Anything else is this item's problem, and must not become the queue's.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // A payload the sender cannot make sense of, a destination the egress policy refuses, a bug.
            // None of them get better by waiting, and letting it out of here stopped every other item
            // behind it too. The type, never the message: an exception can quote a payload (INV-10).
            outcome = SendOutcome.Failed($"This could not be sent ({ex.GetType().Name}).");
        }

        var state = outcome.State;
        if (state == OutboxState.Pending && attempts >= MaxAttempts)
        {
            // Out of patience rather than out of hope. A technician is told, and can retry by hand.
            state = OutboxState.Failed;
        }

        await store.UpdateAsync(
            item with
            {
                State = state,
                Attempts = attempts,
                LastAttemptAt = now,
                LastError = outcome.Message ?? item.LastError,
                RemoteId = outcome.RemoteId ?? item.RemoteId,
                DueAt = state == OutboxState.Pending ? now + RetryAfter(attempts) : item.DueAt,
            },
            ct).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> ResolveAsync(OutboxItem item, CancellationToken ct)
    {
        string? remoteId;
        try
        {
            remoteId = await Confirm!(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A lookup that will not answer must not stop the rest of the queue.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Could not establish it either way. Staying uncertain is the honest outcome, and the item is
            // still counted for the technician.
            //
            // False, not true: this did not do the work, so the caller goes on to the due item behind it.
            return false;
        }

        await store.UpdateAsync(
            remoteId is null

                // It never landed, so it is ordinary work again. The attempt count is kept: a request
                // that timed out still cost an attempt, and forgetting that makes the ceiling meaningless.
                ? item with { State = OutboxState.Pending, DueAt = _time.GetUtcNow() }
                : item with { State = OutboxState.Done, RemoteId = remoteId },
            ct).ConfigureAwait(false);

        return true;
    }
}
