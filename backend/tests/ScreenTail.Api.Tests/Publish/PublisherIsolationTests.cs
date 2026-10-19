using Microsoft.Extensions.Logging;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Publish;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Publish;

/// <summary>
/// One destination's surprise is that destination's answer (2026-10-12 review). A provider is meant to
/// return its failures as values, and the HTTP layer now does; this is the backstop for the bug that
/// still throws. Without it, a note that landed and a time entry that threw became one 500, and the
/// client was told nothing had gone when the note had.
/// </summary>
public sealed class PublisherIsolationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task ADestinationThatThrowsIsAnsweredOnItsOwnAndTheOthersStand()
    {
        var psa = new Scripted { ThrowOnTimeEntry = new InvalidOperationException("A shape nobody expected.") };

        var response = await Publisher.PublishAsync(psa, Bundle(["ticket_note", "time_entry"]), ct: TestContext.Current.CancellationToken);

        var note = response.Results.Single(r => r.Destination == "ticket_note");
        var time = response.Results.Single(r => r.Destination == "time_entry");
        Assert.True(note.Ok);
        Assert.Equal("90001", note.Id);
        AssertMayHaveLanded(time);
    }

    [Fact]
    public async Task AThrowingNoteDoesNotStopTheTimeEntry()
    {
        var psa = new Scripted { ThrowOnNote = new KeyNotFoundException("id") };

        var response = await Publisher.PublishAsync(psa, Bundle(["ticket_note", "time_entry"]), ct: TestContext.Current.CancellationToken);

        AssertMayHaveLanded(response.Results.Single(r => r.Destination == "ticket_note"));
        Assert.True(response.Results.Single(r => r.Destination == "time_entry").Ok);
    }

    [Fact]
    public async Task AThrowingDocumentationPlatformDoesNotUndoTheNote()
    {
        var psa = new Scripted();
        var docs = new ThrowingDocs { ThrowOnArticle = true };

        var response = await api.UseAsync(db => Publisher.PublishAsync(
            psa,
            Bundle(["ticket_note", "kb_article"]) with { Company = "Acme Dental" },
            docs,
            new CompanyMappings(db, Guid.NewGuid(), TimeProvider.System),
            ct: TestContext.Current.CancellationToken));

        Assert.True(response.Results.Single(r => r.Destination == "ticket_note").Ok);
        AssertMayHaveLanded(response.Results.Single(r => r.Destination == "kb_article"));
    }

    [Fact]
    public async Task ACompanyLookupThatThrowsWroteNothingAndMayGoAgain()
    {
        // The throw came before the article was asked for, so nothing can have been saved: unlike the
        // throw above, this one is safe to publish again, and the note still stands.
        var psa = new Scripted();
        var docs = new ThrowingDocs();

        var response = await api.UseAsync(db => Publisher.PublishAsync(
            psa,
            Bundle(["ticket_note", "kb_article"]) with { Company = "Acme Dental" },
            docs,
            new CompanyMappings(db, Guid.NewGuid(), TimeProvider.System),
            ct: TestContext.Current.CancellationToken));

        Assert.True(response.Results.Single(r => r.Destination == "ticket_note").Ok);
        var kb = response.Results.Single(r => r.Destination == "kb_article");
        Assert.False(kb.Ok);
        Assert.Equal("unavailable", kb.Kind);
        Assert.True(kb.Retryable);
        Assert.Contains("nothing was written", kb.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrowIsLoggedByItsTypeAndNothingItCarried()
    {
        var psa = new Scripted { ThrowOnNote = new InvalidOperationException("secret-body-fragment") };
        var log = new Lines();

        _ = await Publisher.PublishAsync(psa, Bundle(["ticket_note"]), log: log, ct: TestContext.Current.CancellationToken);

        var line = Assert.Single(log.Written);
        Assert.Contains("ticket_note", line, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.Contains("unknown", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-body-fragment", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExceptionsWordsAreNeverTheTechniciansMessage()
    {
        // An exception's message can quote what it failed on, which may be the provider's body or the
        // note itself (INV-10 for logs, and the same care for the screen).
        var psa = new Scripted { ThrowOnNote = new InvalidOperationException("secret-body-fragment") };

        var response = await Publisher.PublishAsync(psa, Bundle(["ticket_note"]), ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret-body-fragment", Assert.Single(response.Results).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerWhoHungUpIsNotTurnedIntoAnAnswer()
    {
        using var hungUp = new CancellationTokenSource();
        await hungUp.CancelAsync();
        var psa = new Scripted { ThrowOnNote = new OperationCanceledException(hungUp.Token) };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Publisher.PublishAsync(psa, Bundle(["ticket_note", "time_entry"]), ct: hungUp.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancellationNobodyAskedForIsAnsweredLikeAnyOtherThrow(bool asTimeout)
    {
        // The caller is still waiting, so this is a provider bug wearing a cancellation's clothes (an
        // HttpClient timeout that escaped is a TaskCanceledException). Letting it through would drop the
        // note already written from the answer, the same 500 the backstop is there to prevent.
        var psa = new Scripted { ThrowOnTimeEntry = asTimeout ? new TaskCanceledException() : new OperationCanceledException() };

        var response = await Publisher.PublishAsync(psa, Bundle(["ticket_note", "time_entry"]), ct: TestContext.Current.CancellationToken);

        Assert.True(response.Results.Single(r => r.Destination == "ticket_note").Ok);
        AssertMayHaveLanded(response.Results.Single(r => r.Destination == "time_entry"));
    }

    private static void AssertMayHaveLanded(PublishResultJson row)
    {
        Assert.False(row.Ok);
        Assert.Equal("unknown", row.Kind);
        Assert.False(row.Retryable);
        Assert.False(string.IsNullOrWhiteSpace(row.Error));
        Assert.Matches("Check .+ before (you publish|publishing)", row.Error);
    }

    private static PublishBundle Bundle(IReadOnlyList<string> destinations) => new()
    {
        SessionId = "s-0001",
        TicketId = "48213",
        Minutes = 30,
        StartedAt = new DateTimeOffset(2026, 9, 24, 14, 2, 0, TimeSpan.Zero),
        Destinations = destinations,
        Note = new DraftJson
        {
            Problem = "Printer offline in reception.",
            Steps = [new DraftStepJson { Text = "Restarted the spooler.", Confidence = "high", FrameRefs = [] }],
            Result = "Printing works again.",
            FollowUps = [],
            SuggestedTitle = "Printer offline",
            SuggestedTimeMinutes = 23,
            KbCandidate = false,
            KbReason = "One-off fix",
            Source = "cloud",
            PromptVersion = "note_v1",
        },
    };

    private sealed class Scripted : IPsaProvider
    {
        public Exception? ThrowOnNote { get; init; }

        public Exception? ThrowOnTimeEntry { get; init; }

        public string Name => "scripted";

        public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) => Task.FromResult(ProviderResult.Success(true));

        public Task<ProviderResult<IReadOnlyList<TicketRef>>> SearchTicketsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(ProviderResult.Success<IReadOnlyList<TicketRef>>([]));

        public Task<ProviderResult<IReadOnlyList<TicketRef>>> RecentTicketsAsync(CancellationToken ct = default) =>
            Task.FromResult(ProviderResult.Success<IReadOnlyList<TicketRef>>([]));

        public Task<ProviderResult<PublishedNote>> AddNoteAsync(TicketNote note, CancellationToken ct = default) =>
            ThrowOnNote is { } ex ? throw ex : Task.FromResult(ProviderResult.Success(new PublishedNote("90001", null)));

        public Task<ProviderResult<PublishedNote>> AddTimeEntryAsync(TimeEntry entry, CancellationToken ct = default) =>
            ThrowOnTimeEntry is { } ex ? throw ex : Task.FromResult(ProviderResult.Success(new PublishedNote("70001", null)));
    }

    /// <summary>Throws while listing companies, or, with <see cref="ThrowOnArticle"/>, once the article is asked for.</summary>
    private sealed class ThrowingDocs : IDocProvider
    {
        public bool ThrowOnArticle { get; init; }

        public string Name => "throwing";

        public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) => Task.FromResult(ProviderResult.Success(true));

        public Task<ProviderResult<IReadOnlyList<CompanyRef>>> ListCompaniesAsync(CancellationToken ct = default) => ThrowOnArticle
            ? Task.FromResult(ProviderResult.Success<IReadOnlyList<CompanyRef>>([new CompanyRef("7", "Acme Dental")]))
            : throw new InvalidOperationException("The requested operation requires an element of type 'Array'.");

        public Task<ProviderResult<PublishedArticle>> PublishArticleAsync(KbArticle article, CancellationToken ct = default) =>
            throw new InvalidOperationException("The requested operation requires an element of type 'Object'.");
    }

    /// <summary>Each line written, formatted as the host would format it.</summary>
    private sealed class Lines : ILogger
    {
        public List<string> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Written.Add(formatter(state, exception));
    }
}
