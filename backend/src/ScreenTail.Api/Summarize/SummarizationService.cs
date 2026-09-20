using System.Text.Json;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Summarize;

/// <param name="CostUsd">What this call cost. Estimated by the provider from its own token counts.</param>
public sealed record LlmDraft(string Json, decimal CostUsd);

/// <summary>
/// A model that can turn a session into a note (ST-063).
///
/// One method, because a session gets one call. The abstraction exists so that Gemini Flash is a
/// default rather than a dependency: the scope document names it, the backlog allows OpenAI and
/// Anthropic to be swapped in, and none of the rules in <see cref="SummarizationService"/> know which
/// is running.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Which model answered. Recorded with the cost and shown in diagnostics; never a key.</summary>
    string Name { get; }

    /// <param name="repair">
    /// Why the last attempt was rejected, when this is the second try. The model is told what it got
    /// wrong rather than simply asked again, because asking again usually produces the same answer.
    /// </param>
    Task<ProviderResult<LlmDraft>> DraftAsync(SummarizeBundle bundle, string? repair, CancellationToken ct = default);
}

/// <summary>
/// What each tenant has spent on drafting (ST-063).
///
/// Per tenant and per day. One busy MSP must not stop drafting for everyone else on the same instance,
/// and a cap that resets is a budget rather than a wall.
/// </summary>
public interface ICostLedger
{
    Task<decimal> SpentTodayAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Claims <paramref name="estimateUsd"/> of today's budget before a call is made, or returns null
    /// when the tenant cannot afford it.
    ///
    /// The cap used to be read, and then acted on a model call later, so everything that started in
    /// between passed a check nobody had yet moved. A reservation is a row, which is how the next
    /// request to look can see it.
    /// </summary>
    Task<Guid?> ReserveAsync(Guid tenantId, string sessionId, string provider, decimal estimateUsd, CancellationToken ct = default);

    /// <summary>Replaces a reservation with what the call actually cost. Zero releases it.</summary>
    Task SettleAsync(Guid reservationId, decimal costUsd, CancellationToken ct = default);

    /// <summary>Records a cost with no reservation behind it. For backfills and tests, not the draft path.</summary>
    Task RecordAsync(Guid tenantId, string sessionId, string provider, decimal costUsd, CancellationToken ct = default);
}

public enum SummarizeStatus
{
    Ok,

    /// <summary>No provider is configured on this deployment. Actionable, and not a failure of the session.</summary>
    NotConfigured,

    /// <summary>The tenant has spent its day. Spec §6: the client drafts on the device instead.</summary>
    CostCapReached,

    /// <summary>Nobody answered. The client queues it (ST-064) rather than losing the session.</summary>
    Unavailable,

    /// <summary>The model answered and what it said could not be shown.</summary>
    Invalid,
}

/// <param name="Repaired">The first answer broke the rules and the second one did not. Logged, and worth watching.</param>
public sealed record SummarizeResult(
    SummarizeStatus Status,
    DraftJson? Draft = null,
    string? Reason = null,
    decimal CostUsd = 0,
    string? Provider = null,
    bool UsedFallback = false,
    bool Repaired = false)
{
    public bool Ok => Status == SummarizeStatus.Ok;
}

/// <summary>
/// The one model call a session gets (ST-063).
///
/// Most of this class is about the days it does not work. The happy path is a request and a parse; the
/// value is what happens when a provider is slow, a model returns something malformed, or a bug drafts
/// the same session in a loop and somebody gets a bill.
///
/// <b>The cap is checked before the call, not after.</b> A cap enforced after the request has already
/// been paid for is an alert.
///
/// <b>A rejected draft is retried once, with the reasons.</b> A model that cited a frame it invented
/// usually fixes it when told which one. Once and not in a loop: a second failure is a model having a
/// bad day, and a third is a bill for two notes nobody can use.
///
/// <b>Nothing is stored.</b> INV-7 says the backend never persists a capture; the bundle lives for the
/// length of one call, and what is written afterwards is a cost row with a tenant, a session id, a
/// provider name and a number.
/// </summary>
public sealed class SummarizationService(
    ILlmProvider primary,
    ILlmProvider? fallback,
    ICostLedger ledger,
    SummarizationOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SummarizeResult> DraftAsync(Guid tenantId, SummarizeBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (!options.Configured)
        {
            return new SummarizeResult(
                SummarizeStatus.NotConfigured,
                Reason: "No summarization provider is configured on this deployment.");
        }

        // Claimed before anything is spent, and claimed as a row so the next request to look can see it.
        // Reading the total and writing the cost a model call apart is how five hundred simultaneous
        // requests all passed a ten-dollar cap (2026-09-19 review).
        //
        // A tenant past its budget is told so and drafts on the device (Spec §6's "Cloud drafting paused
        // for today").
        var reservation = await ledger
            .ReserveAsync(tenantId, bundle.SessionId, primary.Name, options.MaxSessionCostUsd, ct)
            .ConfigureAwait(false);

        if (reservation is null)
        {
            return new SummarizeResult(
                SummarizeStatus.CostCapReached,
                Reason: "This tenant has reached its drafting budget for today.");
        }

        return await DraftWithinBudgetAsync(reservation.Value, bundle, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The call itself, with the budget already claimed.
    ///
    /// Its own method so that every way out settles the reservation. An outage costs nothing and must
    /// give the budget back, or one bad afternoon at the provider spends a tenant's whole day.
    /// </summary>
    private async Task<SummarizeResult> DraftWithinBudgetAsync(Guid reservation, SummarizeBundle bundle, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.Timeout);

        var (model, usedFallback, failure) = await ChooseAsync(bundle, deadline.Token).ConfigureAwait(false);
        if (model is null)
        {
            // Nobody answered, so nobody billed us. Give the budget back.
            await ledger.SettleAsync(reservation, 0m, CancellationToken.None).ConfigureAwait(false);
            return new SummarizeResult(
                failure!.Kind == ProviderErrorKind.Unavailable ? SummarizeStatus.Unavailable : SummarizeStatus.Invalid,
                Reason: failure.ToString());
        }

        var provider = usedFallback ? fallback! : primary;
        var cost = model.CostUsd;
        var repaired = false;
        (DraftJson? Draft, string Reason) outcome;

        try
        {
            outcome = Interpret(model.Json, bundle);
            if (outcome.Draft is null)
            {
                // Told what it got wrong rather than simply asked again: asking again usually produces the
                // same answer, and this call is not free.
                var second = await provider.DraftAsync(bundle, outcome.Reason, deadline.Token).ConfigureAwait(false);
                if (second.Ok)
                {
                    cost += second.Value!.CostUsd;
                    outcome = Interpret(second.Value.Json, bundle);
                    repaired = outcome.Draft is not null;
                }
            }
        }
        finally
        {
            // Recorded whatever the outcome, and whatever went wrong getting there.
            //
            // A rejected draft cost real money, and a cap that only counts successes is a cap a broken
            // model walks straight through. The 2026-09-19 review found the harder half: this was the
            // last statement in the method, so anything that threw after the model answered lost the
            // cost entirely — a billed draft, no row, and a daily cap that never moved.
            //
            // CancellationToken.None, not the request's. The money is gone whether or not the technician
            // is still waiting for the answer, and a client that hangs up mid-request was cancelling the
            // write that records what they spent.
            await ledger.SettleAsync(reservation, cost, CancellationToken.None).ConfigureAwait(false);
        }

        return outcome.Draft is null
            ? new SummarizeResult(SummarizeStatus.Invalid, Reason: outcome.Reason, CostUsd: cost, Provider: provider.Name, UsedFallback: usedFallback)
            : new SummarizeResult(SummarizeStatus.Ok, outcome.Draft, null, cost, provider.Name, usedFallback, repaired);
    }

    /// <summary>
    /// Asks the primary, and the fallback only when the primary was unreachable.
    ///
    /// Only an outage falls over. A rejected payload is rejected twice, and a revoked key is not fixed by
    /// spending money somewhere else — both would buy a second failure at the price of a second call.
    /// </summary>
    private async Task<(LlmDraft? Draft, bool UsedFallback, ProviderError? Failure)> ChooseAsync(
        SummarizeBundle bundle,
        CancellationToken ct)
    {
        var first = await primary.DraftAsync(bundle, null, ct).ConfigureAwait(false);
        if (first.Ok)
        {
            return (first.Value, false, null);
        }

        if (first.Error!.Kind != ProviderErrorKind.Unavailable || fallback is null)
        {
            return (null, false, first.Error);
        }

        var second = await fallback.DraftAsync(bundle, null, ct).ConfigureAwait(false);
        return second.Ok ? (second.Value, true, null) : (null, true, second.Error);
    }

    /// <summary>Parses what the model said and applies the post-conditions to it.</summary>
    private static (DraftJson? Draft, string Reason) Interpret(string json, SummarizeBundle bundle)
    {
        DraftJson? draft;
        try
        {
            draft = JsonSerializer.Deserialize<DraftJson>(Extract(json), Json);
        }
        catch (JsonException ex)
        {
            return (null, $"The model did not return the agreed shape: {ex.Message}");
        }

        if (draft is null)
        {
            return (null, "The model returned nothing usable.");
        }

        var reasons = DraftValidator.Check(draft, bundle);
        return reasons.Count == 0
            ? (draft, string.Empty)
            : (null, string.Join(" ", reasons));
    }

    /// <summary>
    /// Pulls the object out of whatever the model wrapped it in.
    ///
    /// Models fence JSON in markdown without being asked, and a draft rejected for a pair of backticks
    /// costs a retry for nothing.
    /// </summary>
    private static string Extract(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
