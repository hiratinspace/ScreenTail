using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// The Review pane the shell holds, and what happens to one it lets go of (T-169).
///
/// Leaving Review used to drop the pane on the floor. Its DisposeAsync is what writes the last of what
/// was typed and commits a delete still inside its undo window, and nothing called it: a different
/// session replacing the pane, the session going away, and the window closing all lost both.
/// </summary>
public sealed class HeldPaneTests
{
    [Fact]
    public async Task APaneReplacedByAnotherIsDisposed()
    {
        var held = new HeldPane();
        var first = new Pane();
        var second = new Pane();

        held.Hold(first);
        held.Hold(second);
        await held.ReleaseAsync();

        Assert.Equal(1, first.Disposed);
        Assert.Equal(1, second.Disposed);
    }

    [Fact]
    public async Task HoldingNothingLetsTheHeldPaneGo()
    {
        // The session went away: Review has nothing to show, and the pane it had is finished with.
        var held = new HeldPane();
        var pane = new Pane();
        held.Hold(pane);

        held.Hold(null);

        Assert.Null(held.Current);
        await held.ReleaseAsync();
        Assert.Equal(1, pane.Disposed);
    }

    [Fact]
    public void ThePaneStillHeldIsNotDisposed()
    {
        // Back to Review from History shows the same pane, edits and all (T-159), so holding it again, or
        // holding it at all, must not dispose it.
        var held = new HeldPane();
        var pane = new Pane();

        held.Hold(pane);
        held.Hold(pane);
        held.LetGo(pane);

        Assert.Same(pane, held.Current);
        Assert.Equal(0, pane.Disposed);
    }

    [Fact]
    public async Task ReleasingWaitsForEveryPaneStillGoing()
    {
        // The window closing, or Quit, waits for the last note write and the delete's answer, not just
        // for them to start.
        var held = new HeldPane();
        var replaced = new Pane { Going = new TaskCompletionSource() };
        var current = new Pane { Going = new TaskCompletionSource() };
        held.Hold(replaced);
        held.Hold(current);

        var released = held.ReleaseAsync();

        Assert.False(released.IsCompleted, "release finished while a replaced pane was still writing");
        replaced.Going.SetResult();
        Assert.False(released.IsCompleted, "release finished while the held pane was still writing");
        current.Going.SetResult();
        await released;
    }

    [Fact]
    public async Task ReleasingAlsoWaitsForAPaneLetGoOfAfterIt()
    {
        // A load that finishes after the window closed lets go of its pane after the release began. Quit
        // waits on the release, so the release waits for that pane too.
        var held = new HeldPane();
        var shown = new Pane { Going = new TaskCompletionSource() };
        var late = new Pane { Going = new TaskCompletionSource() };
        held.Hold(shown);

        var released = held.ReleaseAsync();
        held.LetGo(late);
        shown.Going.SetResult();
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.False(released.IsCompleted, "release finished while a pane let go of after it was still writing");
        late.Going.SetResult();
        await released;
    }

    [Fact]
    public async Task APaneThatArrivesTooLateIsLetGoWithoutBeingShown()
    {
        // A load superseded by another, or finished after the window closed: never held, still disposed.
        var held = new HeldPane();
        var late = new Pane();

        await held.ReleaseAsync();
        held.LetGo(late);

        Assert.Null(held.Current);
        Assert.Equal(1, late.Disposed);
    }

    [Fact]
    public async Task APaneThatFailsAsItGoesDoesNotStopTheRest()
    {
        // A close or a quit must finish whatever one pane does on its way out.
        var held = new HeldPane();
        var failing = new Pane { Fails = true };
        var other = new Pane();
        held.Hold(failing);
        held.Hold(other);

        await held.ReleaseAsync();

        Assert.Equal(1, failing.Disposed);
        Assert.Equal(1, other.Disposed);
    }

    [Fact]
    public void SomethingThatIsNotAPaneIsHeldAndLetGoQuietly()
    {
        // The content area also shows sentences ("No draft to review yet"), which have nothing to dispose.
        var held = new HeldPane();

        held.Hold("Asking the capture service for the session");
        held.Hold(null);

        Assert.True(held.ReleaseAsync().IsCompletedSuccessfully);
    }

    private sealed class Pane : IAsyncDisposable
    {
        public int Disposed { get; private set; }

        public TaskCompletionSource? Going { get; init; }

        public bool Fails { get; init; }

        public async ValueTask DisposeAsync()
        {
            Disposed++;
            if (Going is { } going)
            {
                await going.Task;
            }

            if (Fails)
            {
                throw new InvalidOperationException("the pipe went as the note was written");
            }
        }
    }
}
