using Microsoft.Extensions.Configuration;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// The provider decides the defaults that are about money or a model, and an operator's setting beats
/// them (ST-063, Claude as a drafting provider).
/// </summary>
public sealed class SummarizationOptionsTests
{
    [Fact]
    public void GeminiIsTheDefaultAndItsNumbersAreUnchanged()
    {
        var options = new SummarizationOptions();

        Assert.False(options.IsClaude);
        Assert.Equal("gemini-flash", options.Provider);
        Assert.Equal("gemini-3.6-flash", options.Model);
        Assert.Equal(0.75m, options.InputCostPerMillionUsd);
        Assert.Equal(3.75m, options.OutputCostPerMillionUsd);
        Assert.Equal(10m, options.DailyCostCapUsd);
        Assert.Equal(0.10m, options.MaxSessionCostUsd);
        Assert.Equal(8_192, options.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Timeout);
    }

    [Fact]
    public void ClaudeDraftsOnTheCurrentOpusAtItsListPrice()
    {
        var options = new SummarizationOptions { Provider = "claude" };

        Assert.True(options.IsClaude);
        Assert.Equal("claude-opus-5-5", options.Model);
        Assert.Equal(4m, options.InputCostPerMillionUsd);
        Assert.Equal(20m, options.OutputCostPerMillionUsd);
        Assert.Equal(0.20m, options.CacheReadCostPerMillionUsd);
        Assert.Equal(5m, options.CacheWriteCostPerMillionUsd);
        Assert.Equal("medium", options.Effort);
        Assert.Equal("default", options.RefusalFallback);
        Assert.Equal(16_000, options.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(85), options.Timeout);
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", 2, 10, 0.10)]
    [InlineData("claude-haiku-5-5", 0.10, 0.50, 0.01)]
    public void ACheaperModelIsPricedAsItself(string model, decimal input, decimal output, decimal cacheRead)
    {
        var options = new SummarizationOptions { Provider = "claude", Model = model };

        Assert.Equal(input, options.InputCostPerMillionUsd);
        Assert.Equal(output, options.OutputCostPerMillionUsd);
        Assert.Equal(cacheRead, options.CacheReadCostPerMillionUsd);
    }

    [Fact]
    public void AModelWithNoListedPriceIsPricedHighRatherThanLow()
    {
        var options = new SummarizationOptions { Provider = "claude", Model = "claude-something-new" };

        Assert.True(options.InputCostPerMillionUsd >= 4m);
        Assert.True(options.OutputCostPerMillionUsd >= 20m);
    }

    [Fact]
    public void TheClaudeReservationCoversTheWorstSingleCallOnTheHeaviestBundle()
    {
        // The heaviest bundle a client sends: 25 frames at its 1600 x 900 downscale. Claude reads an image
        // as one token per 28 x 28 pixel patch. The prompt, the schema and a dense session's OCR and
        // speech add about 15,000 tokens. The worst case writes the whole output allowance.
        var options = new SummarizationOptions { Provider = "claude" };
        var frames = 25 * (int)Math.Ceiling(1600 / 28d) * (int)Math.Ceiling(900 / 28d);
        var input = frames + 15_000;

        var worst = ((input * options.InputCostPerMillionUsd) + (options.MaxOutputTokens * options.OutputCostPerMillionUsd)) / 1_000_000m;

        Assert.Equal(47_850, frames);
        Assert.True(options.MaxSessionCostUsd >= worst, $"Reserved ${options.MaxSessionCostUsd} for a call that can cost ${worst:F3}.");
        Assert.True(options.MaxSessionCostUsd <= worst * 1.25m, "The reservation is far above the worst case, which stops a busy tenant early for nothing.");
    }

    [Fact]
    public void TheClaudeDailyCapIsNotSpentByABusyMorning()
    {
        // About a hundred heavy sessions a tenant a day, the same sizing as Gemini's ten dollars at ten
        // cents. A heavy session on Claude Opus 5.5 is about $0.31, so ninety of them have to fit with a
        // reservation still standing.
        var options = new SummarizationOptions { Provider = "claude" };
        const decimal heavy = 0.31m;

        Assert.True((options.DailyCostCapUsd - options.MaxSessionCostUsd) / heavy >= 90);
        Assert.True(options.DailyCostCapUsd > new SummarizationOptions().DailyCostCapUsd);
    }

    [Fact]
    public void WhatTheOperatorSetIsKeptWhicheverOrderItIsRead()
    {
        // The configuration binder sets properties in declaration order, and Model is read before
        // Provider is known. A setting nobody made must stay unset rather than be frozen to Gemini's.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Summarization:DailyCostCapUsd"] = "12",
            ["Summarization:Provider"] = "claude",
            ["Summarization:ApiKey"] = " an-anthropic-key\n",
        }).Build();

        var options = configuration.GetSection(SummarizationOptions.Section).Get<SummarizationOptions>()!;

        Assert.True(options.IsClaude);
        Assert.Equal("an-anthropic-key", options.ApiKey);
        Assert.Equal(12m, options.DailyCostCapUsd);
        Assert.Equal("claude-opus-5-5", options.Model);
        Assert.Equal(0.60m, options.MaxSessionCostUsd);
        Assert.Equal(16_000, options.MaxOutputTokens);
    }

    [Fact]
    public void AGeminiModelSetByTheOperatorIsNotReplaced()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Summarization:Model"] = "gemini-9.9-flash",
        }).Build();

        var options = configuration.GetSection(SummarizationOptions.Section).Get<SummarizationOptions>()!;

        Assert.Equal("gemini-9.9-flash", options.Model);
        Assert.Equal(0.10m, options.MaxSessionCostUsd);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("")]
    public void AProviderThisServiceDoesNotKnowIsRefusedAtStartup(string provider)
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => new SummarizationOptions { Provider = provider }.Validate());

        Assert.Contains("Summarization:Provider", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gemini-flash")]
    [InlineData("claude")]
    [InlineData("Claude")]
    public void TheTwoProvidersAreAccepted(string provider)
    {
        new SummarizationOptions { Provider = provider }.Validate();
    }

    [Fact]
    public void AnEffortClaudeDoesNotAcceptIsRefusedAtStartup()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => new SummarizationOptions { Provider = "claude", Effort = "extreme" }.Validate());

        Assert.Contains("Summarization:Effort", thrown.Message, StringComparison.Ordinal);
    }
}
