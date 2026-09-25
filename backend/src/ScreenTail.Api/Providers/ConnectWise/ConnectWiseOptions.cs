namespace ScreenTail.Api.Providers.ConnectWise;

/// <summary>
/// The deployment's side of talking to ConnectWise Manage (ST-091). A tenant's credential lives in the
/// vault; what is here is ours: the vendor <c>clientId</c> ConnectWise issues per integration, and how
/// patient to be with a rate limit.
/// </summary>
public sealed class ConnectWiseOptions
{
    public const string Section = "ConnectWise";

    /// <summary>
    /// The clientId header ConnectWise requires on every request, issued once per integration vendor at
    /// developer.connectwise.com. Ours, not the tenant's; without it every call is refused before it is
    /// sent, with a message that says which setting is missing.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>First wait after a rate limit; doubles each attempt, with jitter up to itself. Tests set it to zero.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>ST-091 AC2: max 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>ST-092 AC3: 25 at a time.</summary>
    public int PageSize { get; set; } = 25;

    /// <summary>The recent-tickets list on focus (Spec §5 S3) is a glance: ten rows.</summary>
    public int RecentCount { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
