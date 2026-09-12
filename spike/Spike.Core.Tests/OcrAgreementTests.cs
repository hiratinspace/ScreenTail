namespace ScreenTail.Spike.Core.Tests;

public class OcrAgreementTests
{
    [Fact]
    public void IdenticalText_IsFullRecall()
    {
        Assert.Equal(1.0, OcrAgreement.WordRecall("Service status: Stopped", "service STATUS stopped"));
    }

    [Fact]
    public void MissingWords_LowerRecall()
    {
        Assert.Equal(0.5, OcrAgreement.WordRecall("Startup type Automatic Manual", "startup type"));
    }

    [Fact]
    public void RepeatedTruthWords_MustEachBeMatched()
    {
        Assert.Equal(0.5, OcrAgreement.WordRecall("ok ok", "ok"));
    }

    [Fact]
    public void Punctuation_IsIgnored()
    {
        Assert.Equal(1.0, OcrAgreement.WordRecall("IPv4 Address . . . : 10.0.12.44", "ipv4 address 10 0 12 44"));
    }

    [Fact]
    public void EmptyTruth_IsFullRecall()
    {
        Assert.Equal(1.0, OcrAgreement.WordRecall("", "anything"));
    }
}
