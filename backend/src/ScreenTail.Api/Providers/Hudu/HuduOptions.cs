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

    /// <summary>ST-095 AC1: companies are cached ten minutes. The mapping screen asks often and the list rarely changes.</summary>
    public TimeSpan CompanyCacheFor { get; set; } = TimeSpan.FromMinutes(10);
}
