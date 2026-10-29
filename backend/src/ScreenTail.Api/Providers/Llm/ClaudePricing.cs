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
/// <b>A model not listed is priced as the dearest one listed</b>, Claude Fable 5.1 at $10 and $50. A
/// newer model, or a name this table has not heard of, is then over-counted at worst. A cap that
/// over-counts costs a tenant a few sessions; one that counts half is not a cap (review of 2026-10-28,
/// which found unlisted models priced as Claude Opus 5, half of Fable's rate).
/// </summary>
public static class ClaudePricing
{
    private static readonly Dictionary<string, ClaudeRate> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-5-5"] = new(Input: 4m, Output: 20m, CacheRead: 0.20m, CacheWrite: 5m),

        // Cache reads are $0.10 here, not $0.20: the pricing page lists $0.10, and its footnote prices
        // Claude Sonnet 5.5's cache hits at 0.05x the $2 input. (The skill's model summary says $0.20;
        // the live page is the one the bill follows.)
        ["claude-sonnet-5-5"] = new(Input: 2m, Output: 10m, CacheRead: 0.10m, CacheWrite: 2.50m),

        // For prompts up to 100,000 tokens, which every bundle a client sends is (the heaviest is about
        // 63,000). Above that Haiku 5.5 charges five times as much: $0.50 and $2.50, cache reads $0.05.
        // An operator who drafts very long sessions on Haiku sets the higher rates.
        ["claude-haiku-5-5"] = new(Input: 0.10m, Output: 0.50m, CacheRead: 0.01m, CacheWrite: 0.125m),

        // Where Claude Opus 5.5's refusal fallback sends a declined request.
        ["claude-opus-5"] = new(Input: 5m, Output: 25m, CacheRead: 0.50m, CacheWrite: 6.25m),
        ["claude-opus-4-8"] = new(Input: 5m, Output: 25m, CacheRead: 0.50m, CacheWrite: 6.25m),

        // The dearest widely released model, and the price of anything not listed.
        ["claude-fable-5-1"] = new(Input: 10m, Output: 50m, CacheRead: 0.25m, CacheWrite: 12.50m),
    };

    /// <summary>The highest rate in the table, for a model it does not list.</summary>
    private static readonly ClaudeRate Dearest = new(
        Rates.Values.Max(rate => rate.Input),
        Rates.Values.Max(rate => rate.Output),
        Rates.Values.Max(rate => rate.CacheRead),
        Rates.Values.Max(rate => rate.CacheWrite));

    /// <summary>The rates for <paramref name="model"/>, or the dearest listed when it is not listed.</summary>
    public static ClaudeRate For(string? model) =>
        model is not null && Rates.TryGetValue(model.Trim(), out var rate) ? rate : Dearest;

    /// <summary>Whether <paramref name="model"/> has a listed price.</summary>
    public static bool Knows(string? model) => model is not null && Rates.ContainsKey(model.Trim());
}
