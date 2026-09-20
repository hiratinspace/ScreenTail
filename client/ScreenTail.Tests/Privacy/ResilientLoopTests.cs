using ScreenTail.Core.Privacy;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// The loops that keep capture honest must outlive their own bad moments (INV-6; 2026-09-19 review).
///
/// The password-field guard, the sensitive-context guard, the scope coordinator and the scene sampler
/// each ran in a loop that caught cancellation and nothing else. One exception of any other kind — a
/// full disk under a state write is enough — ended the loop for the life of the service, and nothing
/// else noticed. Capture carried on without the thing that was supposed to stop it: a dead password
/// guard means password fields are recorded, and a dead scope coordinator leaves the last scope decision
/// in force for every window that follows. They failed open, silently.
/// </summary>
public sealed class ResilientLoopTests
{
    [Fact]
    public async Task AStepThatThrowsCostsThatStepAndNotTheLoop()
    {
        var steps = 0;
        var failures = new List<Exception>();

        await ResilientLoop.RunAsync(
            next: _ => Task.FromResult(steps < 5),
            step: _ =>
            {
                steps++;
                return steps == 2 ? throw new IOException("disk full") : Task.CompletedTask;
            },
            onFailure: failures.Add,
            TestContext.Current.CancellationToken);

        Assert.Equal(5, steps);
        Assert.IsType<IOException>(Assert.Single(failures));
    }

    [Fact]
    public async Task CancellationStillEndsItQuietly()
    {
        using var stop = new CancellationTokenSource();
        var failures = 0;

        await ResilientLoop.RunAsync(
            next: _ => Task.FromResult(true),
            step: _ =>
            {
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            onFailure: _ => failures++,
            stop.Token);

        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task ATimeoutThatIsNotOurCancellationIsAFailureAndNotAnExit()
    {
        // OperationCanceledException is also what a timed-out call throws. Treating every one of them as
        // "we were asked to stop" is how a loop exits on a slow disk.
        var steps = 0;
        var failures = 0;

        await ResilientLoop.RunAsync(
            next: _ => Task.FromResult(steps < 3),
            step: _ =>
            {
                steps++;
                return steps == 1 ? throw new TaskCanceledException("a call timed out") : Task.CompletedTask;
            },
            onFailure: _ => failures++,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, steps);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task AFailureHandlerThatThrowsDoesNotEndItEither()
    {
        var steps = 0;

        await ResilientLoop.RunAsync(
            next: _ => Task.FromResult(steps < 3),
            step: _ =>
            {
                steps++;
                throw new InvalidOperationException("step");
            },
            onFailure: _ => throw new InvalidOperationException("the logger is broken too"),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, steps);
    }
}
