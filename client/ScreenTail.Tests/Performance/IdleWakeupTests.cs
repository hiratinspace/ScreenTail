using ScreenTail.Core.Capture;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Performance;

/// <summary>
/// ST-031: with no session, the service's loops wait for an event instead of waking on a timer.
///
/// The service records for a fraction of the day. For the rest of it the redaction workers looked for a
/// frame five times a second each, the sensitive-context guard checked for a sighting four times a
/// second, and the password-field guard every second, all to find that there was nothing to do. None of
/// it shows as CPU on a busy profiler, and all of it is a core leaving a low-power state on a laptop
/// running on battery. Each had an event it could have waited for instead: a frame arriving, a session
/// starting.
///
/// Counted timers rather than timings (<see cref="CountingTime"/>), and a frozen clock for the claim that
/// the work still happens when it should: a test that passes with timers that never fire is one no tick
/// could have passed.
/// </summary>
public sealed class IdleWakeupTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private MachineHarness? _harness;

    [Fact]
    public async Task AnIdleRedactionWorkerArmsNoTimer()
    {
        var harness = await HarnessAsync();
        var time = new CountingTime();
        var worker = new RedactionWorker(harness.Store, new ReadsSomething(), new KeepsTheImage(), new RedactionEngine(), harness.Pending, time: time);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = worker.RunAsync(stopping.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await stopping.CancelAsync();
        await running;

        Assert.Equal(0, time.Created);
    }

    [Fact]
    public async Task AFrameArrivingAtAnIdleWorkerIsTakenWithoutWaitingForATick()
    {
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));
        var worker = new RedactionWorker(harness.Store, new ReadsSomething(), new KeepsTheImage(), new RedactionEngine(), harness.Pending, time: new CountingTime(frozen: true));

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = worker.RunAsync(stopping.Token);
        await Task.Delay(100, ct);
        Assert.True(await harness.Machine.TryStageFrameAsync(new StagedFrame("f1", 1_000, FrameTrigger.Click, 80, 60, null, new byte[] { 1, 2, 3 }), ct));

        var redacted = await UntilAsync(() => worker.Progress.Frames == 1);
        await stopping.CancelAsync();
        await running;

        Assert.True(redacted, "a frame staged while the workers were idle was not taken until a timer fired");
    }

    [Fact]
    public async Task WithNoSessionTheSensitiveContextGuardArmsNoTimer()
    {
        var harness = await HarnessAsync();
        var time = new CountingTime();
        var guard = new SensitiveContextGuard(harness.Machine, time);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = guard.RunAsync(stopping.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await stopping.CancelAsync();
        await running;

        Assert.Equal(0, time.Created);
    }

    [Fact]
    public async Task ASightingJustBeforeASessionStartsHoldsItAtItsFirstTurn()
    {
        // The login prompt was read seconds before the session began and is still on screen. The guard
        // used to find out on its next tick; it now takes its first turn as the session starts, which is
        // the event, with no timer needed to get there.
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        var guard = new SensitiveContextGuard(harness.Machine, new CountingTime(frozen: true));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = guard.RunAsync(stopping.Token);

        guard.Seen(LoginScreenHeuristic.Suppression);
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));

        var held = await UntilAsync(() => harness.Machine.State == SessionState.Suppressed);
        await stopping.CancelAsync();
        await running;

        Assert.True(held, "the session recorded over a sighting still in its window until a timer fired");
        Assert.True(guard.Holding);
    }

    [Fact]
    public async Task AListenerThatThrowsDoesNotKeepTheGuardsAsleep()
    {
        // The guards learn that a session started from StateChanged alone, now that they do not tick at
        // idle. A multicast invoke stops at the first handler that throws, so a faulty listener added
        // earlier would leave every later one asleep for the session: a guard failing open (2026-10-27
        // review). Each listener is called on its own now, and a failure is counted.
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        harness.Machine.StateChanged += _ => throw new InvalidOperationException("a listener with a bug");
        var guard = new SensitiveContextGuard(harness.Machine, new CountingTime(frozen: true));
        using var password = new PasswordFieldGuard(harness.Machine, new NotAPassword(), options: new PasswordFieldOptions { PollEvery = TimeSpan.FromMinutes(5) });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var guarding = guard.RunAsync(stopping.Token);
        var watching = password.RunAsync(stopping.Token);
        await Task.Delay(100, ct);

        guard.Seen(LoginScreenHeuristic.Suppression);
        try
        {
            _ = await harness.Machine.StartAsync(Rdp, ct: ct);
        }
        catch (InvalidOperationException)
        {
            // What the faulty listener used to do to the transition itself.
        }

        var held = await UntilAsync(() => harness.Machine.State == SessionState.Suppressed);
        var looked = await UntilAsync(() => password.Passes > 0);
        await stopping.CancelAsync();
        await guarding;
        await watching;

        Assert.True(held, "a listener that threw kept the sensitive-context guard from hearing that the session started");
        Assert.True(looked, "a listener that threw kept the password-field guard from hearing that the session started");
        Assert.True(harness.Machine.ListenerFailures > 0, "the listener's failure was not counted");
    }

    [Fact]
    public async Task WithNoSessionThePasswordFieldGuardDoesNotPoll()
    {
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        using var guard = new PasswordFieldGuard(
            harness.Machine,
            new NotAPassword(),
            options: new PasswordFieldOptions { PollEvery = TimeSpan.FromMilliseconds(10) });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = guard.RunAsync(stopping.Token);

        await Task.Delay(300, ct);
        var idlePasses = guard.Passes;

        // And a session still wakes it, with no focus change to do it.
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));
        var woke = await UntilAsync(() => guard.Passes > idlePasses);
        await stopping.CancelAsync();
        await running;

        Assert.Equal(0, idlePasses);
        Assert.True(woke, "the guard did not look when the session started");
    }

    [Fact]
    public async Task DuringASessionThePasswordFieldPollStillCatchesAFieldNoEventReported()
    {
        // INV-6's safety net. A remote canvas or a control that raises no focus event can put a password
        // field in front without FocusMoved ever being called; the poll is what notices. Taking the poll
        // away at idle must not take it away here.
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        var probe = new SwitchableProbe();
        using var guard = new PasswordFieldGuard(harness.Machine, probe, options: new PasswordFieldOptions { PollEvery = TimeSpan.FromMilliseconds(10) });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = guard.RunAsync(stopping.Token);
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));
        Assert.True(await UntilAsync(() => guard.Passes > 0), "the guard did not look when the session started");

        probe.Focused = FocusedField.Password;
        var held = await UntilAsync(() => harness.Machine.State == SessionState.Suppressed);
        await stopping.CancelAsync();
        await running;

        Assert.True(held, "a password field that raised no focus event was recorded over: the poll did not run during the session");
    }

    [Fact]
    public async Task WhileAHoldIsInPlaceThePollStillLetsGoOfIt()
    {
        // The reverse: focus leaves the field without an event, and the poll is what ends the hold.
        // Without it the session stays suppressed for the rest of the day.
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        var probe = new SwitchableProbe { Focused = FocusedField.Password };
        using var guard = new PasswordFieldGuard(harness.Machine, probe, options: new PasswordFieldOptions { PollEvery = TimeSpan.FromMilliseconds(10) });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = guard.RunAsync(stopping.Token);
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));
        Assert.True(await UntilAsync(() => harness.Machine.State == SessionState.Suppressed), "the field in focus at the start was not held");
        var passes = guard.Passes;
        Assert.True(await UntilAsync(() => guard.Passes > passes), "the guard did not look again after taking the hold");

        probe.Focused = FocusedField.NotPassword;
        var released = await UntilAsync(() => harness.Machine.State == SessionState.Recording);
        await stopping.CancelAsync();
        await running;

        Assert.True(released, "focus left the field without an event and the hold was never let go of");
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task WithNoSessionTheNarrationRecorderLooksRarelyAndANudgeWakesItAtOnce()
    {
        // The host nudges the recorder on every state change, and the nudge cannot be lost: a release
        // while nothing waits leaves the count for the next wait. The timed look is a backstop for a host
        // that forgot to wire it, and it used to come round twice a second all day.
        var ct = TestContext.Current.CancellationToken;
        var looks = 0;
        using var recorder = new ScreenTail.Core.Speech.NarrationRecorder(
            new Speech.NarrationRecorderTests.FakeMicrophone(),
            new Speech.NarrationRecorderTests.FakeRecogniser("unused"),
            (_, _, _) => Task.FromResult(true),
            recording: () =>
            {
                _ = Interlocked.Increment(ref looks);
                return null;
            },
            hearing: () => true);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = recorder.RunAsync(stopping.Token);

        await Task.Delay(2_000, ct);
        var idleLooks = Volatile.Read(ref looks);
        recorder.Nudge();
        var woke = await UntilAsync(() => Volatile.Read(ref looks) > idleLooks);
        await stopping.CancelAsync();
        await running;

        Assert.True(idleLooks <= 2, $"the recorder looked {idleLooks} times in two seconds with no session");
        Assert.True(woke, "a nudge did not wake the recorder");
    }

    private async Task<MachineHarness> HarnessAsync() => _harness ??= await MachineHarness.StartAsync();

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private sealed class ReadsSomething : IFrameTextRecogniser
    {
        public Task<RecognisedText> ReadAsync(ReadOnlyMemory<byte> image, CancellationToken ct = default) =>
            Task.FromResult(new RecognisedText([new OcrWord("Services", 0, 0, 60, 12)], 0.9));
    }

    private sealed class KeepsTheImage : IFrameMasker
    {
        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge) =>
            new(image.ToArray(), 80, 60);
    }

    private sealed class NotAPassword : IFocusedFieldProbe
    {
        public FocusedField Read() => FocusedField.NotPassword;
    }

    /// <summary>What has focus, changed by the test with no focus event to say so.</summary>
    private sealed class SwitchableProbe : IFocusedFieldProbe
    {
        private volatile int _focused = (int)FocusedField.NotPassword;

        public FocusedField Focused
        {
            get => (FocusedField)_focused;
            set => _focused = (int)value;
        }

        public FocusedField Read() => Focused;
    }
}
