using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// The gaps an adversarial review of ST-042 measured, each one a secret the engine claimed to catch and
/// did not. Every case here failed before the fix.
/// </summary>
public sealed class PatternLibraryGapTests
{
    private static readonly RedactionEngine Engine = new();

    [Fact]
    public void APrivateKeyIsMaskedAndNotJustItsHeader()
    {
        // The worst of them. The pattern matched the header line only, so the body — the actual key —
        // was stored, shown in Review, put in the bundle and sent to the summarizer.
        const string Key = """
            -----BEGIN RSA PRIVATE KEY-----
            MIIEpAIBAAKCAQEA3Tz2mr7SZiAMfQyuvBjM9OiJjRazXBZ1BjP5CE/Wm/Rr500P
            RK+Lh9x5eJPo5CAZ3/ANBE0sTK0ZsDGMak2m1g7oruI3dY3VHqIxFTz0Ta1d+NAj
            -----END RSA PRIVATE KEY-----
            """;

        var scrubbed = Engine.ScrubText(Key);

        Assert.DoesNotContain("MIIEpAIBAAKCAQEA", scrubbed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("RK+Lh9x5eJPo5CAZ", scrubbed.Text, StringComparison.Ordinal);
        Assert.True(scrubbed.Counts.ContainsKey(MaskKind.ApiKey));
    }

    [Fact]
    public void AnUnterminatedPrivateKeyIsStillMasked()
    {
        // OCR of a scrolled terminal gives the header and part of the body and no footer. Requiring the
        // END line would have left everything visible on screen unmasked.
        const string Partial = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAAB";

        Assert.DoesNotContain("b3BlbnNzaC1rZXktdjEA", Engine.ScrubText(Partial).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"api_key\": \"A1b2C3d4E5f6G7h8I9j0K1l2M3n4\"")]
    [InlineData("Secret Key: A1b2C3d4E5f6G7h8I9j0K1l2M3n4")]
    [InlineData("Access Token = A1b2C3d4E5f6G7h8I9j0K1l2M3n4")]
    [InlineData("API Key:    A1b2C3d4E5f6G7h8I9j0K1l2M3n4")]
    public void AKeyIsMaskedHoweverItIsIntroduced(string line)
    {
        // A quote between the cue and the value, a two-word cue, or a few more spaces than the pattern
        // allowed — all of them are how a real config file or Postman screenshot looks.
        Assert.DoesNotContain("A1b2C3d4E5f6G7h8I9j0K1l2M3n4", Engine.ScrubText(line).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnformattedSsnIsMaskedWhereSomethingSaysItIsOne()
    {
        // Plenty of systems render the field without separators.
        Assert.DoesNotContain("123456789", Engine.ScrubText("SSN: 123456789").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", Engine.ScrubText("Social Security Number 123456789").Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Order 123456789 shipped")]
    [InlineData("Part number 987654321")]
    [InlineData("Invoice 100200300 is overdue")]
    public void NineDigitsOnTheirOwnAreNotAnSsn(string line)
    {
        // The other half, and the reason the cue is required. ST-042 budgets 2% false positives, and
        // masking every nine-digit run would spend that many times over on one screen of order history.
        Assert.Equal(line, Engine.ScrubText(line).Text);
    }

    [Fact]
    public void ATenantPatternThatTimesOutMeansTheTextWasNotSearched()
    {
        // The INV-1 hole. A skipped pattern was reported as a completed scan, so the frame was stored —
        // with everything that pattern existed to catch still on it.
        var policy = RedactionPolicy.Default with { CustomPatterns = ["^(a+)+$"] };

        // Long enough that the catastrophic pattern cannot finish inside RedactionPolicy.MatchTimeout.
        var result = new RedactionEngine(policy).ScrubText(new string('a', 40_000) + "!");

        Assert.False(result.Complete, "a pattern that could not finish was reported as a completed scan");
    }

    [Fact]
    public void ATenantPatternMatchingNothingStillSearchesTheWholeText()
    {
        // A zero-length match used to end the scan where it occurred, leaving the rest unsearched and
        // reported clean. The card after it has to still be found.
        var policy = RedactionPolicy.Default with { CustomPatterns = [@"(?=x)"] };

        var result = new RedactionEngine(policy).ScrubText("x marks the spot, card 4532 7891 2345 6789");

        Assert.True(result.Complete);
        Assert.DoesNotContain("4532", result.Text, StringComparison.Ordinal);
    }
}
