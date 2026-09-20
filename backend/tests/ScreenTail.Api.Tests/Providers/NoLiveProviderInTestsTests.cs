using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// The suite never calls a real model, and never spends anyone's money.
///
/// This is not hypothetical. The host under test runs as Development so that it behaves like a
/// developer's machine, and Development is exactly where <c>dotnet user-secrets</c> is read — which is
/// where ST-063 tells people to put the provider key. The day the first key was saved, this suite
/// started calling Google on every run, on that machine and no other.
///
/// The symptom was a summarization test failing with a 500 where it expected a 501, which says nothing
/// about the cause. This says it: a test host with a drafting key configured is a bug, whoever's key it
/// is.
/// </summary>
public sealed class NoLiveProviderInTestsTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public void TheTestHostHasNoDraftingProviderConfigured()
    {
        var options = api.Services.GetRequiredService<SummarizationOptions>();

        Assert.False(
            options.Configured,
            "The test host picked up a real provider key. Every run of this suite is now billed to "
                + "whoever owns it, and the results depend on which machine they run on.");
    }

    [Fact]
    public void NoFallbackProviderEither()
    {
        var options = api.Services.GetRequiredService<SummarizationOptions>();

        Assert.True(string.IsNullOrWhiteSpace(options.FallbackApiKey));
    }
}
