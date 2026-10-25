using ScreenTail.Service.Host;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// One capture service per user (ADR-0003; 2026-10-12 review, T-114).
///
/// The mutex used to be taken owned, and a mutex may only be released by the thread that took it.
/// Dispose runs after an await, on whatever thread the continuation lands on, so every clean shutdown
/// ended in an ApplicationException and a non-zero exit code: a service that always crashed on the way
/// out (2026-09-19). The name is now held unowned, and letting go works from any thread. No desktop is
/// needed, so this runs on the hosted runner.
/// </summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public async Task OneServicePerUserAndTheNameIsFreedOnAnyThread()
    {
        var user = "S-1-5-21-test-" + Guid.NewGuid().ToString("N");

        var first = SingleInstance.TryAcquire(user);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(user));

        // Another user's service is not this user's.
        using (var other = SingleInstance.TryAcquire(user + "-other"))
        {
            Assert.NotNull(other);
        }

        // Let go from a thread that is not the one that took it, which is what an awaited shutdown does.
        var took = Environment.CurrentManagedThreadId;
        await Task.Run(
            () =>
            {
                Assert.NotEqual(took, Environment.CurrentManagedThreadId);
                first.Dispose();
            },
            TestContext.Current.CancellationToken);

        using var again = SingleInstance.TryAcquire(user);
        Assert.NotNull(again);
    }
}
