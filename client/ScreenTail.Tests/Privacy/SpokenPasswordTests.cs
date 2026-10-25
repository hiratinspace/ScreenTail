using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

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
    // 2026-10-12 review, S-12. The walk gave up on the first ordinary word, on "which" and "when", and
    // after five words, so each of these was stored verbatim with no audit row. A person says what the
    // password is for before saying what it is, and the assertion ("is", "was reset to") is what marks
    // the secret, however far along the sentence it comes.
    [InlineData("the password for the domain admin account is Winter2026", "Winter2026")]
    [InlineData("the password, which is Winter2026, has not changed", "Winter2026")]
    [InlineData("the password when prompted is Winter2026", "Winter2026")]
    [InlineData("the password has been reset to Spring2027", "Spring2027")]
    [InlineData("the password for the user on ticket 48213 is Winter2026", "Winter2026")]
    public void TheSecretIsMaskedHoweverLongTheRunUpToIt(string spoken, string secret)
    {
        var scrubbed = new RedactionEngine().ScrubText(spoken);

        Assert.DoesNotContain(secret, scrubbed.Text, StringComparison.Ordinal);
        // The audit row is the other half of the finding: a masked word nobody counted is a redaction the
        // customer's audit export denies happened (INV-10).
        Assert.Equal(1, scrubbed.Counts.GetValueOrDefault(MaskKind.Password));
    }

    [Theory]
    // Sentences about a password. Masking any of these teaches technicians to switch the rule off.
    [InlineData("the password is incorrect")]
    [InlineData("the password was wrong again")]
    [InlineData("their password expired")]
    [InlineData("Outlook kept prompting for a password")]
    // Walking further must not start masking the technician's own account of what they did: a number
    // the sentence never asserted is a ticket, and a plain word after a long run-up is prose.
    [InlineData("reset the password for the user on ticket 48213")]
    [InlineData("the password for the domain admin account is in the vault")]
    [InlineData("the password which expired was for the service account")]
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
