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

    /// <summary>The window that asked. Tokens are bound to it (2026-09-20 review).</summary>
    private static readonly Guid Caller = Guid.NewGuid();

    [Fact]
    public void ATokenTheServiceIssuedIsAccepted()
    {
        var confirmations = new Confirmations();

        var token = confirmations.Issue(DestructiveAction.DiscardSession, Caller);

        Assert.True(confirmations.Spend(token, DestructiveAction.DiscardSession, Caller));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void AnythingElseIsRefused(string? token)
    {
        // The whole point. A single crafted frame on the pipe used to be enough.
        var confirmations = new Confirmations();
        _ = confirmations.Issue(DestructiveAction.DiscardSession, Caller);

        Assert.False(confirmations.Spend(token, DestructiveAction.DiscardSession, Caller));
    }

    [Fact]
    public void AWindowCannotSpendAConfirmationAnotherWindowAsked()
    {
        // 2026-09-20 review. The token was a bearer token with nothing tying it to who asked, and every
        // authenticated pipe client shares one issuer — so the impostor this class was written against
        // could sit and wait for the technician to confirm something of their own, then spend it.
        //
        // Two round trips only mean something if both are the same conversation.
        var confirmations = new Confirmations();
        var theWindow = Guid.NewGuid();
        var somebodyElse = Guid.NewGuid();

        var token = confirmations.Issue(DestructiveAction.DiscardSession, theWindow);

        Assert.False(confirmations.Spend(token, DestructiveAction.DiscardSession, somebodyElse));
        Assert.True(confirmations.Spend(token, DestructiveAction.DiscardSession, theWindow));
    }

    [Fact]
    public void AStrangerGuessingCannotCancelTheConfirmationSomebodyIsTyping()
    {
        // Every other way of presenting a token burns it, which is right: its holder may try twice.
        // Not this one. The holder here is the window the token belongs to, and burning it for a
        // connection that is not that window would hand any authenticated client a way to cancel the
        // technician's confirmation exactly as they type the phrase -- a denial of service built out of
        // the defence itself (2026-09-20 review).
        var confirmations = new Confirmations();
        var theWindow = Guid.NewGuid();
        var token = confirmations.Issue(DestructiveAction.EraseEverything, theWindow);

        for (var i = 0; i < 5; i++)
        {
            Assert.False(confirmations.Spend(token, DestructiveAction.EraseEverything, Guid.NewGuid()));
        }

        Assert.True(confirmations.Spend(token, DestructiveAction.EraseEverything, theWindow));
    }

    [Fact]
    public void AskingForeverDoesNotCostTheServiceAnything()
    {
        // Issuing was free and unbounded, and Forget() walks every outstanding entry with a LINQ query
        // and a ToList under the lock. A client in a loop bought itself megabytes of dictionary and made
        // every honest Spend wait behind the scan.
        //
        // Nothing an attacker gains by asking repeatedly is worth having: what they get is a pile of
        // tokens they already had.
        var confirmations = new Confirmations();
        var caller = Guid.NewGuid();

        for (var i = 0; i < 10_000; i++)
        {
            _ = confirmations.Issue(DestructiveAction.DiscardSession, caller);
        }

        Assert.True(confirmations.Outstanding <= Confirmations.MostOutstanding);
    }

    [Fact]
    public void TheNewestTokenIsTheOneThatSurvives()
    {
        // Evicting the oldest keeps the cap from becoming a way to cancel somebody's confirmation: the
        // one the technician is about to type is the one that was just issued.
        var confirmations = new Confirmations();
        var caller = Guid.NewGuid();
        for (var i = 0; i < Confirmations.MostOutstanding; i++)
        {
            _ = confirmations.Issue(DestructiveAction.DiscardSession, caller);
        }

        var newest = confirmations.Issue(DestructiveAction.DiscardSession, caller);

        Assert.True(confirmations.Spend(newest, DestructiveAction.DiscardSession, caller));
    }

    [Fact]
    public void ATokenIsGoodOnce()
    {
        // Otherwise one confirmed discard authorises every later one, and the second round trip is gone.
        var confirmations = new Confirmations();
        var token = confirmations.Issue(DestructiveAction.EraseEverything, Caller);

        Assert.True(confirmations.Spend(token, DestructiveAction.EraseEverything, Caller));
        Assert.False(confirmations.Spend(token, DestructiveAction.EraseEverything, Caller));
    }

    [Fact]
    public void ATokenForOneThingCannotDestroyAnother()
    {
        // Confirming "discard this session" must not turn into permission to delete every session.
        var confirmations = new Confirmations();
        var token = confirmations.Issue(DestructiveAction.DiscardSession, Caller);

        Assert.False(confirmations.Spend(token, DestructiveAction.EraseEverything, Caller));
    }

    [Fact]
    public void ATokenSpentOnTheWrongThingIsStillSpent()
    {
        // It has been seen by something that did not have it, so it is no longer only the UI's.
        var confirmations = new Confirmations();
        var token = confirmations.Issue(DestructiveAction.DiscardSession, Caller);

        Assert.False(confirmations.Spend(token, DestructiveAction.EraseEverything, Caller));
        Assert.False(confirmations.Spend(token, DestructiveAction.DiscardSession, Caller));
    }

    [Fact]
    public void ATokenGoesStale()
    {
        // One left in a log, a crash dump or a screenshot is worthless by the time anybody reads it.
        var clock = new ManualTime(At);
        var confirmations = new Confirmations(clock);
        var token = confirmations.Issue(DestructiveAction.EraseEverything, Caller);

        clock.Advance(Confirmations.GoodFor + TimeSpan.FromSeconds(1));

        Assert.False(confirmations.Spend(token, DestructiveAction.EraseEverything, Caller));
    }

    [Fact]
    public void ATokenIsStillGoodWhileSomebodyIsReadingTheSentence()
    {
        // The other direction. A window so short that an honest technician misses it is a confirmation
        // that gets worked around rather than read.
        var clock = new ManualTime(At);
        var confirmations = new Confirmations(clock);
        var token = confirmations.Issue(DestructiveAction.DiscardSession, Caller);

        clock.Advance(TimeSpan.FromSeconds(20));

        Assert.True(confirmations.Spend(token, DestructiveAction.DiscardSession, Caller));
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
            _ = confirmations.Issue(DestructiveAction.DiscardSession, Caller);
        }

        clock.Advance(Confirmations.GoodFor + TimeSpan.FromSeconds(1));
        _ = confirmations.Issue(DestructiveAction.DiscardSession, Caller);

        Assert.Equal(1, confirmations.Outstanding);
    }

    [Fact]
    public void TwoTokensAreNotTheSameToken()
    {
        var confirmations = new Confirmations();

        Assert.NotEqual(
            confirmations.Issue(DestructiveAction.DiscardSession, Caller),
            confirmations.Issue(DestructiveAction.DiscardSession, Caller));
    }
}
