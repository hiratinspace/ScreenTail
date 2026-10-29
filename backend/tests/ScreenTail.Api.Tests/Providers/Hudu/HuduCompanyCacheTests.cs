using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.Hudu;

namespace ScreenTail.Api.Tests.Providers.Hudu;

/// <summary>
/// One list load per tenant at a time (#229 review). When a tenant's entry expires or is forgotten,
/// every publish arriving at once used to page the whole list itself: at 10,000 companies and Hudu's
/// 25 a page, up to 400 requests each, all against the customer's Hudu. Each load here is a gate the
/// test opens by hand, so nothing depends on how long anything takes.
/// </summary>
public sealed class HuduCompanyCacheTests
{
    private const string Site = "https://acme.huducloud.com/";
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);
    private static readonly CompanyRef[] Old = [new("7", "Acme Dental")];
    private static readonly CompanyRef[] New = [new("7", "Acme Dental"), new("8", "Birch Legal")];

    private readonly AdjustableTime _time = new(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));
    private readonly GatedLoad _load = new();

    [Fact]
    public async Task CallersArrivingTogetherShareOneLoad()
    {
        var cache = new HuduCompanyCache(_time);

        var callers = Enumerable.Range(0, 10).Select(_ => Get(cache)).ToArray();
        await _load.Started(1);
        Assert.Equal(1, _load.Calls);

        _load.Answer(0, New);
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, _load.Calls);
        Assert.All(results, r => Assert.Same(New, r.Value));
        Assert.Same(New, (await Get(cache)).Value);
        Assert.Equal(1, _load.Calls);
    }

    [Fact]
    public async Task TwoTenantsLoadSideBySideAndNeitherWaitsForTheOther()
    {
        var cache = new HuduCompanyCache(_time);

        var first = Get(cache, "t-1");
        await _load.Started(1);
        var second = Get(cache, "t-2");
        await _load.Started(2);

        // The second tenant's list is answered while the first is still out, and arrives without it.
        _load.Answer(1, New);
        Assert.Same(New, (await second).Value);
        Assert.False(first.IsCompleted);

        _load.Answer(0, Old);
        Assert.Same(Old, (await first).Value);
        Assert.Equal(2, _load.Calls);
    }

    [Fact]
    public async Task ALoadThatThrowsReachesEveryCallerAndTheNextCallLoadsAgain()
    {
        var cache = new HuduCompanyCache(_time);

        var callers = Enumerable.Range(0, 5).Select(_ => Get(cache)).ToArray();
        await _load.Started(1);
        _load.Throw(0, new InvalidOperationException("The load broke."));

        foreach (var caller in callers)
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => caller);
            Assert.Equal("The load broke.", thrown.Message);
        }

        Assert.Equal(1, _load.Calls);
        var next = Get(cache);
        await _load.Started(2);
        _load.Answer(1, New);
        Assert.Same(New, (await next).Value);
    }

    [Fact]
    public async Task AFailedAnswerReachesEveryCallerAndIsNotKept()
    {
        // Hudu's own refusal (a revoked key, a rate limit) is what every caller hears, in Hudu's words,
        // and the next call asks again rather than being told the same thing from memory.
        var cache = new HuduCompanyCache(_time);
        var refused = new ProviderError(ProviderErrorKind.Unauthenticated, "Hudu rejected the API key.", "Update it in Settings → Integrations.");

        var callers = Enumerable.Range(0, 5).Select(_ => Get(cache)).ToArray();
        await _load.Started(1);
        _load.Answer(0, ProviderResult.Failure<IReadOnlyList<CompanyRef>>(refused));

        var results = await Task.WhenAll(callers);
        Assert.All(results, r => Assert.Same(refused, r.Error));
        Assert.Equal(1, _load.Calls);

        var next = Get(cache);
        await _load.Started(2);
        _load.Answer(1, New);
        Assert.Same(New, (await next).Value);
    }

    [Fact]
    public async Task OneCallerGivingUpLeavesTheLoadAndTheOthersAlone()
    {
        // The caller who started the load gives up: the load is everyone's, so it goes on for the rest.
        var cache = new HuduCompanyCache(_time);
        using var impatient = new CancellationTokenSource();

        var starter = Get(cache, ct: impatient.Token);
        await _load.Started(1);
        var patient = Get(cache);

        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter);
        Assert.False(_load.TokenOf(0).IsCancellationRequested);
        Assert.False(patient.IsCompleted);

        _load.Answer(0, New);
        Assert.Same(New, (await patient).Value);
        Assert.Equal(1, _load.Calls);
        Assert.Same(New, (await Get(cache)).Value);
        Assert.Equal(1, _load.Calls);
    }

    [Fact]
    public async Task WhenEveryCallerHasGivenUpTheLoadIsStoppedAndTheNextCallLoadsAgain()
    {
        // Nobody is left to read the list, so the rest of the pages are not asked for.
        var cache = new HuduCompanyCache(_time);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        var a = Get(cache, ct: first.Token);
        await _load.Started(1);
        var b = Get(cache, ct: second.Token);
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.False(_load.TokenOf(0).IsCancellationRequested);

        await second.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        Assert.True(_load.TokenOf(0).IsCancellationRequested);

        var next = Get(cache);
        await _load.Started(2);
        _load.Answer(1, New);
        Assert.Same(New, (await next).Value);

        // The stopped load finishing late is not kept over the one that completed.
        _load.Answer(0, Old);
        Assert.Same(New, (await Get(cache)).Value);
        Assert.Equal(2, _load.Calls);
    }

    [Fact]
    public async Task AForgetDuringALoadMeansTheNextCallLoadsAgain()
    {
        // A company-gone 422 forgets the tenant while a list is being read: that list was asked for
        // before Hudu said it was out of date, so it is handed to whoever asked and not kept as fresh.
        var cache = new HuduCompanyCache(_time);

        var before = Get(cache);
        await _load.Started(1);
        cache.Forget("t-1");
        _load.Answer(0, Old);
        Assert.Same(Old, (await before).Value);

        var after = Get(cache);
        await _load.Started(2);
        _load.Answer(1, New);
        Assert.Same(New, (await after).Value);
        Assert.Same(New, (await Get(cache)).Value);
        Assert.Equal(2, _load.Calls);
    }

    [Fact]
    public async Task ACallAfterAForgetDoesNotJoinTheLoadFromBeforeIt()
    {
        var cache = new HuduCompanyCache(_time);

        var before = Get(cache);
        await _load.Started(1);
        cache.Forget("t-1");
        var after = Get(cache);
        await _load.Started(2);

        // The new list lands first and the old one after it: the old one does not replace it.
        _load.Answer(1, New);
        Assert.Same(New, (await after).Value);
        _load.Answer(0, Old);
        Assert.Same(Old, (await before).Value);

        Assert.Same(New, (await Get(cache)).Value);
        Assert.Equal(2, _load.Calls);
    }

    [Fact]
    public async Task ACallerForAnotherSiteDoesNotJoinTheLoad()
    {
        // S-71: a list from the old site is not the new site's, in flight any more than in memory.
        var cache = new HuduCompanyCache(_time);

        var oldSite = Get(cache);
        await _load.Started(1);
        var newSite = Get(cache, site: "https://other.huducloud.com/");
        await _load.Started(2);

        _load.Answer(1, New);
        Assert.Same(New, (await newSite).Value);
        _load.Answer(0, Old);
        Assert.Same(Old, (await oldSite).Value);

        Assert.Same(New, (await Get(cache, site: "https://other.huducloud.com/")).Value);
        Assert.Equal(2, _load.Calls);
    }

    [Fact]
    public async Task AnExpiredListIsLoadedAgainOnceForEveryoneWaiting()
    {
        var cache = new HuduCompanyCache(_time);
        var first = Get(cache);
        await _load.Started(1);
        _load.Answer(0, Old);
        _ = await first;

        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Same(Old, (await Get(cache)).Value);
        Assert.Equal(1, _load.Calls);

        _time.Advance(TimeSpan.FromMinutes(2));
        var callers = Enumerable.Range(0, 5).Select(_ => Get(cache)).ToArray();
        await _load.Started(2);
        Assert.Equal(2, _load.Calls);
        _load.Answer(1, New);
        Assert.All(await Task.WhenAll(callers), r => Assert.Same(New, r.Value));
        Assert.Equal(2, _load.Calls);
    }

    private Task<ProviderResult<IReadOnlyList<CompanyRef>>> Get(HuduCompanyCache cache, string tenant = "t-1", string site = Site, CancellationToken? ct = null) =>
        cache.GetOrAddAsync(tenant, site, KeepFor, _load.Invoke, ct ?? TestContext.Current.CancellationToken);

    /// <summary>A clock the test moves.</summary>
    private sealed class AdjustableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// A list load that answers only when the test says so. Each call is numbered from zero in the
    /// order it arrived, and keeps the token it was given so the test can see whether it was stopped.
    /// </summary>
    private sealed class GatedLoad
    {
        private readonly Lock _gate = new();
        private readonly List<(TaskCompletionSource<ProviderResult<IReadOnlyList<CompanyRef>>> Answer, CancellationToken Token)> _calls = [];
        private readonly List<(int Count, TaskCompletionSource Reached)> _watchers = [];

        public int Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.Count;
                }
            }
        }

        public Task<ProviderResult<IReadOnlyList<CompanyRef>>> Invoke(CancellationToken ct)
        {
            var answer = new TaskCompletionSource<ProviderResult<IReadOnlyList<CompanyRef>>>(TaskCreationOptions.RunContinuationsAsynchronously);
            List<TaskCompletionSource> reached;
            lock (_gate)
            {
                _calls.Add((answer, ct));
                reached = [.. _watchers.Where(w => w.Count <= _calls.Count).Select(w => w.Reached)];
                _watchers.RemoveAll(w => w.Count <= _calls.Count);
            }

            foreach (var watcher in reached)
            {
                watcher.TrySetResult();
            }

            return answer.Task;
        }

        /// <summary>Completes once this many loads have begun.</summary>
        public Task Started(int count)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_calls.Count >= count)
                {
                    return Task.CompletedTask;
                }

                _watchers.Add((count, reached));
            }

            return reached.Task.WaitAsync(TestContext.Current.CancellationToken);
        }

        public CancellationToken TokenOf(int call) => Call(call).Token;

        public void Answer(int call, IReadOnlyList<CompanyRef> companies) => Answer(call, ProviderResult.Success(companies));

        public void Answer(int call, ProviderResult<IReadOnlyList<CompanyRef>> result) => Call(call).Answer.SetResult(result);

        public void Throw(int call, Exception error) => Call(call).Answer.SetException(error);

        private (TaskCompletionSource<ProviderResult<IReadOnlyList<CompanyRef>>> Answer, CancellationToken Token) Call(int call)
        {
            lock (_gate)
            {
                return _calls[call];
            }
        }
    }
}
