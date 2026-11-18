using System.Runtime.CompilerServices;
using ScreenTrail.Core.Speech;
using ScreenTrail.Shared.Schema;
using static ScreenTrail.Tests.Speech.NarrationRecorderTests;

namespace ScreenTrail.Tests.Speech;

/// <summary>
/// A headset unplugged, plugged in, or swapped while a session records (2026-11-18).
///
/// Before this, a microphone that stopped ended narration until the service restarted, a service that
/// started with no microphone never listened at all, and a headset plugged in mid-session was not used
/// until the session paused. A device source (<see cref="IMicrophone.Reopens"/>) now opens whatever is
/// connected a moment after a stop, and moves to another device at the next pause in speech, never
/// mid-sentence. A file or a script still ends narration when it runs out; the other tests rely on that.
/// </summary>
public sealed class MicrophoneComesAndGoesTests
{
    private static readonly NarrationOptions Quick = new() { Reopen = TimeSpan.FromMilliseconds(20) };

    [Fact]
    public async Task AnUnpluggedMicrophoneIsHeardAgainInTheSameSession()
    {
        var microphone = new DeviceSource(
            DeviceSource.Plays([.. Silence(20), .. NarrationRecorderTests.Speech(40), .. Silence(40)], staysOpen: false),
            DeviceSource.NothingConnected,
            DeviceSource.Plays([.. Silence(20), .. NarrationRecorderTests.Speech(40), .. Silence(40)], staysOpen: true));
        var recogniser = new FakeRecogniser("said");
        var kept = new List<TranscriptSegment>();
        using var recorder = Recorder(microphone, recogniser, kept);
        var changes = new List<string>();
        recorder.MicrophoneChanged += changes.Add;

        using var stop = new CancellationTokenSource();
        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => Count(kept) == 2);
        await stop.CancelAsync();
        await running;

        Assert.Equal(2, Count(kept));
        Assert.Equal(2, microphone.Opens);
        Assert.Equal(microphone.Opens, microphone.Closes);
        Assert.Equal(1, recorder.MicrophoneLost);
        Assert.Equal(["lost"], changes);
    }

    [Fact]
    public async Task AMicrophonePluggedInAfterTheServiceStartedIsHeard()
    {
        var microphone = new DeviceSource(
            DeviceSource.NothingConnected,
            DeviceSource.NothingConnected,
            DeviceSource.Plays([.. Silence(20), .. NarrationRecorderTests.Speech(40), .. Silence(40)], staysOpen: true));
        var kept = new List<TranscriptSegment>();
        using var recorder = Recorder(microphone, new FakeRecogniser("said"), kept);

        using var stop = new CancellationTokenSource();
        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => Count(kept) == 1);
        await stop.CancelAsync();
        await running;

        Assert.Equal(1, Count(kept));
        Assert.Equal(1, microphone.Opens);

        // A session spent without a microphone is not a loss, and not one per look.
        Assert.Equal(0, recorder.MicrophoneLost);
    }

    [Fact]
    public async Task ANewDeviceIsUsedFromTheNextPauseAndNeverCutsASentence()
    {
        // The move is noticed in the middle of the first sentence (frame 35 of speech running 20 to 60).
        // The sentence is finished on the old device and kept whole; the listen moves once it ends.
        var microphone = new DeviceSource(
            DeviceSource.Plays([.. Silence(20), .. NarrationRecorderTests.Speech(40), .. Silence(40)], staysOpen: true, movesAt: 35),
            DeviceSource.Plays([.. Silence(20), .. NarrationRecorderTests.Speech(40), .. Silence(40)], staysOpen: true));
        var recogniser = new FakeRecogniser("said");
        var kept = new List<TranscriptSegment>();
        using var recorder = Recorder(microphone, recogniser, kept);
        var changes = new List<string>();
        recorder.MicrophoneChanged += changes.Add;

        using var stop = new CancellationTokenSource();
        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => Count(kept) == 2);
        await stop.CancelAsync();
        await running;

        Assert.Equal(2, Count(kept));
        Assert.Equal(2, microphone.Opens);
        Assert.Equal(1, recorder.MicrophoneSwitched);
        Assert.Equal(0, recorder.MicrophoneLost);
        Assert.Equal(["switched"], changes);

        // Whole: at least the forty frames of speech went to the model as one segment.
        Assert.True(recogniser.Handed[0].Samples >= 40 * Rate * 32 / 1000, $"the first sentence was cut to {recogniser.Handed[0].Samples} samples");
    }

    [Fact]
    public async Task AConnectedDeviceThatGivesNothingIsTriedLessAndLessAndReportedOnce()
    {
        // Blocked, held by another app, or failing: connected, but every open gives nothing. Without the
        // back-off it was opened and closed every two seconds for the whole session, and nothing said so.
        var microphone = new DeviceSource { Otherwise = DeviceSource.Refuses };
        var changes = new List<string>();
        using var recorder = Recorder(microphone, new FakeRecogniser("x"), []);
        recorder.MicrophoneChanged += changes.Add;

        using var stop = new CancellationTokenSource();
        var running = recorder.RunAsync(stop.Token);
        await Task.Delay(400);
        await stop.CancelAsync();
        await running;

        // 20, 40, 80, 160 ms waits fit about five looks in 400 ms; a fixed 20 ms would be twenty.
        Assert.InRange(microphone.Looks, 2, 8);
        Assert.Equal(["unavailable"], changes);
        Assert.Equal(0, recorder.MicrophoneLost);
    }

    [Fact]
    public async Task NothingMovesWhileNoSessionIsRecording()
    {
        // Between sessions the device stays closed whatever is plugged in: INV-9 and the in-use indicator.
        var microphone = new DeviceSource(DeviceSource.Plays(Silence(5), staysOpen: true));
        var kept = new List<TranscriptSegment>();
        using var recorder = new NarrationRecorder(
            microphone,
            new FakeRecogniser("x"),
            (_, segment, _) => Task.FromResult(Add(kept, segment)),
            () => null,
            () => true,
            Quick);

        using var stop = new CancellationTokenSource();
        var running = recorder.RunAsync(stop.Token);
        await Task.Delay(150);
        await stop.CancelAsync();
        await running;

        Assert.Equal(0, microphone.Opens);
    }

    private static NarrationRecorder Recorder(IMicrophone microphone, FakeRecogniser recogniser, List<TranscriptSegment> kept) =>
        new(
            microphone,
            recogniser,
            (_, segment, _) => Task.FromResult(Add(kept, segment)),
            () => new NarrationSession("session-a", 0),
            () => true,
            Quick);

    private static bool Add(List<TranscriptSegment> kept, TranscriptSegment segment)
    {
        lock (kept)
        {
            kept.Add(segment);
        }

        return true;
    }

    private static int Count(List<TranscriptSegment> kept)
    {
        lock (kept)
        {
            return kept.Count;
        }
    }

    /// <summary>
    /// A device that can be unplugged and plugged in: each open takes the next script, and "nothing
    /// connected" yields nothing, as <c>WindowsMicrophone</c> does when no device resolves.
    /// </summary>
    private sealed class DeviceSource(params DeviceSource.Script[] opens) : IMicrophone
    {
        private readonly Queue<Script> _opens = new(opens);
        private volatile bool _moving;
        private volatile bool _present = opens.Length == 0 || opens[0].Present;
        private Script _otherwise = NothingConnected;

        /// <summary>What every open gives once the scripts have run out.</summary>
        public Script Otherwise { init => _otherwise = value; }

        public sealed record Script(short[][]? Frames, bool StaysOpen, int MovesAt, bool Present = true);

        /// <summary>Nothing plugged in: the listen gives nothing and the device name reads null, as WindowsMicrophone's does.</summary>
        public static Script NothingConnected { get; } = new(null, false, -1, Present: false);

        /// <summary>Plugged in but giving nothing: blocked, held by another app, or failing.</summary>
        public static Script Refuses { get; } = new(null, false, -1);

        public static Script Plays(short[][] frames, bool staysOpen, int movesAt = -1) => new(frames, staysOpen, movesAt);

        public string? Connected { get; init; } = "Headset";

        public int Opens { get; private set; }

        public int Closes { get; private set; }

        /// <summary>Every listen asked for, whether or not anything opened.</summary>
        public int Looks { get; private set; }

        public string? DeviceName => _present ? Connected : null;

        public int SampleRate => Rate;

        public bool Reopens => true;

        public bool DeviceMoved => _moving;

        public async IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            Looks++;
            var script = _opens.Count > 0 ? _opens.Dequeue() : _otherwise;
            _present = script.Present;
            if (script.Frames is null)
            {
                yield break;
            }

            Opens++;
            _moving = false;
            try
            {
                for (var i = 0; i < script.Frames.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (i == script.MovesAt)
                    {
                        _moving = true;
                    }

                    yield return script.Frames[i];
                    await Task.Yield();
                }

                var quiet = Silence(1)[0];
                while (script.StaysOpen && !ct.IsCancellationRequested)
                {
                    yield return quiet;
                    await Task.Delay(1, CancellationToken.None);
                }
            }
            finally
            {
                Closes++;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
