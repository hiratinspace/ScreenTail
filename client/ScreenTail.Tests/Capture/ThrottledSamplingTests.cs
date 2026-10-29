using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Perf;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Performance;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Capture;

/// <summary>
/// ST-031 AC2 on the sampling side: over the processor budget the screen is looked at less often, and that
/// is the first thing to give. A look skipped is a picture never taken, so a frame that is taken is staged
/// waiting for redaction exactly as at full capture (INV-1).
/// </summary>
public sealed class ThrottledSamplingTests
{
    private static readonly RemoteTool ScreenConnect = new() { Kind = RemoteToolKind.Screenconnect, ClientVersion = "24.1" };

    [Theory]
    [InlineData(CaptureLevel.Full, 8)]
    [InlineData(CaptureLevel.FewerSamples, 4)]
    [InlineData(CaptureLevel.FewestSamples, 2)]
    [InlineData(CaptureLevel.OcrOneAtATime, 2)]
    [InlineData(CaptureLevel.OcrClicksOnly, 0)]
    public async Task OverBudgetTheScreenIsLookedAtLessOften(CaptureLevel level, int looks)
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new FlippingCapturer();
        var scenes = new SceneRecorder(harness.Machine, capturer, InScope, EagerSampler(), Throttles.At(level));

        for (var tick = 0; tick < 8; tick++)
        {
            await scenes.SampleAsync(ct);
        }

        Assert.Equal(looks, capturer.Grids);
    }

    [Fact]
    public async Task AFrameTakenOverBudgetIsStagedWaitingForRedactionAsAtFullCapture()
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new FlippingCapturer();
        var scenes = new SceneRecorder(harness.Machine, capturer, InScope, EagerSampler(), Throttles.At(CaptureLevel.OcrOneAtATime));

        for (var tick = 0; tick < 8; tick++)
        {
            await scenes.SampleAsync(ct);
        }

        // Two looks: the baseline, and one change that was kept.
        Assert.Equal(1, capturer.Captures);
        Assert.True(harness.Pending.TryTake(out var queued));
        Assert.Equal(FrameTrigger.SceneChange, queued.Frame.Trigger);
        Assert.Equal(0, await harness.Store.CountAllPendingFramesAsync(ct));
        Assert.Empty((await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!.Frames);
    }

    [Fact]
    public async Task AThrottledSamplerStillSeesTheSessionEnd()
    {
        // Skipping looks must not skip the reset: a session that stops and starts again is a new scene,
        // so its first look is a baseline and never a frame.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new FlippingCapturer();
        var scenes = new SceneRecorder(harness.Machine, capturer, InScope, EagerSampler(), Throttles.At(CaptureLevel.FewerSamples));
        await scenes.SampleAsync(ct);
        await scenes.SampleAsync(ct);   // the baseline

        Assert.True(await harness.Machine.PauseAsync(ct));
        await scenes.SampleAsync(ct);
        Assert.True(await harness.Machine.ResumeAsync(ct));
        await scenes.SampleAsync(ct);
        await scenes.SampleAsync(ct);   // a baseline again, not a change

        Assert.Equal(2, capturer.Grids);
        Assert.Equal(0, capturer.Captures);
    }

    private static ScopeDecision? InScope() =>
        new(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing ScreenConnect.", 99);

    /// <summary>No quiet period, so every changed look is kept and the tests need no clock.</summary>
    private static SceneSampler EagerSampler() => new(options: new SceneSamplerOptions { MinimumGap = TimeSpan.Zero });

    /// <summary>A scene grid that flips between two opposite ramps on every look, so every look after the first is a change.</summary>
    private sealed class FlippingCapturer : IScreenshotCapturer
    {
        private static readonly byte[] Rising = Ramp(rising: true);
        private static readonly byte[] Falling = Ramp(rising: false);

        public int Captures { get; private set; }

        public int Grids { get; private set; }

        public CapturedFrame? CaptureForegroundWindow(nint expected = 0)
        {
            Captures++;
            return new CapturedFrame([1, 2, 3, 4], 100, 80, 200, 160, default);
        }

        public byte[]? CaptureSceneGrid(nint expected = 0) => Grids++ % 2 == 0 ? Rising : Falling;

        private static byte[] Ramp(bool rising)
        {
            var grid = new byte[PerceptualHash.GridLength];
            for (var i = 0; i < grid.Length; i++)
            {
                var value = ((i % PerceptualHash.Columns) * 10) + ((i / PerceptualHash.Columns) * 5);
                grid[i] = (byte)(rising ? value : 255 - value);
            }

            return grid;
        }
    }
}
