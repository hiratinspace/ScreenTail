using ScreenTail.Api.Providers;
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

        public int Calls { get; private set; }

        public Task<ProviderResult<LlmDraft>> DraftAsync(SummarizeBundle bundle, string? repair, CancellationToken ct = default)
        {
            Calls++;
            if (Failure is { } error)
            {
                return Task.FromResult(ProviderResult.Failure<LlmDraft>(error));
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

        public Task<decimal> SpentTodayAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(tenantId == Tenant ? SpentToday : SpentByOthers);

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
