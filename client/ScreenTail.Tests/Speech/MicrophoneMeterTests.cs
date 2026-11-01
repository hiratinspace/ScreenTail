using System.Reflection;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// Settings, Capture's level meter on the service's side (ST-080 AC3). It is a new time the microphone
/// opens outside a session, so these pin when it may: only while the card keeps asking, never while a
/// session holds the microphone, closed by itself once the asking stops, and with nowhere to keep what
/// it hears.
/// </summary>
public sealed class MicrophoneMeterTests : IAsyncDisposable
{
    private readonly ScriptedMicrophone _microphone = new();
    // Moved by hand, and its timers never fire: what closes the device in each test is the thing the
    // test names, not a real-time tick that happened to come round first.
    private readonly StillClock _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private bool _sessionHoldsMicrophone;

    [Fact]
    public async Task NothingOpensAndNoTimerIsArmedUntilTheCardAsks()
    {
        var time = new CountingTime(frozen: true);
        await using var meter = new MicrophoneMeter(_microphone, () => false, time);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(0, _microphone.Open);
        Assert.Empty(_microphone.OpenedIds);
        Assert.Equal(0, time.Created);
        Assert.False(meter.IsOpen);
    }

    [Fact]
    public async Task TheFirstRequestOpensTheDeviceAskedForAndTheLevelFollowsWhatItHears()
    {
        await using var meter = Meter();

        var first = await meter.ReadAsync(ScriptedMicrophone.HeadsetId, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);
        _microphone.Feed(ScriptedMicrophone.Loud());
        var heard = await ReadUntilAsync(meter, ScriptedMicrophone.HeadsetId, MicrophoneLevelStates.Listening);

        Assert.Equal(MicrophoneLevelStates.Opening, first.State);
        Assert.Equal([ScriptedMicrophone.HeadsetId], _microphone.OpenedIds);
        Assert.Equal(ScriptedMicrophone.HeadsetId, heard.DeviceId);
        Assert.InRange(heard.Level, 0.8, 1.0);
    }

    [Fact]
    public async Task ALeaseThatIsNotRenewedClosesTheDeviceByItself()
    {
        // The card hidden, the window behind another, the UI hung or gone: in every case the requests
        // stop, and the microphone must close without anyone telling it to.
        await using var meter = Meter();
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);

        // Renewed: each request buys one more lease.
        _time.Advance(TimeSpan.FromSeconds(1));
        _ = await meter.ReadAsync(null, Ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        await meter.ExpireAsync();
        Assert.Equal(1, _microphone.Open);

        // Not renewed.
        _time.Advance(TimeSpan.FromSeconds(1));
        await meter.ExpireAsync();

        Assert.Equal(0, _microphone.Open);
        Assert.False(meter.IsOpen);
    }

    [Fact]
    public async Task AFrameThatArrivesAfterTheLeaseClosesTheDeviceEvenWithoutTheTimer()
    {
        await using var meter = Meter();
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);

        _time.Advance(MicrophoneMeter.DefaultLease + TimeSpan.FromMilliseconds(1));
        _microphone.Feed(ScriptedMicrophone.Loud());

        await WaitUntilAsync(() => _microphone.Open == 0);
        Assert.Equal(0, _microphone.Open);
    }

    [Fact]
    public async Task OnTheRealClockTheMeterClosesASilentDeviceByItsOwnTimer()
    {
        // No frames arrive and nobody calls anything: only the meter's own timer can close it.
        await using var meter = new MicrophoneMeter(_microphone, () => false, TimeProvider.System, lease: TimeSpan.FromMilliseconds(100));
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);

        await WaitUntilAsync(() => _microphone.Open == 0);

        Assert.Equal(0, _microphone.Open);
        Assert.False(meter.IsOpen);
    }

    [Fact]
    public async Task StopClosesTheDeviceAtOnceAndDisarmsTheTimer()
    {
        var time = new CountingTime(frozen: true);
        await using var meter = new MicrophoneMeter(_microphone, () => false, time);
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);
        Assert.Equal(1, time.Live);

        await meter.StopAsync();

        Assert.Equal(0, _microphone.Open);
        Assert.Equal(0, time.Live);
        Assert.False(meter.IsOpen);
    }

    [Fact]
    public async Task WhileASessionHoldsTheMicrophoneTheMeterDoesNotOpenIt()
    {
        _sessionHoldsMicrophone = true;
        await using var meter = Meter();

        var reading = await meter.ReadAsync(null, Ct);

        Assert.Equal(MicrophoneLevelStates.Session, reading.State);
        Assert.Equal(0, reading.Level);
        Assert.Empty(_microphone.OpenedIds);
    }

    [Fact]
    public async Task ASessionThatStartsWhileTheMeterIsOpenClosesIt()
    {
        await using var meter = Meter();
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);

        _sessionHoldsMicrophone = true;
        await meter.SessionChangedAsync();

        Assert.Equal(0, _microphone.Open);
        Assert.Equal(MicrophoneLevelStates.Session, (await meter.ReadAsync(null, Ct)).State);
        Assert.Single(_microphone.OpenedIds);
    }

    [Fact]
    public async Task AChosenDeviceThatIsNotConnectedFallsBackToWindowsDefault()
    {
        await using var meter = Meter();

        var reading = await meter.ReadAsync("{0.0.1.00000000}.{left-at-home}", Ct);

        Assert.Equal(ScriptedMicrophone.BuiltInId, reading.DeviceId);
        await WaitUntilAsync(() => _microphone.Open == 1);
        Assert.Equal([ScriptedMicrophone.BuiltInId], _microphone.OpenedIds);
    }

    [Fact]
    public async Task ChoosingAnotherDeviceClosesTheFirstBeforeTheSecondOpens()
    {
        await using var meter = Meter();
        _ = await meter.ReadAsync(ScriptedMicrophone.HeadsetId, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);

        _ = await meter.ReadAsync(ScriptedMicrophone.BuiltInId, Ct);
        await WaitUntilAsync(() => _microphone.OpenedIds.Count == 2);

        Assert.Equal([ScriptedMicrophone.HeadsetId, ScriptedMicrophone.BuiltInId], _microphone.OpenedIds);
        await WaitUntilAsync(() => _microphone.Open == 1);
        Assert.Equal(1, _microphone.Open);
    }

    [Fact]
    public async Task WithNoDeviceConnectedNothingOpensAndTheCardIsTold()
    {
        _microphone.Connected.Clear();
        await using var meter = Meter();

        var reading = await meter.ReadAsync(null, Ct);
        var again = await meter.ReadAsync(null, Ct);

        Assert.Equal(MicrophoneLevelStates.NoMicrophone, reading.State);
        Assert.Equal(MicrophoneLevelStates.NoMicrophone, again.State);
        Assert.Empty(_microphone.OpenedIds);
    }

    [Fact]
    public async Task ADeviceThatStopsIsReportedAndLeftAloneForAMomentBeforeItIsTriedAgain()
    {
        await using var meter = Meter();
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);

        _microphone.Unplug();
        await WaitUntilAsync(() => _microphone.Open == 0);
        var gone = await ReadUntilAsync(meter, null, MicrophoneLevelStates.Unavailable);
        var soon = await meter.ReadAsync(null, Ct);
        Assert.Single(_microphone.OpenedIds);

        _time.Advance(MicrophoneMeter.RetryAfter + TimeSpan.FromSeconds(1));
        var later = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.OpenedIds.Count == 2);

        Assert.Equal(MicrophoneLevelStates.Unavailable, gone.State);
        Assert.Equal(MicrophoneLevelStates.Unavailable, soon.State);
        Assert.Equal(MicrophoneLevelStates.Opening, later.State);
        Assert.Equal(2, _microphone.OpenedIds.Count);
    }

    [Fact]
    public void TheMeterHasNowhereToKeepAudioAndNothingToSendItTo()
    {
        // Levels only (INV-9): a frame is a local of the listening loop and becomes one number. A field
        // that could hold samples, or a recogniser, a store or a transcript sink, would be a place for
        // what the technician said to outlive the frame it arrived in.
        var forbidden = new[] { typeof(ISpeechRecogniser), typeof(AppendTranscript), typeof(ISessionStore), typeof(Stream), typeof(NarrationRecorder) };
        foreach (var field in typeof(MicrophoneMeter).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        {
            Assert.False(MentionsSamples(field.FieldType), $"{field.Name} ({field.FieldType}) could hold audio samples");
            Assert.DoesNotContain(forbidden, f => f.IsAssignableFrom(field.FieldType));
        }
    }

    [Fact]
    public void TheLongestStretchIsFiveMinutes()
    {
        // An owner decision (2026-10-31 review of #244), pinned so that it changes on purpose.
        Assert.Equal(TimeSpan.FromMinutes(5), MicrophoneMeter.MaxOpen);
    }

    [Fact]
    public async Task AfterItsLongestStretchTheMeterClosesAndStaysClosedUntilTheCardIsPutAway()
    {
        // A Settings window left in front all afternoon keeps asking. The meter still lets go.
        await using var meter = new MicrophoneMeter(_microphone, () => false, _time, cap: TimeSpan.FromSeconds(10));
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);
        for (var second = 0; second < 11; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _ = await meter.ReadAsync(null, Ct);
        }

        await meter.ExpireAsync();
        var refused = await meter.ReadAsync(null, Ct);

        Assert.Equal(0, _microphone.Open);
        Assert.Equal(MicrophoneLevelStates.TimedOut, refused.State);
        Assert.Single(_microphone.OpenedIds);

        // Put away and looked at again: it opens.
        await meter.StopAsync();
        var again = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.OpenedIds.Count == 2);

        Assert.Equal(MicrophoneLevelStates.Opening, again.State);
        Assert.Equal(2, _microphone.OpenedIds.Count);
    }

    [Fact]
    public async Task AFrameAfterTheLongestStretchClosesTheDeviceEvenWithoutTheTimer()
    {
        await using var meter = new MicrophoneMeter(_microphone, () => false, _time, cap: TimeSpan.FromSeconds(10));
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);
        for (var second = 0; second < 11; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _ = await meter.ReadAsync(null, Ct);
        }

        _microphone.Feed(ScriptedMicrophone.Loud());
        await WaitUntilAsync(() => _microphone.Open == 0);

        Assert.Equal(0, _microphone.Open);
        Assert.Equal(MicrophoneLevelStates.TimedOut, (await meter.ReadAsync(null, Ct)).State);
    }

    [Fact]
    public async Task TheWallClockMovingNeitherClosesTheMeterNorKeepsItOpen()
    {
        // A time zone change or a clock sync moves the wall clock, not the time that has passed.
        await using var meter = Meter();
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);

        _time.JumpWall(TimeSpan.FromHours(1));
        await meter.ExpireAsync();
        Assert.Equal(1, _microphone.Open);

        _time.JumpWall(TimeSpan.FromHours(-3));
        _time.Advance(MicrophoneMeter.DefaultLease + TimeSpan.FromMilliseconds(1));
        await meter.ExpireAsync();
        Assert.Equal(0, _microphone.Open);
    }

    [Fact]
    public async Task AListenThatWillNotLetGoIsWaitedForBeforeAnotherOpens()
    {
        // A slow driver: told to stop, it carries on. A second stream on the same device is not the answer.
        _microphone.IgnoresCancellation = true;
        await using var meter = new MicrophoneMeter(_microphone, () => false, _time, closeWait: TimeSpan.FromMilliseconds(50));
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);

        await meter.StopAsync();
        var meanwhile = await meter.ReadAsync(null, Ct);
        await Task.Delay(50, Ct);

        Assert.Equal(MicrophoneLevelStates.Closed, meanwhile.State);
        Assert.Single(_microphone.OpenedIds);
        Assert.True(meter.IsOpen);

        _microphone.Unplug();
        await WaitUntilAsync(() => _microphone.Open == 0);
        await WaitUntilAsync(() => !meter.IsOpen);
        var later = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.OpenedIds.Count == 2);

        Assert.Equal(MicrophoneLevelStates.Opening, later.State);
        Assert.Equal(2, _microphone.OpenedIds.Count);
    }

    [Theory]
    [InlineData(SessionState.Idle, false)]
    [InlineData(SessionState.Recording, true)]
    [InlineData(SessionState.Paused, true)]
    [InlineData(SessionState.Suppressed, true)]
    [InlineData(SessionState.Finalizing, true)]
    [InlineData(SessionState.DraftPending, false)]
    [InlineData(SessionState.DraftReady, false)]
    [InlineData(SessionState.DraftFailed, false)]
    public void TheseSessionStatesHoldTheMicrophone(SessionState state, bool held)
    {
        Assert.Equal(held, MicrophoneMeter.HeldBy(state));
    }

    [Fact]
    public void EverySessionStateIsDecided()
    {
        // A state added later is a decision, not a default: this fails until the theory above names it.
        Assert.Equal(8, Enum.GetValues<SessionState>().Length);
    }

    [Fact]
    public async Task AsTheServiceRunsItTheMachinesSessionClosesAndHoldsOffTheMeter()
    {
        // The host builds the meter through ForMachine. Recording, paused and suppressed each refuse it,
        // and a session starting closes one that is open.
        await using var harness = await MachineHarness.StartAsync();
        var machine = harness.Machine;
        await using var meter = MicrophoneMeter.ForMachine(_microphone, machine, _time);
        _ = await meter.ReadAsync(null, Ct);
        await WaitUntilAsync(() => _microphone.Open == 1);

        Assert.True(await machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, ct: Ct));
        await WaitUntilAsync(() => _microphone.Open == 0);
        Assert.Equal(0, _microphone.Open);
        Assert.Equal(MicrophoneLevelStates.Session, (await meter.ReadAsync(null, Ct)).State);

        Assert.True(await machine.PauseAsync(Ct));
        Assert.Equal(MicrophoneLevelStates.Session, (await meter.ReadAsync(null, Ct)).State);

        Assert.True(await machine.ResumeAsync(Ct));
        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, Ct));
        Assert.Equal(MicrophoneLevelStates.Session, (await meter.ReadAsync(null, Ct)).State);
        Assert.Single(_microphone.OpenedIds);

        Assert.True(await machine.StopAsync(Ct));
        await WaitUntilAsync(() => !MicrophoneMeter.HeldBy(machine.State));
        Assert.Equal(MicrophoneLevelStates.Opening, (await meter.ReadAsync(null, Ct)).State);
    }

    [Fact]
    public void TheMetersAnswerCarriesALevelAndNothingElseThatCouldHoldAudio()
    {
        // The wire half of levels only: a new member on the answer is a decision someone has to make here.
        Assert.Equal(
            [("DeviceId", typeof(string)), ("Level", typeof(double)), ("RequestId", typeof(int?)), ("State", typeof(string))],
            Members(typeof(MicrophoneLevelReported)));
        Assert.Equal(
            [("DeviceId", typeof(string)), ("RequestId", typeof(int))],
            Members(typeof(GetMicrophoneLevelCommand)));
        Assert.Equal([("RequestId", typeof(int))], Members(typeof(StopMicrophoneLevelCommand)));
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(32767, 1.0)]
    [InlineData(short.MinValue, 1.0)]
    [InlineData(1036, 0.5)]
    [InlineData(10, 0.0)]
    public void TheLevelIsThePeakOnADecibelScale(int peak, double expected)
    {
        short[] samples = [0, (short)peak, 0];

        Assert.Equal(expected, MicrophoneLevel.Of(samples), precision: 2);
    }

    public ValueTask DisposeAsync() => _microphone.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MicrophoneMeter Meter() => new(_microphone, () => _sessionHoldsMicrophone, _time);

    private static (string, Type)[] Members(Type type) =>
        [.. type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => (p.Name, p.PropertyType))
            .OrderBy(p => p.Name, StringComparer.Ordinal)];

    private static bool MentionsSamples(Type type) =>
        type == typeof(short) || (type.IsArray && MentionsSamples(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(MentionsSamples));

    private static async Task<MeterReading> ReadUntilAsync(MicrophoneMeter meter, string? deviceId, string state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        MeterReading reading;
        do
        {
            reading = await meter.ReadAsync(deviceId, Ct);
            if (reading.State == state)
            {
                return reading;
            }

            await Task.Delay(10, Ct);
        }
        while (DateTime.UtcNow < deadline);
        return reading;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }
    }

    /// <summary><see cref="ManualTime"/>'s clock with timers that never fire.</summary>
    private sealed class StillClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        private long _wall;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks) + Interlocked.Read(ref _wall), TimeSpan.Zero);

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        /// <summary>The wall clock moves and no time passes: a time zone, a sync.</summary>
        public void JumpWall(TimeSpan by) => Interlocked.Add(ref _wall, by.Ticks);
    }
}
