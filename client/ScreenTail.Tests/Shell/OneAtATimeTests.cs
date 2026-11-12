using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// The guard the UI's dialog and send commands hold (review of #274): a second invoke while the first is
/// running does nothing, and the screen can tell the action is running for as long as it is.
/// </summary>
public sealed class OneAtATimeTests
{
    [Fact]
    public async Task ASecondRunWhileTheFirstIsUpDoesNothing()
    {
        var guard = new OneAtATime();
        var held = new TaskCompletionSource();
        var runs = 0;

        var first = guard.RunAsync(async () =>
        {
            runs++;
            await held.Task;
        });
        var second = guard.RunAsync(() =>
        {
            runs++;
            return Task.CompletedTask;
        });

        Assert.True(second.IsCompletedSuccessfully);
        Assert.True(guard.Busy);
        Assert.Equal(1, runs);

        held.SetResult();
        await first;
        Assert.False(guard.Busy);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ARunAskedForFromInsideTheFirstBeforeItAwaitsIsRefused()
    {
        // A dialog shown before the first await runs a nested message loop, where a queued invoke can
        // arrive. Busy is already on by then.
        var guard = new OneAtATime();
        var inner = 0;

        await guard.RunAsync(() => guard.RunAsync(() =>
        {
            inner++;
            return Task.CompletedTask;
        }));

        Assert.Equal(0, inner);
        Assert.False(guard.Busy);
    }

    [Fact]
    public async Task ItSaysWhenItTurnsOnAndOff()
    {
        var guard = new OneAtATime();
        var seen = new List<bool>();

        await guard.RunAsync(
            () =>
            {
                seen.Add(guard.Busy);
                return Task.CompletedTask;
            },
            () => seen.Add(guard.Busy));

        Assert.Equal([true, true, false], seen);
    }

    [Fact]
    public async Task AnActionThatThrowsLetsTheNextOneRun()
    {
        var guard = new OneAtATime();
        var runs = 0;

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => guard.RunAsync(() => throw new InvalidOperationException("no")));
        await guard.RunAsync(() => Task.FromResult(++runs));

        Assert.False(guard.Busy);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task OneAfterTheOtherBothRun()
    {
        var guard = new OneAtATime();
        var runs = 0;

        await guard.RunAsync(() => Task.FromResult(++runs));
        await guard.RunAsync(() => Task.FromResult(++runs));

        Assert.Equal(2, runs);
    }
}
