using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.Hudu;

namespace ScreenTail.Api.Tests.Publish;

/// <summary>
/// The real Hudu provider, as the service builds it, with one thing added: it keeps the token the
/// publish asked for the company list with. That is the publish deadline's own token, which the deadline
/// cancels on the thread that fires it, so a test on a <see cref="HeldClock"/> reads exactly when the
/// deadline came.
///
/// The stand-in Hudu cannot show it. Since #240 the company list is loaded once for every caller waiting,
/// on a token of the cache's own, which is cancelled only after the last caller has gone, and then on
/// another thread.
/// </summary>
internal sealed class WatchedDocs
{
    /// <summary>The token of the latest company-list call, or none before the first.</summary>
    public CancellationToken ListedWith { get; private set; }

    /// <summary>Puts the watched factory in place of the one Program.cs registers.</summary>
    public void Install(IServiceCollection services)
    {
        services.TryAddScoped<HuduProviderFactory>();
        _ = services.Replace(ServiceDescriptor.Scoped<IDocProviderFactory>(provider => new Factory(provider.GetRequiredService<HuduProviderFactory>(), this)));
    }

    private sealed class Factory(IDocProviderFactory inner, WatchedDocs watch) : IDocProviderFactory
    {
        public async Task<IDocProvider?> ForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
            await inner.ForTenantAsync(tenantId, ct).ConfigureAwait(false) is { } docs ? new Watched(docs, watch) : null;
    }

    private sealed class Watched(IDocProvider docs, WatchedDocs watch) : IDocProvider
    {
        public string Name => docs.Name;

        public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) => docs.CheckAsync(ct);

        public Task<ProviderResult<IReadOnlyList<CompanyRef>>> ListCompaniesAsync(CancellationToken ct = default)
        {
            watch.ListedWith = ct;
            return docs.ListCompaniesAsync(ct);
        }

        public Task<ProviderResult<PublishedArticle>> PublishArticleAsync(KbArticle article, CancellationToken ct = default) => docs.PublishArticleAsync(article, ct);
    }
}
