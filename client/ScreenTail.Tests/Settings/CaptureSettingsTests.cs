using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Core.Input;
using ScreenTail.Core.Settings;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>The capture settings document (ST-080, Spec §5 S5): what it refuses, what it turns into, and what it changes in place.</summary>
public sealed class CaptureSettingsTests
{
    private static readonly RemoteToolRegistry Registry = RemoteToolRegistry.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));

    [Fact]
    public void TheDefaultsCaptureRemoteToolsOnlyStartByThemselvesAndKeepTheShippedHotkeys()
    {
        var settings = new CaptureSettings();

        Assert.Empty(settings.DisabledTools);
        Assert.False(settings.CaptureAllWindows);
        Assert.True(settings.AutoStart);
        Assert.Null(settings.GraceSeconds);
        Assert.Equal("medium", settings.Sensitivity);
        Assert.Empty(settings.Problems());
        Assert.Equal(HotkeyBindings.Defaults[HotkeyAction.StartCapture], settings.ToBindings().For(HotkeyAction.StartCapture));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(301)]
    public void GraceOutsideThirtyToThreeHundredSecondsIsRefused(int seconds)
    {
        Assert.Contains(new CaptureSettings { GraceSeconds = seconds }.Problems(), p => p.Contains("300", StringComparison.Ordinal));
    }

    [Fact]
    public void AHotkeyThatDoesNotParseOrClashesIsRefusedWithASuggestion()
    {
        var unparsable = new CaptureSettings { Hotkeys = new Dictionary<string, string> { ["StartCapture"] = "Ctrl+" } };
        var clashing = new CaptureSettings { Hotkeys = new Dictionary<string, string> { ["StartCapture"] = "Ctrl+Alt+S" } };

        Assert.Contains(unparsable.Problems(), p => p.Contains("StartCapture", StringComparison.Ordinal));
        var conflict = Assert.Single(clashing.Conflicts());
        Assert.NotNull(conflict.Suggestion);
    }

    [Theory]
    [InlineData("low", 20, 5)]
    [InlineData("medium", 10, 3)]
    [InlineData("high", 5, 2)]
    public void SensitivityMapsToTheSamplersThreshold(string sensitivity, int threshold, int gapSeconds)
    {
        var options = new CaptureSettings { Sensitivity = sensitivity }.ToSceneOptions();

        Assert.Equal(threshold, options.Threshold);
        Assert.Equal(TimeSpan.FromSeconds(gapSeconds), options.MinimumGap);
        Assert.Contains(new CaptureSettings { Sensitivity = "extreme" }.Problems(), p => p.Contains("Low, Medium or High", StringComparison.Ordinal));
    }

    [Fact]
    public void AToolSwitchedOffIsOutOfScopeAndSaysSo()
    {
        var policy = new ScopePolicy(Registry);
        var window = new ForegroundWindowInfo(4242, 42, "mstsc", "Remote Desktop Connection", "TscShellContainerClass", null, false, DateTimeOffset.UnixEpoch);
        Assert.True(policy.Decide(window).MayCaptureFrames);

        policy.Apply(new ScopeOptions { DisabledTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { policy.Decide(window).ToolId! } });

        var decision = policy.Decide(window);
        Assert.False(decision.MayCaptureFrames);
        Assert.Contains("switched off in Settings", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTriggersGraceAndTheSamplersOptionsChangeInPlace()
    {
        var trigger = new SessionTrigger(new ScopePolicy(Registry), grace: TimeSpan.FromSeconds(120));
        trigger.Apply(TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(45), trigger.Grace);

        var sampler = new SceneSampler();
        sampler.Apply(new SceneSamplerOptions { Threshold = 5 });
        Assert.Equal(5, sampler.Options.Threshold);
    }
}
