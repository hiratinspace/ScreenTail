using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// Which device opens (ST-080 AC3): the chosen one when connected, Windows' default otherwise. A session
/// never fails to start, or goes without narration, because a chosen device has gone.
/// </summary>
public sealed class MicrophoneChoiceTests
{
    private static readonly MicrophoneDevice Headset = new("{0.0.1.00000000}.{headset}", "Headset", IsDefault: false);
    private static readonly MicrophoneDevice BuiltIn = new("{0.0.1.00000000}.{built-in}", "Built-in microphone", IsDefault: true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoChoiceIsWindowsDefault(string? chosen)
    {
        Assert.Equal(new ResolvedMicrophone(BuiltIn, ChosenIsMissing: false), MicrophoneChoice.Resolve([Headset, BuiltIn], chosen));
    }

    [Fact]
    public void AConnectedChoiceIsTheOneThatOpens()
    {
        Assert.Equal(new ResolvedMicrophone(Headset, ChosenIsMissing: false), MicrophoneChoice.Resolve([Headset, BuiltIn], Headset.Id));
        Assert.Equal(Headset, MicrophoneChoice.Resolve([Headset, BuiltIn], Headset.Id.ToUpperInvariant()).Device);
    }

    [Fact]
    public void AChoiceThatIsNotConnectedFallsBackToTheDefaultAndSaysSo()
    {
        Assert.Equal(new ResolvedMicrophone(BuiltIn, ChosenIsMissing: true), MicrophoneChoice.Resolve([BuiltIn], Headset.Id));
    }

    [Fact]
    public void WithNoDefaultMarkedTheFirstDeviceIsUsed()
    {
        var other = BuiltIn with { IsDefault = false };

        Assert.Equal(Headset, MicrophoneChoice.Resolve([Headset, other], null).Device);
        Assert.Equal(Headset, MicrophoneChoice.Resolve([Headset, other], "{gone}").Device);
    }

    [Fact]
    public void WithNothingConnectedNothingOpens()
    {
        Assert.Equal(new ResolvedMicrophone(null, ChosenIsMissing: false), MicrophoneChoice.Resolve([], null));
        Assert.Equal(new ResolvedMicrophone(null, ChosenIsMissing: true), MicrophoneChoice.Resolve([], Headset.Id));
    }
}
