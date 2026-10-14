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
