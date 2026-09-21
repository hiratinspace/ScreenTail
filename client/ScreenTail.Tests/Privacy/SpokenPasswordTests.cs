using ScreenTail.Core.Privacy;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// The spoken-password rule, against how people actually speak (ST-040; 2026-09-20 review).
///
/// The rule masked exactly one token, the one immediately after the cue, and a person saying a password
/// out loud rarely puts it there. "The password is, uh, Winter2026" masked "is"; "the password on the
/// router is Winter2026" masked "on"; "password is Winter 2026" masked "Winter" and left the year.
///
/// Every one of those wrote a <c>TranscriptRedacted</c> row on the way past, so the audit log recorded a
/// redaction that had not happened while the credential went into the store and into the bundle. A rule
/// that reports success is worse than one that reports nothing.
///
/// The other half is not over-reaching: "the password is incorrect" is a sentence a technician writes,
/// and a rule that masks half of it is a rule they will ask to have turned off.
/// </summary>
public sealed class SpokenPasswordTests
{
    [Theory]
    // The four the review found, verified against the old rule before this was written.
    [InlineData("The password is, uh, Winter2026.", "Winter2026")]
    [InlineData("The password on the router is Winter2026", "Winter2026")]
    [InlineData("the password is the usual Winter2026", "Winter2026")]
    [InlineData("password is Winter 2026", "2026")]
    // And the shapes that already worked, which must keep working.
    [InlineData("password is Hunter2!", "Hunter2")]
    [InlineData("pwd: hunter2", "hunter2")]
    [InlineData("The password was reset to Spring2027", "Spring2027")]
    public void TheSecretIsMaskedAndNotTheWordInFrontOfIt(string spoken, string secret)
    {
        var masked = Mask(spoken);

        Assert.DoesNotContain(secret, masked, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", masked, StringComparison.Ordinal);
    }

    [Theory]
    // Sentences about a password. Masking any of these teaches technicians to switch the rule off.
    [InlineData("the password is incorrect")]
    [InlineData("the password was wrong again")]
    [InlineData("their password expired")]
    [InlineData("Outlook kept prompting for a password")]
    public void ASentenceAboutAPasswordIsNotAPassword(string spoken)
    {
        Assert.Equal(spoken, Mask(spoken));
    }

    [Fact]
    public void TheCueItselfSurvivesSoTheTranscriptStillReads()
    {
        // Review shows this to a technician. "[REDACTED]" on its own says nothing about what happened.
        Assert.StartsWith("The password is", Mask("The password is Winter2026"), StringComparison.Ordinal);
    }

    private static string Mask(string text)
    {
        var engine = new RedactionEngine();
        return engine.ScrubText(text).Text;
    }
}
