using ScreenTail.Core.Perf;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Performance;

/// <summary>
/// ST-031 AC2: ten seconds over the processor budget and capture gives way, scene sampling first and OCR
/// after, one step at a time, and it comes back only once the process has stayed well under the budget.
///
/// A processor clock and a wall clock the test moves by hand, so every reading is the one written down
/// here and nothing depends on how busy the machine running the suite is.
/// </summary>
public sealed class CaptureThrottleTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private MachineHarness? _harness;

    [Fact]
    public void TheBudgetIsFifteenPercentOfAFourCoreMachine()
    {
        Assert.Equal(0.15, CpuBudget.RecordingShareOf(4), 6);

        // A bigger machine is held to the same amount of processor, 60% of one core.
        Assert.Equal(0.075, CpuBudget.RecordingShareOf(8), 6);

        // A smaller one to 15% of itself, never to more of it because the reference machine had four cores.
        Assert.Equal(0.15, CpuBudget.RecordingShareOf(2), 6);
        Assert.Equal(0.15, CpuBudget.RecordingShareOf(1), 6);
    }

    [Fact]
    public void TenSecondsOverBudgetLowersSceneSamplingFirst()
    {
        var (throttle, cpu, time) = Rig(processors: 4);

        Run(throttle, cpu, time, share: 0.25, seconds: 9);
        Assert.Equal(CaptureLevel.Full, throttle.Level);

        Run(throttle, cpu, time, share: 0.25, seconds: 1);

        Assert.Equal(CaptureLevel.FewerSamples, throttle.Level);
        Assert.Equal(2, throttle.SampleEveryNth);
        Assert.False(throttle.ReadsOneFrameAtATime);
        Assert.True(throttle.LooksAtScenes);
    }

    [Fact]
    public void StayingOverBudgetGivesUpSamplingBeforeOcrOneStepEveryTenSeconds()
    {
        var (throttle, cpu, time) = Rig(processors: 4);
        var changes = new List<CaptureLevelChange>();
        throttle.Changed += changes.Add;

        // A step every ten seconds, never sooner: each one is given its own ten seconds to work.
        foreach (var expected in new[] { CaptureLevel.FewerSamples, CaptureLevel.FewestSamples, CaptureLevel.OcrOneAtATime, CaptureLevel.OcrClicksOnly })
        {
            var before = throttle.Level;
            Run(throttle, cpu, time, share: 0.5, seconds: 9);
            Assert.Equal(before, throttle.Level);
            Run(throttle, cpu, time, share: 0.5, seconds: 1);
            Assert.Equal(expected, throttle.Level);
        }

        Run(throttle, cpu, time, share: 0.5, seconds: 20);

        Assert.Equal(
            [
                (CaptureLevel.Full, CaptureLevel.FewerSamples),
                (CaptureLevel.FewerSamples, CaptureLevel.FewestSamples),
                (CaptureLevel.FewestSamples, CaptureLevel.OcrOneAtATime),
                (CaptureLevel.OcrOneAtATime, CaptureLevel.OcrClicksOnly),
            ],
            changes.Select(change => (change.From, change.To)));

        // The reading that tipped it, as a share of the machine, is what the host logs.
        Assert.All(changes, change => Assert.Equal(0.5, change.Share, 6));
        Assert.Equal(CaptureLevel.OcrClicksOnly, throttle.Level);
        Assert.True(throttle.ReadsOneFrameAtATime);

        // The deepest step reads only what a click or a marked moment asked for: no scene frames at all.
        Assert.False(throttle.LooksAtScenes);
    }

    [Fact]
    public void ADipUnderBudgetStartsTheTenSecondsAgain()
    {
        var (throttle, cpu, time) = Rig(processors: 4);

        Run(throttle, cpu, time, share: 0.3, seconds: 9);
        Run(throttle, cpu, time, share: 0.1, seconds: 1);
        Run(throttle, cpu, time, share: 0.3, seconds: 9);

        Assert.Equal(CaptureLevel.Full, throttle.Level);

        Run(throttle, cpu, time, share: 0.3, seconds: 1);
        Assert.Equal(CaptureLevel.FewerSamples, throttle.Level);
    }

    [Fact]
    public void JustUnderBudgetNeverThrottles()
    {
        var (throttle, cpu, time) = Rig(processors: 4);

        Run(throttle, cpu, time, share: 0.149, seconds: 120);

        Assert.Equal(CaptureLevel.Full, throttle.Level);
    }

    [Fact]
    public void ItStepsBackUpOnlyAfterStayingWellUnderBudgetForThirtySeconds()
    {
        var (throttle, cpu, time) = Rig(processors: 4);
        Run(throttle, cpu, time, share: 0.3, seconds: 20);
        Assert.Equal(CaptureLevel.FewestSamples, throttle.Level);

        // Under the budget but inside the band below it: the level holds, however long it lasts. Stepping
        // back up here is the step that would put the process over again.
        Run(throttle, cpu, time, share: 0.135, seconds: 120);
        Assert.Equal(CaptureLevel.FewestSamples, throttle.Level);

        Run(throttle, cpu, time, share: 0.05, seconds: 29);
        Assert.Equal(CaptureLevel.FewestSamples, throttle.Level);

        Run(throttle, cpu, time, share: 0.05, seconds: 1);
        Assert.Equal(CaptureLevel.FewerSamples, throttle.Level);

        Run(throttle, cpu, time, share: 0.05, seconds: 30);
        Assert.Equal(CaptureLevel.Full, throttle.Level);
    }

    [Fact]
    public void ALoadHoveringAtTheBudgetDoesNotFlap()
    {
        // Alternating a little over and a little under: never ten seconds over, never thirty well under.
        var (throttle, cpu, time) = Rig(processors: 4);
        Run(throttle, cpu, time, share: 0.3, seconds: 10);
        var changes = 0;
        throttle.Changed += _ => changes++;

        for (var i = 0; i < 60; i++)
        {
            Run(throttle, cpu, time, share: 0.16, seconds: 5);
            Run(throttle, cpu, time, share: 0.13, seconds: 5);
        }

        Assert.Equal(CaptureLevel.FewerSamples, throttle.Level);
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData(0.7, CaptureLevel.FewerSamples)]
    [InlineData(0.5, CaptureLevel.Full)]
    public void AnEightProcessorMachineIsHeldToSixtyPercentOfOneCore(double cores, CaptureLevel expected)
    {
        var (throttle, cpu, time) = Rig(processors: 8);

        for (var second = 0; second < 10; second++)
        {
            cpu.Use(TimeSpan.FromSeconds(cores));
            time.Advance(TimeSpan.FromSeconds(1));
            throttle.Observe();
        }

        Assert.Equal(expected, throttle.Level);
    }

    [Fact]
    public void TheEndOfASessionPutsCaptureBackToFullAndForgetsTheReadings()
    {
        var (throttle, cpu, time) = Rig(processors: 4);
        Run(throttle, cpu, time, share: 0.5, seconds: 30);
        Assert.Equal(CaptureLevel.OcrOneAtATime, throttle.Level);
        var changes = new List<CaptureLevelChange>();
        throttle.Changed += changes.Add;

        throttle.Reset();

        Assert.Equal(CaptureLevel.Full, throttle.Level);
        Assert.Equal((CaptureLevel.OcrOneAtATime, CaptureLevel.Full), (Assert.Single(changes).From, changes[0].To));

        // Processor used between sessions is not charged to the next one: its first reading is a baseline.
        cpu.Use(TimeSpan.FromSeconds(400));
        time.Advance(TimeSpan.FromMinutes(5));
        throttle.Observe();
        Run(throttle, cpu, time, share: 0.5, seconds: 9);
        Assert.Equal(CaptureLevel.Full, throttle.Level);
    }

    [Fact]
    public async Task TheLoopPutsCaptureBackToFullWhenTheSessionEnds()
    {
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        var rig = new ThrottleRig(new CaptureThrottleOptions { ReadEvery = TimeSpan.FromMilliseconds(10) });
        var throttle = rig.Throttle;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = throttle.RunAsync(harness.Machine, null, stopping.Token);
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));

        // The loop's own readings take it down: a minute of every core flat out between two of them, four
        // times. Between minutes the hand-moved clock stands still, so those readings say nothing.
        foreach (var level in new[] { CaptureLevel.FewerSamples, CaptureLevel.FewestSamples, CaptureLevel.OcrOneAtATime, CaptureLevel.OcrClicksOnly })
        {
            rig.Cpu.Use(TimeSpan.FromMinutes(4));
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            Assert.True(await UntilAsync(() => throttle.Level == level), $"the loop's readings left capture at {throttle.Level}, not {level}");
        }

        await Task.Delay(100, ct);
        Assert.Equal(CaptureLevel.OcrClicksOnly, throttle.Level);

        Assert.True(await harness.Machine.StopAsync(ct));
        var back = await UntilAsync(() => throttle.Level == CaptureLevel.Full);
        await stopping.CancelAsync();
        await running;

        Assert.True(back, $"the session ended and capture stayed at {throttle.Level}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASessionThatEndsAndAnotherThatStartsBetweenTwoReadingsStartsAtFull(bool discard)
    {
        // The loop looks at the machine once a second. A discard goes straight from Recording to Idle, and a
        // short finalize is over well inside a second, so the next reading can find a new session recording
        // without the loop ever having seen the old one end. The new session must not inherit the old
        // level or the old readings (2026-10-29 review).
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        var (throttle, cpu, time) = Rig(processors: 4);
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));
        throttle.Turn(harness.Machine);
        Run(throttle, cpu, time, share: 0.5, seconds: 40);
        Assert.Equal(CaptureLevel.OcrClicksOnly, throttle.Level);

        Assert.True(discard ? await harness.Machine.DiscardAsync(ct) : await harness.Machine.StopAsync(ct));
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));
        cpu.Use(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(1));
        throttle.Turn(harness.Machine);

        Assert.Equal(CaptureLevel.Full, throttle.Level);

        // And that turn was a baseline: nine more seconds over budget are not yet ten.
        Run(throttle, cpu, time, share: 0.5, seconds: 9);
        Assert.Equal(CaptureLevel.Full, throttle.Level);
    }

    [Fact]
    public void ASubscriberThatThrowsDoesNotCostTheNextStepItsTenSeconds()
    {
        // The host logs from Changed. A subscriber that throws must not leave the count where it was, or
        // the next over-budget reading would take another step at once.
        var (throttle, cpu, time) = Rig(processors: 4);
        var thrown = 0;
        throttle.Changed += _ =>
        {
            if (thrown++ == 0)
            {
                throw new InvalidOperationException("the logger fell over");
            }
        };

        for (var second = 0; second < 10; second++)
        {
            cpu.Use(TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromSeconds(1));
            try
            {
                throttle.Observe();
            }
            catch (InvalidOperationException)
            {
            }
        }

        Assert.Equal(CaptureLevel.FewerSamples, throttle.Level);
        Run(throttle, cpu, time, share: 0.5, seconds: 9);
        Assert.Equal(CaptureLevel.FewerSamples, throttle.Level);
    }

    [Fact]
    public async Task WithNoSessionTheThrottleReadsNothingAndArmsNoTimer()
    {
        var harness = await HarnessAsync();
        var time = new CountingTime();
        var cpu = new CountingProcessorTime();
        var throttle = new CaptureThrottle(cpu, time, new CaptureThrottleOptions { ReadEvery = TimeSpan.FromMilliseconds(10) });

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = throttle.RunAsync(harness.Machine, null, stopping.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await stopping.CancelAsync();
        await running;

        Assert.Equal(0, time.Created);
        Assert.Equal(0, cpu.Reads);
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private async Task<MachineHarness> HarnessAsync() => _harness ??= await MachineHarness.StartAsync();

    /// <summary>A throttle over a fake processor and a hand-moved clock, with its first reading taken.</summary>
    private static (CaptureThrottle Throttle, FakeProcessorTime Cpu, ManualTime Time) Rig(int processors)
    {
        var cpu = new FakeProcessorTime(processors);
        var time = new ManualTime(At);
        var throttle = new CaptureThrottle(cpu, time);

        // The first reading is only where the next one is measured from.
        throttle.Observe();
        return (throttle, cpu, time);
    }

    /// <summary>One reading a second for <paramref name="seconds"/>, each at <paramref name="share"/> of the machine.</summary>
    private static void Run(CaptureThrottle throttle, FakeProcessorTime cpu, ManualTime time, double share, int seconds)
    {
        for (var second = 0; second < seconds; second++)
        {
            cpu.Use(TimeSpan.FromSeconds(share * cpu.Processors));
            time.Advance(TimeSpan.FromSeconds(1));
            throttle.Observe();
        }
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class CountingProcessorTime : IProcessorTime
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public TimeSpan Used
        {
            get
            {
                _ = Interlocked.Increment(ref _reads);
                return TimeSpan.Zero;
            }
        }

        public int Processors => 4;
    }
}
