namespace ScreenTail.Api.Providers.Hudu;

/// <summary>The deployment's side of talking to Hudu (ST-095). The tenant's key is in the vault; this is patience and the cache's life.</summary>
public sealed class HuduOptions
{
    public const string Section = "Hudu";

    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromMilliseconds(500);

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Hudu pages at 25; more is refused.</summary>
    public int PageSize { get; set; } = 25;

    /// <summary>ST-095 AC1: companies are cached ten minutes. The mapping screen asks often and the list rarely changes.</summary>
    public TimeSpan CompanyCacheFor { get; set; } = TimeSpan.FromMinutes(10);
}
