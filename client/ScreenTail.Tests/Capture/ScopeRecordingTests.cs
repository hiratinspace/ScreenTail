using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Capture;

/// <summary>
/// INV-6's scope clause, proved at the store (ST-048, weaknesses P0-4).
///
/// The state half of INV-6 — paused and suppressed write nothing — has always been proved here, by
/// <see cref="StateMachineTests.PausedAndSuppressedWriteNothing"/>. The scope half was not. The drop for
/// an excluded or out-of-scope window lived in the Windows capture loop, which no test project
/// references, and the only test that mentioned scope asserted on a <see cref="ScopeDecision"/> it built
/// itself — a restatement of the predicate rather than a proof that anything is dropped. Deleting the
/// drop left the whole suite green while the product wrote keystroke counts taken in a customer's
/// password manager.
///
/// These tests read the encrypted store afterwards, so they fail if the rule is removed from anywhere:
/// the decision, the loop, or the machine.
///
/// The scene sampler and mark moment had the same gap until 2026-10-14: their scope gates sat in Windows
/// classes no test referenced (2026-10-12 review, T-10). They live in Core now and are proved here too.
/// </summary>
public sealed class ScopeRecordingTests
{
    private static readonly RemoteTool ScreenConnect = new() { Kind = RemoteToolKind.Screenconnect, ClientVersion = "24.1" };

    [Fact]
    public async Task AnExcludedWindowWritesTheClickAndNothingElse()
    {
        // ST-023's rule and INV-6's, together: the click survives so Review can show "clicks logged, no
        // frames" rather than a silent gap, and everything keyboard-derived is dropped because a burst
        // count taken in a password manager is not made harmless by having no picture beside it.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var recorder = new SessionRecorder(
            harness.Machine,
            capturer,
            () => new ScopeDecision(CaptureScope.Excluded, null, null, "Not capturing — 1Password is on your excluded list.", 77));

        await recorder.RecordAsync(Everything(harness.Machine.NowMs), ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Single(stored.Events.OfType<ClickEvent>());
        Assert.Empty(stored.Events.OfType<TypingBurstEvent>());
        Assert.Empty(stored.Events.OfType<ShortcutEvent>());
        Assert.Empty(stored.Events.OfType<EnterEvent>());
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
        Assert.Equal(0, capturer.Captures);
        Assert.Equal(3, recorder.DroppedOutOfScope);
    }

    [Fact]
    public async Task AnOutOfScopeWindowWritesTheClickAndNothingElse()
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var recorder = new SessionRecorder(
            harness.Machine,
            capturer,
            () => new ScopeDecision(CaptureScope.OutOfScope, null, null, "Not capturing — Outlook is not part of this session.", 88));

        await recorder.RecordAsync(Everything(harness.Machine.NowMs), ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Single(stored.Events.OfType<ClickEvent>());
        Assert.Empty(stored.Events.OfType<TypingBurstEvent>());
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
        Assert.Equal(0, capturer.Captures);
    }

    [Fact]
    public async Task AWindowNothingIsKnownAboutWritesTheClickAndNothingElse()
    {
        // No scope decision has arrived yet — the session began before the foreground watcher reported.
        // Knowing nothing about the window in front is not a reason to record a keystroke count in it.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var recorder = new SessionRecorder(harness.Machine, capturer, () => null);

        await recorder.RecordAsync(Everything(harness.Machine.NowMs), ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Single(stored.Events.OfType<ClickEvent>());
        Assert.Empty(stored.Events.OfType<TypingBurstEvent>());
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
        Assert.Equal(0, capturer.Captures);
    }

    [Fact]
    public async Task AnInScopeWindowWritesEverythingAndTakesThePicture()
    {
        // The other half of the claim. A rule that drops everything would pass the three tests above.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var recorder = new SessionRecorder(
            harness.Machine,
            capturer,
            () => new ScopeDecision(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing ScreenConnect.", 99));

        await recorder.RecordAsync(Everything(harness.Machine.NowMs), ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Single(stored.Events.OfType<ClickEvent>());
        Assert.Single(stored.Events.OfType<TypingBurstEvent>());
        Assert.Single(stored.Events.OfType<ShortcutEvent>());
        Assert.Single(stored.Events.OfType<EnterEvent>());
        // The picture was taken and accepted; it waits in memory to be read (ADR-0006).
        Assert.Equal(1, harness.Pending.DepthFor(harness.Machine.SessionId!));
        Assert.Equal(1, capturer.Captures);
        Assert.Equal(0, recorder.DroppedOutOfScope);
        Assert.Equal(99, capturer.Expected);
    }

    [Fact]
    public async Task NothingIsWrittenWhileTheSessionIsPaused()
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        Assert.True(await harness.Machine.PauseAsync(ct));
        var capturer = new CountingCapturer();
        var recorder = new SessionRecorder(
            harness.Machine,
            capturer,
            () => new ScopeDecision(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing ScreenConnect.", 99));

        await recorder.RecordAsync(Everything(harness.Machine.NowMs), ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Empty(stored.Events.OfType<ClickEvent>());
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
        Assert.Equal(0, capturer.Captures);
    }

    [Fact]
    public async Task ABurstOfClicksInScopeIsOnePicture()
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var recorder = new SessionRecorder(
            harness.Machine,
            capturer,
            () => new ScopeDecision(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing ScreenConnect.", 99));
        var now = harness.Machine.NowMs;

        await recorder.RecordAsync(
            [Click(now), Click(now + 20), Click(now + 40)],
            ct);

        Assert.Equal(3, (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!.Events.OfType<ClickEvent>().Count());
        Assert.Equal(1, capturer.Captures);
    }

    [Fact]
    public async Task AClickUnderAnInScopeDecisionThatNamesNoWindowTakesNoPicture()
    {
        // The capturer reads a zero window as "whatever is in front". A decision that allows frames but
        // names no window has nothing to prove the picture against, so none is taken (INV-5).
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var recorder = new SessionRecorder(
            harness.Machine,
            capturer,
            () => new ScopeDecision(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing ScreenConnect."));

        await recorder.RecordAsync([Click(harness.Machine.NowMs)], ct);

        Assert.Single((await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!.Events.OfType<ClickEvent>());
        Assert.Equal(0, capturer.Captures);
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
    }

    [Theory]
    [InlineData(CaptureScope.Excluded)]
    [InlineData(CaptureScope.OutOfScope)]
    [InlineData(null)]
    public async Task AnOutOfScopeWindowProducesNoSceneFrame(CaptureScope? scope)
    {
        // The screen changes on every look, which is exactly what makes the sampler want a picture. The
        // decision names a real window, so only the scope rule stands between the sampler and Outlook or
        // 1Password. The gate is before the look, not after it: not even the grid is taken (INV-5).
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var scenes = new SceneRecorder(harness.Machine, capturer, () => Forbidding(scope), EagerSampler());

        for (var look = 0; look < 4; look++)
        {
            await scenes.SampleAsync(ct);
        }

        Assert.Equal(0, capturer.Grids);
        Assert.Equal(0, capturer.Captures);
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
    }

    [Fact]
    public async Task AnInScopeWindowThatChangesProducesASceneFrameOfThatWindow()
    {
        // The other half: a gate that refused everything would pass the theory above.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var scenes = new SceneRecorder(harness.Machine, capturer, InScope, EagerSampler());

        await scenes.SampleAsync(ct);   // the baseline, never kept
        await scenes.SampleAsync(ct);   // changed: kept

        Assert.Equal(2, capturer.Grids);
        Assert.Equal(99, capturer.GridExpected);
        Assert.Equal(1, capturer.Captures);
        Assert.Equal(99, capturer.Expected);
        Assert.True(harness.Pending.TryTake(out var queued));
        Assert.Equal(FrameTrigger.SceneChange, queued.Frame.Trigger);
    }

    [Fact]
    public async Task ASceneWhoseWindowIsGoneByThePictureIsAMissingFrameNotABlankOne()
    {
        // The technician moved to another window between the look and the picture, so the capturer found
        // the window in front was not the one judged and took nothing.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer { WindowGone = true };
        var scenes = new SceneRecorder(harness.Machine, capturer, InScope, EagerSampler());
        var missed = 0;
        var staged = 0;
        scenes.FrameMissed += () => missed++;
        scenes.FrameStaged += _ => staged++;

        await scenes.SampleAsync(ct);
        await scenes.SampleAsync(ct);

        Assert.Equal(1, capturer.Captures);
        Assert.Equal(1, missed);
        Assert.Equal(0, staged);
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
    }

    [Fact]
    public async Task ASceneUnderAnInScopeDecisionThatNamesNoWindowIsNotSampled()
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var scenes = new SceneRecorder(harness.Machine, capturer, InScopeWithNoWindow, EagerSampler());

        await scenes.SampleAsync(ct);
        await scenes.SampleAsync(ct);

        Assert.Equal(0, capturer.Grids);
        Assert.Equal(0, capturer.Captures);
    }

    [Fact]
    public async Task NoSceneIsSampledWhileTheSessionIsPaused()
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        Assert.True(await harness.Machine.PauseAsync(ct));
        var capturer = new CountingCapturer();
        var scenes = new SceneRecorder(harness.Machine, capturer, InScope, EagerSampler());

        await scenes.SampleAsync(ct);
        await scenes.SampleAsync(ct);

        Assert.Equal(0, capturer.Grids);
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
    }

    [Fact]
    public async Task AScenePausedAndResumedStartsFromAFreshBaseline()
    {
        // Comparing against what was on screen before the pause would report a change that happened while
        // capture was off. The grids alternate, so without the fresh baseline the first look after resuming
        // would be kept.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var scenes = new SceneRecorder(harness.Machine, capturer, InScope, EagerSampler());
        await scenes.SampleAsync(ct);
        await scenes.SampleAsync(ct);
        Assert.Equal(1, capturer.Captures);

        Assert.True(await harness.Machine.PauseAsync(ct));
        await scenes.SampleAsync(ct);
        Assert.True(await harness.Machine.ResumeAsync(ct));
        await scenes.SampleAsync(ct);

        Assert.Equal(1, capturer.Captures);

        // One look since the fresh start, and it became the baseline.
        Assert.Equal(new SceneCounts(1, 0, 0, 0, 0), scenes.Counts);
    }

    [Fact]
    public async Task AResetAskedForBetweenLooksStartsTheNextFromAFreshBaseline()
    {
        // What a new session asks for. It may arrive from another thread, so it is applied by the next look.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var capturer = new CountingCapturer();
        var scenes = new SceneRecorder(harness.Machine, capturer, InScope, EagerSampler());
        await scenes.SampleAsync(ct);

        scenes.Reset();
        scenes.Reset();
        await scenes.SampleAsync(ct);

        Assert.Equal(2, capturer.Grids);
        Assert.Equal(0, capturer.Captures);
        Assert.Equal(new SceneCounts(1, 0, 0, 0, 0), scenes.Counts);
    }

    [Theory]
    [InlineData(CaptureScope.Excluded)]
    [InlineData(CaptureScope.OutOfScope)]
    [InlineData(null)]
    public async Task AMarkMomentOverAnExcludedWindowTakesNoFrameButWritesTheMarker(CaptureScope? scope)
    {
        // Through the machine, the way the hotkey and the pipe command both arrive. Asking for a frame is
        // not permission to take one, but the marker still says the technician thought this mattered.
        var capturer = new CountingCapturer();
        var sources = new ScopedCaptureSources(capturer, () => Forbidding(scope), () => { });
        var outOfScope = 0;
        sources.MomentOutOfScope += () => outOfScope++;
        await using var harness = await MachineHarness.StartAsync(sources: sources);
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);

        Assert.True(await harness.Machine.MarkMomentAsync(ct));

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Single(stored.Events.OfType<MarkerEvent>());
        Assert.Equal(0, capturer.Captures);
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
        Assert.Equal(1, outOfScope);
    }

    [Fact]
    public async Task AMarkMomentInScopeTakesTheFrameOfTheJudgedWindow()
    {
        var capturer = new CountingCapturer();
        var resets = 0;
        var sources = new ScopedCaptureSources(capturer, InScope, () => resets++);
        await using var harness = await MachineHarness.StartAsync(sources: sources);
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);

        Assert.True(await harness.Machine.MarkMomentAsync(ct));

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Single(stored.Events.OfType<MarkerEvent>());
        Assert.Equal(1, capturer.Captures);
        Assert.Equal(99, capturer.Expected);
        Assert.True(harness.Pending.TryTake(out var queued));
        Assert.Equal(FrameTrigger.Marker, queued.Frame.Trigger);
        Assert.Equal(1, resets);
    }

    [Fact]
    public async Task AMarkMomentUnderAnInScopeDecisionThatNamesNoWindowTakesNoFrame()
    {
        var capturer = new CountingCapturer();
        var sources = new ScopedCaptureSources(capturer, InScopeWithNoWindow, () => { });
        await using var harness = await MachineHarness.StartAsync(sources: sources);
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);

        Assert.True(await harness.Machine.MarkMomentAsync(ct));

        Assert.Equal(0, capturer.Captures);
        Assert.Equal(0, harness.Pending.DepthFor(harness.Machine.SessionId!));
    }

    [Fact]
    public async Task AMarkMomentAfterTheSessionStopsTakesNothing()
    {
        // A chord that lands just after Stop reaches the sources with no session behind them.
        var capturer = new CountingCapturer();
        var resets = 0;
        var sources = new ScopedCaptureSources(capturer, InScope, () => resets++);
        await using var harness = await MachineHarness.StartAsync(sources: sources);
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        Assert.True(await harness.Machine.StopAsync(ct));

        await sources.MarkMomentAsync(ct);

        Assert.Equal(0, capturer.Captures);
        Assert.Equal(2, resets);
    }

    private static ScopeDecision? InScope() =>
        new(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing ScreenConnect.", 99);

    private static ScopeDecision? InScopeWithNoWindow() =>
        new(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing ScreenConnect.");

    /// <summary>A decision that forbids frames yet names a real window, so the window check alone cannot save it.</summary>
    private static ScopeDecision? Forbidding(CaptureScope? scope) =>
        scope is { } forbidden ? new(forbidden, null, null, "Not capturing.", 77) : null;

    /// <summary>No quiet period, so a second changed look is kept and the tests need no clock.</summary>
    private static SceneSampler EagerSampler() => new(options: new SceneSamplerOptions { MinimumGap = TimeSpan.Zero });

    private static ClickEvent Click(long tsMs) => new() { TsMs = tsMs, X = 4, Y = 5, Button = MouseButton.Left };

    private static List<SessionEvent> Everything(long tsMs) =>
    [
        Click(tsMs),
        new TypingBurstEvent { TsMs = tsMs + 1, CharCount = 12 },
        new ShortcutEvent { TsMs = tsMs + 2 },
        new EnterEvent { TsMs = tsMs + 3 },
    ];

    /// <summary>
    /// Stands in for the Windows screen grab, and remembers what scope told it to expect. Its scene grid
    /// flips between two opposite ramps on every look, so every look after the first is a change.
    /// </summary>
    private sealed class CountingCapturer : IScreenshotCapturer
    {
        private static readonly byte[] Rising = Ramp(rising: true);
        private static readonly byte[] Falling = Ramp(rising: false);

        public int Captures { get; private set; }

        public nint Expected { get; private set; }

        public int Grids { get; private set; }

        public nint GridExpected { get; private set; }

        /// <summary>The window in front is no longer the one judged, so the full capture comes back empty.</summary>
        public bool WindowGone { get; init; }

        public CapturedFrame? CaptureForegroundWindow(nint expected = 0)
        {
            Captures++;
            Expected = expected;
            return WindowGone ? null : new CapturedFrame([1, 2, 3, 4], 100, 80, 200, 160, default);
        }

        public byte[]? CaptureSceneGrid(nint expected = 0)
        {
            GridExpected = expected;
            return Grids++ % 2 == 0 ? Rising : Falling;
        }

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
