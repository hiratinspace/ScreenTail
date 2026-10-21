using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Detection;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Detection;
using ScreenTail.Tests.Windows;

namespace ScreenTail.Tests.Windows.Detection;

/// <summary>
/// ST-022 against real windows. These create actual top-level windows, put them in front, and time how long
/// the watcher takes to notice — the only way to know whether the hook is delivering.
///
/// They need an interactive desktop, so they skip on a hosted runner (session 0) and run for real on the
/// spare laptop. Skipping is the honest outcome there: a foreground test on a machine with no foreground
/// proves nothing, and passing it vacuously would be worse than not running it.
/// </summary>
public sealed class ForegroundWatcherTests
{
    /// <summary>
    /// Whether a timing measured here means anything. A GitHub-hosted runner is a shared cloud VM whose
    /// numbers the test loop has never counted (docs/dev/windows-test-loop.md) — it measured 322 ms against
    /// the 100 ms budget on a build that the laptop passed. Correctness is still checked everywhere; only
    /// the budget is held back for hardware we can reason about.
    /// </summary>
    private static bool PerformanceCounts => Hardware.PerformanceCounts;

    [Fact]
    public async Task AFocusChangeIsReportedWithinTheBudget()
    {
        Hardware.RequireDesktop();
        var title = $"ScreenTail probe {Guid.NewGuid():N}";
        await using var watcher = new WindowsForegroundWatcher();
        var seen = new TaskCompletionSource<ForegroundWindowInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += info =>
        {
            if (info.Title == title)
            {
                seen.TrySetResult(info);
            }
        };
        await watcher.StartAsync(TestContext.Current.CancellationToken);

        using var window = DesktopWindow.Create(title);
        var clock = Stopwatch.StartNew();
        window.RequireForeground();
        var reported = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.Equal(title, reported.Title);
        Assert.Equal(Environment.ProcessId, reported.ProcessId);
        Assert.Equal(Path.GetFileNameWithoutExtension(Environment.ProcessPath), reported.ProcessName);
        Assert.False(reported.IsElevated);
        Record($"Focus change reported in **{clock.ElapsedMilliseconds} ms** (budget 100 ms, enforced: {PerformanceCounts})");
        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count; the laptop enforces this.");
        Assert.True(clock.ElapsedMilliseconds < 100, $"took {clock.ElapsedMilliseconds} ms, budget is 100 ms");
    }

    [Fact]
    public async Task RetitlingTheWindowInFrontIsReported()
    {
        // What a browser does when the technician switches tabs: same window, new title.
        Hardware.RequireDesktop();
        var first = $"ScreenTail before {Guid.NewGuid():N}";
        var second = $"ScreenTail after {Guid.NewGuid():N}";

        // The safety-net poll is pushed out of reach, so the new title can only arrive through the watcher's
        // name-change hook, which is what this test is named for. With the default two-second poll it passed
        // on a window that never raised a name change at all: a STATIC control, which handles WM_SETTEXT
        // itself. The window is an ordinary one, which announces its renames as an application's does.
        var ct = TestContext.Current.CancellationToken;
        using var witness = NameChangeWitness.Start();
        await using var watcher = new WindowsForegroundWatcher(pollInterval: TimeSpan.FromMinutes(10));
        var focused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renamed = new TaskCompletionSource<ForegroundWindowInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += info =>
        {
            if (info.Title == first)
            {
                focused.TrySetResult();
            }
            else if (info.Title == second)
            {
                renamed.TrySetResult(info);
            }
        };
        await watcher.StartAsync(ct);
        Assert.True(watcher.UsingHook, "The foreground watcher's event hook was not installed, so a rename cannot reach it.");

        using var window = DesktopWindow.CreateOrdinary(first);
        window.RequireForeground();

        // A new title for the window already reported is held back for the change filter's settle time, and
        // only the poll offers it again. So the rename waits until the focus change has been reported and
        // that settle time has passed twice over; renamed sooner, it is the poll's to deliver, not the
        // hook's. A hosted run that renamed 200 ms after taking the foreground never saw it reported.
        await focused.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Task.Delay(new ForegroundChangeFilter().TitleSettle * 2, ct);
        var inFrontBefore = window.IsForeground;
        window.Retitle(second);
        var inFrontAfter = window.IsForeground;

        try
        {
            var reported = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(second, reported.Title);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"The watcher never reported the window in front by its new title (in front before the rename: {inFrontBefore}, after: {inFrontAfter}, now: {window.IsForeground}; object ids the test's hook saw for it: {witness.ObjectIds(window.Handle)}).");
        }
    }

    [Fact]
    public async Task AWindowThatOnlyMovesAroundIsNeverReportedAsTheForegroundWindow()
    {
        // ST-048 (weaknesses P0-5). SetWinEventHook's first two arguments are an inclusive range, and the
        // watcher passed EVENT_SYSTEM_FOREGROUND and EVENT_OBJECT_NAMECHANGE as though they were a pair —
        // subscribing to every event type between them. Each arrived with its own window handle, and any
        // handle different from the last was published as the foreground window, so ScopePolicy re-ran
        // against windows that were never in front. This is that bug in the shape a technician creates:
        // a window redrawing in the background while they work in the remote session.
        Hardware.RequireDesktop();
        var ct = TestContext.Current.CancellationToken;
        var frontTitle = $"ScreenTail front {Guid.NewGuid():N}";
        var noisyTitle = $"ScreenTail noisy {Guid.NewGuid():N}";

        using var noisy = DesktopWindow.Create(noisyTitle);
        using var front = DesktopWindow.Create(frontTitle);
        front.RequireForeground();

        await using var watcher = new WindowsForegroundWatcher();
        var reported = new List<string?>();
        watcher.Changed += info =>
        {
            lock (reported)
            {
                reported.Add(info.Title);
            }
        };
        await watcher.StartAsync(ct);
        await Task.Delay(200, ct);

        for (var i = 0; i < 200; i++)
        {
            noisy.Jiggle(300 + (i % 40), 200 + (i % 40));
            await Task.Delay(5, ct);
        }

        await Task.Delay(300, ct);

        string[] seen;
        lock (reported)
        {
            seen = [.. reported.Where(t => t is not null).Select(t => t!)];
        }

        Record($"200 background window moves produced **{seen.Count(t => t == noisyTitle)}** foreground reports for the moving window (must be 0)");
        Assert.DoesNotContain(noisyTitle, seen);

        // Everything the watcher reported, not what it happens to hold now. The two are different in the
        // input-injection job, where another test's keystrokes can raise the task switcher and take the
        // foreground while this one is running — which failed the assertion for a reason that has nothing
        // to do with what is being tested. The list is cumulative, so the real window being in it is just
        // as strong a proof that the watcher is working, and it cannot be undone by a later window.
        Assert.Contains(frontTitle, seen);
    }

    [Fact]
    public async Task ABackgroundWindowRetitlingItselfIsNeverReportedAsTheForegroundWindow()
    {
        // The test above moves a window, and the watcher stopped listening to moves in ST-048, so it
        // cannot see what keeps the remaining subscription honest. EVENT_OBJECT_NAMECHANGE is raised by
        // every window on the desktop that renames itself: a browser in the background loading a page, a
        // build log counting up, a chat client's unread badge. The only thing standing between those and
        // scope (and the typing gate behind it) is the check that the window renaming itself is the one in
        // front (INV-6). Remove it and each rename here is published as the foreground window, because a
        // different handle is never held back by the change filter.
        Hardware.RequireDesktop();
        var ct = TestContext.Current.CancellationToken;
        var noisyPrefix = $"ScreenTail retitling {Guid.NewGuid():N}";
        var frontTitle = $"ScreenTail front {Guid.NewGuid():N}";
        var frontRenamed = $"ScreenTail front renamed {Guid.NewGuid():N}";

        // Shown, so nothing can say its renames went unreported only because it was hidden; and shown
        // without being activated, so it never holds the foreground for a moment of its own. Both are
        // ordinary windows, because the STATIC windows the other tests use raise no name change at all.
        using var noisy = DesktopWindow.CreateOrdinary(noisyPrefix);
        noisy.ShowBehind();
        using var front = DesktopWindow.CreateOrdinary(frontTitle);
        front.RequireForeground();

        // The control. Fifty silent renames prove nothing unless they were delivered to a hook like the
        // watcher's, so the test installs one of its own and counts them. An earlier version renamed the
        // window in front instead and waited for the watcher to report it, which timed out on the hosted
        // runner: its windows were STATIC controls, whose renames raise no name change, so nothing was
        // ever delivered and the wait could only run out. The witness says so directly, and shows delivery
        // whichever window is in front.
        using var witness = NameChangeWitness.Start();

        // The safety-net poll pushed out of reach, so that what the watcher reports below can only have
        // come through its hooks.
        await using var watcher = new WindowsForegroundWatcher(pollInterval: TimeSpan.FromMinutes(10));
        var reported = new List<string?>();
        watcher.Changed += info =>
        {
            lock (reported)
            {
                reported.Add(info.Title);
            }
        };
        await watcher.StartAsync(ct);

        // Without the hook nothing here reaches the watcher by name change at all. On a desktop the hook
        // installs; when it does not, that is a failure.
        Assert.True(watcher.UsingHook, "The foreground watcher's event hook was not installed, so name changes cannot be tested.");
        await Task.Delay(200, ct);

        for (var i = 0; i < 50; i++)
        {
            noisy.Retitle($"{noisyPrefix} {i}");
            await Task.Delay(10, ct);
        }

        var delivered = await witness.WaitForAsync(noisy.Handle, NameChangeWitness.WindowObject, TimeSpan.FromSeconds(5), ct);
        Assert.True(
            delivered,
            $"No rename of the background window reached a name-change hook as a change of the window's own title (object ids seen: {witness.ObjectIds(noisy.Handle)}), so the watcher's silence about them would prove nothing.");

        // Both hooks are handed each event at the same moment, and the watcher's pump has nothing else to
        // do. Without the filter the first rename is published at once, because a different window is
        // never held back.
        await Task.Delay(500, ct);

        // The other half of the control: the watcher does act on a name change, when it is the window in
        // front that renames itself. Without this, a filter that dropped every name change would leave the
        // count above at zero and every test here green, with tab switches left to a poll that is out of
        // reach here.
        _ = front.TakeForeground(TimeSpan.FromSeconds(2));
        await Task.Delay(400, ct);
        var frontBefore = front.IsForeground;
        front.Retitle(frontRenamed);
        var frontAfter = front.IsForeground;
        _ = await witness.WaitForAsync(front.Handle, NameChangeWitness.WindowObject, TimeSpan.FromSeconds(2), ct);
        await Task.Delay(300, ct);

        string[] seen;
        lock (reported)
        {
            seen = [.. reported.Where(t => t is not null).Select(t => t!)];
        }

        var leaked = seen.Count(t => t.StartsWith(noisyPrefix, StringComparison.Ordinal));
        Record($"50 background renames reached the test's hook {witness.Count(noisy.Handle, NameChangeWitness.WindowObject)} times and produced **{leaked}** foreground reports for the renaming window (must be 0; it was in front at the end: {noisy.IsForeground})");
        Record($"A rename of the window in front (in front before: {frontBefore}, after: {frontAfter}) reached the test's hook with object ids {witness.ObjectIds(front.Handle)} and was reported by the watcher: {seen.Contains(frontRenamed)}");
        Assert.Equal(0, leaked);

        // Assert.Contains(frontRenamed, seen) in effect. xUnit's Contains takes no message, and a failure here
        // needs to say whether the window was in front and whether its rename was delivered at all.
        Assert.True(
            seen.Contains(frontRenamed),
            $"The watcher did not report the window in front by its new title (in front before the rename: {frontBefore}, after: {frontAfter}; object ids the test's hook saw for it: {witness.ObjectIds(front.Handle)}).");
    }

    [Fact]
    public async Task TheWatcherCostsAlmostNothingWhileTheDesktopIsBusy()
    {
        // AC3's budget, measured under the condition ST-031 actually budgets for. The earlier version of
        // this test measured a deliberately idle desktop, which is the one condition where the event-range
        // bug above costs nothing: no events, no wasted work, 0.000% of a core, and a green test sitting
        // on top of a CPU firehose (ST-048, weaknesses P0-5). A window moving in the background is the
        // cheapest honest approximation of a remote-desktop control redrawing under the cursor.
        Hardware.RequireDesktop();
        using var noisy = DesktopWindow.Create($"ScreenTail busy {Guid.NewGuid():N}");
        await using var watcher = new WindowsForegroundWatcher();
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        using var churn = new CancellationTokenSource();
        var churning = Task.Run(
            async () =>
            {
                for (var i = 0; !churn.IsCancellationRequested; i++)
                {
                    noisy.Jiggle(300 + (i % 40), 200 + (i % 40));
                    await Task.Delay(10, CancellationToken.None);
                }
            },
            CancellationToken.None);

        // Two things this test learned the hard way.
        //
        // It measures the watcher's own pump thread, not the process: the first version measured the test
        // host with other tests running in parallel and reported 2.1%, which said nothing about the watcher.
        //
        // And it measures for long enough to mean something. Windows accounts thread CPU in scheduler
        // quanta of about 15.625 ms, so over three seconds the smallest non-zero answer possible is exactly
        // 0.52% — which is what this reported, twice, while the true figure was somewhere between zero and
        // one tick. Over twenty seconds a single tick is 0.08%, so the budget is now measuring the watcher
        // rather than the clock's resolution.
        // Twenty seconds on the laptop, where the answer is enforced; a second on a hosted runner, where
        // it is skipped four lines below. The long window exists to beat Windows' 15.625 ms accounting
        // quantum, which only matters when the number is going to be believed -- and the twenty seconds
        // were being spent on the critical path of every client pull request to produce a figure nothing
        // would read (2026-09-20 review). What survives on both is the check above that the watcher
        // started and its pump thread can be found.
        var window = PerformanceCounts ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(1);
        var before = PumpThreadTime(watcher.PumpThreadId);
        Assert.SkipWhen(before is null, "Could not find the watcher's pump thread.");
        var start = Stopwatch.GetTimestamp();
        await Task.Delay(window, TestContext.Current.CancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(start);
        var used = PumpThreadTime(watcher.PumpThreadId)!.Value - before!.Value;

        await churn.CancelAsync();
        await churning;

        var percent = used.TotalMilliseconds / elapsed.TotalMilliseconds * 100;
        var quanta = used.TotalMilliseconds / 15.625;
        Record($"Watcher used **{percent:F3}%** of a core over {elapsed.TotalSeconds:F0}s with a window moving 100 times a second — {quanta:F0} scheduler quanta (budget 0.5%, enforced: {PerformanceCounts})");
        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count; the laptop enforces this.");
        Assert.True(percent < 0.5, $"the watcher used {percent:F3}% of a core on a busy desktop, budget is 0.5%");
    }

    [Fact]
    public async Task StartingAndStoppingLeavesNoThreadBehind()
    {
        Hardware.RequireDesktop();
        var pumps = new List<uint>();

        for (var i = 0; i < 3; i++)
        {
            var watcher = new WindowsForegroundWatcher();
            await watcher.StartAsync(TestContext.Current.CancellationToken);
            pumps.Add(watcher.PumpThreadId);
            await watcher.DisposeAsync();
        }

        // Each watcher's own pump thread must be gone. Counting the process's threads instead looked right
        // and wasn't: the thread pool grows and shrinks by itself, which failed this on a busy runner.
        Assert.Equal(3, pumps.Distinct().Count());
        using var self = Process.GetCurrentProcess();
        var alive = self.Threads.Cast<ProcessThread>().Select(t => (uint)t.Id).ToHashSet();
        Assert.All(pumps, pump => Assert.DoesNotContain(pump, alive));
    }

    [Fact]
    public async Task TheWatcherReportsWhatIsAlreadyInFrontWhenItStarts()
    {
        Hardware.RequireDesktop();
        await using var watcher = new WindowsForegroundWatcher();

        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Whatever it is, it must be described rather than left blank.
        Assert.NotNull(watcher.Current);
        if (!watcher.Current.IsNone)
        {
            Assert.NotEqual(0, watcher.Current.ProcessId);
        }
    }

    private static void Record(string measurement) => Measurements.Record(measurement);

    /// <summary>CPU time used by one OS thread, or null when it has gone.</summary>
    private static TimeSpan? PumpThreadTime(uint threadId)
    {
        using var self = Process.GetCurrentProcess();
        foreach (ProcessThread thread in self.Threads)
        {
            if (thread.Id == threadId)
            {
                return thread.TotalProcessorTime;
            }
        }

        return null;
    }

}
