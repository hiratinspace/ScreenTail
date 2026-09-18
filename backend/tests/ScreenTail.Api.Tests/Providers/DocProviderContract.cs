using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// The rules every documentation provider has to follow (ST-090). Hudu is ST-095; IT Glue is later.
/// </summary>
public abstract class DocProviderContract
{
    protected abstract IDocProvider Provider { get; }

    protected abstract void Break(ProviderError? error);

    protected abstract string KnownCompanyId { get; }

    [Fact]
    public async Task CheckSucceedsWhenTheCredentialsWork()
    {
        Assert.True((await Provider.CheckAsync(TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task CompaniesComeBackWithNamesAPersonCanRecognise()
    {
        // The mapping in ST-097 is made by a human reading this list. Identifiers alone would make it a
        // guess, and a wrong guess publishes one customer's runbook into another's knowledge base.
        var result = await Provider.ListCompaniesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.All(result.Value!, company => Assert.False(string.IsNullOrWhiteSpace(company.Name)));
    }

    [Fact]
    public async Task AnArticleForAnUnknownCompanyIsRefused()
    {
        var article = new KbArticle("no-such-company", "Title", "Body", []);

        var result = await Provider.PublishArticleAsync(article, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task ArticlesAreDraftsByDefault()
    {
        // A knowledge-base article written by a model and published live is a support article nobody read
        // before a customer did.
        Assert.True(new KbArticle(KnownCompanyId, "Title", "Body", []).Draft);
    }

    [Fact]
    public async Task APublishedArticleComesBackWithAnIdentifier()
    {
        var article = new KbArticle(KnownCompanyId, "Printer offline", "Problem, steps, result.", []);

        var result = await Provider.PublishArticleAsync(article, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.Id));
    }

    [Fact]
    public async Task OnlyAnOutageIsWorthRetrying()
    {
        foreach (var kind in Enum.GetValues<ProviderErrorKind>())
        {
            Break(new ProviderError(kind, "Something happened.", "Do something."));
            var result = await Provider.CheckAsync(TestContext.Current.CancellationToken);

            Assert.Equal(kind == ProviderErrorKind.Unavailable, result.Error!.Retryable);
        }

        Break(null);
    }
}

/// <summary>The contract, run against the fake. ST-095 adds the same class over the real Hudu client.</summary>
public sealed class FakeDocsMeetTheContract : DocProviderContract
{
    private readonly ScreenTail.Api.Providers.Fake.FakeDocProvider _provider = new();

    protected override IDocProvider Provider => _provider;

    protected override string KnownCompanyId => "c-1";

    protected override void Break(ProviderError? error) => _provider.Fail = error;
}
