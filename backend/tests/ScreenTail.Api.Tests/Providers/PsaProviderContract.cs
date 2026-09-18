using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// The rules every PSA provider has to follow, written once (ST-090).
///
/// A real connector is a lot of HTTP and a little behaviour, and it is the behaviour that decides whether
/// a technician can trust Publish. Writing these once and running them against each implementation means
/// ConnectWise (ST-091) and HaloPSA (ST-121) cannot disagree about what a missing ticket does, or about
/// whether a wrong API key is worth retrying.
///
/// Derive, supply a provider, and the contract runs. A connector that cannot pass these is not finished,
/// whatever its own tests say.
/// </summary>
public abstract class PsaProviderContract
{
    /// <summary>A working provider, and a way to make it fail.</summary>
    protected abstract IPsaProvider Provider { get; }

    /// <summary>Makes the next call fail this way, or null to stop failing.</summary>
    protected abstract void Break(ProviderError? error);

    /// <summary>A ticket that exists in this provider, for the publish tests.</summary>
    protected abstract string KnownTicketId { get; }

    [Fact]
    public async Task CheckSucceedsWhenTheCredentialsWork()
    {
        var result = await Provider.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task AFailureIsAValueAndNotAnException()
    {
        // The whole shape of the interface. A PSA being down is an ordinary outcome of pressing Publish,
        // and a caller that has to catch it will eventually catch it in the wrong place.
        Break(new ProviderError(ProviderErrorKind.Unavailable, "The PSA did not answer.", "Try again in a minute."));

        var result = await Provider.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Break(null);
    }

    [Fact]
    public async Task OnlyAnOutageIsWorthRetrying()
    {
        // Retrying a wrong API key is how an integration becomes an account lockout, and retrying an
        // invalid payload is the same rejection several hundred times.
        foreach (var kind in Enum.GetValues<ProviderErrorKind>())
        {
            Break(new ProviderError(kind, "Something happened.", "Do something."));
            var result = await Provider.CheckAsync(TestContext.Current.CancellationToken);

            Assert.False(result.Ok);
            Assert.Equal(kind == ProviderErrorKind.Unavailable, result.Error!.Retryable);
        }

        Break(null);
    }

    [Fact]
    public async Task EveryFailureSaysWhatHappenedAndWhatToDo()
    {
        // Spec §4's pattern, enforced rather than reviewed. A message that stops at "what happened" is a
        // support call; the second half is the whole value of the first.
        Break(new ProviderError(ProviderErrorKind.Unauthenticated, "The PSA rejected the key.", "Update it in Settings."));

        var result = await Provider.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(result.Error!.What));
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Todo));
        Assert.EndsWith(".", result.Error.What, StringComparison.Ordinal);
        Assert.EndsWith(".", result.Error.Todo, StringComparison.Ordinal);
        Break(null);
    }

    [Fact]
    public async Task ASearchTooShortToMeanAnythingIsRefused()
    {
        var result = await Provider.SearchTicketsAsync("ab", TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
    }

    [Fact]
    public async Task ASearchThatMatchesNothingIsAnEmptyListAndNotAnError()
    {
        // "No such ticket" is an answer. Making it an error would put a red banner in front of a
        // technician who simply typed a number that is not theirs.
        var result = await Provider.SearchTicketsAsync("zzzznotathing", TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task ASearchResultNamesTheCompanyAsWellAsTheTicket()
    {
        // Spec §5 S3 shows company beside summary so a technician cannot publish to the right number at
        // the wrong client, which is the mistake that ends a customer relationship.
        var result = await Provider.SearchTicketsAsync(KnownTicketId, TestContext.Current.CancellationToken);

        var ticket = Assert.Single(result.Value!);
        Assert.False(string.IsNullOrWhiteSpace(ticket.Company));
        Assert.False(string.IsNullOrWhiteSpace(ticket.Summary));
    }

    [Fact]
    public async Task ANoteOnAMissingTicketIsNotFoundRatherThanInvalid()
    {
        // The two need different words in front of a technician: one means pick again, the other means
        // something is wrong with what we built.
        var note = new TicketNote("does-not-exist", "Body", Internal: true, []);

        var result = await Provider.AddNoteAsync(note, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task APublishedNoteComesBackWithAnIdentifier()
    {
        // Without one, a retry after a dropped connection cannot tell "already published" from "not
        // published", and the technician gets two copies or none.
        var note = new TicketNote(KnownTicketId, "Problem, steps, result.", Internal: true, []);

        var result = await Provider.AddNoteAsync(note, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.Id));
    }

    [Fact]
    public async Task ATimeEntryOfNoMinutesIsRefused()
    {
        var entry = new TimeEntry(KnownTicketId, DateTimeOffset.UtcNow, 0, "Notes", Billable: true);

        var result = await Provider.AddTimeEntryAsync(entry, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
    }
}

/// <summary>The contract, run against the fake. ST-091 adds the same class over the real ConnectWise client.</summary>
public sealed class FakePsaMeetsTheContract : PsaProviderContract
{
    private readonly ScreenTail.Api.Providers.Fake.FakePsaProvider _provider = new();

    protected override IPsaProvider Provider => _provider;

    protected override string KnownTicketId => "48213";

    protected override void Break(ProviderError? error) => _provider.Fail = error;
}
