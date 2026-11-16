namespace ScreenTail.Api.Providers.Hudu;

/// <summary>The deployment's side of talking to Hudu (ST-095). The tenant's key is in the vault; this is patience and the cache's life.</summary>
public sealed class HuduOptions
{
    public const string Section = "Hudu";

    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The longest wait for a rate limit before trying again. A provider that asks for longer is not
    /// called again inside its window: the publish answers at once, and says when to come back.
    /// </summary>
    public TimeSpan MaxRetryWait { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Hudu pages at 25; more is refused.</summary>
    public int PageSize { get; set; } = 25;

    /// <summary>
    /// The most companies read from one tenant's Hudu (2026-10-12 review). An MSP with more is rare; a
    /// site that pages for ever is a site that is not Hudu, and on a shared backend it would be paged
    /// for every tenant's cost. Past the cap the list is an error, never a shorter list.
    /// </summary>
    public int MaxCompanies { get; set; } = 10_000;

    /// <summary>
    /// The most characters of company ids and names kept from one tenant's Hudu (2026-11-15 review), about
    /// 2 MB in memory. A real list is a few tens of characters a company: at <see cref="MaxCompanies"/>
    /// that is around 400,000, and this is an average of a hundred a company, so only a site that pads
    /// every name meets it. Past it the list is an error, never a shorter list, like the company cap.
    /// </summary>
    public long MaxCompanyListChars { get; set; } = 1_000_000;

    /// <summary>ST-095 AC1: companies are cached ten minutes. The mapping screen asks often and the list rarely changes.</summary>
    public TimeSpan CompanyCacheFor { get; set; } = TimeSpan.FromMinutes(10);
}
