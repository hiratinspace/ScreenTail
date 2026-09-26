using ScreenTail.Core.Net;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Settings;
using ScreenTail.Shared.Schema;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>The technician's own settings document (ST-081): what it holds, what it refuses, and what it turns into.</summary>
public sealed class ClientSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheDefaultsAreTheProductsAndTelemetryIsOff()
    {
        var settings = new ClientSettings();

        Assert.False(settings.LocalOnly);
        Assert.False(settings.Telemetry);
        Assert.Equal(7, settings.RetentionDays);
        Assert.True(settings.Ssn && settings.Cards && settings.ApiKeys && settings.Passwords);
        Assert.False(settings.Emails);
        Assert.Empty(settings.Problems());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void RetentionOutsideOneToThirtyDaysIsRefused(int days)
    {
        Assert.Contains(new ClientSettings { RetentionDays = days }.Problems(), p => p.Contains("30 days", StringComparison.Ordinal));
    }

    [Fact]
    public void ABadCustomPatternOrAProcessPathIsRefused()
    {
        var problems = new ClientSettings { CustomPatterns = ["("], ExcludedProcesses = [@"C:\x\y.exe"] }.Problems();

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void TheRedactionPolicyFollowsTheToggles()
    {
        var policy = new ClientSettings { Emails = true, Cards = false, CustomPatterns = [@"ACME-\d{6}"] }.ToRedactionPolicy();

        Assert.True(policy.Emails);
        Assert.False(policy.Cards);
        Assert.True(policy.IsEnabled(MaskKind.CustomPattern));
    }

    [Fact]
    public void TheDocumentRoundTripsThroughTheStoreAndABadFileIsTheDefaults()
    {
        var store = new ClientSettingsStore(Path.Combine(_dir, "settings.json"));
        Assert.Equal(new ClientSettings(), store.Load());

        store.Save(new ClientSettings { LocalOnly = true, RetentionDays = 14, ExcludedProcesses = ["keepass"] });
        Assert.Equal(14, store.Load().RetentionDays);
        Assert.Equal(["keepass"], store.Load().ExcludedProcesses);

        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json");
        Assert.Equal(new ClientSettings(), store.Load());
    }

    [Theory]
    // Admin policy set (a version other than the default): retention is the admin's; local-only is the
    // admin's only when locked.
    [InlineData("p1", 3, true, true, 14, false, 3, true, true)]
    [InlineData("p1", 3, false, false, 14, true, 3, true, false)]
    // No admin policy: everything is the technician's.
    [InlineData("default", 7, false, false, 14, true, 14, true, false)]
    public void ThePolicyAndTheTechniciansSettingsResolveTogether(string version, int policyDays, bool policyLocalOnly, bool locked, int userDays, bool userLocalOnly, int expectedDays, bool expectedLocalOnly, bool expectedEnforced)
    {
        var applied = PolicyApplication.Resolve(new TenantPolicy(version, policyDays, policyLocalOnly, locked, false), new ClientSettings { RetentionDays = userDays, LocalOnly = userLocalOnly });

        Assert.Equal(TimeSpan.FromDays(expectedDays), applied.Retention);
        Assert.Equal(expectedLocalOnly, applied.LocalOnly);
        Assert.Equal(expectedEnforced, applied.Enforced);
        Assert.Equal(version != "default", applied.RetentionEnforced);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
