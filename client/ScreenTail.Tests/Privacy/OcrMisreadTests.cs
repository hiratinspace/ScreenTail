using ScreenTail.Core.Privacy;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// Secrets as the Windows OCR engine read them on the hosted runner's probe (ST-042, after #275,
/// OcrScaledSecretTests in the Windows suite), not as they were drawn: the prefix's underscore read as
/// a space or as nothing, the l of "live" read as 1, a colon read as a full stop. Every string here is
/// one the probe printed. And what the rules for them must leave alone.
/// </summary>
public sealed class OcrMisreadTests
{
    private const int Height = 24;

    private static readonly RedactionEngine Engine = new();

    /// <summary>The engine as it was before these rules, to show each test fails without them.</summary>
    private static readonly RedactionEngine Before = new(null, (text, policy, incomplete) => PatternLibrary.Find(text, policy, incomplete, MisreadRules.None));

    [Theory]
    [InlineData("token: ghp UF08Ck081n7dTVlviUuvpxdK4qCLWA03Qsmi")]
    [InlineData("token: ghp 4WdRauEquyaZNaLkUr37eJ6RRKsfAhJTx1yP")]
    [InlineData("token: ghp_yxu 1 ZiQPbozBtyFrMiY8k4SyGiFKNHtZdkvvu")]
    [InlineData("key sk_1ive_pqg1DDViD1kZEN1tDQ9WNy6ir")]
    [InlineData("key sk live_pqglDDViDlkZENltDQ9MVNy6ir")]
    [InlineData("key sk_liveJ4NoZPCULsha9cr2LyFMlDpNKy")]
    public void AKeyWithItsPrefixMisreadIsMasked(string text)
    {
        var scrubbed = Engine.ScrubText(text).Text;

        Assert.Contains("[SECRET]", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("[SECRET]", Before.ScrubText(text).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8KAzOOKOWQcGytQX")]
    [InlineData("ha9cr2LyFMlDpNKy")]
    [InlineData("--key sk_1ive_S6uaMAQCip")]
    [InlineData("token: ghp_oNPOmOkyKT1 L505DoyOj6MWlXltf6F3jZSYV")]
    public void ARandomRunIsMaskedWhateverIntroducesIt(string text)
    {
        Assert.Contains("[SECRET]", Engine.ScrubText(text).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[SECRET]", Before.ScrubText(text).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("the printer queue was cleared after a restart and the spooler service stopped again on Monday")]
    [InlineData("var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId)); Int32.TryParse(value, out var orderCount);")]
    [InlineData("private readonly IInvoiceDeviceRepository _ticketRepository; GetUserByIdAsync(cancellationToken)")]
    [InlineData("Reception-PC04   10.0.12.44   2026-11-03 14:22   Online   3f2a9c1e-77b4-4c1a-9d2e-5b8e0f1a2c3d")]
    [InlineData("Office365ProPlusRetail DESKTOP-4F2K9QH KB5034441-x64-Windows11 C:\\Users\\jsmith\\AppData\\Local\\Temp\\Setup2026.log")]
    [InlineData("12,345.67   4821   99887766   1234.50   7")]
    public void TextThatIsNotRandomIsLeftAlone(string text)
    {
        Assert.Equal(text, Engine.ScrubText(text).Text);
    }

    [Theory]
    [InlineData("pass. Juniper8749Quiver", "pass. [REDACTED]")]
    [InlineData("pass Tangerine3327Wobble", "pass [REDACTED]")]
    [InlineData("pass, Marigold7975Quiver", "pass, [REDACTED]")]
    public void APasswordAfterACueWithItsColonMisreadIsMasked(string text, string expected)
    {
        Assert.Equal(expected, Engine.ScrubText(text).Text);
        Assert.Equal(text, Before.ScrubText(text).Text);
    }

    [Theory]
    [InlineData("Tests pass on Windows 11 and fail on 10")]
    [InlineData("PASS FAIL PASS")]
    [InlineData("pass the test page to the front desk")]
    [InlineData("pass it on to tier 2")]
    [InlineData("pass 4 to 6 of the rollout")]
    public void PassAsAWordIsLeftAlone(string text)
    {
        Assert.Equal(text, Engine.ScrubText(text).Text);
    }

    /// <summary>
    /// The wrapped key as the probe read it at 150%, Consolas 11pt: the prefix's l read as 1, cut by the
    /// row's end, its rest at the start of the row below. Both pieces are masked in the picture.
    /// </summary>
    [Theory]
    [InlineData("sk_1ive_pqg1DDViD1", "kZEN1tDQ9WNy6ir")]
    [InlineData("sk_liveJ4NoZPCULs", "ha9cr2LyFMlDpNKy")]
    public void AWrappedKeyWithItsPrefixMisreadIsMaskedOnBothRows(string first, string second)
    {
        OcrWord[] words =
        [
            new("deploy", 24, 100, 70, Height),
            new("--key", 104, 100, 60, Height),
            new(first, 174, 100, 230, Height),
            new(second, 24, 130, 190, Height),
            new("--dry-run", 224, 130, 100, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.True(Covered(redaction, words[2]), "the first row's piece is left unpainted");
        Assert.True(Covered(redaction, words[3]), "the second row's piece is left unpainted");
        Assert.DoesNotContain(first, redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(second, redaction.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wrapped key with its first piece read as three words, "sk live DZSw4HaEhh", as the probe read
    /// it at 125% in Segoe UI 11pt on 17 pages of 20.
    /// </summary>
    [Fact]
    public void AWrappedKeyReadAsThreeWordsIsMaskedOnBothRows()
    {
        OcrWord[] words =
        [
            new("--key", 24, 100, 60, Height),
            new("sk", 94, 100, 24, Height),
            new("live", 128, 100, 44, Height),
            new("DZSw4HaEhh", 182, 100, 130, Height),
            new("8KAzOOKOWQcGytQX", 24, 130, 200, Height),
            new("--dry-run", 234, 130, 70, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.All(words[1..5], w => Assert.True(Covered(redaction, w), $"a piece of the key at x {w.X}, y {w.Y} is left unpainted"));
        Assert.False(Covered(redaction, words[5]), "the row below lost a word that is not the key");
    }

    private static bool Covered(FrameRedaction redaction, OcrWord word) =>
        redaction.Regions.Any(r =>
            r.X <= word.X && r.Y <= word.Y
            && r.X + r.Width >= word.X + word.Width
            && r.Y + r.Height >= word.Y + word.Height);
}
