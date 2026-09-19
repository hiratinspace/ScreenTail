namespace ScreenTail.Api.Summarize;

/// <summary>
/// How this deployment drafts notes (ST-063).
///
/// <b>The key has no default and is never in the repository.</b> It is read from the environment, like
/// the signing key and the connection string, for the same reason: a default in source is a credential
/// in every clone, and a deployment that works without anyone setting one is a deployment nobody
/// realises is using somebody else's account.
///
/// Set it as <c>Summarization__ApiKey</c>, and the fallback as <c>Summarization__FallbackApiKey</c>.
/// With no key configured the service says so — a technician is told drafting is not set up, which is
/// true and actionable — rather than failing at the first session of the day.
/// </summary>
public sealed class SummarizationOptions
{
    public const string Section = "Summarization";

    /// <summary>
    /// Which provider drafts. Gemini Flash by default: it is the cheapest of the three at this shape of
    /// request, it reads images natively, and the abstraction means changing it costs one class.
    /// </summary>
    public string Provider { get; set; } = "gemini-flash";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Who drafts when the first one is down. Optional: without it a 5xx becomes a queued retry rather
    /// than a second bill, which is the right default for a pilot.
    /// </summary>
    public string? FallbackProvider { get; set; }

    public string? FallbackApiKey { get; set; }

    /// <summary>
    /// What one tenant may spend on drafting in a day, in US dollars.
    ///
    /// A cap rather than an alert. The scope document budgets under ten cents a session, so this is
    /// roughly a hundred sessions — far past a busy day for one MSP, and close enough to notice a bug
    /// that drafts the same session in a loop before it becomes a bill.
    /// </summary>
    public decimal DailyCostCapUsd { get; set; } = 10m;

    /// <summary>How long one draft may take before it is abandoned and queued (AC1 budgets 30 s p95).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Whether this deployment can draft at all.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);
}
