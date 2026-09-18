namespace ScreenTail.Api.Providers;

/// <param name="Id">The provider's own identifier for the company.</param>
public sealed record CompanyRef(string Id, string Name);

/// <param name="Draft">
/// True to publish as a draft. v1 always does: a knowledge-base article written by a model and published
/// live is a support article nobody read before a customer did.
/// </param>
public sealed record KbArticle(
    string CompanyId,
    string Title,
    string Body,
    IReadOnlyList<NoteAttachment> Attachments,
    bool Draft = true);

public sealed record PublishedArticle(string Id, Uri? Url);

/// <summary>
/// A documentation platform: Hudu in v1, IT Glue later.
///
/// Separate from <see cref="IPsaProvider"/> because the two are chosen independently — an MSP may run
/// ConnectWise with Hudu, or Autotask with IT Glue — and because the failure modes differ: a PSA refusing
/// a note stops the billing record, a doc platform refusing an article does not.
/// </summary>
public interface IDocProvider
{
    string Name { get; }

    Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Companies, for mapping a PSA company onto a documentation one (ST-097). Publishing an article to
    /// the wrong company is a customer's runbook in another customer's knowledge base.
    /// </summary>
    Task<ProviderResult<IReadOnlyList<CompanyRef>>> ListCompaniesAsync(CancellationToken ct = default);

    Task<ProviderResult<PublishedArticle>> PublishArticleAsync(KbArticle article, CancellationToken ct = default);
}
