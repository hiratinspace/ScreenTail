using System.Reflection;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;

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
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
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
}
