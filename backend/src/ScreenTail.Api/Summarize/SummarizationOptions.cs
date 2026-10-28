using ScreenTail.Api.Providers.Llm;

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
///
/// <b>The provider decides every default that is about money or a model.</b> A setting the operator
/// leaves alone takes the configured provider's value; one they set is kept whichever provider runs.
/// </summary>
public sealed class SummarizationOptions
{
    public const string Section = "Summarization";

    /// <summary>The value of <see cref="Provider"/> that drafts with Gemini Flash. The default.</summary>
    public const string Gemini = "gemini-flash";

    /// <summary>The value of <see cref="Provider"/> that drafts with Claude, through Anthropic's API.</summary>
    public const string Claude = "claude";

    /// <summary>The model a Gemini deployment asks for when <see cref="Model"/> is not set.</summary>
    public const string DefaultGeminiModel = "gemini-3.6-flash";

    /// <summary>
    /// The model a Claude deployment asks for when <see cref="Model"/> is not set: the current Opus. The
    /// owner chooses a cheaper one (<c>claude-sonnet-5-5</c>, <c>claude-haiku-5-5</c>) with the setting;
    /// the code does not choose it for them.
    /// </summary>
    public const string DefaultClaudeModel = "claude-opus-5-5";

    /// <summary>
    /// Which provider drafts: <see cref="Gemini"/> (the default) or <see cref="Claude"/>.
    ///
    /// Gemini Flash by default: it is the cheapest at this shape of request, it reads images natively,
    /// and the abstraction means changing it costs one class. Claude is there because the owner has
    /// Anthropic credits and wants drafting on it for now (2026-10-28): set
    /// <c>Summarization__Provider=claude</c> and put an Anthropic key in <c>Summarization__ApiKey</c>.
    ///
    /// Anything else is refused at startup (<see cref="Validate"/>), because a typo would otherwise send
    /// one company's key to the other.
    /// </summary>
    public string Provider { get; set; } = Gemini;

    /// <summary>Whether Claude drafts on this deployment.</summary>
    public bool IsClaude => string.Equals(Provider?.Trim(), Claude, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The provider key.
    ///
    /// Trimmed on the way in, because a key mounted from a file or a secrets store almost always arrives
    /// with a trailing newline — and an HTTP header value cannot contain one, so the request threw a
    /// FormatException that no catch here covered. Every draft came back as a 500 on a deployment whose
    /// key was perfectly correct (2026-09-19 review).
    /// </summary>
    public string ApiKey
    {
        get => _apiKey;
        set => _apiKey = value?.Trim() ?? string.Empty;
    }

    public string FallbackApiKey
    {
        get => _fallbackApiKey;
        set => _fallbackApiKey = value?.Trim() ?? string.Empty;
    }

    private string _apiKey = string.Empty;

    private string _fallbackApiKey = string.Empty;

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
    ///
    /// Unset, it is <see cref="DefaultGeminiModel"/> or <see cref="DefaultClaudeModel"/>, by provider.
    /// </summary>
    public string Model
    {
        get => _model ?? (IsClaude ? DefaultClaudeModel : DefaultGeminiModel);
        set => _model = value;
    }

    /// <summary>
    /// How much detail the model is given in each picture, in the provider's own words. Gemini only.
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
    /// The Gemini default is the Gemini Flash 3.x standard rate read from Google's pricing page on
    /// 2026-09-19. <b>Google's rates are scheduled to double on 2027-01-01</b>, so an operator who cares
    /// about the exact figure sets them here rather than waiting for a release.
    ///
    /// The Claude default is the configured model's list price (<see cref="ClaudePricing"/>, read from
    /// Anthropic's pricing page on 2026-10-28): $4 a million on Claude Opus 5.5.
    /// </summary>
    public decimal InputCostPerMillionUsd
    {
        get => _inputCost ?? (IsClaude ? ClaudeRate.Input : 0.75m);
        set => _inputCost = value;
    }

    /// <summary>
    /// What output costs, per million tokens: $3.75 on Gemini Flash, $20 on Claude Opus 5.5. Thinking is
    /// billed at this rate too, so it is counted against it: Gemini reports it beside the answer's own
    /// count, Claude inside it.
    /// </summary>
    public decimal OutputCostPerMillionUsd
    {
        get => _outputCost ?? (IsClaude ? ClaudeRate.Output : 3.75m);
        set => _outputCost = value;
    }

    /// <summary>
    /// What a prompt-cache read costs, per million tokens. Claude reports these tokens apart from the
    /// rest of the input, and Claude Opus 5.5 charges $0.20 for them. Gemini does not use it.
    /// </summary>
    public decimal CacheReadCostPerMillionUsd
    {
        get => _cacheReadCost ?? (IsClaude ? ClaudeRate.CacheRead : InputCostPerMillionUsd);
        set => _cacheReadCost = value;
    }

    /// <summary>
    /// What a prompt-cache write costs, per million tokens, at the five-minute rate ($5 on Claude Opus
    /// 5.5). The drafting request asks for no caching, so this should go unused; it is counted all the
    /// same, because a line of the bill that is not read is a line the cap does not see.
    /// </summary>
    public decimal CacheWriteCostPerMillionUsd
    {
        get => _cacheWriteCost ?? (IsClaude ? ClaudeRate.CacheWrite : InputCostPerMillionUsd);
        set => _cacheWriteCost = value;
    }

    /// <summary>
    /// How hard Claude thinks before it answers: <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or
    /// <c>max</c>. Claude only.
    ///
    /// Sent explicitly rather than left to the model's own default, because the default moves between
    /// models (Claude Opus 5.5's is medium, Claude Opus 5's was high) and this one setting decides most
    /// of what a draft costs and how long it takes. Thinking cannot be switched off on Claude Opus 5.5;
    /// this is the control instead. Medium is where Anthropic says to start; low is worth measuring once
    /// there is a key to measure it with, because a note is a report of what happened.
    /// </summary>
    public string Effort { get; set; } = "medium";

    /// <summary>
    /// What Claude does when its safety classifiers decline a session. Claude only.
    ///
    /// <c>default</c>, the default, asks the API to run a declined request again on the model Anthropic
    /// recommends for that kind of refusal, inside the same call (server-side fallback, beta
    /// <c>server-side-fallback-2026-07-01</c>). Benign IT work can trip a classifier, and without this a
    /// false positive is a note the technician writes by hand. On a model with no fallback (Claude Haiku
    /// 5.5) the request runs alone and a refusal stays a refusal.
    ///
    /// A comma-separated list of model ids names the substitutes instead (beta
    /// <c>server-side-fallback-2026-06-01</c>), and <c>none</c> turns it off. Either way, a session the
    /// whole chain declines is a clear error, never half a note.
    /// </summary>
    public string RefusalFallback { get; set; } = "default";

    /// <summary>
    /// Who drafts when the first one is down. Optional: without it a 5xx becomes a queued retry rather
    /// than a second bill, which is the right default for a pilot.
    /// </summary>
    public string? FallbackProvider { get; set; }

    /// <summary>
    /// What one tenant may spend on drafting in a day, in US dollars.
    ///
    /// A cap rather than an alert. The scope document budgets under ten cents a session, so on Gemini
    /// this is ten dollars, roughly a hundred sessions: far past a busy day for one MSP, and close
    /// enough to notice a bug that drafts the same session in a loop before it becomes a bill.
    ///
    /// <b>On Claude it is thirty dollars</b>, sized the same way: about a hundred of the heaviest session
    /// a client can send (about $0.31 each on Claude Opus 5.5; <see cref="MaxSessionCostUsd"/> shows the
    /// arithmetic), or several hundred everyday three-frame sessions (about $0.08 each). Ten dollars
    /// would have stopped drafting after some thirty heavy sessions, which a busy MSP reaches by lunch.
    /// </summary>
    public decimal DailyCostCapUsd
    {
        get => _dailyCap ?? (IsClaude ? 30m : 10m);
        set => _dailyCap = value;
    }

    /// <summary>
    /// The most one session may be assumed to cost while its draft is in flight, in US dollars.
    ///
    /// The cap used to be check-then-act: what a tenant had spent was read, and what this call cost was
    /// written, a model call apart. Everything that started in between passed a check nobody had yet
    /// moved, so a device opening five hundred requests at once ran up five hundred drafts against a cap
    /// of ten dollars (2026-09-19 review).
    ///
    /// So this much is reserved before the call and settled to the real figure afterwards. It is also
    /// what a call we stopped waiting for is charged, since nobody said what it cost. Being generous
    /// here only means a busy tenant stops one session early.
    ///
    /// On Gemini it is the scope document's per-session ceiling of ten cents, which the measured worst
    /// case (25 frames, $0.0167) sits well under.
    ///
    /// <b>On Claude it is sixty cents</b>: the worst single call at Claude Opus 5.5's prices, estimated
    /// from the documented image cost because no key was available to measure with on 2026-10-28. The
    /// heaviest bundle a client sends is 25 frames at its 1600 x 900 downscale. Claude reads an image as
    /// one token per 28 x 28 pixel patch, so each frame is 58 x 33 = 1,914 tokens and the frames 47,850.
    /// The prompt, the schema and a dense session's OCR and speech (1,500 characters of OCR a frame,
    /// twenty minutes of talking) add about 15,000, so the input is about 63,000 tokens, or $0.25. A note
    /// at medium effort, thinking included, is a few thousand output tokens (about $0.06), so the heavy
    /// session costs about $0.31. The worst case is all of <see cref="MaxOutputTokens"/>, 16,000 tokens
    /// at $20 a million, $0.32, for $0.57 in all. The Gemini figure would have charged a Claude timeout a
    /// sixth of what it may have cost. Only a bundle near the server's own text ceiling
    /// (<see cref="BundleLimits.MaxTotalTextChars"/>) overshoots it, by up to about twice.
    ///
    /// <b>A refusal fallback can make one call two attempts.</b> With <see cref="RefusalFallback"/> at
    /// <c>default</c>, a session Claude Opus 5.5 declines is run again on another model (Claude Opus 5 or Opus 4.8, both at
    /// $5 and $25) inside the same call, and both attempts are billed. The worst such call is the declined
    /// attempt's input ($0.25) plus a full fallback answer (63,000 tokens in, 16,000 out: $0.72), about
    /// $0.97. That is not what the reservation is sized for, because a declined IT support session is
    /// rare and the real figure replaces the reservation as soon as the call ends; the cap only lets one
    /// such call per tenant overshoot by the difference. Set this higher, or the fallback to <c>none</c>,
    /// where that matters.
    /// </summary>
    public decimal MaxSessionCostUsd
    {
        get => _maxSessionCost ?? (IsClaude ? 0.60m : 0.10m);
        set => _maxSessionCost = value;
    }

    /// <summary>
    /// How long one draft may take before it is abandoned and queued (AC1 budgets 30 s p95).
    ///
    /// Sixty seconds on Gemini, which measured 10.5 s on the heaviest bundle. Eighty-five on Claude:
    /// Claude Opus 5.5 always thinks before it writes, and a heavy session at medium effort has not been
    /// measured (no key on 2026-10-28). Sixty would risk abandoning, and charging the estimate for, the
    /// sessions that matter most. Eighty-five stays inside the client's hundred-second wait, and
    /// Program.cs keeps the backend's own request backstop thirty seconds longer than this.
    /// </summary>
    public TimeSpan Timeout
    {
        get => _timeout ?? (IsClaude ? TimeSpan.FromSeconds(85) : TimeSpan.FromSeconds(60));
        set => _timeout = value;
    }

    /// <summary>
    /// The most the model may write in one answer.
    ///
    /// A note with its steps, its follow-ups and its titles measures a few hundred tokens; eight
    /// thousand is room for a session nobody has had yet, and a wall against the completion that never
    /// stops. Output costs five times input, so this is the one number that bounds the worst case.
    ///
    /// Sixteen thousand on Claude, because Claude's thinking counts against the same limit and cannot be
    /// switched off on Claude Opus 5.5. A limit sized for the note alone would cut the note off, which
    /// is a draft the technician has to write by hand.
    /// </summary>
    public int MaxOutputTokens
    {
        get => _maxOutputTokens ?? (IsClaude ? 16_000 : 8_192);
        set => _maxOutputTokens = value;
    }

    /// <summary>Whether this deployment can draft at all.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>The effort levels Claude accepts.</summary>
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    private string? _model;

    private decimal? _inputCost;

    private decimal? _outputCost;

    private decimal? _cacheReadCost;

    private decimal? _cacheWriteCost;

    private decimal? _dailyCap;

    private decimal? _maxSessionCost;

    private TimeSpan? _timeout;

    private int? _maxOutputTokens;

    /// <summary>The configured Claude model's list prices, for the rates nobody set.</summary>
    private ClaudeRate ClaudeRate => ClaudePricing.For(Model);

    /// <summary>
    /// Refuses a provider or an effort this service cannot send, at startup rather than at the first
    /// session of the day. An unknown provider is the dangerous one: the key would go to the wrong
    /// company.
    /// </summary>
    public void Validate()
    {
        var provider = Provider?.Trim() ?? string.Empty;
        if (!string.Equals(provider, Gemini, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(provider, Claude, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Summarization:Provider is \"{provider}\". It must be \"{Gemini}\" or \"{Claude}\"; the service "
                + "refuses to start rather than send the key to a provider it was not meant for.");
        }

        if (IsClaude && !Efforts.Contains(Effort?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Summarization:Effort is \"{Effort}\". It must be one of: {string.Join(", ", Efforts)}.");
        }

        // Claude Haiku 5.5 has no server-side fallback, and a list of fallback models is a 400 on every
        // draft. "default" and "none" are both fine there: with "default" the model runs alone.
        var fallback = RefusalFallback?.Trim() ?? string.Empty;
        if (IsClaude
            && Model.Trim().StartsWith("claude-haiku-5-5", StringComparison.OrdinalIgnoreCase)
            && fallback.Length > 0
            && !string.Equals(fallback, "default", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fallback, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Summarization:RefusalFallback names models (\"{fallback}\"), and {Model} has no fallback: "
                + "every draft would be refused. Set it to default or none.");
        }
    }
}
