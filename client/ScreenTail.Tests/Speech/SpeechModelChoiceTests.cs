using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// Which model a machine gets, and why it is no longer a question about cores (ST-027, ST-031;
/// 2026-09-20 efficiency review).
///
/// The rule was <c>processors >= 8 ? small.en : base.en</c>, asked with
/// <see cref="Environment.ProcessorCount"/> — which counts logical processors. The reference laptop is
/// four cores with hyper-threading, so it reports eight and has been running <c>small.en</c>: three
/// times the weights and about three times the compute of the model the budget was written around.
///
/// <b>Cores were the wrong question.</b> ADR-0001 measured <c>base</c> on that laptop at 505 MB
/// resident, against ST-031's 600 MB for the whole of ScreenTail. Of that, 148 MB is the weights, so
/// everything else — the runtime, the store, the capture loops — is about 357 MB. Swap in small.en's
/// 488 MB and the total is around 845 MB whatever the machine is. No number of cores makes a model fit
/// in memory it does not fit in.
/// </summary>
public sealed class SpeechModelChoiceTests
{
    /// <summary>ST-031's ceiling for all of ScreenTail, not for speech alone.</summary>
    private const long BudgetBytes = 600L * 1024 * 1024;

    /// <summary>
    /// Everything resident that is not the weights, from ADR-0001's measurement of base.en on the
    /// reference laptop: 505 MB resident less the 148 MB the model file occupies.
    /// </summary>
    private const long MeasuredOverheadBytes = (505L * 1024 * 1024) - 147_964_211;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    public void NoNumberOfCoresBuysAModelThatDoesNotFitInMemory(int processors)
    {
        var chosen = SpeechModels.For(processors);

        Assert.True(
            chosen.Bytes + MeasuredOverheadBytes < BudgetBytes,
            $"{chosen.Name} needs about {(chosen.Bytes + MeasuredOverheadBytes) / 1024 / 1024} MB resident "
                + $"against a {BudgetBytes / 1024 / 1024} MB budget on a machine with {processors} processors.");
    }

    [Fact]
    public void TheReferenceLaptopGetsTheModelTheBudgetWasWrittenAround()
    {
        // Four cores, eight logical processors: the machine ST-031's numbers were measured on.
        Assert.Equal(SpeechModels.BaseEnglish, SpeechModels.For(8));
    }

    [Fact]
    public void TheBiggerModelIsStillThereForSomebodyWhoAsksForItOnPurpose()
    {
        // Kept, not deleted. It is materially better on product names, which is where a wrong word
        // becomes a wrong fact in a ticket, and an operator who knows what it costs may still want it
        // (ST-047). What it may not be is the answer to a question nobody asked.
        Assert.Contains(SpeechModels.SmallEnglish, SpeechModels.All);
        Assert.True(SpeechModels.SmallEnglish.Bytes > SpeechModels.BaseEnglish.Bytes);
    }
}
