using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Input;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Capture;

/// <summary>
/// What happens to typing at the edges of capture: a pause, a password field, another window, the first
/// instant of a session (INV-6, INV-2; 2026-09-19 review).
///
/// A keystroke count was kept in a counter that nothing ever emptied except writing it down. The hooks
/// run for the life of the service and the counter ran with them — through a pause, through a password
/// field, through a customer's password manager — and the next click after capture came back closed the
/// burst and wrote it into the session. Fourteen characters, with the time they were typed: the length
/// of a password, recorded by the mechanism that exists to not record it. Every rule about when typing
/// may be recorded was applied to the finished event, which was far too late.
///
/// These drive raw signals through the recorder, as the hook loop does, and read the encrypted store
/// afterwards. Signal timestamps here are plain milliseconds since the session started.
/// </summary>
public sealed class InputBoundaryTests
{
    private static readonly RemoteTool ScreenConnect = new() { Kind = RemoteToolKind.Screenconnect, ClientVersion = "24.1" };

    private static readonly ScopeDecision InScope =
        new(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing ScreenConnect.", 99);

    private static readonly ScopeDecision PasswordManager =
        new(CaptureScope.Excluded, null, null, "Not capturing — 1Password is on your excluded list.", 77);

    [Fact]
    public async Task APasswordTypedWhileCaptureIsSuppressedLeavesNoCountBehind()
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var recorder = Recorder(harness, () => InScope);

        Assert.True(await harness.Machine.SuppressAsync(CaptureStateReason.PasswordField, ct));
        await Drain(recorder, Keys(14, from: 1_000), ct);
        Assert.True(await harness.Machine.UnsuppressAsync(ct));
        await Drain(recorder, [Click(at: 9_000)], ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Empty(stored.Events.OfType<TypingBurstEvent>());
        Assert.Single(stored.Events.OfType<ClickEvent>());
    }

    [Fact]
    public async Task TypingWhilePausedLeavesNoCountBehind()
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var recorder = Recorder(harness, () => InScope);

        Assert.True(await harness.Machine.PauseAsync(ct));
        await Drain(recorder, Keys(9, from: 1_000), ct);
        Assert.True(await harness.Machine.ResumeAsync(ct));
        await Drain(recorder, [new InputSignal(InputKind.Enter, 9_000)], ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Empty(stored.Events.OfType<TypingBurstEvent>());
    }

    [Fact]
    public async Task TypingInAPasswordManagerIsNotCountedOnceFocusReturnsToTheSession()
    {
        // Scope is read when the batch is handled, so a burst typed in one window and closed by a click
        // in another was judged by the window the click landed in.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var scope = PasswordManager;
        var recorder = Recorder(harness, () => scope);

        await Drain(recorder, Keys(14, from: 1_000), ct);
        scope = InScope;
        await Drain(recorder, [Click(at: 9_000)], ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Empty(stored.Events.OfType<TypingBurstEvent>());
    }

    [Fact]
    public async Task TypingInTheSessionIsStillCounted()
    {
        // The other direction. A fix that drops every burst passes all of the above.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var recorder = Recorder(harness, () => InScope);

        await Drain(recorder, Keys(5, from: 1_000), ct);
        await Drain(recorder, [new InputSignal(InputKind.Enter, 2_000)], ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Equal(5, Assert.Single(stored.Events.OfType<TypingBurstEvent>()).CharCount);
    }

    [Fact]
    public async Task AClickFromJustBeforeTheSessionBeganIsLeftOutAndCaptureCarriesOn()
    {
        // The click that brings a remote tool to the front is what starts the session, so it is stamped
        // a few milliseconds before the session exists and its session time is negative. The store
        // refuses a negative time, the exception left the drain loop — which caught only cancellation —
        // and click capture was dead for the life of the service while the pill went on saying
        // Recording. The most ordinary way to begin a session was the way to break it.
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var recorder = Recorder(harness, () => InScope);

        await Drain(recorder, [Click(at: -40), Click(at: 5_000)], ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Equal(5_000, Assert.Single(stored.Events.OfType<ClickEvent>()).TsMs);
    }

    [Fact]
    public async Task KeysFromBeforeTheSessionBeganAreNotItsFirstBurst()
    {
        await using var harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Machine.StartAsync(ScreenConnect, ct: ct);
        var recorder = Recorder(harness, () => InScope);

        await Drain(recorder, [.. Keys(6, from: -900), new InputSignal(InputKind.Enter, 3_000)], ct);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, ct))!;
        Assert.Empty(stored.Events.OfType<TypingBurstEvent>());
        Assert.Single(stored.Events.OfType<EnterEvent>());
    }

    private static SessionRecorder Recorder(MachineHarness harness, Func<ScopeDecision?> scope) =>
        new(harness.Machine, new NoPictures(), scope);

    private static Task Drain(SessionRecorder recorder, InputSignal[] signals, CancellationToken ct) =>
        recorder.RecordSignalsAsync(
            signals,
            toSessionMs: timestamp => timestamp,
            elapsed: (from, to) => TimeSpan.FromMilliseconds(to - from),
            ct);

    private static InputSignal[] Keys(int count, long from) =>
        [.. Enumerable.Range(0, count).Select(i => new InputSignal(InputKind.PrintableKey, from + (i * 100)))];

    private static InputSignal Click(long at) => new(InputKind.Click, at, 4, 5);

    private sealed class NoPictures : IScreenshotCapturer
    {
        public CapturedFrame? CaptureForegroundWindow(int maxEdge = Downscale.MaxEdge, nint expected = 0) => null;

        public byte[]? CaptureSceneGrid(nint expected = 0) => null;
    }
}
