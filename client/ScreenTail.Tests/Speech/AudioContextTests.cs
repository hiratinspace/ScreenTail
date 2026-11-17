using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// ST-031: the encoder's context sized to a segment is never smaller than the segment and its margin, and
/// sizing is off unless the service is told to turn it on.
/// </summary>
public sealed class AudioContextTests
{
    [Theory]
    [InlineData(0.5, 512)]
    [InlineData(2.2, 512)]
    [InlineData(9.2, 512)]
    [InlineData(9.3, 1024)]
    [InlineData(15.8, 1024)]
    [InlineData(19.4, 1024)]
    [InlineData(19.5, AudioContext.Full)]
    [InlineData(20.0, AudioContext.Full)]
    [InlineData(29.0, AudioContext.Full)]
    public void TheContextIsTheSmallestSizeThatHoldsTheSegmentAndItsMargin(double seconds, int expected)
    {
        Assert.Equal(expected, AudioContext.For(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void NoSegmentIsGivenLessContextThanItsAudioAndMarginNeed()
    {
        for (var ms = 0; ms <= 30_000; ms += 10)
        {
            var audio = TimeSpan.FromMilliseconds(ms);
            var context = AudioContext.For(audio);
            Assert.True(
                context == AudioContext.Full || context >= (audio + AudioContext.Margin).TotalSeconds * AudioContext.PerSecond,
                $"{ms} ms of audio was given a context of {context}");
        }
    }

    [Fact]
    public void TheLongestSegmentTheGateCutsGetsTheFullWindow()
    {
        Assert.Equal(AudioContext.Full, AudioContext.For(new SpeechGateOptions().MaximumSegment));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes please", false)]
    [InlineData("1", true)]
    [InlineData(" true ", true)]
    [InlineData("ON", true)]
    public void SizingIsOffUnlessTheVariableTurnsItOn(string? value, bool expected)
    {
        Assert.Equal(expected, AudioContext.Enabled(name => name == AudioContext.Variable ? value : null));
    }
}
