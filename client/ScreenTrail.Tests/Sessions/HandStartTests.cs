using ScreenTrail.Core.Sessions;

namespace ScreenTrail.Tests.Sessions;

/// <summary>
/// ST-071, Spec §5 S1 as built v0.7.2: the rules a start by hand meets before any window is looked at, and
/// the words the tray's balloon says when one refuses. Ctrl+Alt+R and the tray's Start capture take the same
/// start in the service; the window-free half is held here.
/// </summary>
public sealed class HandStartTests
{
    [Theory]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.DraftReady)]
    [InlineData(SessionState.DraftFailed)]
    [InlineData(SessionState.DraftPending)]
    public void BetweenSessionsAStartGoesOn(SessionState state)
    {
        Assert.Null(HandStart.Before(held: false, state));
    }

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Paused)]
    [InlineData(SessionState.Suppressed)]
    [InlineData(SessionState.Finalizing)]
    public void WithASessionUnderWayNothingNewStarts(SessionState state)
    {
        Assert.Equal(HandStartOutcome.Busy, HandStart.Before(held: false, state));
    }

    [Theory]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Recording)]
    public void AHeldStartIsHeldWhateverElseIsTrue(SessionState state)
    {
        // ST-113: Windows Installer is replacing ScreenTrail, and the hold is said first.
        Assert.Equal(HandStartOutcome.Held, HandStart.Before(held: true, state));
    }

    [Theory]
    [InlineData(HandStartOutcome.NoRemoteSession, HandStart.NoRemoteSession)]
    [InlineData(HandStartOutcome.NotIndicated, HandStart.NotIndicated)]
    [InlineData(HandStartOutcome.Held, ManualStart.Updating)]
    [InlineData(HandStartOutcome.Busy, HandStart.Busy)]
    public void EachRefusalIsAFailedResultWithItsSentence(HandStartOutcome outcome, string words)
    {
        var result = HandStart.Result(7, outcome);

        Assert.False(result.Ok);
        Assert.Equal(7, result.RequestId);
        Assert.Equal(words, result.Error);
    }

    [Fact]
    public void AStartIsAnOkResultWithNoWords()
    {
        var result = HandStart.Result(3, HandStartOutcome.Started);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
    }

    [Fact]
    public void EverySentenceFitsABalloonAndSaysWhatToDo()
    {
        // A balloon holds 255 characters. No em dash, no exclamation mark (owner, 2026-11-13).
        foreach (var outcome in Enum.GetValues<HandStartOutcome>().Where(o => o != HandStartOutcome.Started))
        {
            var words = HandStart.Words(outcome)!;
            Assert.InRange(words.Length, 1, 255);
            Assert.DoesNotContain("—", words, StringComparison.Ordinal);
            Assert.DoesNotContain("!", words, StringComparison.Ordinal);
        }

        Assert.Contains("Open the remote session", HandStart.NoRemoteSession, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Alt+R", HandStart.NoRemoteSession, StringComparison.Ordinal);
    }
}
