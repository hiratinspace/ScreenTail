using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// ST-112: the service's stop waits for the speech model to let go for so long and no longer (owner's
/// decision, 2026-11-05). A sign-out with the technician talking spent the whole four-second budget
/// waiting for a transcription nobody would read.
/// </summary>
public sealed class StopBoundTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WorkThatFinishesInsideTheBoundIsWaitedFor()
    {
        var released = false;
        async Task ReleaseAsync()
        {
            await Task.Yield();
            released = true;
        }

        Assert.True(await StopBound.WithinAsync(ReleaseAsync(), StopBound.Default));
        Assert.True(released);
    }

    [Fact]
    public async Task WorkThatNeverFinishesIsLeftAtTheBound()
    {
        var time = new TickingTime(Start);
        var never = new TaskCompletionSource();

        var waiting = StopBound.WithinAsync(never.Task, StopBound.Default, time);
        time.Advance(StopBound.Default - TimeSpan.FromMilliseconds(1));
        Assert.False(waiting.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // Left, not cancelled: it carries on and finishes by itself.
        Assert.False(never.Task.IsCompleted);
        never.SetResult();
    }

    [Fact]
    public async Task AFaultInsideTheBoundIsThrown()
    {
        var failed = Task.FromException(new InvalidOperationException("the release failed"));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => StopBound.WithinAsync(failed, StopBound.Default));
    }

    [Fact]
    public void TheBoundIsWellInsideTheStopBudget()
    {
        // CloseRequests.DefaultBudget is four seconds, the whole stop; the model's share is a fraction.
        Assert.True(StopBound.Default <= TimeSpan.FromSeconds(1));
        Assert.True(StopBound.Default > TimeSpan.Zero);
    }
}
