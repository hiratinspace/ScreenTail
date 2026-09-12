namespace ScreenTail.Spike.Core.Tests;

public class MarkerPixelProbeTests
{
    private static byte[] Pixels(int count, byte r, byte g, byte b)
    {
        var buffer = new byte[count * 4];
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = b;
            buffer[i + 1] = g;
            buffer[i + 2] = r;
            buffer[i + 3] = 0xFF;
        }

        return buffer;
    }

    [Fact]
    public void AllMarker_IsOne()
    {
        Assert.Equal(1.0, MarkerPixelProbe.MatchFraction(Pixels(100, 0xFF, 0x00, 0xFF), 0xFF, 0x00, 0xFF));
    }

    [Fact]
    public void NoMarker_IsZero()
    {
        Assert.Equal(0.0, MarkerPixelProbe.MatchFraction(Pixels(100, 0x20, 0x30, 0x40), 0xFF, 0x00, 0xFF));
    }

    [Fact]
    public void HalfMarker_IsHalf()
    {
        var buffer = Pixels(50, 0xFF, 0x00, 0xFF).Concat(Pixels(50, 0, 0, 0)).ToArray();

        Assert.Equal(0.5, MarkerPixelProbe.MatchFraction(buffer, 0xFF, 0x00, 0xFF));
    }

    [Fact]
    public void JpegLikeDrift_WithinTolerance_Matches()
    {
        Assert.Equal(1.0, MarkerPixelProbe.MatchFraction(Pixels(10, 0xF0, 0x10, 0xF4), 0xFF, 0x00, 0xFF, tolerance: 24));
        Assert.Equal(0.0, MarkerPixelProbe.MatchFraction(Pixels(10, 0xC0, 0x10, 0xF4), 0xFF, 0x00, 0xFF, tolerance: 24));
    }

    [Fact]
    public void EmptyBuffer_IsZero()
    {
        Assert.Equal(0.0, MarkerPixelProbe.MatchFraction([], 0xFF, 0x00, 0xFF));
    }

    [Fact]
    public void NonBgraLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => MarkerPixelProbe.MatchFraction(new byte[5], 0xFF, 0x00, 0xFF));
    }
}
