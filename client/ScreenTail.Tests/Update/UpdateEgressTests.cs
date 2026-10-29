using System.Net;
using ScreenTail.Core.Net;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Update;

namespace ScreenTail.Tests.Update;

/// <summary>
/// The update check's own egress purpose (ST-113, INV-8): to the update hosts and nowhere else, and not
/// under local-only unless the tenant's admin allowed it. Decided by the same policy and guard every other
/// request goes through.
/// </summary>
public sealed class UpdateEgressTests
{
    private static readonly Uri Backend = new("https://api.screentail.example/");
    private static readonly Uri Manifest = new("https://updates.screentail.example/feed/stable/win-x64.json");
    private static readonly Uri Download = new("https://downloads.screentail.example/screentail/0.1.20/ScreenTail.msi");

    [Fact]
    public void AnUpdateCheckMayReachTheUpdateHostsAndNothingElse()
    {
        var policy = Policy();

        Assert.True(policy.Decide(EgressPurpose.UpdateCheck, Manifest).Allowed);
        Assert.True(policy.Decide(EgressPurpose.UpdateCheck, Download).Allowed);
        Assert.False(policy.Decide(EgressPurpose.UpdateCheck, Backend).Allowed);
        Assert.False(policy.Decide(EgressPurpose.UpdateCheck, new Uri("https://" + SpeechModels.Hosts.First() + "/x")).Allowed);
        Assert.False(policy.Decide(EgressPurpose.UpdateCheck, new Uri("http://updates.screentail.example/feed/stable/win-x64.json")).Allowed);
    }

    [Theory]
    [InlineData(EgressPurpose.Backend)]
    [InlineData(EgressPurpose.Summarisation)]
    [InlineData(EgressPurpose.Publish)]
    [InlineData(EgressPurpose.Policy)]
    [InlineData(EgressPurpose.ModelDownload)]
    public void NoOtherPurposeReachesTheUpdateHosts(EgressPurpose purpose)
    {
        // The update hosts are not a place the client's other traffic may go: a bundle sent to them would
        // be a bundle sent to a static file host nobody configured for it.
        Assert.False(Policy().Decide(purpose, Manifest).Allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalOnlyStopsUpdateChecksByDefault(bool lockedByTheAdmin)
    {
        var policy = Policy();
        policy.Apply(localOnly: true, enforced: lockedByTheAdmin);

        var decision = policy.Decide(EgressPurpose.UpdateCheck, Manifest);

        Assert.False(decision.Allowed);
        Assert.Contains("Local-only", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdminWhoAllowsItLetsUpdateChecksAndNothingElseThroughLocalOnly()
    {
        var policy = Policy();
        policy.Apply(localOnly: true, enforced: true, updatesUnderLocalOnly: true);

        Assert.True(policy.Decide(EgressPurpose.UpdateCheck, Manifest).Allowed);
        Assert.True(policy.Decide(EgressPurpose.UpdateCheck, Download).Allowed);
        Assert.False(policy.Decide(EgressPurpose.Summarisation, Backend).Allowed);
        Assert.False(policy.Decide(EgressPurpose.Backend, Backend).Allowed);
        Assert.False(policy.Decide(EgressPurpose.ModelDownload, new Uri("https://" + SpeechModels.Hosts.First() + "/x")).Allowed);
        Assert.False(policy.Decide(EgressPurpose.UpdateCheck, Backend).Allowed);
    }

    [Fact]
    public async Task ARedirectOffTheUpdateHostsIsRefusedByTheGuard()
    {
        // The package host answering 302 to somewhere else would otherwise take the download anywhere.
        var far = new Redirecting(new Uri("https://cdn.elsewhere.example/ScreenTail.msi"));
        var guard = new EgressGuard(Policy(), far);
        using var http = new HttpClient(guard);
        using var request = EgressRequest.For(HttpMethod.Get, Download, EgressPurpose.UpdateCheck);

        var refused = await Assert.ThrowsAsync<EgressBlockedException>(() => http.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("cdn.elsewhere.example", refused.Host);
        Assert.Equal(1, far.Requests);
    }

    [Fact]
    public void TheClientsEgressShapePutsTheTrustFilesHostsOnTheUpdateListOnly()
    {
        using var signer = new TestSigner();
        var trust = TestManifests.Trust(signer.Public);

        var settings = ClientEgress.Settings(Backend, trust);

        Assert.Equal(trust.Hosts.Order(StringComparer.Ordinal), settings.UpdateHosts.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(TestManifests.PackageHost, settings.BackendHosts);
        Assert.DoesNotContain(TestManifests.PackageHost, settings.PublishHosts);
        Assert.DoesNotContain(TestManifests.PackageHost, settings.ModelHosts);
        Assert.False(settings.UpdatesUnderLocalOnly);
    }

    private static EgressPolicy Policy()
    {
        using var signer = new TestSigner();
        return new EgressPolicy(ClientEgress.Settings(Backend, TestManifests.Trust(signer.Public)));
    }

    private sealed class Redirecting(Uri to) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = to;
            return Task.FromResult(response);
        }
    }
}
