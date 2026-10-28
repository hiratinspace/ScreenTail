namespace ScreenTail.Api.Providers.Llm;

/// <summary>What one Claude model charges, in US dollars per million tokens.</summary>
/// <param name="CacheWrite">The five-minute cache write. The drafting request does not ask for one.</param>
public sealed record ClaudeRate(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);

/// <summary>
/// Anthropic's list prices for the models a Claude deployment may draft with, and for the models its
/// refusal fallback may hand a request to (ST-063).
///
/// Read from https://platform.claude.com/docs/en/about-claude/pricing on 2026-10-28, standard tier,
/// global routing. Used for the cost cap, not for an invoice: the defaults in
/// <see cref="Summarize.SummarizationOptions"/> come from here, and an operator whose bill disagrees
/// sets the rates there rather than waiting for a release.
///
/// <b>A model not listed is priced as Claude Opus 5</b>, the dearest one here. A cap that over-counts a
/// model it does not know costs a tenant a few sessions; one that under-counts is not a cap.
/// </summary>
public static class ClaudePricing
{
    private static readonly Dictionary<string, ClaudeRate> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-5-5"] = new(Input: 4m, Output: 20m, CacheRead: 0.20m, CacheWrite: 5m),
        ["claude-sonnet-5-5"] = new(Input: 2m, Output: 10m, CacheRead: 0.10m, CacheWrite: 2.50m),

        // Prompts up to 100,000 tokens. The heaviest bundle a client sends is about 63,000.
        ["claude-haiku-5-5"] = new(Input: 0.10m, Output: 0.50m, CacheRead: 0.01m, CacheWrite: 0.125m),

        // Where Claude Opus 5.5's refusal fallback sends a declined request.
        ["claude-opus-5"] = new(Input: 5m, Output: 25m, CacheRead: 0.50m, CacheWrite: 6.25m),
        ["claude-opus-4-8"] = new(Input: 5m, Output: 25m, CacheRead: 0.50m, CacheWrite: 6.25m),
    };

    /// <summary>The rates for <paramref name="model"/>, or Claude Opus 5's when it is not listed.</summary>
    public static ClaudeRate For(string? model) =>
        model is not null && Rates.TryGetValue(model.Trim(), out var rate) ? rate : Rates["claude-opus-5"];

    /// <summary>Whether <paramref name="model"/> has a listed price.</summary>
    public static bool Knows(string? model) => model is not null && Rates.ContainsKey(model.Trim());
}
