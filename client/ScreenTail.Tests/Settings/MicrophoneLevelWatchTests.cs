using ScreenTail.Core.Settings;
using ScreenTail.Core.Speech;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// Settings, Capture's level meter on the UI's side (ST-080 AC3): it asks for the level only while the
/// card is looked at, stops asking the moment it is not, tells the service to close the microphone after
/// its last request, and never shows a reading that arrived after the card went away.
/// </summary>
public sealed class MicrophoneLevelWatchTests : IDisposable
{
    private readonly FakeLevels _levels = new();

    // Each pause between two requests waits for the test to let it go, so a test counts requests exactly.
    private readonly SemaphoreSlim _tick = new(0);

    [Fact]
    public async Task NothingIsAskedUntilTheCardIsLookedAt()
    {
        await using var watch = Watch();

        await Task.Delay(100, Ct);

        Assert.Equal(0, _levels.Reads);
        Assert.Empty(_levels.Calls);
        Assert.False(watch.Watching);
        Assert.Equal(MicrophoneLevelWatch.NotWatching, watch.Status);
    }

    [Fact]
    public async Task LookingAtTheCardAsksForTheLevelOfTheDeviceInThePickerAndShowsIt()
    {
        await using var watch = Watch();
        watch.DeviceId = "{0.0.1.00000000}.{headset}";

        await watch.SetWatchingAsync(true);
        await WaitUntilAsync(() => _levels.Reads >= 1);
        await WaitUntilAsync(() => watch.Level > 0);

        Assert.True(watch.Watching);
        Assert.Equal(0.4, watch.Level);
        Assert.Equal(MicrophoneLevelWatch.WhileWatching, watch.Status);
        Assert.Equal("{0.0.1.00000000}.{headset}", _levels.DeviceIds[0]);
    }

    [Fact]
    public async Task OneRequestAtATimeWhileLookedAt()
    {
        await using var watch = Watch();
        await watch.SetWatchingAsync(true);
        await WaitUntilAsync(() => _levels.Reads == 1);

        await Task.Delay(100, Ct);
        Assert.Equal(1, _levels.Reads);

        _tick.Release();
        await WaitUntilAsync(() => _levels.Reads == 2);
        Assert.Equal(2, _levels.Reads);
    }

    [Fact]
    public async Task HidingTheCardStopsTheAskingThenClosesTheMicrophone()
    {
        await using var watch = Watch();
        await watch.SetWatchingAsync(true);
        _tick.Release(3);
        await WaitUntilAsync(() => _levels.Reads >= 2);

        await watch.SetWatchingAsync(false);
        var asked = _levels.Reads;
        _tick.Release(5);
        await Task.Delay(100, Ct);

        Assert.Equal(asked, _levels.Reads);
        Assert.Equal("stop", _levels.Calls[^1]);
        Assert.Equal(1, _levels.Calls.Count(c => c == "stop"));
        Assert.False(watch.Watching);
        Assert.Equal(0, watch.Level);
        Assert.Equal(MicrophoneLevelWatch.NotWatching, watch.Status);
    }

    [Fact]
    public async Task AReadingThatArrivesAfterTheCardWasHiddenIsNotShown()
    {
        var late = new TaskCompletionSource<MeterReading?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _levels.Next = late.Task;
        await using var watch = Watch();
        var shown = new List<double>();
        watch.Changed += () =>
        {
            lock (shown)
            {
                shown.Add(watch.Level);
            }
        };
        await watch.SetWatchingAsync(true);
        await WaitUntilAsync(() => _levels.Reads == 1);

        var hiding = watch.SetWatchingAsync(false);
        late.SetResult(new MeterReading(MicrophoneLevelStates.Listening, 0.9, null));
        await hiding;

        // Not even for a moment: the late reading never reached the screen.
        Assert.DoesNotContain(0.9, shown);
        Assert.Equal(0, watch.Level);
        Assert.Equal(MicrophoneLevelWatch.NotWatching, watch.Status);
        Assert.Equal("stop", _levels.Calls[^1]);
    }

    [Fact]
    public async Task SayingTheSameThingTwiceDoesNothingTwice()
    {
        await using var watch = Watch();

        await watch.SetWatchingAsync(false);
        Assert.Empty(_levels.Calls);

        await watch.SetWatchingAsync(true);
        await watch.SetWatchingAsync(true);
        await WaitUntilAsync(() => _levels.Reads == 1);
        await watch.SetWatchingAsync(false);
        await watch.SetWatchingAsync(false);

        Assert.Equal(1, _levels.Calls.Count(c => c == "stop"));
        Assert.Equal(1, _levels.Reads);
    }

    [Fact]
    public async Task AWindowThatCannotAnswerAsksForNothing()
    {
        // A hung UI thread: the request waits for it, so the service hears nothing and lets go.
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var watch = new MicrophoneLevelWatch(_levels, ct => _tick.WaitAsync(ct), ct => answered.Task);

        await watch.SetWatchingAsync(true);
        await Task.Delay(150, Ct);
        Assert.Equal(0, _levels.Reads);

        answered.SetResult();
        await WaitUntilAsync(() => _levels.Reads == 1);
        Assert.Equal(1, _levels.Reads);
    }

    [Fact]
    public async Task AFailingRequestIsShownAsNoAnswerAndTheCardCarriesOn()
    {
        _levels.Fails = true;
        await using var watch = Watch();

        await watch.SetWatchingAsync(true);
        await WaitUntilAsync(() => watch.Status != MicrophoneLevelWatch.NotWatching);
        Assert.Contains("did not answer", watch.Status, StringComparison.Ordinal);
        _tick.Release();
        await WaitUntilAsync(() => _levels.Reads >= 2);
        Assert.True(_levels.Reads >= 2);

        await watch.SetWatchingAsync(false);
        Assert.False(watch.Watching);
        _levels.Fails = false;
        await watch.SetWatchingAsync(true);
        await WaitUntilAsync(() => watch.Level > 0);
        Assert.Equal(0.4, watch.Level);
    }

    [Fact]
    public async Task AStopThatNeverAnswersDoesNotHoldTheCard()
    {
        _levels.StopHangs = true;

        // Not disposed on the way out: if the stop is not bounded, disposing would hang the run instead
        // of failing this test.
        var watch = Watch();
        await watch.SetWatchingAsync(true);
        await WaitUntilAsync(() => _levels.Reads == 1);

        var hiding = watch.SetWatchingAsync(false);
        var finished = await Task.WhenAny(hiding, Task.Delay(MicrophoneLevelWatch.StopWait * 3, Ct));

        Assert.Same(hiding, finished);
        Assert.False(watch.Watching);
        var asked = _levels.Reads;
        await watch.SetWatchingAsync(true);
        await WaitUntilAsync(() => _levels.Reads > asked);
        Assert.True(_levels.Reads > asked);
        _levels.StopHangs = false;
        await watch.DisposeAsync();
    }

    [Theory]
    [InlineData(MicrophoneLevelStates.Session, "between sessions")]
    [InlineData(MicrophoneLevelStates.NoMicrophone, "No microphone")]
    [InlineData(MicrophoneLevelStates.Unavailable, "privacy settings")]
    [InlineData(MicrophoneLevelStates.Opening, "Opening")]
    [InlineData(MicrophoneLevelStates.Listening, "nothing is recorded, kept or sent")]
    public void EachStateHasItsSentence(string state, string words)
    {
        Assert.Contains(words, MicrophoneLevelWatch.Describe(new MeterReading(state, 0, null)), StringComparison.Ordinal);
        Assert.Contains("did not answer", MicrophoneLevelWatch.Describe(null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyALiveReadingMovesTheMeter()
    {
        _levels.Reading = new MeterReading(MicrophoneLevelStates.Session, 0.7, null);
        await using var watch = Watch();

        await watch.SetWatchingAsync(true);
        await WaitUntilAsync(() => watch.Status != MicrophoneLevelWatch.NotWatching);

        Assert.Equal(0, watch.Level);
        Assert.Contains("between sessions", watch.Status, StringComparison.Ordinal);
    }

    public void Dispose() => _tick.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MicrophoneLevelWatch Watch() => new(_levels, ct => _tick.WaitAsync(ct));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }
    }

    private sealed class FakeLevels : IMicrophoneLevelGateway
    {
        private int _reads;

        public MeterReading Reading { get; set; } = new(MicrophoneLevelStates.Listening, 0.4, null);

        /// <summary>When set, the next request waits for it and ignores cancellation, as an answer already on its way does.</summary>
        public Task<MeterReading?>? Next { get; set; }

        public int Reads => Volatile.Read(ref _reads);

        public bool Fails { get; set; }

        public bool StopHangs { get; set; }

        public List<string?> DeviceIds { get; } = [];

        public List<string> Calls { get; } = [];

        public async Task<MeterReading?> ReadLevelAsync(string? deviceId, CancellationToken ct = default)
        {
            lock (Calls)
            {
                Calls.Add("read");
                DeviceIds.Add(deviceId);
            }

            _ = Interlocked.Increment(ref _reads);
            if (Fails)
            {
                throw new IOException("The pipe broke.");
            }

            if (Next is { } next)
            {
                Next = null;
                return await next.ConfigureAwait(false);
            }

            return Reading;
        }

        public Task StopLevelAsync(CancellationToken ct = default)
        {
            lock (Calls)
            {
                Calls.Add("stop");
            }

            return StopHangs ? Task.Delay(Timeout.Infinite, CancellationToken.None) : Task.CompletedTask;
        }
    }
}
