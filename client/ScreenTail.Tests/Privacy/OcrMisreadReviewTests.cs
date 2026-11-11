using ScreenTail.Core.Privacy;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// What the review of PR #278 found the misread rules let through (ST-042): an AWS access key ID with
/// its I misread, base64 whose symbols did not count toward its length, a key the OCR engine split in two
/// with a space, and "pass:" read with its colon as a dash, a bar or a letter, or written "pw:". Each
/// is shown missed by the rules as they were (<see cref="Round1"/>). Every string is synthetic.
/// </summary>
public sealed class OcrMisreadReviewTests
{
    private static readonly RedactionEngine Engine = new();

    /// <summary>The rules for misreads as they were before the review.</summary>
    private static readonly RedactionEngine Round1 = new(null, (text, policy, incomplete) =>
        PatternLibrary.Find(text, policy, incomplete, MisreadRules.Prefixes | MisreadRules.RandomRuns | MisreadRules.BarePass));

    [Theory]
    [InlineData("key AK1A2E0A8F3B244C9986 ok")]
    [InlineData("AKlAQ7MZ4PX2W9R3T6Y8")]
    [InlineData("AK|AQ7MZ4PX2W9R3T6Y8")]
    [InlineData("ASIA5HV2N7K4Q9W3E6R1")]
    [InlineData("AS1A5HV2N7K4Q9W3E6R1")]
    [InlineData("id AKIA Q7MZ4PX2W9R3T6Y8")]
    public void AnAwsKeyIdWithItsPrefixMisreadIsMasked(string text)
    {
        Assert.Contains("[SECRET]", Engine.ScrubText(text).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[SECRET]", Round1.ScrubText(text).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ASIA PACIFIC REGION OFFICE")]
    [InlineData("ASIAN MARKETS REPORT 2026")]
    [InlineData("AKIA")]
    public void WordsThatStartLikeAnAwsKeyAreLeftAlone(string text)
    {
        Assert.Equal(text, Engine.ScrubText(text).Text);
    }

    [Theory]
    [InlineData("Xv9+Lp2/Rm8s+Tz4")]
    [InlineData("k7P/q2Z+x9M/b4N==")]
    [InlineData("Q2w/E4r+T6y/U8i=")]
    public void Base64WhoseSymbolsMakeItsLengthIsARandomRun(string key)
    {
        Assert.Equal("[SECRET]", Engine.ScrubText(key).Text);
        Assert.Equal(key, Round1.ScrubText(key).Text);
    }

    [Theory]
    [InlineData("token x9Kq2Lm7 Pz4Rt8Vw saved", "token [SECRET] saved")]
    [InlineData("k3Y8pQ1vZ6 mT0wR5", "[SECRET]")]
    [InlineData("key A8kR2pX7mQ4zL9w t3Vb6Nc1Hy5Jd0s", "key [SECRET]")]
    public void AKeyTheEngineSplitWithASpaceIsOneRandomRun(string text, string scrubbed)
    {
        Assert.Equal(scrubbed, Engine.ScrubText(text).Text);
        Assert.DoesNotContain("[SECRET]", Round1.ScrubText(text).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Office365 ProPlusRetail")]
    [InlineData("Int32 TryParse(value)")]
    [InlineData("Accounts-PC23 10.0.4.12 Online 5b8e0f1a-2c3d-4e5f-8a9b-0c1d2e3f4a5b")]
    [InlineData("the printer queue was cleared after a restart")]
    [InlineData("SHA256 HashData")]
    public void TwoWordsThatTogetherDoNotLookRandomAreLeftAlone(string text)
    {
        Assert.Equal(text, Engine.ScrubText(text).Text);
    }

    [Theory]
    [InlineData("pass - Juniper8749Quiver", "pass - [REDACTED]")]
    [InlineData("pass| Tangerine3327Wobble", "pass| [REDACTED]")]
    [InlineData("passi Juniper8749Quiver", "passi [REDACTED]")]
    [InlineData("pw: Tangerine3327Wobble", "pw: [REDACTED]")]
    public void APasswordAfterACueMisreadOrShortIsMasked(string text, string scrubbed)
    {
        Assert.Equal(scrubbed, Engine.ScrubText(text).Text);
        Assert.Equal(text, Round1.ScrubText(text).Text);
    }

    private const string Base62 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// What share of random strings of each kind the rules take, the review's measurement made a floor:
    /// it found base64 of 16 taken 58% of the time and a base62 key of 16 to 31 split by one space never.
    /// Hexadecimal, small letters and digits, capitals and digits, and base32 have no rule of their own
    /// without a cue; they are measured here and documented as gaps (docs/security/data-flow.md).
    /// </summary>
    [Fact]
    public void RandomStringsOfEachKindAreTakenAsOftenAsMeasured()
    {
        var random = new Random(278);
        string Of(string alphabet, int length) => string.Concat(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]));
        double Share(Func<string> make) => Enumerable.Range(0, 2000).Count(_ => Engine.ScrubText(make()).Text.Replace("[SECRET]", string.Empty, StringComparison.Ordinal).Trim().Length == 0) / 2000.0;

        var base64 = Share(() => Of(Base62 + "+/", 16));
        var split = Share(() =>
        {
            var key = Of(Base62, random.Next(16, 32));
            var at = random.Next(4, key.Length - 3);
            return key[..at] + " " + key[at..];
        });
        var whole = Share(() => Of(Base62, random.Next(16, 32)));
        var aws = Share(() => "AK" + "1l|"[random.Next(3)] + "A" + Of("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", 16));
        var hex = Share(() => Of("0123456789abcdef", random.Next(16, 41)));
        var small = Share(() => Of("abcdefghijklmnopqrstuvwxyz0123456789", random.Next(16, 41)));
        var capitals = Share(() => Of("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", random.Next(16, 41)));
        var base32 = Share(() => Of("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", random.Next(16, 41)));

        // Measured (seed 278, 2,000 each): base64 of 16 87.6% (51.8% before the review), a split base62
        // key 88.1% (0%), a whole one 96.0%, a misread AWS key ID 100% (0%); hexadecimal, small letters
        // and digits, capitals and digits and base32 0%. Floors are the measurement less a tenth.
        var measured = $"base64 16 {base64:P1}, split base62 {split:P1}, whole base62 {whole:P1}, aws {aws:P1}, hex {hex:P1}, small {small:P1}, capitals {capitals:P1}, base32 {base32:P1}";
        Assert.True(base64 >= 0.78 && split >= 0.79 && whole >= 0.86 && aws >= 0.9, measured);
    }

    [Theory]
    [InlineData("pass - the test")]
    [InlineData("pass | FAIL")]
    [InlineData("pw: none")]
    [InlineData("passi lunghi")]
    [InlineData("the pw field is empty")]
    public void TheShortAndMisreadCuesLeaveOrdinaryTextAlone(string text)
    {
        Assert.Equal(text, Engine.ScrubText(text).Text);
    }
}
