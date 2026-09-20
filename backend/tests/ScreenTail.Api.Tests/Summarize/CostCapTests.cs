using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// The daily spending cap, against the real ledger and under concurrency (ST-063; 2026-09-19 review).
///
/// The cap was check-then-act. <c>SpentTodayAsync</c> and the write that recorded a cost were a model
/// call apart — ten to forty seconds — and nothing happened in between. Every request that started
/// while the tenant was a penny under its budget passed the check, so a device that opened five hundred
/// of them at once ran up five hundred drafts against a cap of ten dollars. A cap enforced at the start
/// of a queue of one is not a cap.
///
/// It is a reservation now: the estimated ceiling is written before the call and settled to the real
/// figure after, so concurrent requests see each other. The cost of that is a session's worth of
/// over-estimate while a draft is in flight, which errs towards drafting one session too few rather than
/// a hundred too many.
///
/// <c>CostLedger</c> itself had no test at all before this. Only the fakes were exercised, so the query
/// the cap actually reads had never run anywhere — and EF's SQLite provider refuses <c>Sum</c> over
/// <c>decimal</c>, which is why nobody noticed.
/// </summary>
public sealed class CostCapTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task TheLedgerCanActuallyAddUpWhatATenantSpent()
    {
        // The production query, run for real. It had never been executed by anything.
        var tenantId = Guid.NewGuid();
        _ = await UseLedgerAsync(async ledger =>
        {
            await ledger.RecordAsync(tenantId, "s1", "gemini", 0.02m);
            await ledger.RecordAsync(tenantId, "s2", "gemini", 0.03m);
        });

        var spent = await UseLedgerAsync(ledger => ledger.SpentTodayAsync(tenantId));

        Assert.Equal(0.05m, spent);
    }

    [Fact]
    public async Task OneTenantsSpendingIsNotAnothersConcern()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        _ = await UseLedgerAsync(async ledger =>
        {
            await ledger.RecordAsync(mine, "s1", "gemini", 0.02m);
            await ledger.RecordAsync(theirs, "s2", "gemini", 9.99m);
        });

        Assert.Equal(0.02m, await UseLedgerAsync(ledger => ledger.SpentTodayAsync(mine)));
    }

    [Fact]
    public async Task YesterdaysSpendingDoesNotCountAgainstToday()
    {
        var tenantId = Guid.NewGuid();
        _ = await UseLedgerAsync(async ledger => await ledger.RecordAsync(tenantId, "s1", "gemini", 5m));
        await api.UseAsync(db => db.DraftCosts
            .Where(c => c.TenantId == tenantId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.At, DateTimeOffset.UtcNow.AddDays(-1))));

        Assert.Equal(0m, await UseLedgerAsync(ledger => ledger.SpentTodayAsync(tenantId)));
    }

    [Fact]
    public async Task ARunawayClientCannotOutrunTheCap()
    {
        // The finding. Fifty requests at once against a cap of one dollar. Check-then-act let all fifty
        // through, because none of them had recorded anything by the time the others looked: fifty
        // drafts on a one-dollar budget.
        //
        // The guarantee now is that a tenant overshoots by at most one reservation. It is not zero, and
        // saying so is the point: two requests that arrive together may both fit under the cap and both
        // be allowed, and that is a budget behaving like a budget.
        var tenantId = Guid.NewGuid();
        var options = new SummarizationOptions { ApiKey = "test-key", DailyCostCapUsd = 1m };

        var drafted = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            var result = await UseLedgerAsync(options, ledger =>
                new SummarizationService(new SlowLlm(0.05m), null, ledger, options)
                    .DraftAsync(tenantId, Bundle($"s-{i}"), TestContext.Current.CancellationToken));
            return result.Ok;
        }));

        var spent = await UseLedgerAsync(options, ledger => ledger.SpentTodayAsync(tenantId));

        Assert.Contains(drafted, ok => !ok);
        Assert.Contains(drafted, ok => ok);
        Assert.True(
            spent <= options.DailyCostCapUsd + options.MaxSessionCostUsd,
            $"Spent ${spent} against a ${options.DailyCostCapUsd} cap with a ${options.MaxSessionCostUsd} reservation.");
    }

    [Fact]
    public async Task ASessionThatCostMoreThanItsReservationStillStopsTheNextOne()
    {
        // The reservation is an estimate, so a session can cost more than it claimed — a repair retry
        // doubles the bill, and the ceiling is a guess about a provider's prices. What must not happen
        // is the cap staying broken: the truth is settled, and the next request reads the truth.
        var tenantId = Guid.NewGuid();
        var options = new SummarizationOptions { ApiKey = "test-key", DailyCostCapUsd = 0.50m };

        var first = await UseLedgerAsync(options, ledger =>
            new SummarizationService(new SlowLlm(2m), null, ledger, options)
                .DraftAsync(tenantId, Bundle("s-1"), TestContext.Current.CancellationToken));

        var second = await UseLedgerAsync(options, ledger =>
            new SummarizationService(new SlowLlm(2m), null, ledger, options)
                .DraftAsync(tenantId, Bundle("s-2"), TestContext.Current.CancellationToken));

        Assert.True(first.Ok);
        Assert.Equal(SummarizeStatus.CostCapReached, second.Status);
    }

    [Fact]
    public async Task ATenantWithBudgetLeftStillDrafts()
    {
        // The control: a reservation that refuses everybody would pass the test above.
        var tenantId = Guid.NewGuid();
        var options = new SummarizationOptions { ApiKey = "test-key", DailyCostCapUsd = 10m };

        var result = await UseLedgerAsync(options, ledger =>
            new SummarizationService(new SlowLlm(0.02m), null, ledger, options)
                .DraftAsync(tenantId, Bundle("s-1"), TestContext.Current.CancellationToken));

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(0.02m, await UseLedgerAsync(ledger => ledger.SpentTodayAsync(tenantId)));
    }

    [Fact]
    public async Task AReservationIsSettledToWhatWasActuallySpent()
    {
        // While a draft is in flight the tenant is charged the ceiling; afterwards, the truth. Without
        // the settle, one cheap session would eat a tenth of the day's budget.
        var tenantId = Guid.NewGuid();
        var options = new SummarizationOptions { ApiKey = "test-key" };

        _ = await UseLedgerAsync(ledger =>
            new SummarizationService(new SlowLlm(0.015m), null, ledger, options)
                .DraftAsync(tenantId, Bundle("s-1"), TestContext.Current.CancellationToken));

        Assert.Equal(0.015m, await UseLedgerAsync(ledger => ledger.SpentTodayAsync(tenantId)));
        Assert.Equal(1, await api.UseAsync(db => db.DraftCosts.CountAsync(c => c.TenantId == tenantId)));
    }

    [Fact]
    public async Task AProviderThatNeverAnsweredCostsNothing()
    {
        // An outage must not eat the day's budget. The reservation is released when there is no bill.
        var tenantId = Guid.NewGuid();
        var options = new SummarizationOptions { ApiKey = "test-key" };
        var down = new SlowLlm(0m)
        {
            Failure = new ProviderError(ProviderErrorKind.Unavailable, "The model did not answer.", "It is queued."),
        };

        var result = await UseLedgerAsync(ledger =>
            new SummarizationService(down, null, ledger, options)
                .DraftAsync(tenantId, Bundle("s-1"), TestContext.Current.CancellationToken));

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Equal(0m, await UseLedgerAsync(ledger => ledger.SpentTodayAsync(tenantId)));
    }

    /// <summary>The deployment's settings. A test that wants a different cap passes its own.</summary>
    private static readonly SummarizationOptions Options = new() { ApiKey = "test-key" };

    private static SummarizeBundle Bundle(string sessionId) => new()
    {
        SessionId = sessionId,
        DurationMs = 60_000,
        Frames = [new BundleFrame("f1", 1, "Services")],
        Transcript = [new BundleSegment("t1", 2, "restarting it")],
    };

    /// <summary>A real <see cref="CostLedger"/> over the fixture's database, in its own scope.</summary>
    private Task<T> UseLedgerAsync<T>(Func<CostLedger, Task<T>> work) => UseLedgerAsync(Options, work);

    private Task<T> UseLedgerAsync<T>(SummarizationOptions settings, Func<CostLedger, Task<T>> work) =>
        api.UseAsync(db => work(new CostLedger(db, TimeProvider.System, settings)));

    private Task<bool> UseLedgerAsync(Func<CostLedger, Task> work) =>
        api.UseAsync(async db =>
        {
            await work(new CostLedger(db, TimeProvider.System, Options));
            return true;
        });

    /// <summary>Takes long enough that the other requests are all inside the window the race needs.</summary>
    private sealed class SlowLlm(decimal cost) : ILlmProvider
    {
        public string Name => "slow";

        public ProviderError? Failure { get; init; }

        public async Task<ProviderResult<LlmDraft>> DraftAsync(SummarizeBundle bundle, string? repair, CancellationToken ct = default)
        {
            await Task.Delay(50, ct);
            return Failure is { } error
                ? ProviderResult.Failure<LlmDraft>(error)
                : ProviderResult.Success(new LlmDraft(Draft, cost));
        }

        private static string Draft => """
            {"problem": "p", "steps": [], "result": "r", "follow_ups": [], "suggested_title": "t",
             "suggested_time_minutes": 1, "kb_candidate": false, "kb_reason": "k", "source": "cloud",
             "prompt_version": "note_v1"}
            """;
    }
}
