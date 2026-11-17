using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ScreenTrail.Api.Providers.ConnectWise;
using ScreenTrail.Api.Vault;

namespace ScreenTrail.Api.Tests.Providers.ConnectWise;

/// <summary>
/// What is said while the deployment has no ConnectWise clientId (owner decision 2026-11-16): a technician
/// is told who finishes the setup, with no configuration key; the owner and the server log get the setting.
/// </summary>
public sealed partial class ConnectWiseSetupTests
{
    [Fact]
    public void TheTechniciansWordsHaveNoConfigurationKeyInThem()
    {
        var technician = $"{ConnectWiseSetup.NotSetUp} {ConnectWiseSetup.FinishIt}";

        Assert.Equal("ConnectWise is not fully set up yet. Ask your ScreenTrail owner to finish the ConnectWise setup.", technician);
        Assert.False(ConfigurationKey().IsMatch(technician), technician);
        Assert.DoesNotContain("ClientId", technician, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheOwnerIsToldTheSettingThatIsMissing() =>
        Assert.Equal(
            "ConnectWise is not fully set up yet. Set ConnectWise:ClientId on the ScreenTrail server, or ask whoever runs it to.",
            VaultRefusal.ForOwner($"{ConnectWiseSetup.NotSetUp} {ConnectWiseSetup.FinishIt}"));

    [Fact]
    public void TheServerLogNamesTheSettingOnceAsAWarning()
    {
        var log = new ListLogger();

        ConnectWiseSetup.Announce(log);

        var (level, line) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("ConnectWise:ClientId", line, StringComparison.Ordinal);
    }

    /// <summary>A configuration key as the backend names them: Section:Name, or Section__Name.</summary>
    [GeneratedRegex(@"\b[A-Z][A-Za-z]+(:|__)[A-Z][A-Za-z]+\b")]
    private static partial Regex ConfigurationKey();

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Line)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
