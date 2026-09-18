using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.Fake;

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
