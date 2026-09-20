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

    [Theory]
    [InlineData("4111 1111 1111 1111 123", "[CARD] 123")] // a CVV
    [InlineData("4111 1111 1111 1111 90210", "[CARD] 90210")] // a zip code
    [InlineData("Order 12345 4111 1111 1111 1111", "Order 12345 [CARD]")] // a number in front
    [InlineData("exp 0927 4111 1111 1111 1111 123", "exp 0927 [CARD] 123")] // both sides, as a payment form reads
    [InlineData("5500-0000-0000-0004 737", "[CARD] 737")]
    [InlineData("3782 822463 10005 1234", "[CARD] 1234")] // American Express groups 4-6-5
    public void ACardNumberIsStillACardNumberWithAnotherNumberBesideIt(string text, string expected)
    {
        // Found in the 2026-09-19 review, and the ordinary case rather than a contrived one. OCR joins a
        // page's words with single spaces, so whatever number sits beside the card on a payment form —
        // the CVV, the expiry, the zip code, an order number — arrived in the same run of digit groups.
        // The pattern took the whole run as one candidate, the checksum failed on the whole run, and
        // nothing looked inside it. The card was stored, shown in Review and sent to the summarizer with
        // its CVV next to it.
        Assert.Equal(expected, Engine.ScrubText(text).Text);
    }

    [Fact]
    public void ACardNumberInsideALongRowOfNumbersDoesNotSurvive()
    {
        // A table row. Which neighbours get masked with it is not the point and is allowed to vary;
        // that no digit of the card is left is the point.
        var scrubbed = Engine.ScrubText("1001 2002 3003 4111 1111 1111 1111 4004 5005").Text;

        Assert.DoesNotContain("4111", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("1111", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Invoice 1001 2002 3003 4004 5005 6006")] // 1001 2002 3003 4004 passes the checksum by chance
    [InlineData("Asset 100 200 300 400 500")]
    [InlineData("4111 1111 1111 1112 123")] // one digit off a real card, beside a CVV
    public void LookingInsideARunDoesNotStartMaskingTablesOfOrdinaryNumbers(string text)
    {
        // The price of looking inside a run is that one window in ten passes the checksum by accident.
        // Windows are therefore only considered when they begin with a digit a payment card can begin
        // with (2 to 6: Mastercard, American Express, Visa, Discover), which the first case here does
        // not. Without that, a technician's screenshot of any spreadsheet comes back full of holes, and
        // a redactor that cries wolf is one that gets switched off.
        Assert.Equal(text, Engine.ScrubText(text).Text);
    }

    [Theory]
    [InlineData("(unclosed")]
    [InlineData("a{2,1}")]
    [InlineData("[z-a]")]
    [InlineData("*")]
    public void ATenantPatternThatIsNotAPatternIsSkippedRatherThanFatal(string bad)
    {
        // An administrator types their own redaction patterns (ST-047), and a typed regex is a regex
        // with a typo in it sooner or later. Building it threw ArgumentException on the first frame of
        // every session, which took the redaction worker's loop with it: no frame was ever made
        // readable again, and the tenant that configured the pattern was the one that lost capture
        // (2026-09-19 review).
        //
        // The frame still has to be scanned by everything else, and the scan has to report itself as
        // incomplete — a pattern that did not run is text that was not searched, and ADR-0004 says such
        // a frame is discarded rather than stored.
        var engine = new RedactionEngine(new RedactionPolicy { CustomPatterns = [bad] });

        var scrubbed = engine.ScrubText("the card is 4111 1111 1111 1111");

        Assert.False(scrubbed.Complete);
        Assert.DoesNotContain("4111", scrubbed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AGoodTenantPatternStillRuns()
    {
        var engine = new RedactionEngine(new RedactionPolicy { CustomPatterns = ["ACME-[0-9]{4}"] });

        var scrubbed = engine.ScrubText("asset ACME-1234 was replaced");

        Assert.True(scrubbed.Complete);
        Assert.DoesNotContain("ACME-1234", scrubbed.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("P@ss.word1!")]
    [InlineData("tomato99")]
    [InlineData("Winter-2026!")]
    [InlineData("hunter2;extra")]
    public void TheStoredTextHidesAsMuchAsThePictureDoes(string secret)
    {
        // 2026-09-19 review. The image masks every word a match touches — "a partly painted number is
        // still readable" — and the stored text masked only the matched span. So a value the pattern
        // stopped short of kept its tail in ocr_text: painted out of the picture, and sent to the model
        // and shown in Review in writing.
        //
        // "P@ss.word1!" is the clearest case: the password pattern's value ends at the full stop, so the
        // picture lost the whole word and the text kept ".word1!".
        var words = Words("the", "password", "is", secret);

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain(secret, redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret[^4..], redaction.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWordsAroundASecretAreLeftAlone()
    {
        // The control. Widening to whole words must not swallow the sentence: a note that says
        // "[REDACTED]" where the technician wrote three useful words is a note nobody can read.
        var redaction = Engine.RedactFrame(Words("the", "password", "is", "P@ss.word1!", "on", "the", "reception", "workstation"));

        Assert.Contains("reception workstation", redaction.Text, StringComparison.Ordinal);
        Assert.Contains("the password is", redaction.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretSpreadAcrossWordsTakesAllOfThemWithIt()
    {
        // A card number is four OCR words. Masking to the match alone would leave whichever groups the
        // pattern's edges fell short of.
        var redaction = Engine.RedactFrame(Words("card", "4111", "1111", "1111", "1111", "expires", "09/27"));

        Assert.DoesNotContain("4111", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("1111", redaction.Text, StringComparison.Ordinal);
        Assert.Contains("expires", redaction.Text, StringComparison.Ordinal);
    }

    /// <summary>Laid out left to right, as a line of OCR on one row.</summary>
    private static OcrWord[] Words(params string[] text)
    {
        var words = new OcrWord[text.Length];
        var x = 0;
        for (var i = 0; i < text.Length; i++)
        {
            words[i] = new OcrWord(text[i], x, 0, text[i].Length * 8, 14);
            x += (text[i].Length * 8) + 8;
        }

        return words;
    }
}
