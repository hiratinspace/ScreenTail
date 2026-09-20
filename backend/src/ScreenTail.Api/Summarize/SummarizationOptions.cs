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
    /// Which model, by the name the provider publishes it under.
    ///
    /// <b>A setting rather than a constant, because model names expire.</b> The first build of this
    /// asked for <c>gemini-2.0-flash</c>; by the time a key was pointed at it Google had retired that
    /// name, and every draft in every deployment was a 404 that only a code change and a release could
    /// fix. Retirements are announced months ahead, so this is the kind of outage an operator should be
    /// able to end in one line of configuration.
    ///
    /// Pinned rather than an alias such as <c>gemini-flash-latest</c>. An alias moves under a running
    /// deployment, and this one number decides what a draft costs, how long it takes and what shape it
    /// comes back in — all three of which are tested against a specific model.
    /// </summary>
    public string Model { get; set; } = "gemini-3.6-flash";

    /// <summary>
    /// How much detail the model is given in each picture, in the provider's own words.
    ///
    /// Low, and measured rather than assumed. On 2026-09-19, against the heaviest bundle a client may
    /// send (25 frames), gemini-3.6-flash took <b>39.9 s and $0.055</b> at its default resolution and
    /// <b>10.5 s and $0.015</b> at low. The default misses ST-063's thirty-second budget outright; low
    /// makes it with room for a slow network on top.
    ///
    /// It costs us little, because the model is not reading these pictures for their text. Every frame
    /// arrives with the redaction worker's own OCR beside it, already masked, and that is what the note
    /// is built from; the picture is there for layout and context. Sending it small also means the model
    /// sees less of whatever OCR missed, which is the right direction for INV-1.
    ///
    /// Blank leaves the choice to the provider, so a renamed or withdrawn setting does not need a
    /// release to stop being sent.
    /// </summary>
    public string MediaResolution { get; set; } = "MEDIA_RESOLUTION_LOW";

    /// <summary>
    /// What the provider charges for input, in US dollars per million tokens.
    ///
    /// Configuration rather than a constant, for the same reason as <see cref="Model"/>: the number in
    /// source was right for a model that no longer exists and wrong for the one that replaced it by a
    /// factor of ten. A cost cap reading a stale rate is not a cap.
    ///
    /// The defaults are Gemini Flash 3.x standard rates read from Google's pricing page on 2026-09-19.
    /// <b>They are scheduled to double on 2027-01-01</b>, so an operator who cares about the exact
    /// figure sets them here rather than waiting for a release.
    /// </summary>
    public decimal InputCostPerMillionUsd { get; set; } = 0.75m;

    /// <summary>
    /// What output costs, per million tokens. Thinking is billed at this rate too and is not included in
    /// the answer's own token count, so both are counted against it.
    /// </summary>
    public decimal OutputCostPerMillionUsd { get; set; } = 3.75m;

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
