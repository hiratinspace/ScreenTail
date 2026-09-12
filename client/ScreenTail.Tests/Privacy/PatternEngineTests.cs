using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>ST-042: the pattern library, the text scrub and the boxes that get painted over in a frame.</summary>
public sealed class PatternEngineTests
{
    private static readonly RedactionEngine Engine = new();

    [Theory]
    // Test numbers from the card networks' published set — all Luhn-valid, none real.
    [InlineData("4111111111111111")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("5500005555555559")]
    [InlineData("378282246310005")]
    public void LuhnValidCardsBecomeCard(string number)
    {
        var result = Engine.ScrubText($"Charged {number} on file");

        Assert.Equal("Charged [CARD] on file", result.Text);
        Assert.Equal(1, result.Counts[MaskKind.Card]);
    }

    [Theory]
    [InlineData("4111111111111112")] // one digit off: fails Luhn
    [InlineData("1234567890123456")]
    [InlineData("Order 20260912001234 shipped")] // an order number, not a card
    public void NumbersThatFailLuhnAreLeftAlone(string text)
    {
        var result = Engine.ScrubText(text);

        Assert.Equal(text, result.Text);
        Assert.False(result.Changed);
    }

    [Fact]
    public void TranscriptPasswordValueIsRedactedAndTheCueKept()
    {
        var result = Engine.ScrubText("the password is Winter2026");

        Assert.Equal("the password is [REDACTED]", result.Text);
        Assert.Equal(1, result.Counts[MaskKind.Password]);
    }

    [Theory]
    [InlineData("pwd: hunter2", "pwd: [REDACTED]")]
    [InlineData("passphrase = correct-horse", "passphrase = [REDACTED]")]
    [InlineData("I set the PIN to 4821 for her", "I set the PIN to [REDACTED] for her")]
    [InlineData("their password was Sp00lerFix!", "their password was [REDACTED]!")]
    public void PasswordCuesInSpeechAndOnScreen(string text, string expected) =>
        Assert.Equal(expected, Engine.ScrubText(text).Text);

    [Theory]
    [InlineData("the password is incorrect")]
    [InlineData("her password expired last night")]
    [InlineData("I walked him through a password reset")]
    public void TalkingAboutPasswordsIsNotASecret(string text) =>
        Assert.Equal(text, Engine.ScrubText(text).Text);

    [Fact]
    public void SsnIsMasked()
    {
        var result = Engine.ScrubText("SSN 123-45-6789 on the form");

        Assert.Equal("SSN [SSN] on the form", result.Text);
        Assert.Equal(1, result.Counts[MaskKind.Ssn]);
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("xoxb-1234567890-abcdefghij")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz012345")]
    public void CredentialShapesBecomeSecret(string credential)
    {
        var result = Engine.ScrubText($"key {credential} here");

        Assert.Equal("key [SECRET] here", result.Text);
        Assert.Equal(1, result.Counts[MaskKind.ApiKey]);
    }

    [Fact]
    public void ApiKeyCueMasksTheValueThatFollows() =>
        Assert.Equal("api_key=[SECRET]", Engine.ScrubText("api_key=A1b2C3d4E5f6G7h8I9j0K1l2M3n4").Text);

    [Fact]
    public void EmailsAreKeptUnlessTheTenantAsksOtherwise()
    {
        const string Text = "emailed sarah.jones@acmedental.com about it";

        Assert.Equal(Text, Engine.ScrubText(Text).Text);
        Assert.Equal("emailed [EMAIL] about it", new RedactionEngine(RedactionPolicy.Strict).ScrubText(Text).Text);
    }

    [Fact]
    public void TurningAPatternOffLeavesItsMatchesAlone()
    {
        const string Text = "card 4111111111111111 and SSN 123-45-6789";
        var engine = new RedactionEngine(new RedactionPolicy { Cards = false });

        var result = engine.ScrubText(Text);

        Assert.Equal("card 4111111111111111 and SSN [SSN]", result.Text);
        Assert.False(result.Counts.ContainsKey(MaskKind.Card));
    }

    [Fact]
    public void TenantPatternsAreAppliedAndValidated()
    {
        var policy = new RedactionPolicy { CustomPatterns = ["ACME-\\d{6}", "(?i)internal use only"] };
        var engine = new RedactionEngine(policy);

        var result = engine.ScrubText("badge ACME-481203, Internal Use Only");

        Assert.Equal("badge [REDACTED], [REDACTED]", result.Text);
        Assert.Equal(2, result.Counts[MaskKind.CustomPattern]);
        Assert.Null(RedactionPolicy.ValidateCustomPattern("ACME-\\d{6}"));
        Assert.NotNull(RedactionPolicy.ValidateCustomPattern("ACME-(\\d{6}"));
        Assert.NotNull(RedactionPolicy.ValidateCustomPattern(".*"));
        Assert.NotNull(RedactionPolicy.ValidateCustomPattern("  "));
    }

    [Fact]
    public void ARunawayTenantPatternCannotStallCapture()
    {
        // Classic catastrophic backtracking. The match timeout means we lose this pattern, not the session.
        var engine = new RedactionEngine(new RedactionPolicy { CustomPatterns = ["(a+)+$"] });
        var text = new string('a', 40) + "!";

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = engine.ScrubText(text);
        clock.Stop();

        Assert.Equal(text, result.Text);
        Assert.True(clock.ElapsedMilliseconds < 1_000, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void OverlappingMatchesAreResolvedOnce()
    {
        // The card is also a run of digits the custom pattern wants; only one replacement may land.
        var engine = new RedactionEngine(new RedactionPolicy { CustomPatterns = ["\\d{4}"] });

        Assert.Equal("pay [CARD] now", engine.ScrubText("pay 4111111111111111 now").Text);
    }

    [Fact]
    public void FrameMasksEveryWordTheMatchTouches()
    {
        // A card split across four OCR words: the region must cover all four, or part of it stays readable.
        IReadOnlyList<OcrWord> words =
        [
            new("Card:", 10, 100, 50, 20),
            new("4111", 70, 100, 45, 20),
            new("1111", 120, 100, 45, 20),
            new("1111", 170, 100, 45, 20),
            new("1111", 220, 100, 45, 20),
            new("expires", 275, 100, 70, 20),
        ];

        var redaction = new RedactionEngine().RedactFrame(words);

        var region = Assert.Single(redaction.Regions);
        Assert.Equal(MaskKind.Card, region.Kind);
        Assert.Equal(70, region.X);
        Assert.Equal(100, region.Y);
        Assert.Equal(195, region.Width); // 70 → 265, all four digit groups
        Assert.Equal(20, region.Height);
        Assert.Equal("Card: [CARD] expires", redaction.Text);
        Assert.Equal(1, redaction.Counts[MaskKind.Card]);
    }

    [Fact]
    public void AFrameWithNothingSensitiveGetsNoRegions()
    {
        IReadOnlyList<OcrWord> words = [new("Print", 0, 0, 40, 18), new("Spooler:", 45, 0, 60, 18), new("Stopped", 110, 0, 60, 18)];

        var redaction = new RedactionEngine().RedactFrame(words);

        Assert.Empty(redaction.Regions);
        Assert.Equal("Print Spooler: Stopped", redaction.Text);
        Assert.Empty(redaction.Counts);
    }

    [Fact]
    public void CountsAreTheOnlyThingSafeToLog()
    {
        var redaction = new RedactionEngine(RedactionPolicy.Strict).RedactFrame(
        [
            new("4111111111111111", 0, 0, 100, 20),
            new("123-45-6789", 110, 0, 80, 20),
            new("sarah@acme.com", 200, 0, 90, 20),
        ]);

        // INV-10: a diagnostics line may say "3 regions masked", never what they contained.
        Assert.Equal(3, redaction.Counts.Values.Sum());
        Assert.DoesNotContain("4111", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("6789", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sarah", redaction.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AScanThatCouldNotFinishIsReportedAsIncomplete()
    {
        // A detector that runs out of its match budget leaves text it never searched. Reporting "clean"
        // there would store a frame nobody checked, so the result says so and ST-041 purges the frame.
        var engine = new RedactionEngine(RedactionPolicy.Default, (_, _, onIncomplete) =>
        {
            onIncomplete();
            return [];
        });

        var result = engine.ScrubText("card 4111111111111111");

        Assert.False(result.Complete);
        Assert.False(result.Changed);
        Assert.False(engine.RedactFrame([new OcrWord("4111111111111111", 0, 0, 10, 10)]).Complete);
    }

    [Fact]
    public void ANormalScanIsComplete()
    {
        Assert.True(Engine.ScrubText("card 4111111111111111").Complete);
        Assert.True(Engine.RedactFrame([new OcrWord("hello", 0, 0, 10, 10)]).Complete);
    }

    [Theory]
    [InlineData("4111111111111111", true)] // 16 digits, Luhn-valid
    [InlineData("378282246310005", true)] // 15 digits (Amex)
    [InlineData("79927398713", false)] // Luhn-valid but only 11 digits: too short to be a card
    [InlineData("411111111111111111111", false)] // too long
    public void OnlyCardLengthLuhnValidNumbersAreMasked(string candidate, bool masked) =>
        Assert.Equal(masked, Engine.ScrubText($"value {candidate} end").Text.Contains("[CARD]", StringComparison.Ordinal));
}
