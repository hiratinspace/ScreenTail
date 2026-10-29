using ScreenTail.Core.Capture;
using ScreenTail.Core.Perf;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Performance;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// ST-031 AC2 on the reading side: over the processor budget, OCR gives way by reading fewer frames at once
/// and then by waiting between them. It never gives way by reading a frame less carefully (INV-1): a frame
/// that is stored has been read, searched and painted exactly as it would have been at full capture, and a
/// frame waiting its turn waits in memory and never reaches the store unread.
/// </summary>
public sealed class ThrottledRedactionTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private MachineHarness? _harness;

    [Fact]
    public async Task AtFullCaptureTwoFramesAreReadAtOnce()
    {
        // The other half of the next test: a worker that only ever read one frame would pass it.
        var (worker, recogniser, _) = await StartAsync(Throttles.At(CaptureLevel.Full));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = worker.RunAsync(stopping.Token);
        await StageAsync("f1", "f2");

        var both = await UntilAsync(() => recogniser.Started == 2);
        recogniser.Release();
        await stopping.CancelAsync();
        await running;

        Assert.True(both, $"only {recogniser.Started} of two frames was read at once at full capture");
        Assert.Equal(2, recogniser.MostAtOnce);
    }

    [Fact]
    public async Task OverBudgetOnlyOneFrameIsReadAtATime()
    {
        var (worker, recogniser, _) = await StartAsync(Throttles.At(CaptureLevel.OcrOneAtATime));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = worker.RunAsync(stopping.Token);
        await StageAsync("f1", "f2");

        Assert.True(await UntilAsync(() => recogniser.Started == 1));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var startedWhileOneWasBeingRead = recogniser.Started;

        recogniser.Release();
        var finished = await UntilAsync(() => worker.Progress.Frames == 2);
        await stopping.CancelAsync();
        await running;

        Assert.Equal(1, startedWhileOneWasBeingRead);
        Assert.Equal(1, recogniser.MostAtOnce);
        Assert.True(finished, "the second frame was never read");
    }

    [Fact]
    public async Task AReaderSetAsideComesBackWhenCaptureReturnsToFull()
    {
        var throttle = Throttles.At(CaptureLevel.OcrOneAtATime);
        var (worker, recogniser, _) = await StartAsync(throttle);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = worker.RunAsync(stopping.Token);
        await StageAsync("f1", "f2");
        Assert.True(await UntilAsync(() => recogniser.Started == 1));

        // The session ends, so capture goes back to full; the first frame is still being read.
        throttle.Reset();

        var second = await UntilAsync(() => recogniser.Started == 2);
        recogniser.Release();
        await stopping.CancelAsync();
        await running;

        Assert.True(second, "the reader set aside over budget did not come back when the level moved");
    }

    [Fact]
    public async Task DeferredOcrWaitsAfterEachFrameAndTheFrameWaitingIsNeverStoredUnread()
    {
        // A frozen clock: the pause after the first frame never ends, so the second is read only if
        // something skipped the pause.
        var (worker, recogniser, store) = await StartAsync(Throttles.At(CaptureLevel.OcrDeferred), new CountingTime(frozen: true));
        recogniser.Release();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = worker.RunAsync(stopping.Token);
        await StageAsync("f1", "f2");

        Assert.True(await UntilAsync(() => worker.Progress.Frames == 1));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var read = recogniser.Started;
        var sessionId = Harness.Machine.SessionId!;
        var stored = (await store.LoadSessionAsync(sessionId, TestContext.Current.CancellationToken))!.Frames;
        var waiting = Harness.Pending.DepthFor(sessionId);
        await stopping.CancelAsync();
        await running;

        Assert.Equal(1, read);
        Assert.Single(stored);
        Assert.Equal(1, waiting);
        Assert.Equal(0, await store.CountAllPendingFramesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OneFrameAtATimeWithoutTheDeferralReadsTheNextAtOnce()
    {
        // The other half of the test above, on the same frozen clock: the pause belongs to the deepest
        // level only.
        var (worker, recogniser, _) = await StartAsync(Throttles.At(CaptureLevel.OcrOneAtATime), new CountingTime(frozen: true));
        recogniser.Release();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = worker.RunAsync(stopping.Token);
        await StageAsync("f1", "f2");

        var both = await UntilAsync(() => worker.Progress.Frames == 2);
        await stopping.CancelAsync();
        await running;

        Assert.True(both, "one frame at a time held the second frame back as if it were deferred");
    }

    [Theory]
    [InlineData(CaptureLevel.Full)]
    [InlineData(CaptureLevel.OcrOneAtATime)]
    [InlineData(CaptureLevel.OcrDeferred)]
    public async Task AFrameStoredUnderAnyLevelIsRedactedExactlyAsAtFullCapture(CaptureLevel level)
    {
        // INV-1: throttling reads fewer frames, never a frame less carefully.
        var (worker, recogniser, store) = await StartAsync(Throttles.At(level));
        recogniser.Release();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = worker.RunAsync(stopping.Token);
        await StageAsync("f1");

        Assert.True(await UntilAsync(() => worker.Progress.Frames == 1));
        await stopping.CancelAsync();
        await running;

        var frame = Assert.Single((await store.LoadSessionAsync(Harness.Machine.SessionId!, TestContext.Current.CancellationToken))!.Frames);
        Assert.False(frame.RedactionPending);
        Assert.Equal("Card [CARD]", frame.OcrText);
        Assert.Equal(MaskKind.Card, Assert.Single(frame.MaskedRegions).Kind);
        Assert.Equal(new RedactionEngine().RedactFrame(GatedRecogniser.Words).Regions, frame.MaskedRegions);
        Assert.Equal([1, 2, 3, 4, 1], (await store.GetRedactedFrameImageAsync("f1", TestContext.Current.CancellationToken))!);
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private MachineHarness Harness => _harness ?? throw new InvalidOperationException("no harness yet");

    private async Task<(RedactionWorker Worker, GatedRecogniser Recogniser, SqliteSessionStore Store)> StartAsync(
        CaptureThrottle throttle,
        TimeProvider? time = null)
    {
        _harness = await MachineHarness.StartAsync();
        Assert.True(await _harness.Machine.StartAsync(Rdp, ct: TestContext.Current.CancellationToken));
        var recogniser = new GatedRecogniser();
        var worker = new RedactionWorker(
            _harness.Store,
            recogniser,
            new MarkingMasker(),
            new RedactionEngine(),
            _harness.Pending,
            time: time,
            throttle: throttle);
        return (worker, recogniser, _harness.Store);
    }

    private async Task StageAsync(params string[] ids)
    {
        foreach (var id in ids)
        {
            Assert.True(await Harness.Machine.TryStageFrameAsync(
                new StagedFrame(id, Harness.Machine.NowMs, FrameTrigger.Click, 800, 600, null, new byte[] { 1, 2, 3, 4 }),
                TestContext.Current.CancellationToken));
        }
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>Reads a card number, and holds every read until the test lets them go.</summary>
    private sealed class GatedRecogniser : IFrameTextRecogniser
    {
        public static readonly OcrWord[] Words = [new("Card", 10, 40, 50, 20), new("4111111111111111", 70, 40, 200, 20)];

        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _atOnce;
        private int _mostAtOnce;

        public int Started => Volatile.Read(ref _started);

        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public void Release() => _gate.TrySetResult();

        public async Task<RecognisedText> ReadAsync(ReadOnlyMemory<byte> image, CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _started);
            var now = Interlocked.Increment(ref _atOnce);
            int most;
            while (now > (most = Volatile.Read(ref _mostAtOnce)) && Interlocked.CompareExchange(ref _mostAtOnce, now, most) != most)
            {
            }

            try
            {
                await _gate.Task.WaitAsync(ct);
                return new RecognisedText(Words, 0.95);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _atOnce);
            }
        }
    }

    /// <summary>Appends how many regions it painted, so the stored image is known to be its output.</summary>
    private sealed class MarkingMasker : IFrameMasker
    {
        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge) =>
            new([.. image.Span.ToArray(), (byte)regions.Count], 800, 600);
    }
}
