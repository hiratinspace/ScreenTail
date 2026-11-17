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

    /// <summary>
    /// The longest wait for a rate limit before trying again. A provider that asks for longer is not
    /// called again inside its window: the publish answers at once, and says when to come back.
    /// </summary>
    public TimeSpan MaxRetryWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>ST-091 AC2: max 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>ST-092 AC3: 25 at a time.</summary>
    public int PageSize { get; set; } = 25;

    /// <summary>The recent-tickets list on focus (Spec §5 S3) is a glance: ten rows.</summary>
    public int RecentCount { get; set; } = 10;

    /// <summary>
    /// How many companies a company lookup reads at most (ST-097). An exact name is one or two; a name
    /// with quotes in it is asked for in pieces and can match more, and only the exact one is kept.
    /// </summary>
    public int CompanyMatches { get; set; } = 100;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// What is said while this deployment has no ConnectWise clientId (<see cref="ConnectWiseOptions.ClientId"/>).
/// A technician is told who can finish the setup, never the server setting: that is for the owner, on
/// the web admin's card (<see cref="Vault.VaultRefusal.ForOwner"/>), and for the server log at start.
/// </summary>
public static partial class ConnectWiseSetup
{
    public const string NotSetUp = "ConnectWise is not fully set up yet.";

    /// <summary>For a technician's device: who finishes it, with no configuration key in it.</summary>
    public const string FinishIt = "Ask your ScreenTail owner to finish the ConnectWise setup.";

    /// <summary>For the owner: the setting that is missing, and who sets it.</summary>
    public const string SetTheClientId = "Set ConnectWise:ClientId on the ScreenTail server, or ask whoever runs it to.";

    /// <summary>Says once, at start, that every ConnectWise call will be refused until the clientId is set.</summary>
    public static void Announce(ILogger logger) => MissingClientId(logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ConnectWise:ClientId is not set, so every ConnectWise call is refused before it is sent until it is")]
    private static partial void MissingClientId(ILogger logger);
}
