using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// What each session is still owed in narration (ST-027): the count finalize waits on and the switch that
/// drops a discarded or finished session's leftovers.
/// </summary>
public sealed class NarrationBacklogTests
{
    [Fact]
    public void ASessionIsOwedWhatWasQueuedForItUntilEachIsSettled()
    {
        var backlog = new NarrationBacklog();
        backlog.Owe("a");
        backlog.Owe("a");
        backlog.Owe("b");

        Assert.Equal(2, backlog.DepthFor("a"));
        backlog.Settle("a");
        Assert.Equal(1, backlog.DepthFor("a"));
        Assert.True(backlog.Wants("a"));
        backlog.Settle("a");

        // Gone at zero, so a busy day leaves nothing behind.
        Assert.Equal(0, backlog.DepthFor("a"));
        Assert.False(backlog.Wants("a"));
        Assert.Equal(1, backlog.DepthFor("b"));
    }

    [Fact]
    public void AForgottenSessionWantsNothingAndSettlingItChangesNothing()
    {
        var backlog = new NarrationBacklog();
        backlog.Owe("a");
        backlog.Owe("a");

        Assert.Equal(2, backlog.Forget("a"));
        Assert.False(backlog.Wants("a"));

        // The recorder settles what it was holding as it lets go of it; that must not go negative or
        // bring the session back.
        backlog.Settle("a");
        backlog.Settle("a");
        Assert.Equal(0, backlog.DepthFor("a"));
        Assert.False(backlog.Wants("a"));
        Assert.Equal(0, backlog.Forget("a"));
    }

    [Fact]
    public void ASessionOnceForgottenIsOwedNothingMore()
    {
        // The recorder asks which session it is hearing, then queues what it heard. A finalize or a discard
        // can fall between the two, and the segment then arrives for a session that has already been let
        // go of. It must not bring the session back: that would have the model transcribe words nobody
        // can store, and would be waited on by nothing.
        var backlog = new NarrationBacklog();
        backlog.Owe("a");
        _ = backlog.Forget("a");

        backlog.Owe("a");
        Assert.Equal(0, backlog.DepthFor("a"));
        Assert.False(backlog.Wants("a"));

        // A session with nothing owed when it ended is just as finished.
        _ = backlog.Forget("b");
        backlog.Owe("b");
        Assert.False(backlog.Wants("b"));

        // And the next session is owed as usual.
        backlog.Owe("c");
        Assert.True(backlog.Wants("c"));
        Assert.Equal(1, backlog.DepthFor("c"));
    }

    [Fact]
    public void OnlyTheLastFewFinishedSessionsAreRemembered()
    {
        // Bounded: a service that runs for months forgets the ids of sessions long over. The race above is
        // a few milliseconds wide, so only the most recent ones can still matter.
        var backlog = new NarrationBacklog();
        for (var i = 0; i < 1_000; i++)
        {
            _ = backlog.Forget($"s-{i}");
        }

        backlog.Owe("s-999");
        Assert.False(backlog.Wants("s-999"));
        backlog.Owe("s-0");
        Assert.True(backlog.Wants("s-0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ASessionHasToBeNamed(string? session)
    {
        var backlog = new NarrationBacklog();

        _ = Assert.ThrowsAny<ArgumentException>(() => backlog.Owe(session!));
        _ = Assert.ThrowsAny<ArgumentException>(() => backlog.Wants(session!));
    }
}
