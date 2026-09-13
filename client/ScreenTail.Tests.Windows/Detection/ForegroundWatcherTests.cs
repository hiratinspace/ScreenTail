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
    private static bool HasDesktop =>
        new WindowsCapabilityProbe().Probe()[Capability.DesktopSession].State == CapabilityState.Ok;

    /// <summary>
    /// Whether a timing measured here means anything. A GitHub-hosted runner is a shared cloud VM whose
    /// numbers the test loop has never counted (docs/dev/windows-test-loop.md) — it measured 322 ms against
    /// the 100 ms budget on a build that the laptop passed. Correctness is still checked everywhere; only
    /// the budget is held back for hardware we can reason about.
    /// </summary>
    private static bool PerformanceCounts => !string.Equals(
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"),
        "github-hosted",
        StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task AFocusChangeIsReportedWithinTheBudget()
    {
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
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
        Assert.SkipUnless(window.TakeForeground(), "Windows refused to change the foreground window on this desktop.");
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
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
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
        Assert.SkipUnless(window.TakeForeground(), "Windows refused to change the foreground window on this desktop.");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        window.Retitle(second);

        var reported = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(second, reported.Title);
    }

    [Fact]
    public async Task TheWatcherCostsAlmostNothingWhileNothingHappens()
    {
        // AC3: idle CPU < 0.5%. The hook is event-driven; the timer is the only thing ticking.
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
        await using var watcher = new WindowsForegroundWatcher();
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

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

        var percent = used.TotalMilliseconds / elapsed.TotalMilliseconds * 100;
        var quanta = used.TotalMilliseconds / 15.625;
        Record($"Watcher used **{percent:F3}%** of a core over {elapsed.TotalSeconds:F0}s idle — {quanta:F0} scheduler quanta (budget 0.5%, enforced: {PerformanceCounts})");
        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count; the laptop enforces this.");
        Assert.True(percent < 0.5, $"the watcher used {percent:F3}% of a core while idle, budget is 0.5%");
    }

    [Fact]
    public async Task StartingAndStoppingLeavesNoThreadBehind()
    {
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
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
        Assert.SkipUnless(HasDesktop, "No interactive desktop on this runner.");
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
