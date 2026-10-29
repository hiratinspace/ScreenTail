using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ScreenTail.Core.Net;
using ScreenTail.Core.Update;

namespace ScreenTail.Tests.Update;

/// <summary>
/// The update fields of the tenant's policy (ST-113, INV-11): the channel and whether it is locked, the
/// interval, and whether update checks may pass local-only. The admin's where set, enforced here.
/// </summary>
public sealed class UpdatePolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void WithNoPolicyTheDefaultsAreStableADayAndNothingUnderLocalOnly()
    {
        var resolved = UpdatePolicies.Resolve(TenantPolicy.Default, UpdateChannel.Stable);

        Assert.Equal(new UpdatePolicy(UpdateChannel.Stable, false, TimeSpan.FromHours(24), false), resolved);
    }

    [Fact]
    public void TheTechniciansChannelAppliesWhileTheAdminHasNotChosenOne()
    {
        var policy = TenantPolicy.Default with { Version = "p1", UpdateChannel = "stable", UpdateChannelLocked = false };

        Assert.Equal(UpdateChannel.Pilot, UpdatePolicies.Resolve(policy, UpdateChannel.Pilot).Channel);
        Assert.False(UpdatePolicies.Resolve(policy, UpdateChannel.Pilot).ChannelLocked);
    }

    [Fact]
    public void TheAdminsChannelWinsWhenLocked()
    {
        var policy = TenantPolicy.Default with { Version = "p1", UpdateChannel = "stable", UpdateChannelLocked = true };

        var resolved = UpdatePolicies.Resolve(policy, UpdateChannel.Pilot);

        Assert.Equal(UpdateChannel.Stable, resolved.Channel);
        Assert.True(resolved.ChannelLocked);
    }

    [Fact]
    public void ALockedChannelThisVersionDoesNotKnowMeansNoChecksRatherThanAGuess()
    {
        var policy = TenantPolicy.Default with { Version = "p1", UpdateChannel = "nightly", UpdateChannelLocked = true };

        var resolved = UpdatePolicies.Resolve(policy, UpdateChannel.Pilot);

        Assert.Null(resolved.Channel);
        Assert.True(resolved.ChannelLocked);
        Assert.Equal(UpdatePolicies.UnknownChannel, resolved.Problem);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(24, 24)]
    [InlineData(500, 168)]
    public void TheIntervalIsTheAdminsWithZeroMeaningOff(int hours, int? expected)
    {
        var policy = TenantPolicy.Default with { Version = "p1", UpdateCheckHours = hours };

        Assert.Equal(expected is { } h ? TimeSpan.FromHours(h) : null, UpdatePolicies.Resolve(policy, UpdateChannel.Stable).Every);
    }

    [Fact]
    public void OnlyTheAdminCanLetUpdateChecksThroughLocalOnly()
    {
        Assert.False(UpdatePolicies.Resolve(TenantPolicy.Default, UpdateChannel.Stable).UnderLocalOnly);
        Assert.True(UpdatePolicies.Resolve(TenantPolicy.Default with { Version = "p1", UpdateChecksUnderLocalOnly = true }, UpdateChannel.Stable).UnderLocalOnly);
    }

    [Fact]
    public async Task APolicyFromABackendBeforeTheUpdateFieldsStillSyncsWithTheDefaults()
    {
        // The four fields are optional on the client, so a backend that does not send them yet is not
        // refused (sync would stop, and with it every lock the admin set).
        var older = """{"version":"p20260925140200000","retentionDays":3,"localOnly":true,"localOnlyLocked":true,"captureAllWindows":true}""";
        var sync = Sync(older);

        Assert.True(await sync.FetchAsync(TestContext.Current.CancellationToken));

        Assert.Equal("stable", sync.Current.UpdateChannel);
        Assert.False(sync.Current.UpdateChannelLocked);
        Assert.Equal(24, sync.Current.UpdateCheckHours);
        Assert.False(sync.Current.UpdateChecksUnderLocalOnly);
    }

    [Fact]
    public async Task TheSharedContractCarriesTheUpdateFields()
    {
        using var stream = typeof(UpdatePolicyTests).Assembly.GetManifestResourceStream("ScreenTail.Tests.tenant-policy.v1.json")!;
        var body = JsonNode.Parse(stream)!["response"]!.ToJsonString();
        var sync = Sync(body);

        Assert.True(await sync.FetchAsync(TestContext.Current.CancellationToken));

        // Every value in the contract differs from the defaults, so a field this side stopped reading
        // shows up as a default.
        Assert.Equal("pilot", sync.Current.UpdateChannel);
        Assert.True(sync.Current.UpdateChannelLocked);
        Assert.Equal(6, sync.Current.UpdateCheckHours);
        Assert.True(sync.Current.UpdateChecksUnderLocalOnly);
    }

    [Fact]
    public void TheTechniciansChannelIsKeptInAFileOfItsOwn()
    {
        var path = Path.Combine(_dir, "updates.json");
        var preferences = new UpdatePreferences(path);

        Assert.Equal(UpdateChannel.Stable, preferences.Load());
        Assert.True(preferences.Save(UpdateChannel.Pilot));
        Assert.Equal(UpdateChannel.Pilot, new UpdatePreferences(path).Load());

        File.WriteAllText(path, "{\"channel\":\"nightly\"}");
        Assert.Equal(UpdateChannel.Stable, preferences.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private PolicySync Sync(string body)
    {
        var http = new HttpClient(new OneAnswer(body)) { BaseAddress = new Uri("https://api.screentail.example/") };
        return new PolicySync(http, () => "token", new FilePolicyCache(Path.Combine(_dir, "policy.json")));
    }

    private sealed class OneAnswer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
