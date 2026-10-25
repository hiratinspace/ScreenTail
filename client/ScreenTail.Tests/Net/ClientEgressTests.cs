using ScreenTail.Core.Net;
using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Net;

/// <summary>
/// The one egress shape the client's processes build (ST-046, INV-8; 2026-10-12 review, T-90).
///
/// The service's host named the model hosts in its composition root and the <c>--transcribe</c> tool
/// built a bare policy with none, so the tool could never fetch a model: a live defect in the thing
/// ST-027's word error rate is measured with. Both now ask the same function, and this holds it to
/// every hop a model download makes.
/// </summary>
public sealed class ClientEgressTests
{
    [Fact]
    public void TheServiceEgressPolicyAllowsEveryModelHop()
    {
        var policy = new EgressPolicy(ClientEgress.Settings(backend: null));

        foreach (var model in SpeechModels.All)
        {
            Assert.True(policy.Decide(EgressPurpose.ModelDownload, model.Source).Allowed, $"{model.Name} at {model.Source}");
        }

        // The redirect the publisher answers with, and every older name still on the list.
        foreach (var host in SpeechModels.Hosts)
        {
            Assert.True(policy.Decide(EgressPurpose.ModelDownload, new Uri($"https://{host}/some/file.bin")).Allowed, host);
        }
    }

    [Fact]
    public void AModelHostIsNotABackendAndTheBackendIsNotAModelHost()
    {
        // Two lists for two purposes, so a bug that clears one does not open the other (INV-8).
        var backend = new Uri("https://api.screentail.example");
        var settings = ClientEgress.Settings(backend);
        var policy = new EgressPolicy(settings);

        Assert.Equal(["api.screentail.example"], settings.BackendHosts);
        Assert.Equal(["api.screentail.example"], settings.PublishHosts);
        Assert.False(policy.Decide(EgressPurpose.ModelDownload, backend).Allowed);
        Assert.False(policy.Decide(EgressPurpose.Summarisation, new Uri("https://huggingface.co/")).Allowed);
        Assert.True(policy.Decide(EgressPurpose.Summarisation, backend).Allowed);
    }

    [Fact]
    public void NoBackendMeansNothingButTheModelsMayLeave()
    {
        var settings = ClientEgress.Settings(backend: null);

        Assert.Empty(settings.BackendHosts);
        Assert.Empty(settings.PublishHosts);
        Assert.Equal(SpeechModels.Hosts, settings.ModelHosts);
    }
}
