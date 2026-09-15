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
        Assert.Equal(0, await harness.Store.CountPendingFramesAsync(harness.Machine.SessionId!, ct));
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
        Assert.Equal(0, await harness.Store.CountPendingFramesAsync(harness.Machine.SessionId!, ct));
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
        Assert.Equal(0, await harness.Store.CountPendingFramesAsync(harness.Machine.SessionId!, ct));
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
        Assert.Equal(1, await harness.Store.CountPendingFramesAsync(harness.Machine.SessionId!, ct));
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
        Assert.Equal(0, await harness.Store.CountPendingFramesAsync(harness.Machine.SessionId!, ct));
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

    private static ClickEvent Click(long tsMs) => new() { TsMs = tsMs, X = 4, Y = 5, Button = MouseButton.Left };

    private static List<SessionEvent> Everything(long tsMs) =>
    [
        Click(tsMs),
        new TypingBurstEvent { TsMs = tsMs + 1, CharCount = 12 },
        new ShortcutEvent { TsMs = tsMs + 2 },
        new EnterEvent { TsMs = tsMs + 3 },
    ];

    /// <summary>Stands in for the Windows screen grab, and remembers what scope told it to expect.</summary>
    private sealed class CountingCapturer : IScreenshotCapturer
    {
        public int Captures { get; private set; }

        public nint Expected { get; private set; }

        public CapturedFrame? CaptureForegroundWindow(int maxEdge = Downscale.MaxEdge, nint expected = 0)
        {
            Captures++;
            Expected = expected;
            return new CapturedFrame([1, 2, 3, 4], 100, 80, 200, 160, default);
        }

        public byte[]? CaptureSceneGrid(nint expected = 0) => null;
    }
}
