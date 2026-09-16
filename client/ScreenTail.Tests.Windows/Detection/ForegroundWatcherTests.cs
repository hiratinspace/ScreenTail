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
        await using var watcher = new WindowsForegroundWatcher();
        var renamed = new TaskCompletionSource<ForegroundWindowInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += info =>
        {
            if (info.Title == second)
            {
                renamed.TrySetResult(info);
            }
        };
        await watcher.StartAsync(TestContext.Current.CancellationToken);

        using var window = DesktopWindow.Create(first);
        window.RequireForeground();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        window.Retitle(second);

        var reported = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(second, reported.Title);
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
        var window = TimeSpan.FromSeconds(20);
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
