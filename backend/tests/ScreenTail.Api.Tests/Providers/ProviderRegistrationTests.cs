using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Demo;
using ScreenTail.Api.Providers.Fake;
using ScreenTail.Api.Providers.Hudu;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// Where the fakes may and may not appear (ST-090).
/// </summary>
public sealed class ProviderRegistrationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public void NoFakeProviderIsRegisteredInTheApplication()
    {
        // A fake PSA in a deployment tells a technician their note was published when nothing was, and
        // the ticket stays empty until a customer asks why. Registration is the only way that happens,
        // so registration is what is checked.
        using var scope = api.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<FakePsaProvider>());
        Assert.Null(scope.ServiceProvider.GetService<FakeDocProvider>());
        Assert.Null(scope.ServiceProvider.GetService<IPsaProvider>());
        Assert.Null(scope.ServiceProvider.GetService<IDocProvider>());

        // The demo PSA (ST-090) is the same danger with a label on it, so it is held to the same rule:
        // without Providers:Demo, which only Development accepts, the PSA is the tenant's own.
        Assert.Null(scope.ServiceProvider.GetService<DemoStore>());
        Assert.IsType<ConnectWiseProviderFactory>(scope.ServiceProvider.GetRequiredService<IPsaProviderFactory>());
        Assert.IsType<HuduProviderFactory>(scope.ServiceProvider.GetRequiredService<IDocProviderFactory>());
    }

    [Fact]
    public void TheDemoIsOnlyEverBuiltInItsOwnFolderAndOnlySwitchedOnBehindItsSetting()
    {
        // ST-090. The demo's providers are made by its own factories and nowhere else, and the two lines
        // that put it in the service are each behind the switch, the mapping behind Development as well.
        // The switch itself is refused outside Development (DemoPsaTests), so between them a deployment
        // cannot reach any of it.
        var sources = ProviderSources();
        var demoFolder = $"{Path.DirectorySeparatorChar}Providers{Path.DirectorySeparatorChar}Demo{Path.DirectorySeparatorChar}";
        var offenders = sources
            .Where(file => !file.Contains(demoFolder, StringComparison.Ordinal))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("new DemoPsaProvider", StringComparison.Ordinal)
                    || text.Contains("new DemoDocProvider", StringComparison.Ordinal)
                    || text.Contains("new DemoStore", StringComparison.Ordinal)
                    || (text.Contains("AddDemoProviders", StringComparison.Ordinal) && !file.EndsWith("Program.cs", StringComparison.Ordinal));
            })
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0, $"The demo is built or registered in: {string.Join(", ", offenders)}");

        var program = File.ReadAllText(sources.Single(file => file.EndsWith($"{Path.DirectorySeparatorChar}Program.cs", StringComparison.Ordinal)));
        var validated = program.IndexOf("demo.Validate(builder.Environment.IsDevelopment())", StringComparison.Ordinal);
        var registered = program.IndexOf("AddDemoProviders()", StringComparison.Ordinal);
        var mapped = program.IndexOf("MapDemoPsa()", StringComparison.Ordinal);
        Assert.True(registered > 0 && program.LastIndexOf("AddDemoProviders()", StringComparison.Ordinal) == registered, "AddDemoProviders is called once, in Program.cs.");
        Assert.True(mapped > 0 && program.LastIndexOf("MapDemoPsa()", StringComparison.Ordinal) == mapped, "MapDemoPsa is called once, in Program.cs.");
        Assert.True(validated > 0 && validated < registered, "The switch is validated before anything is registered.");
        Assert.Contains("if (demo.Demo)", program[Math.Max(0, registered - 120)..registered], StringComparison.Ordinal);
        Assert.Contains("if (app.Environment.IsDevelopment() && demo.Demo)", program[Math.Max(0, mapped - 120)..mapped], StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptedDrafterIsOnlyEverBuiltInProgramAndOnlyBehindItsSetting()
    {
        // ST-013. A note no model wrote is the same danger as a publish nobody received, so the scripted
        // test provider is held to the demo's rule: built in Program.cs alone, behind its setting, which is
        // validated first and refused outside Development (ScriptedProviderTests).
        var sources = ProviderSources();
        var offenders = sources
            .Where(file => !file.EndsWith($"{Path.DirectorySeparatorChar}Program.cs", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("new ScriptedProvider", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0, $"The scripted provider is built in: {string.Join(", ", offenders)}");

        // ST-063 merged the provider choice into one ILlmProvider factory, which SummarizationService takes
        // from the container. The scripted provider is that factory's first branch, behind its setting, and
        // the single Validate that refuses it outside Development runs before anything is registered.
        var program = File.ReadAllText(sources.Single(file => file.EndsWith($"{Path.DirectorySeparatorChar}Program.cs", StringComparison.Ordinal)));
        const string validate = "summarization.Validate(builder.Environment.IsDevelopment())";
        var validated = program.IndexOf(validate, StringComparison.Ordinal);
        var factory = program.IndexOf("builder.Services.AddScoped<ILlmProvider>(", StringComparison.Ordinal);
        var service = program.IndexOf("builder.Services.AddScoped<SummarizationService>(", StringComparison.Ordinal);
        var built = program.IndexOf("new ScriptedProvider()", StringComparison.Ordinal);
        Assert.True(validated > 0 && program.LastIndexOf("summarization.Validate(", StringComparison.Ordinal) == validated, "Validate is called once, in Program.cs.");
        Assert.True(built > 0 && program.LastIndexOf("new ScriptedProvider", StringComparison.Ordinal) == built, "ScriptedProvider is built once, in Program.cs.");
        Assert.True(validated < factory && factory < built && built < service, "The setting is validated, then the provider is built inside the ILlmProvider factory.");
        Assert.Contains("if (summarization.Scripted)", program[Math.Max(0, built - 80)..built], StringComparison.Ordinal);
        Assert.Contains("services.GetRequiredService<ILlmProvider>()", program[service..], StringComparison.Ordinal);

        // And without the setting, which is how the fixture runs, the provider is not configured at all.
        using var scope = api.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<ScreenTail.Api.Summarize.SummarizationOptions>().Scripted);
        Assert.IsType<ScreenTail.Api.Providers.Llm.GeminiProvider>(scope.ServiceProvider.GetRequiredService<ScreenTail.Api.Summarize.ILlmProvider>());
    }

    [Theory]
    [InlineData(nameof(ScreenTail.Api.Providers.ConnectWise.ConnectWiseProvider))]
    [InlineData(nameof(ScreenTail.Api.Providers.Hudu.HuduProvider))]
    public void EachProviderClientStopsReadingAnAnswerAtTheLimit(string name)
    {
        // 2026-10-12 review. A tenant chooses the site, and the backend is shared: without a limit an
        // answer that never ends is held in memory until the server has none left for anybody.
        var factory = api.Services.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(name);

        Assert.Equal(ProviderHttp.MaxResponseBytes, client.MaxResponseContentBufferSize);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    [Fact]
    public void TheFakesAreOnlyEverConstructedByTests()
    {
        // ST-091 and ST-095 register the real ones. Until then nothing does, and a provider resolved out
        // of the container would be a fake by definition.
        var sources = ProviderSources();
        var offenders = sources
            .Where(file => !file.Contains("/Providers/Fake/", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("new Fake", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, $"A fake provider is constructed in: {string.Join(", ", offenders)}");
    }

    private static IReadOnlyList<string> ProviderSources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src");
            if (Directory.Exists(candidate))
            {
                return [.. Directory.EnumerateFiles(candidate, "*.cs", SearchOption.AllDirectories)
                    .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
            }

            directory = directory.Parent;
        }

        return [];
    }
}
