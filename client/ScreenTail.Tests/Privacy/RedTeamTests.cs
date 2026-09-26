using ScreenTail.Core.Privacy;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// ST-115, round one: adversarial inputs through the redaction engine at the text level — what OCR reads
/// off a screen and what the transcriber hears — with the default policy. Each row is a scenario from
/// <c>docs/security/redteam-01.md</c>: a secret that must not survive, or a sentence that must. The
/// frame-level pass over real screenshots on the VM harness is round two (ST-013).
/// </summary>
public sealed class RedTeamTests
{
    private static readonly RedactionEngine Engine = new();

    /// <summary>
    /// The must-mask corpus. The key-shaped rows are assembled from parts at run time, not written as
    /// literals: GitHub's push protection reads a committed SendGrid or Google key the way our engine
    /// does, and a test corpus that cannot be pushed is a corpus nobody runs. The parts join to the
    /// same shapes the engine has to catch.
    /// </summary>
    public static TheoryData<string, string, string> Secrets()
    {
        var data = new TheoryData<string, string, string>();
        void Row(string scenario, string text, string secret) => data.Add(scenario, text, secret);
        static string J(params string[] parts) => string.Concat(parts);

        // Social security numbers, written the ways people write them.
        Row("R01", "SSN 123.45.6789 on the form", "123.45.6789");
        Row("R02", "social security no: 123 45 6789", "123 45 6789");
        Row("R03", "SSN# 123456789", "123456789");
        Row("R04", "Social Security Number = 123-45-6789.", "123-45-6789");

        // Cards, the ways a payment screen shows them.
        Row("R05", "Card 4111.1111.1111.1111 exp 09/28", "4111.1111.1111.1111");
        Row("R06", "PAN: 4111111111111111 CVV 123", "4111111111111111");
        Row("R07", "Amex 3782 822463 10005 on file", "3782 822463 10005");
        Row("R08", "card no 5500-0055-5555-5559", "5500-0055-5555-5559");

        // Keys and tokens as they appear in consoles, config files and chat.
        var awsSecret = J("wJalrXUtnFEMI/K7MDENG/", "bPxRfiCYEXAMPLEKEY");
        Row("R09", "aws_secret_access_key = " + awsSecret, "wJalrXUtnFEMI");
        var githubPat = J("github_", "pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz");
        Row("R10", githubPat, "pat_11ABCDEFG");
        var stripe = J("sk_", "test_4eC39HqLyjWDarjtT1zd");
        Row("R11", "Secret key " + stripe, "4eC39HqLyjWDarjtT1zd");
        var azure = J("Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1", "OUzFT50uSRZ6IFsuFq2UVErCz4I6tq==");
        Row("R12", "DefaultEndpointsProtocol=https;AccountName=acme;AccountKey=" + azure + ";", "Eby8vdM02xNOcqFlqUwJ");
        Row("R13", "Server=db;User Id=sa;Password=Sup3r$ecret!;Encrypt=true", "Sup3r$ecret!");
        var oauth = J("ya29", ".a0AfH6SMBx1cQ5eXkTOmAgbKqRcvvZ5vGz2NGrLsq");
        Row("R14", "Authorization: Bearer " + oauth, "a0AfH6SMBx1cQ5");
        var webhook = J("https://hooks.slack.com/", "services/T0AB1CDEF/B0GH2IJKL/aBcDeFgHiJkLmNoPqRsTuVwX");
        Row("R15", "webhook " + webhook, "aBcDeFgHiJkLmNoPqRsTuVwX");
        Row("R16", "https://api.example.com/v1/export?token=9f8e7d6c5b4a39281706f5e4d3c2b1a0ffee", "9f8e7d6c5b4a39281706f5e4d3c2b1a0ffee");
        var google = J("AIza", "SyD-9tSrke72PouQMnMX-a7eZSW0jk");
        Row("R17", "GOOGLE_MAPS_KEY=" + google, "SyD-9tSrke72PouQMnMX");
        var sendgrid = J("SG.", "ngeVfQFYQlKU0ufo8x5d", ".", "TwL2iGABf9DHoTf09kqeF8tA");
        Row("R18", "SENDGRID_API_KEY=" + sendgrid, "ngeVfQFYQlKU0ufo8x5d");
        Row("R19", "-----BEGIN PGP PRIVATE KEY BLOCK-----\nlQOYBGV3d3cBCADH8Q5Kx7n4aPz0j9vQ2u1w3m5k7Jf9Zb2c4Xy6Vt8Lr0Nq1Hs3Dp5G\n-----END PGP PRIVATE KEY BLOCK-----", "lQOYBGV3d3cBCADH8Q5K");
        Row("R20", "-----BEGIN EC PRIVATE KEY-----\nMHQCAQEEIBkg1Y3z8m2p6q0r4t5u7v9w1x3y5z7a9b1c3d5e7f9g1h3j5k7oAoGCCqGSM49\n-----END EC PRIVATE KEY-----", "MHQCAQEEIBkg1Y3z8m2p");
        var openai = J("sk-", "proj-abcdefghijklmnopqrstuvwxyz0123456789");
        Row("R21", "export OPENAI_API_KEY=" + openai, "abcdefghijklmnopqrstuvwxyz0123456789");
        var npm = J("npm_", "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789");
        Row("R22", "npm token " + npm, "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789");

        // Passwords as people say and type them.
        Row("R23", "the wifi password is Winter 2026", "Winter 2026");
        Row("R24", "pass: hunter2", "hunter2");
        Row("R25", "temporary password Welcome1! then they change it", "Welcome1!");
        Row("R26", "PIN: 4821", "4821");
        Row("R27", "their passcode is 0791", "0791");
        Row("R28", "password=P%40ssw0rd&username=jo", "P%40ssw0rd");
        return data;
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void ASecretDoesNotSurviveTheDefaultPolicy(string scenario, string text, string secret)
    {
        var scrubbed = Engine.ScrubText(text);

        Assert.True(scrubbed.Complete, scenario);
        Assert.DoesNotContain(secret, scrubbed.Text, StringComparison.Ordinal);
    }

    [Theory]
    // Numbers that look like something and are not.
    [InlineData("N01", "Ticket #48213 opened 2026-09-25 by Jo")]
    [InlineData("N02", "Phone 555-123-4567, ext 204")]
    [InlineData("N03", "Order 1234 5678 9012 shipped")]
    [InlineData("N04", "Version 10.0.19041.1 build 2026")]
    [InlineData("N05", "IP 10.0.0.1, gateway 10.0.0.254, MAC 00:1A:2B:3C:4D:5E")]
    [InlineData("N06", "Serial 4C8-2X9-7Q1, asset 100234")]
    [InlineData("N07", "Invoice 20260925-001 for $1,234.56")]
    [InlineData("N08", "Meeting 2026-09-25 14:02 to 14:32")]
    // Sentences a technician writes about credentials, without one in them.
    [InlineData("N09", "the password field was empty so the form would not submit")]
    [InlineData("N10", "reset the password and the PIN for the user, then had them sign in")]
    [InlineData("N11", "asked them to choose a new password at first login")]
    [InlineData("N12", "the API key in the vault is fine; the client was pointing at the wrong region")]
    public void AnOrdinarySentenceIsLeftAlone(string scenario, string text)
    {
        var scrubbed = Engine.ScrubText(text);

        Assert.True(scrubbed.Complete, scenario);
        Assert.Equal(text, scrubbed.Text);
    }

    [Fact]
    public void ACardSplitAcrossOcrWordsIsMaskedAsOneRegion()
    {
        var words = new OcrWord[] { new("Card", 0, 0, 40, 12), new("4111", 50, 0, 40, 12), new("1111", 100, 0, 40, 12), new("1111", 150, 0, 40, 12), new("1111", 200, 0, 40, 12) };

        var frame = Engine.RedactFrame(words);

        Assert.True(frame.Complete);
        Assert.DoesNotContain("4111", frame.Text, StringComparison.Ordinal);
        Assert.NotEmpty(frame.Regions);
    }
}
