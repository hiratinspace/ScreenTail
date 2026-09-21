using ScreenTail.Core.Net;
using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// The allowlist has to contain the host the model actually comes from (ST-027, INV-8).
///
/// The published URL is on huggingface.co and answers 302 to a content host, so the second hop is the
/// one that carries the file — and the guard decides each hop on its own. The list named two CDN hosts
/// that the publisher no longer uses, so the hop was refused, and because
/// <c>EgressBlockedException</c> was missing from the catch in <c>PrepareAsync</c> the refusal faulted a
/// background task that nobody awaited. No log line, no narration, no sign of either.
///
/// Verified by hand on 2026-09-20: all three model URLs answer
/// <c>302 -> https://us.aws.cdn.hf.co/xet-bridge-us/...</c>.
///
/// <b>That host is region-specific</b>, which the "us." says out loud. This test does not pretend
/// otherwise; it pins what was verified, so that a publisher who moves again fails here rather than in
/// silence on a technician's laptop.
/// </summary>
public sealed class SpeechModelHostsTests
{
    private static readonly EgressPolicy Policy = new(new EgressSettings { ModelHosts = SpeechModels.Hosts });

    [Theory]
    [InlineData("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin")]
    [InlineData("https://us.aws.cdn.hf.co/xet-bridge-us/641ab5d15d107c5c5f346372/ff7d10f8")]
    public void TheHopsAModelDownloadActuallyTakesAreAllowed(string url)
    {
        Assert.True(Policy.Decide(EgressPurpose.ModelDownload, new Uri(url)).Allowed);
    }

    [Fact]
    public void EveryModelIsPublishedWhereTheFirstHopIsAllowed()
    {
        foreach (var model in new[] { SpeechModels.BaseEnglish, SpeechModels.SmallEnglish, SpeechModels.TinyEnglish })
        {
            Assert.True(
                Policy.Decide(EgressPurpose.ModelDownload, model.Source).Allowed,
                $"{model.Name} is published on a host the guard would refuse.");
        }
    }

    [Fact]
    public void SomebodyElsesHostIsStillNotAModelHost()
    {
        // The list stays exact host names. A near-miss that reads like the real one is the thing that
        // would make a suffix rule tempting.
        Assert.False(Policy.Decide(EgressPurpose.ModelDownload, new Uri("https://hf.co.evil.example/m")).Allowed);
        Assert.False(Policy.Decide(EgressPurpose.ModelDownload, new Uri("https://notus.aws.cdn.hf.co/m")).Allowed);
    }
}
