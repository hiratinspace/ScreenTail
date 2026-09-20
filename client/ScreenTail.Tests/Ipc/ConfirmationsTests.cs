using ScreenTail.Core.Ipc;

namespace ScreenTail.Tests.Ipc;

/// <summary>
/// What it takes to destroy something (ST-085, Spec §3; 2026-09-19 review).
///
/// The spec has asked for a typed confirmation before an irreversible delete since it was written, and
/// nothing enforced it. "Discard session" sat one item below "Stop and draft" in the tray menu and threw
/// the session away on a single click, and <c>erase_all_local_data</c> had no fields at all — so one
/// frame on the pipe wiped the store.
///
/// The rule lives in the service rather than in the dialog, because a confirmation the UI is trusted to
/// have shown is one a process that is not the UI does not have to show. The pipe proves the peer is the
/// same user, and a compromised same-user process is the same user too.
/// </summary>
public sealed class ConfirmationsTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATokenTheServiceIssuedIsAccepted()
    {
        var confirmations = new Confirmations();

        var token = confirmations.Issue(DestructiveAction.DiscardSession);

        Assert.True(confirmations.Spend(token, DestructiveAction.DiscardSession));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void AnythingElseIsRefused(string? token)
    {
        // The whole point. A single crafted frame on the pipe used to be enough.
        var confirmations = new Confirmations();
        _ = confirmations.Issue(DestructiveAction.DiscardSession);

        Assert.False(confirmations.Spend(token, DestructiveAction.DiscardSession));
    }

    [Fact]
    public void ATokenIsGoodOnce()
    {
        // Otherwise one confirmed discard authorises every later one, and the second round trip is gone.
        var confirmations = new Confirmations();
        var token = confirmations.Issue(DestructiveAction.EraseEverything);

        Assert.True(confirmations.Spend(token, DestructiveAction.EraseEverything));
        Assert.False(confirmations.Spend(token, DestructiveAction.EraseEverything));
    }

    [Fact]
    public void ATokenForOneThingCannotDestroyAnother()
    {
        // Confirming "discard this session" must not turn into permission to delete every session.
        var confirmations = new Confirmations();
        var token = confirmations.Issue(DestructiveAction.DiscardSession);

        Assert.False(confirmations.Spend(token, DestructiveAction.EraseEverything));
    }

    [Fact]
    public void ATokenSpentOnTheWrongThingIsStillSpent()
    {
        // It has been seen by something that did not have it, so it is no longer only the UI's.
        var confirmations = new Confirmations();
        var token = confirmations.Issue(DestructiveAction.DiscardSession);

        Assert.False(confirmations.Spend(token, DestructiveAction.EraseEverything));
        Assert.False(confirmations.Spend(token, DestructiveAction.DiscardSession));
    }

    [Fact]
    public void ATokenGoesStale()
    {
        // One left in a log, a crash dump or a screenshot is worthless by the time anybody reads it.
        var clock = new ManualTime(At);
        var confirmations = new Confirmations(clock);
        var token = confirmations.Issue(DestructiveAction.EraseEverything);

        clock.Advance(Confirmations.GoodFor + TimeSpan.FromSeconds(1));

        Assert.False(confirmations.Spend(token, DestructiveAction.EraseEverything));
    }

    [Fact]
    public void ATokenIsStillGoodWhileSomebodyIsReadingTheSentence()
    {
        // The other direction. A window so short that an honest technician misses it is a confirmation
        // that gets worked around rather than read.
        var clock = new ManualTime(At);
        var confirmations = new Confirmations(clock);
        var token = confirmations.Issue(DestructiveAction.DiscardSession);

        clock.Advance(TimeSpan.FromSeconds(20));

        Assert.True(confirmations.Spend(token, DestructiveAction.DiscardSession));
    }

    [Fact]
    public void TokensThatWereNeverUsedDoNotPileUp()
    {
        // Issuing costs nothing and destroys nothing, so there is no rate limit — which means a caller
        // that asks a thousand times must not leave a thousand entries behind for ever.
        var clock = new ManualTime(At);
        var confirmations = new Confirmations(clock);
        for (var i = 0; i < 1_000; i++)
        {
            _ = confirmations.Issue(DestructiveAction.DiscardSession);
        }

        clock.Advance(Confirmations.GoodFor + TimeSpan.FromSeconds(1));
        _ = confirmations.Issue(DestructiveAction.DiscardSession);

        Assert.Equal(1, confirmations.Outstanding);
    }

    [Fact]
    public void TwoTokensAreNotTheSameToken()
    {
        var confirmations = new Confirmations();

        Assert.NotEqual(
            confirmations.Issue(DestructiveAction.DiscardSession),
            confirmations.Issue(DestructiveAction.DiscardSession));
    }
}
