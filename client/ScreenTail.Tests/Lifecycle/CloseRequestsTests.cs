using System.Diagnostics;
using ScreenTail.Core.Lifecycle;

namespace ScreenTail.Tests.Lifecycle;

/// <summary>
/// When Windows asks a ScreenTail process to close (ST-112): what each message is answered, when the stop
/// starts, and that the wait Windows is held for is bounded. The window that receives the messages is
/// Windows-only (SessionEndWindowTests); the rules are here.
/// </summary>
public sealed class CloseRequestsTests
{
    private static readonly CloseRequest RestartManager = new(CloseKind.RestartManager, Critical: false);

    [Theory]
    [InlineData(0x00000000L, CloseKind.SystemShutdown, false, "system-shutdown")]
    [InlineData(0x80000000L, CloseKind.SignOut, false, "sign-out")]
    [InlineData(0x00000001L, CloseKind.RestartManager, false, "restart-manager")]
    [InlineData(0x40000000L, CloseKind.SystemShutdown, true, "system-shutdown-critical")]
    [InlineData(0xC0000000L, CloseKind.SignOut, true, "sign-out-critical")]
    // Restart Manager's flag is the one that means "only this program", so it wins over the others.
    [InlineData(0x80000001L, CloseKind.RestartManager, false, "restart-manager")]
    // lParam is pointer-sized; a sign-extended value from a 64-bit lParam reads the same.
    [InlineData(unchecked((long)0xFFFFFFFF80000000UL), CloseKind.SignOut, false, "sign-out")]
    public void TheEndSessionFlagsSayWhoAsked(long flags, CloseKind kind, bool critical, string forLog)
    {
        var request = CloseRequest.FromEndSession(flags);

        Assert.Equal(kind, request.Kind);
        Assert.Equal(critical, request.Critical);
        Assert.Equal(forLog, request.ForLog);
    }

    [Theory]
    [InlineData(0x00000000L)]
    [InlineData(0x80000000L)]
    [InlineData(0x00000001L)]
    [InlineData(0x40000000L)]
    public void QueryEndSessionIsAlwaysYesAndStopsNothing(long flags)
    {
        // Capture is not a reason to keep a machine up, and an installer refused leaves its files for a restart.
        var stops = 0;
        var requests = new CloseRequests(() => stops++, new TaskCompletionSource().Task);

        Assert.True(CloseRequests.MayEnd(CloseRequest.FromEndSession(flags)));
        Assert.Equal(0, stops);
        Assert.Equal(0, requests.StopsAsked);
        Assert.Null(requests.First);
    }

    [Fact]
    public void AnEndThatDidNotHappenStopsNothing()
    {
        // WM_ENDSESSION with FALSE: another program refused, and the session goes on recording.
        var stops = 0;
        var requests = new CloseRequests(() => stops++, new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(50));

        var watch = Stopwatch.StartNew();
        var outcome = requests.Ended(ending: false, RestartManager);

        Assert.Equal(CloseOutcome.NotClosing, outcome);
        Assert.Equal(0, stops);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "nothing to wait for");
    }

    [Fact]
    public async Task EndSessionStartsTheStopAndReturnsOnlyOnceTheProcessHasStopped()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new CloseRequests(() => stopAsked.TrySetResult(), stopped.Task, TimeSpan.FromSeconds(5));
        CloseRequest? logged = null;
        requests.Requested += request => logged = request;

        // The window's thread, held inside WM_ENDSESSION.
        var answering = Task.Factory.StartNew(() => requests.Ended(ending: true, RestartManager), TaskCreationOptions.LongRunning);
        await stopAsked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The stop was asked for, and the answer is still held: Windows may end the process once it returns.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(answering.IsCompleted);

        stopped.SetResult();
        Assert.Equal(CloseOutcome.Stopped, await answering.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(RestartManager, logged);
        Assert.Equal(RestartManager, requests.First);
    }

    [Fact]
    public void TheWaitIsBoundedWhenTheProcessDoesNotStopInTime()
    {
        var stops = 0;
        var ranOut = 0;
        var budget = TimeSpan.FromMilliseconds(200);
        var requests = new CloseRequests(() => stops++, new TaskCompletionSource().Task, budget);
        requests.WaitRanOut += _ => ranOut++;

        var watch = Stopwatch.StartNew();
        var outcome = requests.Ended(ending: true, new CloseRequest(CloseKind.SignOut, Critical: false));
        watch.Stop();

        Assert.Equal(CloseOutcome.StillStopping, outcome);
        Assert.Equal(1, stops);
        Assert.Equal(1, ranOut);
        Assert.True(watch.Elapsed >= budget - TimeSpan.FromMilliseconds(20), $"waited {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"waited {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TheDefaultWaitIsInsideTheFiveSecondsWindowsAllows()
    {
        Assert.True(CloseRequests.DefaultBudget > TimeSpan.Zero);
        Assert.True(CloseRequests.DefaultBudget < TimeSpan.FromSeconds(5));
        Assert.Equal(CloseRequests.DefaultBudget, new CloseRequests(() => { }, Task.CompletedTask).Budget);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CloseRequests(() => { }, Task.CompletedTask, TimeSpan.FromSeconds(6)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CloseRequests(() => { }, Task.CompletedTask, TimeSpan.Zero));
    }

    [Fact]
    public void TheStopIsAskedForOnceWhateverArrivesAfterIt()
    {
        // Restart Manager's pair, then the end-session pair as the session ends, then an installer's WM_CLOSE.
        var stops = 0;
        var requests = new CloseRequests(() => stops++, Task.CompletedTask, TimeSpan.FromMilliseconds(100));

        Assert.Equal(CloseOutcome.Stopped, requests.Ended(ending: true, RestartManager));
        Assert.Equal(CloseOutcome.Stopped, requests.Ended(ending: true, new CloseRequest(CloseKind.SignOut, Critical: false)));
        requests.Close();

        Assert.Equal(1, stops);
        Assert.Equal(1, requests.StopsAsked);
        Assert.Equal(RestartManager, requests.First);
    }

    [Fact]
    public void CloseMessageStartsTheStopAndDoesNotWait()
    {
        // WM_CLOSE is posted: nobody waits on the answer, so the window's thread is not held either.
        var stops = 0;
        var requests = new CloseRequests(() => stops++, new TaskCompletionSource().Task, TimeSpan.FromSeconds(4));

        var watch = Stopwatch.StartNew();
        requests.Close();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(1, stops);
        Assert.Equal(new CloseRequest(CloseKind.CloseMessage, Critical: false), requests.First);
    }

    [Fact]
    public void NothingThrowsIntoTheWindowProcedure()
    {
        // A stop that throws (the host already disposed), a failed stop, and listeners that throw.
        var stopped = new TaskCompletionSource();
        stopped.SetException(new InvalidOperationException("stopping failed"));
        var requests = new CloseRequests(() => throw new ObjectDisposedException("host"), stopped.Task, TimeSpan.FromMilliseconds(100));
        requests.Requested += _ => throw new IOException("log closed");
        requests.WaitRanOut += _ => throw new IOException("log closed");

        Assert.Equal(CloseOutcome.Stopped, requests.Ended(ending: true, RestartManager));
        requests.Close();
        Assert.Equal(1, requests.StopsAsked);

        var never = new CloseRequests(() => { }, new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(50));
        never.WaitRanOut += _ => throw new IOException("log closed");
        Assert.Equal(CloseOutcome.StillStopping, never.Ended(ending: true, RestartManager));
    }
}
