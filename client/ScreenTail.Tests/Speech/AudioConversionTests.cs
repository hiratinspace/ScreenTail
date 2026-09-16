using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// ST-027. Whatever the capture device is configured for, the model wants 16 kHz mono 16-bit, and the
/// conversion has to be right: a channel count read wrong turns speech into a chipmunk, and a sample
/// rate read wrong turns it into a drone. Both transcribe into confident nonsense rather than into
/// nothing, which is the failure mode that would reach a customer's ticket.
///
/// In Core so it can be checked without a sound card, which is the only way this gets checked at all
/// from a Mac.
/// </summary>
public sealed class AudioConversionTests
{
    [Fact]
    public void AlreadyTheRightFormatIsCopiedThrough()
    {
        var pcm = Pcm16([100, -100, 3000, -3000]);

        var converted = AudioConversion.ToMono16k(pcm, new AudioFormat(16_000, 1, 16, false));

        Assert.Equal([100, -100, 3000, -3000], converted.ToArray());
    }

    [Fact]
    public void StereoIsAveragedRatherThanHavingAChannelDropped()
    {
        // Dropping a channel loses a technician who happens to be speaking into one side of a headset.
        var pcm = Pcm16([1000, 2000, -1000, -3000]);

        var converted = AudioConversion.ToMono16k(pcm, new AudioFormat(16_000, 2, 16, false));

        Assert.Equal([1500, -2000], converted.ToArray());
    }

    [Fact]
    public void FortyEightKilohertzBecomesSixteen()
    {
        var pcm = Pcm16([.. Enumerable.Range(0, 480).Select(i => (short)(i * 10))]);

        var converted = AudioConversion.ToMono16k(pcm, new AudioFormat(48_000, 1, 16, false));

        Assert.Equal(160, converted.Length);

        // Roughly every third sample: the shape survives, which is what the model reads.
        Assert.InRange(converted.Span[80], 2300, 2500);
    }

    [Fact]
    public void ThirtyTwoBitFloatBecomesSixteenBitAtFullScale()
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 1.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), -1.0f);

        var converted = AudioConversion.ToMono16k(bytes, new AudioFormat(16_000, 1, 32, true));

        Assert.Equal(short.MaxValue, converted.Span[0]);
        Assert.Equal(short.MinValue, converted.Span[1]);
    }

    [Fact]
    public void AFloatOverOneIsClampedRatherThanWrappingRound()
    {
        // A device with gain applied can hand back samples outside [-1, 1]. Wrapping would turn the
        // loudest moment of a sentence into its opposite, which is audible as a click and transcribes
        // as a word boundary that was not there.
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 4.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), -4.0f);

        var converted = AudioConversion.ToMono16k(bytes, new AudioFormat(16_000, 1, 32, true));

        Assert.Equal(short.MaxValue, converted.Span[0]);
        Assert.Equal(short.MinValue, converted.Span[1]);
    }

    [Fact]
    public void APartialFrameAtTheEndIsIgnoredRatherThanRead()
    {
        // WASAPI hands over whole frames, but a truncated read would otherwise be interpreted as a
        // sample built from one byte of one sample and one of the next.
        var pcm = new byte[] { 0x10, 0x20, 0x30 };

        var converted = AudioConversion.ToMono16k(pcm, new AudioFormat(16_000, 1, 16, false));

        Assert.Single(converted.ToArray());
    }

    [Fact]
    public void AFormatWeDoNotUnderstandIsSilenceRatherThanNoise()
    {
        // 24-bit packed, which no shared-mode capture endpoint uses. Returning nothing loses narration;
        // guessing would feed the model something that is not audio at all.
        Assert.True(AudioConversion.ToMono16k(new byte[9], new AudioFormat(16_000, 1, 24, false)).IsEmpty);
    }

    [Fact]
    public void NoSamplesIsNoSamples()
    {
        Assert.True(AudioConversion.ToMono16k([], new AudioFormat(48_000, 2, 32, true)).IsEmpty);
    }

    private static byte[] Pcm16(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), samples[i]);
        }

        return bytes;
    }
}
