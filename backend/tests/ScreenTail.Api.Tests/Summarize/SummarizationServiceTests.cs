using System.Diagnostics;
using System.Net;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// ST-063. The one model call a session gets, and everything that can go wrong with it.
///
/// Three of the four things tested here are failures, which is the right proportion: the happy path is a
/// POST and a parse, and the value of this class is what it does on the days a provider is slow, a model
/// returns something malformed, or a bug drafts the same session in a loop and somebody gets a bill.
/// </summary>
public sealed class SummarizationServiceTests
{
    [Fact]
    public async Task AGoodDraftComesBackWithWhatItCost()
    {
        var provider = new FakeLlm { Reply = Good() };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("Nothing would print from the reception workstation.", result.Draft!.Problem);
        Assert.Equal(0.04m, result.CostUsd);
        Assert.Equal(0.04m, ledger.Recorded);
    }

    [Fact]
    public async Task AProviderThatIsDownFallsOverToTheOtherOne()
    {
        // ST-063 AC2. A 5xx is not a reason to lose a technician's session; it is a reason to ask
        // somebody else. Only an outage does this — a rejected payload would be rejected twice.
        var primary = new FakeLlm { Failure = new ProviderError(ProviderErrorKind.Unavailable, "The model did not answer.", "Try again shortly.") };
        var fallback = new FakeLlm { Reply = Good(), Name = "fallback" };
        var service = Service(primary, fallback);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.True(result.UsedFallback);
        Assert.Equal("fallback", result.Provider);
    }

    [Fact]
    public async Task ARefusalIsNotRetriedAgainstTheFallback()
    {
        // A wrong payload is wrong at both providers, and a key that has been revoked is not fixed by
        // spending money somewhere else.
        var primary = new FakeLlm { Failure = new ProviderError(ProviderErrorKind.Invalid, "The request was malformed.", "This is a bug; report it.") };
        var fallback = new FakeLlm { Reply = Good() };
        var service = Service(primary, fallback);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task WithNoFallbackAnOutageIsReportedRatherThanInvented()
    {
        var primary = new FakeLlm { Failure = new ProviderError(ProviderErrorKind.Unavailable, "The model did not answer.", "Try again shortly.") };
        var service = Service(primary);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Contains("did not answer", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADraftThatBreaksTheRulesIsSentBackOnceToBeFixed()
    {
        // A model that cited a frame it invented usually fixes it when told which one. One retry, not a
        // loop: a second failure is a model having a bad day, and a third is a bill.
        var provider = new FakeLlm
        {
            Reply = WithStep("Restarted it.", frames: ["f-invented"], transcript: ["t1"]),
            ReplyOnRetry = Good(),
        };
        var service = Service(provider);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(2, provider.Calls);
        Assert.True(result.Repaired);
    }

    [Fact]
    public async Task ADraftThatBreaksTheRulesTwiceIsRefusedRatherThanShown()
    {
        // The whole point of the post-conditions. A note that cites evidence it does not have is worse
        // than no note: the technician is asked to review something that looks checked and is not.
        var provider = new FakeLlm { Reply = WithStep("Restarted it.", frames: ["f-invented"], transcript: ["t1"]) };
        var service = Service(provider);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(SummarizeStatus.Invalid, result.Status);
        Assert.Equal(2, provider.Calls);
        Assert.Contains("f-invented", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomethingThatIsNotJsonAtAllIsTreatedAsABrokenDraft()
    {
        var provider = new FakeLlm { Reply = "I'm sorry, I can't help with that." };
        var service = Service(provider);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(SummarizeStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ATenantThatHasSpentItsDayIsStoppedBeforeTheCallNotAfter()
    {
        // ST-063 AC3, and the reason the check is first: a cap enforced after the request has already
        // been paid for is an alert, not a cap. Spec §6 tells the technician their draft was made on
        // their own device instead.
        var provider = new FakeLlm { Reply = Good() };
        var service = Service(provider, ledger: new FakeLedger { SpentToday = 10m });

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(SummarizeStatus.CostCapReached, result.Status);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task TheCapCountsOneTenantAndNotTheDeployment()
    {
        // One busy MSP must not stop drafting for everyone else on the same instance.
        var ledger = new FakeLedger { SpentToday = 10m, SpentByOthers = 0m };
        var service = Service(new FakeLlm { Reply = Good() }, ledger: ledger);

        var mine = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);
        ledger.SpentToday = 0m;
        var theirs = await service.DraftAsync(Guid.NewGuid(), Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.CostCapReached, mine.Status);
        Assert.True(theirs.Ok);
    }

    [Fact]
    public async Task ADeploymentWithNoKeySaysSoRatherThanFailingAtTheFirstSession()
    {
        var service = new SummarizationService(
            new FakeLlm { Reply = Good() },
            fallback: null,
            new FakeLedger(),
            new SummarizationOptions { ApiKey = string.Empty });

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(SummarizeStatus.NotConfigured, result.Status);
    }

    [Fact]
    public void NothingInTheServiceCanHoldABundle()
    {
        // ST-063 AC4 and INV-7, asserted the way it can be. A reachability test is unreliable in a debug
        // build — locals stay alive for the whole method — so this checks the failure mode instead: the
        // cache somebody adds "just for retries". There is nowhere to put one.
        //
        // The other half is proved by SummarizationPersistsNothingTests, which counts every row in every
        // table across a request. What remains is a profiler run, and it is a manual step by nature.
        var fields = typeof(SummarizationService)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Where(field => Mentions(field.FieldType))
            .Select(field => field.Name)
            .ToList();

        Assert.True(fields.Count == 0, $"The service can hold a bundle in: {string.Join(", ", fields)}");
    }

    /// <summary>Whether a type is, or contains, the thing that must not be kept.</summary>
    private static bool Mentions(Type type) =>
        type == typeof(SummarizeBundle)
        || type == typeof(DraftJson)
        || (type.IsGenericType && type.GetGenericArguments().Any(Mentions))
        || (type.IsArray && Mentions(type.GetElementType()!));

    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SummarizationService Service(FakeLlm primary, FakeLlm? fallback = null, FakeLedger? ledger = null) =>
        new(primary, fallback, ledger ?? new FakeLedger(), new SummarizationOptions { ApiKey = "test-key", DailyCostCapUsd = 10m });

    [Fact]
    public async Task ACallThatCouldNotBeRecordedIsStillACallThatWasPaidFor()
    {
        // 2026-09-19 review. The ledger write is the last thing that happens, so anything that threw
        // after the model answered lost the cost: a billed draft, no row, and a daily cap that never
        // moved. A client that hangs up mid-request did it with the request's own cancellation token.
        //
        // The money is gone whatever happened next, so the row is written with a token nobody can cancel.
        var provider = new FakeLlm { Reply = Good() };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);
        using var hungUp = new CancellationTokenSource();

        ledger.OnRecord = () => hungUp.Cancel();
        var result = await service.DraftAsync(Tenant, Bundle(), hungUp.Token);

        Assert.Equal(0.04m, ledger.Recorded);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task ADraftThatCannotBeReadIsStillACallThatWasPaidFor()
    {
        // The model answered, and was paid, and what it said could not be used. The cap has to see it.
        var provider = new FakeLlm { Reply = "not json at all" };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Invalid, result.Status);
        Assert.Equal(0.08m, ledger.Recorded);
    }

    [Fact]
    public async Task AModelWeStoppedWaitingForIsAssumedToHaveBeenBilled()
    {
        // 2026-09-20 review. A timeout was settled at nothing, on the reasoning that nobody answered so
        // nobody billed us. That is true of a refused connection and false of a sixty-second deadline:
        // the model was sent a request and very likely ran it, and a provider that bills for work it did
        // does not care that we hung up. Settling those at zero makes the daily cap blind to exactly the
        // requests that cost the most.
        var provider = new FakeLlm
        {
            Failure = new ProviderError(ProviderErrorKind.Unavailable, "Too slow.", "Try again.")
            {
                MayHaveBeenBilled = true,
            },
        };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Equal(new SummarizationOptions().MaxSessionCostUsd, ledger.Recorded);
    }

    [Fact]
    public async Task AProviderNobodyCouldReachStillCostsNothing()
    {
        // The other half, and the reason the flag exists rather than a blanket rule: a provider having a
        // bad afternoon must not spend a tenant's whole day. Nothing was sent, so nothing is owed.
        var provider = new FakeLlm
        {
            Failure = new ProviderError(ProviderErrorKind.Unavailable, "No answer.", "Try again."),
        };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        _ = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(0m, ledger.Recorded);
        Assert.Equal(1, ledger.Settles);
    }

    [Fact]
    public async Task AProviderThatThrowsGivesTheBudgetBack()
    {
        // 2026-09-20 review. The first call to the model sat outside the try that settles the
        // reservation, so a provider that threw rather than returning a failure left ten cents claimed
        // and never released. A hundred of those is a tenant's whole day, spent on nothing: the way the
        // production wiring was broken at the time, every single request did it.
        var provider = new FakeLlm { Throws = new InvalidOperationException("misconfigured") };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken));

        Assert.Equal(1, ledger.Settles);
        Assert.Equal(0m, ledger.Recorded);
    }

    [Theory]
    // Program.cs sets both from one setting and arms the service's deadline first, so the deadline wins.
    // The first case gives HttpClient.Timeout some slack so the deadline wins every time here as well:
    // two equal timers race, and the old rule only went wrong when the deadline won.
    [InlineData(100, 300)]
    [InlineData(100, -1)] // The service's deadline is the only one.
    [InlineData(5_000, 100)] // HttpClient.Timeout is the one that fires.
    public async Task ADraftTheDeadlineCutOffIsSettledAtTheEstimate(int deadlineMs, int httpTimeoutMs)
    {
        // 2026-10-12 review. Whether a call may have been billed was read from which token fired, and
        // in the deployed wiring the service's own deadline always fires first. Every production timeout
        // was settled at nothing, the client retried it, and the daily cap never moved during exactly
        // the slowdown it exists for. The request left; that is the whole of the question.
        using var handler = new HangingHandler();
        var (service, ledger) = Real(handler, deadlineMs, httpTimeoutMs);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Equal(new SummarizationOptions().MaxSessionCostUsd, ledger.Recorded);
        Assert.Equal(1, ledger.Settles);
    }

    [Theory]
    [InlineData(100, 300)]
    [InlineData(100, -1)]
    [InlineData(5_000, 100)]
    public async Task ADraftThatNeverLeftCostsNothingHoweverItTimedOut(int deadlineMs, int httpTimeoutMs)
    {
        // The other direction of the same rule. A connection that never opened carried nothing to the
        // model, and charging it would let a network outage spend a tenant's whole day.
        using var handler = new HangingHandler(readsTheRequest: false);
        var (service, ledger) = Real(handler, deadlineMs, httpTimeoutMs);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Equal(0m, ledger.Recorded);
        Assert.Equal(1, ledger.Settles);
    }

    [Fact]
    public async Task ADraftWhoseCallerHungUpAfterItWasSentIsStillCharged()
    {
        // The client's own timeout ends the request on the backend too, and the model does not hear
        // about it. DraftSender retries on the promise that this call was settled at its estimate.
        using var handler = new HangingHandler();
        var (service, ledger) = Real(handler, deadlineMs: 60_000, httpTimeoutMs: 60_000);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var drafting = service.DraftAsync(Tenant, Bundle(), caller.Token);
        await handler.Sent.Task.WaitAsync(TestContext.Current.CancellationToken);
        await caller.CancelAsync();
        var result = await drafting;

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Equal(new SummarizationOptions().MaxSessionCostUsd, ledger.Recorded);
        Assert.Equal(1, ledger.Settles);
    }

    [Fact]
    public async Task ARepairTheDeadlineCutOffIsChargedOnTopOfTheFirstAnswer()
    {
        // The first answer broke the rules and cost what its usage says. The repair was sent and never
        // came back, which is a second call the model may have run, and the cap has to see both.
        using var handler = new HangingHandler(first: GeminiAnswer(
            WithStep("Restarted it.", frames: ["f-invented"], transcript: ["t1"]),
            promptTokens: 40_000));
        var (service, ledger) = Real(handler, deadlineMs: 300, httpTimeoutMs: 300);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        // The repair never answered, so nothing was shown to be unfixable: an outage the client queues,
        // not an invalid draft it fails for good (review round 2 of the 2026-10-12 fixes).
        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Equal(0.03m + new SummarizationOptions().MaxSessionCostUsd, ledger.Recorded);
    }

    [Fact]
    public async Task ARepairThatHadAnOutageIsAnOutageAndCountsWhatItMayHaveCost()
    {
        // Review round 2 of the 2026-10-12 fixes. The first answer broke a rule and the call that would
        // have fixed it never answered: a 429 under the free tier's per-minute limit, or the deadline. It
        // was answered as invalid, which the client fails for good, so a transient outage on the second
        // call lost the session. Nothing has shown the session cannot be drafted, so it is queued, and
        // told when to come back when the provider named a time.
        var provider = new FakeLlm
        {
            Reply = WithStep("Restarted it.", frames: ["f-invented"], transcript: ["t1"]),
            FailureOnRetry = new ProviderError(ProviderErrorKind.Unavailable, "Too slow.", "Try again.", TimeSpan.FromSeconds(30))
            {
                MayHaveBeenBilled = true,
            },
        };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.Contains("Too slow.", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.Draft);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(0.04m + new SummarizationOptions().MaxSessionCostUsd, ledger.Recorded);
        Assert.Equal(ledger.Recorded, result.CostUsd);
        Assert.Equal(1, ledger.Settles);
    }

    [Fact]
    public async Task ARepairThatNeverLeftCostsOnlyTheFirstAnswer()
    {
        var provider = new FakeLlm
        {
            Reply = WithStep("Restarted it.", frames: ["f-invented"], transcript: ["t1"]),
            FailureOnRetry = new ProviderError(ProviderErrorKind.Unavailable, "No answer.", "Try again."),
        };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Null(result.RetryAfter);
        Assert.Equal(0.04m, ledger.Recorded);
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unauthenticated)]
    [InlineData(ProviderErrorKind.Forbidden)]
    [InlineData(ProviderErrorKind.NotFound)]
    public async Task ARepairThisDeploymentWasRefusedIsASettingToFix(ProviderErrorKind kind)
    {
        // A key rotated, or a model retired, between the two calls. The same setting as when the first
        // call is refused, so the same answer: queued until an operator fixes it, with the first answer's
        // bill still counted.
        var provider = new FakeLlm
        {
            Reply = WithStep("Restarted it.", frames: ["f-invented"], transcript: ["t1"]),
            FailureOnRetry = new ProviderError(kind, "The drafting model rejected this deployment's API key.", "Check the Summarization__ApiKey setting on the server."),
        };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.NotConfigured, result.Status);
        Assert.Contains("Summarization__ApiKey", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0.04m, ledger.Recorded);
        Assert.Equal(0.04m, result.CostUsd);
    }

    [Fact]
    public async Task ARepairTheProviderRefusedForWhatItHeldLeavesTheFirstReason()
    {
        // The provider read the repair and refused it for its content, which asking again does not
        // change. That is the invalid outcome, and the first answer's reason is what went wrong.
        var provider = new FakeLlm
        {
            Reply = WithStep("Restarted it.", frames: ["f-invented"], transcript: ["t1"]),
            FailureOnRetry = new ProviderError(ProviderErrorKind.Invalid, "The model refused the request.", "Draft this note by hand."),
        };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Invalid, result.Status);
        Assert.Contains("f-invented", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.RetryAfter);
        Assert.Equal(0.04m, ledger.Recorded);
    }

    [Fact]
    public async Task ARepairToTheFallbackThatHadAnOutageIsAnOutage()
    {
        // The primary was down, the fallback's first answer broke a rule, and the fallback did not answer
        // the repair. Still an outage, and the fallback is named as the provider that was asked.
        var primary = new FakeLlm { Failure = new ProviderError(ProviderErrorKind.Unavailable, "Down.", "Try again.") };
        var fallback = new FakeLlm
        {
            Name = "fallback",
            Reply = WithStep("Restarted it.", frames: ["f-invented"], transcript: ["t1"]),
            FailureOnRetry = new ProviderError(ProviderErrorKind.Unavailable, "No answer.", "Try again."),
        };
        var ledger = new FakeLedger();
        var service = Service(primary, fallback, ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.True(result.UsedFallback);
        Assert.Equal("fallback", result.Provider);
        Assert.Equal(0.04m, ledger.Recorded);
    }

    [Theory]
    [InlineData(true, 0.14)] // The fallback answered: its own cost on top of the estimate.
    [InlineData(false, 0.10)] // The fallback could not be reached either: the primary's estimate stands.
    public async Task APrimaryThatMayHaveBeenBilledIsChargedWhateverTheFallbackDoes(bool fallbackAnswers, double expected)
    {
        // Falling over used to replace the primary's failure with the fallback's, and with it the only
        // record that the first call may have been paid for.
        var primary = new FakeLlm
        {
            Failure = new ProviderError(ProviderErrorKind.Unavailable, "Too slow.", "Try again.")
            {
                MayHaveBeenBilled = true,
            },
        };
        var fallback = fallbackAnswers
            ? new FakeLlm { Reply = Good(), Name = "fallback" }
            : new FakeLlm { Failure = new ProviderError(ProviderErrorKind.Unavailable, "No answer.", "Try again.") };
        var ledger = new FakeLedger();
        var service = Service(primary, fallback, ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(fallbackAnswers, result.Ok);
        Assert.Equal(1, fallback.Calls);
        Assert.Equal((decimal)expected, ledger.Recorded);
    }

    [Fact]
    public async Task APrimaryThatMayHaveBeenBilledIsStillChargedWhenTheFallbackThrows()
    {
        // The primary's possible charge must already be counted when the fallback is asked. A fallback
        // that throws rather than failing skips everything after the call that throws, and a charge held
        // only in its answer was settled at nothing.
        var primary = new FakeLlm
        {
            Failure = new ProviderError(ProviderErrorKind.Unavailable, "Too slow.", "Try again.")
            {
                MayHaveBeenBilled = true,
            },
        };
        var fallback = new FakeLlm { Throws = new InvalidOperationException("misconfigured"), Name = "fallback" };
        var ledger = new FakeLedger();
        var service = Service(primary, fallback, ledger);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken));

        Assert.Equal(1, fallback.Calls);
        Assert.Equal(new SummarizationOptions().MaxSessionCostUsd, ledger.Recorded);
        Assert.Equal(1, ledger.Settles);
    }

    [Fact]
    public async Task TwoCallsThatMayBothHaveBeenBilledAreChargedTwice()
    {
        var slow = new ProviderError(ProviderErrorKind.Unavailable, "Too slow.", "Try again.") { MayHaveBeenBilled = true };
        var ledger = new FakeLedger();
        var service = Service(new FakeLlm { Failure = slow }, new FakeLlm { Failure = slow }, ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Equal(2 * new SummarizationOptions().MaxSessionCostUsd, ledger.Recorded);
        Assert.Equal(1, ledger.Settles);
    }

    [Theory]
    [InlineData("steps", "null")]
    [InlineData("steps", "[null]")]
    [InlineData("follow_ups", "null")]
    [InlineData("follow_ups", "[null]")]
    [InlineData("problem", "null")]
    [InlineData("result", "null")]
    [InlineData("suggested_title", "null")]
    [InlineData("kb_reason", "null")]
    [InlineData("source", "null")]
    [InlineData("prompt_version", "null")]
    [InlineData("source", "\"elsewhere\"")]
    [InlineData("source", "\"Cloud\"")]
    [InlineData("source", "\"LOCAL\"")]
    [InlineData("steps", """[{"text": "Restarted it.", "confidence": "high", "frame_refs": null, "transcript_refs": ["t1"]}]""")]
    [InlineData("steps", """[{"text": "Restarted it.", "confidence": "high", "frame_refs": [null], "transcript_refs": ["t1"]}]""")]
    [InlineData("steps", """[{"text": "Restarted it.", "confidence": "high", "frame_refs": ["f1"], "transcript_refs": null}]""")]
    [InlineData("steps", """[{"text": "Restarted it.", "confidence": "high", "frame_refs": ["f1"], "transcript_refs": [null]}]""")]
    [InlineData("steps", """[{"text": null, "confidence": "high", "frame_refs": ["f1"], "transcript_refs": ["t1"]}]""")]
    [InlineData("steps", """[{"text": "Restarted it.", "confidence": null, "frame_refs": ["f1"], "transcript_refs": ["t1"]}]""")]
    [InlineData("steps", """[{"text": "Restarted it.", "confidence": "medium", "frame_refs": ["f1"], "transcript_refs": ["t1"]}]""")]
    [InlineData("steps", """[{"text": "Restarted it.", "confidence": "HIGH", "frame_refs": ["f1"], "transcript_refs": ["t1"]}]""")]
    [InlineData("steps", """[{"text": "Restarted it.", "confidence": "Low", "frame_refs": ["f1"], "transcript_refs": ["t1"]}]""")]
    public async Task ANullCollectionFromTheModelIsABrokenDraftNotACrash(string field, string value)
    {
        // 2026-10-12 review (T-205). A model can write null where the schema says a list, and the
        // validator walked the list: a NullReferenceException after the call was paid for, a 500, and no
        // repair. The same for a value the client cannot store, which made a 200 the client could not
        // read and so retried, billed, eight times. Each is a draft that broke the rules, told to the
        // model once like any other.
        var broken = Replace(Good(), field, value);
        var provider = new FakeLlm { Reply = broken, ReplyOnRetry = broken };
        var ledger = new FakeLedger();
        var service = Service(provider, ledger: ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Invalid, result.Status);
        Assert.Contains(field, result.Reason, StringComparison.Ordinal);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(0.08m, ledger.Recorded);
    }

    [Fact]
    public async Task ABrokenFirstAnswerThatIsRepairedIsADraft()
    {
        var provider = new FakeLlm { Reply = Replace(Good(), "steps", "null"), ReplyOnRetry = Good() };
        var service = Service(provider);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Reason);
        Assert.True(result.Repaired);
    }

    [Fact]
    public async Task ABilledAnswerWithNoTextIsChargedWhatItCost()
    {
        // 2026-10-12 review (S-58). A 200 the model ran and wrote nothing useful in is still on the bill.
        using var handler = new HangingHandler(first: GeminiAnswer(string.Empty, promptTokens: 40_000));
        var (service, ledger) = Real(handler, deadlineMs: 60_000, httpTimeoutMs: 60_000);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Invalid, result.Status);
        Assert.Equal(0.03m, ledger.Recorded);
        Assert.Equal(1, ledger.Settles);
    }

    [Fact]
    public async Task AProviderThatNamesWhenToComeBackIsPassedOn()
    {
        // 2026-10-12 review (E-38). Google says when its quota reopens, and the 503 is the only way the
        // client's outbox can hear it.
        var provider = new FakeLlm
        {
            Failure = new ProviderError(ProviderErrorKind.Unavailable, "Rate limited.", "Later.", TimeSpan.FromMinutes(7)),
        };
        var service = Service(provider);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
        Assert.Equal(TimeSpan.FromMinutes(7), result.RetryAfter);
    }

    [Fact]
    public async Task AFailureThatIsNotAnOutageSaysNothingAboutWhenToComeBack()
    {
        var provider = new FakeLlm
        {
            Failure = new ProviderError(ProviderErrorKind.Invalid, "Refused.", "A bug.", TimeSpan.FromMinutes(7)),
        };
        var service = Service(provider);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.Invalid, result.Status);
        Assert.Null(result.RetryAfter);
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unauthenticated)]
    [InlineData(ProviderErrorKind.Forbidden)]
    [InlineData(ProviderErrorKind.NotFound)]
    public async Task AProviderThatRefusesThisDeploymentIsASettingToFixNotADraftThatFailed(ProviderErrorKind kind)
    {
        // Review round 1 of the 2026-10-12 fixes. A wrong or rotated key, an account that may not call the
        // model and a retired model are each one setting away from drafting the same session. They were
        // answered as invalid, which the client now fails for good, so every session finished while an
        // operator's key was wrong would have been lost. Not configured is what the client queues.
        var provider = new FakeLlm
        {
            Failure = new ProviderError(kind, "The drafting model rejected this deployment's API key.", "Check the Summarization__ApiKey setting on the server."),
        };
        var fallback = new FakeLlm { Reply = Good() };
        var ledger = new FakeLedger();
        var service = Service(provider, fallback, ledger: ledger);

        var result = await service.DraftAsync(Tenant, Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(SummarizeStatus.NotConfigured, result.Status);
        Assert.Contains("Summarization__ApiKey", result.Reason, StringComparison.Ordinal);

        // Still not a reason to spend money at the other provider, and the request cost nothing.
        Assert.Equal(0, fallback.Calls);
        Assert.Equal(0m, ledger.Recorded);
    }

    /// <summary>The draft with one top-level field's value swapped for raw JSON.</summary>
    private static string Replace(string draft, string field, string rawValue)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(draft)!.AsObject();
        node[field] = System.Text.Json.Nodes.JsonNode.Parse(rawValue);
        return node.ToJsonString();
    }

    /// <summary>
    /// The production shape: a real <see cref="GeminiProvider"/> over an HttpClient with its own timeout,
    /// inside a service with its own deadline. -1 is no HttpClient timeout at all.
    /// </summary>
    private static (SummarizationService Service, FakeLedger Ledger) Real(HttpMessageHandler handler, int deadlineMs, int httpTimeoutMs)
    {
        var options = new SummarizationOptions { ApiKey = "test-key", Timeout = TimeSpan.FromMilliseconds(deadlineMs) };
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/"),
            Timeout = httpTimeoutMs < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(httpTimeoutMs),
        };
        var ledger = new FakeLedger();
        return (new SummarizationService(new GeminiProvider(http, options, "the note prompt"), null, ledger, options), ledger);
    }

    private static HttpResponseMessage GeminiAnswer(string text, int promptTokens) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""
            {
              "candidates": [ { "content": { "parts": [ { "text": {{System.Text.Json.JsonSerializer.Serialize(text)}} } ] } } ],
              "usageMetadata": { "promptTokenCount": {{promptTokens}}, "candidatesTokenCount": 0 }
            }
            """),
    };

    /// <summary>
    /// Takes the request the way a transport does, by writing it out, and then never answers. With
    /// <c>readsTheRequest</c> false it hangs before taking anything, which is a connection that never
    /// opened. Answers the first request with <c>first</c> when one is given.
    /// </summary>
    private sealed class HangingHandler(HttpResponseMessage? first = null, bool readsTheRequest = true) : HttpMessageHandler
    {
        private HttpResponseMessage? _first = first;

        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (readsTheRequest)
            {
                _ = await request.Content!.ReadAsByteArrayAsync(ct);
            }

            if (Interlocked.Exchange(ref _first, null) is { } answer)
            {
                return answer;
            }

            _ = Sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Exchange(ref _first, null)?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static SummarizeBundle Bundle() => new()
    {
        SessionId = "s1",
        DurationMs = 20 * 60 * 1000,
        Frames = [new BundleFrame("f1", 1_000, "Services Print Spooler Stopped")],
        Transcript = [new BundleSegment("t1", 1_200, "clearing the queue now")],
    };

    private static string Good() => WithStep("Found the Print Spooler service stopped.", ["f1"], ["t1"]);

    private static string WithStep(string text, string[] frames, string[] transcript) => $$"""
        {
          "problem": "Nothing would print from the reception workstation.",
          "steps": [{"text": {{System.Text.Json.JsonSerializer.Serialize(text)}}, "confidence": "high",
                     "frame_refs": {{System.Text.Json.JsonSerializer.Serialize(frames)}},
                     "transcript_refs": {{System.Text.Json.JsonSerializer.Serialize(transcript)}}}],
          "result": "Printing works again.",
          "follow_ups": [],
          "suggested_title": "Printer offline",
          "suggested_time_minutes": 10,
          "kb_candidate": true,
          "kb_reason": "A common fix worth writing down.",
          "source": "cloud",
          "prompt_version": "note_v1"
        }
        """;

    private sealed class FakeLlm : ILlmProvider
    {
        public string Name { get; set; } = "fake";

        public string? Reply { get; set; }

        public string? ReplyOnRetry { get; set; }

        public ProviderError? Failure { get; set; }

        /// <summary>How the repair call fails, when it is asked for one.</summary>
        public ProviderError? FailureOnRetry { get; set; }

        /// <summary>A provider that fails by throwing, which is what a bug in one looks like.</summary>
        public Exception? Throws { get; set; }

        public int Calls { get; private set; }

        public Task<ProviderResult<LlmDraft>> DraftAsync(SummarizeBundle bundle, string? repair, CancellationToken ct = default)
        {
            Calls++;
            if (Throws is not null)
            {
                throw Throws;
            }

            if (Failure is { } error)
            {
                return Task.FromResult(ProviderResult.Failure<LlmDraft>(error));
            }

            if (repair is not null && FailureOnRetry is { } retryError)
            {
                return Task.FromResult(ProviderResult.Failure<LlmDraft>(retryError));
            }

            var json = repair is not null && ReplyOnRetry is not null ? ReplyOnRetry : Reply!;
            return Task.FromResult(ProviderResult.Success(new LlmDraft(json, 0.04m)));
        }
    }

    private sealed class FakeLedger : ICostLedger
    {
        public decimal SpentToday { get; set; }

        public decimal SpentByOthers { get; set; }

        public decimal Recorded { get; private set; }

        /// <summary>Lets a test make the world change at the moment the row is written.</summary>
        public Action? OnRecord { get; set; }

        /// <summary>
        /// The cap this fake enforces when a reservation is asked for. The deployment's own default, so
        /// a test that sets SpentToday against it reads the way it did when the service held the cap.
        /// </summary>
        public decimal Cap { get; set; } = new SummarizationOptions().DailyCostCapUsd;

        public Task<decimal> SpentTodayAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(tenantId == Tenant ? SpentToday : SpentByOthers);

        public Task<Guid?> ReserveAsync(Guid tenantId, string sessionId, string provider, decimal estimateUsd, CancellationToken ct = default)
        {
            var spent = tenantId == Tenant ? SpentToday : SpentByOthers;
            return Task.FromResult<Guid?>(spent >= Cap ? null : Guid.NewGuid());
        }

        /// <summary>How many reservations were settled, at any figure. One per reservation or it leaked.</summary>
        public int Settles { get; private set; }

        public Task SettleAsync(Guid reservationId, decimal costUsd, CancellationToken ct = default)
        {
            Settles++;
            return RecordAsync(Tenant, "settled", "fake", costUsd, ct);
        }

        public Task RecordAsync(Guid tenantId, string sessionId, string provider, decimal costUsd, CancellationToken ct = default)
        {
            OnRecord?.Invoke();

            // A real ledger writes to the database with the token it is handed. If that token is the
            // request's, a client that hung up takes the row with it.
            ct.ThrowIfCancellationRequested();
            Recorded += costUsd;
            return Task.CompletedTask;
        }
    }
}
