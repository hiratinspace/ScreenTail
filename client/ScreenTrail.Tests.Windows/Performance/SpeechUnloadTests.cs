using System.Globalization;
using System.Net.Http;
using System.Runtime.Versioning;
using ScreenTrail.Core.Net;
using ScreenTrail.Core.Speech;
using ScreenTrail.Service.Speech;

namespace ScreenTrail.Tests.Windows.Performance;

/// <summary>
/// What the speech model holds while loaded, and how much of it the idle unload gives back (ST-031,
/// ST-027).
///
/// The service loads the model at the first session and lets it go after five quiet minutes, because
/// ADR-0001 measured the whole process at 505 MB with it, against 600 MB for all of ScreenTrail. Whether
/// letting go actually returns the memory is a question about whisper.cpp's allocator on Windows, and only
/// a real load on a real machine answers it.
///
/// The laptop measures it with the default model. The file is kept between runs in a folder of the tests'
/// own, filled once from the copy the demo downloaded if there is one and from the publisher otherwise,
/// through the same egress allowlist and hash check the service uses. A hosted runner would download
/// 148 MB on every pull request to produce a number nobody reads, so there it says so and stops.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SpeechUnloadTests
{
    private static readonly SpeechModel Model = SpeechModels.BaseEnglish;

    [Fact]
    public async Task TheModelGivesItsMemoryBackAfterTheIdleUnload()
    {
        if (!Hardware.PerformanceCounts)
        {
            Measurements.Record($"Speech model memory: not measured on a hosted runner, which would download {Model.Name} ({Model.Bytes / (1024 * 1024)} MB) on every run; the laptop measures it");
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        // From a released model: nothing else in this process may still hold one when "before" is read. A
        // stop whose release outlived its bound frees its model only when the native call returns, and run
        // 44027063937 read 947 MB "before loading" against 242 MB loaded, another test's model going away
        // under this measurement. Waited for, never measured through: a model still held after the wait is
        // a release that did not finish, which is a fault of its own.
        var others = await NoModelHeldAsync(OthersReleasedWithin, ct);
        Assert.True(others == 0, string.Create(CultureInfo.InvariantCulture, $"{others} speech model(s) still held in this process {OthersReleasedWithin.TotalSeconds:F0} s into this test, so its memory cannot be measured from a released model"));

        var cache = ModelCache();
        await using var whisper = new WhisperRecogniser(
            new ModelDownload(new HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null))))),
            Model,
            cache,
            unloadAfter: TimeSpan.FromSeconds(3));

        ProcessCost.Settle();
        var before = ProcessCost.Sample();
        Assert.True(await whisper.PrepareAsync(ct), $"the model could not be had: {whisper.Unavailable}");

        // One segment, so the context's working buffers are allocated as they are in a session, not only
        // the weights. Quiet noise rather than silence: what is transcribed does not matter here.
        var random = new Random(31);
        var audio = new short[16_000 * 5];
        for (var i = 0; i < audio.Length; i++)
        {
            audio[i] = (short)random.Next(-200, 200);
        }

        _ = await whisper.TranscribeAsync(new SpeechSegment(0, 5_000, Truncated: false), audio, ct);
        Assert.True(whisper.Loaded, "the model was not in memory after a segment");
        ProcessCost.Settle();
        var loaded = ProcessCost.Sample();

        // The unload is the recogniser's own idle timer, three seconds here and five minutes in the service.
        // The processors go first and the model after them, so both are waited for.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((whisper.Loaded || WhisperRecogniser.ModelsInMemory > 0) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
        }

        Assert.False(whisper.Loaded, "the model was still loaded thirty seconds after its idle period ended");
        Assert.Equal(0, WhisperRecogniser.ModelsInMemory);
        ProcessCost.Settle();
        var after = ProcessCost.Sample();

        var took = loaded.PrivateBytes - before.PrivateBytes;
        var gaveBack = loaded.PrivateBytes - after.PrivateBytes;
        Measurements.Record(string.Create(
            CultureInfo.InvariantCulture,
            $"Speech model {Model.Name}: private memory {ProcessCost.Megabytes(before.PrivateBytes)} before loading, "
            + $"{ProcessCost.Megabytes(loaded.PrivateBytes)} loaded and used once (**+{ProcessCost.Megabytes(took)}**), "
            + $"{ProcessCost.Megabytes(after.PrivateBytes)} after the idle unload (**{ProcessCost.Megabytes(gaveBack)} given back**, "
            + $"{(took > 0 ? gaveBack * 100.0 / took : 0):F0}%); working set {ProcessCost.Megabytes(loaded.WorkingSet)} loaded, {ProcessCost.Megabytes(after.WorkingSet)} after "
            + $"(reported, not enforced: first measurement, see docs/dev/performance.md)"));
    }

    /// <summary>
    /// ST-112, owner's decision 2026-11-05: the service's stop never waits on a transcription in flight.
    /// Whisper.net will not free a model while whisper.cpp is still working on it, so a sign-out while the
    /// technician was talking spent the whole four-second budget in the recogniser's disposal (laptop runs
    /// 42031407710 and 42051212812). Here ten minutes of audio, far more work than the test allows, is in
    /// flight when the recogniser is disposed as the stop disposes it. The disposal returns inside its
    /// bound, and the native call answers the abort within <see cref="AbortAnsweredWithin"/>.
    /// The real microphone cannot be made to speak on cue, so StopTimeTests cannot put a segment in flight
    /// at the ask; this is the same disposal, with one certainly in flight.
    /// </summary>
    [Fact]
    public async Task AStopDoesNotWaitForATranscriptionInFlight()
    {
        if (!Hardware.PerformanceCounts)
        {
            Measurements.Record($"Stop mid-transcription: not measured on a hosted runner, which would download {Model.Name} ({Model.Bytes / (1024 * 1024)} MB) on every run; the laptop measures it");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var abandoned = 0;
        var whisper = new WhisperRecogniser(
            new ModelDownload(new HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null))))),
            Model,
            ModelCache(),
            abandoned: () => abandoned++);
        Assert.True(await whisper.PrepareAsync(ct), $"the model could not be had: {whisper.Unavailable}");

        // Ten minutes of quiet noise: twenty of whisper.cpp's thirty-second windows, each needing its own
        // encoder pass (about 4.9 s each at the full context on one thread on the laptop, run 44027063937),
        // so a call that ignored the abort would run for well over AbortAnsweredWithin after the stop. A
        // minute (two windows) could finish inside it unaborted and prove nothing. Half a second in, the
        // call is still computing the mel spectrogram of all ten minutes, which whisper.cpp does before
        // the first window and does not abort either; AStopInsideAnEncoderPassWaitsForTheRestOfIt measures
        // that and the pass a service-sized segment is in.
        var random = new Random(37);
        var audio = new short[16_000 * 600];
        for (var i = 0; i < audio.Length; i++)
        {
            audio[i] = (short)random.Next(-200, 200);
        }

        var transcribing = whisper.TranscribeAsync(new SpeechSegment(0, 600_000, Truncated: false), audio, CancellationToken.None);
        _ = transcribing.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        await Task.Delay(500, ct);
        Assert.False(transcribing.IsCompleted, "ten minutes of audio were transcribed in half a second, so nothing was in flight at the stop");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await whisper.DisposeAsync();
        var disposed = clock.Elapsed;

        // The native call let go when the recogniser's release ran to its end: the release waits for the
        // processor, which waits for whisper.cpp, and then frees the model. The transcription's own task
        // cannot say when that was, because Whisper.net ends its enumerator the moment the token is
        // cancelled while the native call runs on; until 2026-11-17 this test timed that task, which ended
        // with the disposal whatever whisper.cpp was doing. Waited for only until the abort's deadline, so
        // a call that ignores the abort fails here rather than after its ten minutes, and waited for at
        // all so that no model outlives this test into the next one's memory figure.
        var remaining = AbortAnsweredWithin - clock.Elapsed;
        _ = await Task.WhenAny(whisper.Released, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct));
        var letGo = clock.Elapsed;
        var answered = whisper.Released.IsCompleted;

        // Dropped, not half-kept: the call ends cancelled or with nothing, never with words.
        Transcribed? heard = null;
        if (transcribing.IsCompletedSuccessfully)
        {
            heard = await transcribing;
        }

        Measurements.Record(string.Create(
            CultureInfo.InvariantCulture,
            $"Stop mid-transcription ({Model.Name}, ten minutes of audio in flight): the recogniser's disposal returned in **{disposed.TotalMilliseconds:F0} ms** "
            + $"(enforced under {StopBound.Default.TotalMilliseconds:F0} ms + {SchedulingMargin.TotalMilliseconds:F0} ms); "
            + $"the native call {(answered ? "let go and the model was freed" : "had not let go")} **{letGo.TotalMilliseconds:F0} ms** after the stop (enforced under {AbortAnsweredWithin.TotalMilliseconds:F0} ms); "
            + $"left running past the stop's bound: {(abandoned > 0 ? "yes" : "no")} (reported)"));

        // 2026-11-08, owner proxy. The stop's bound stays StopBound.Default (500 ms), and what is enforced
        // is that the stop never waits past it, plus a little for the scheduler. Whether the native call
        // let go inside that bound (the recogniser's "abandoned", logged as "The speech model was still
        // busy at stop") is reported, not enforced: whisper.cpp reads its abort only between windows,
        // encoder passes and decoder steps, never inside the mel spectrogram or an encoder pass, and
        // Whisper.net 1.9.1 offers nothing finer (WhisperRecogniser._stopping). When it is past the bound
        // the release finishes by itself and frees the model once nothing uses it. The abort itself is
        // enforced: the call lets go within AbortAnsweredWithin of the stop, which a call running on
        // through its ten minutes would not. The narration segment it was working on is still dropped, by
        // the owner's decision of 2026-11-05: NarrationRecorder.AbandonedAtStop, a different count, one per
        // segment in flight.
        Assert.True(
            disposed < StopBound.Default + SchedulingMargin,
            string.Create(CultureInfo.InvariantCulture, $"the stop waited on the native call: disposal with a transcription in flight took {disposed.TotalMilliseconds:F0} ms, bound {StopBound.Default.TotalMilliseconds:F0} ms + {SchedulingMargin.TotalMilliseconds:F0} ms"));
        Assert.True(
            answered,
            string.Create(CultureInfo.InvariantCulture, $"the abort was not answered: the native call still held the model {letGo.TotalMilliseconds:F0} ms after the stop, bound {AbortAnsweredWithin.TotalMilliseconds:F0} ms"));
        Assert.Null(heard);
    }

    /// <summary>
    /// What a stop in the middle of a segment waits for, and why the abort cannot shorten it (2026-11-17).
    /// Reported, not enforced: the first measurement.
    ///
    /// whisper.cpp reads the abort before each window, after each whole encoder pass and after each
    /// decoder step. A segment the service hands over is at most <see cref="SpeechGateOptions.MaximumSegment"/>,
    /// one window, so the work it cannot interrupt is the state and mel spectrogram before the pass and
    /// the pass itself. This builds a processor as the recogniser does (one thread, the full context),
    /// times those, and stops one segment just after its encoder pass begins, timing how long whisper.cpp
    /// keeps the processor after that. It also times the mel spectrogram of the ten minutes
    /// <see cref="AStopDoesNotWaitForATranscriptionInFlight"/> puts in flight, which is where that test's
    /// stop lands.
    /// </summary>
    [Fact]
    public async Task AStopInsideAnEncoderPassWaitsForTheRestOfIt()
    {
        if (!Hardware.PerformanceCounts)
        {
            Measurements.Record($"Stop inside an encoder pass: not measured on a hosted runner, which would download {Model.Name} ({Model.Bytes / (1024 * 1024)} MB) on every run; the laptop measures it");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var cache = ModelCache();
        var path = Path.Combine(cache, $"ggml-{Model.Name}.bin");
        using (var http = new HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null)))))
        {
            Assert.True(await new ModelDownload(http).EnsureAsync(Model, path, null, ct), "the model could not be had");
        }

        var longest = (int)new SpeechGateOptions().MaximumSegment.TotalSeconds;
        var segment = Noise(longest, 43);
        var tenMinutes = Noise(600, 37);
        using var factory = Whisper.net.WhisperFactory.FromPath(path);
        var clock = new System.Diagnostics.Stopwatch();

        // A whole segment, not stopped: when its one encoder pass begins, and when the call is done.
        var begun = TimeSpan.Zero;
        await using (var whole = Processor(factory, () => begun = clock.Elapsed))
        {
            clock.Restart();
            await foreach (var _ in whole.ProcessAsync(segment, ct))
            {
            }
        }

        var wholeCall = clock.Elapsed;

        // The same segment, stopped 100 ms into its encoder pass. Let go when the processor's disposal
        // returns, which waits for the native call.
        var inPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = Processor(factory, () => _ = inPass.TrySetResult());
        using var stopping = new CancellationTokenSource();
        var running = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var _ in stopped.ProcessAsync(segment, stopping.Token))
                    {
                    }
                }
                catch (OperationCanceledException)
                {
                    // The stop: Whisper.net ends the enumerator at once, and whisper.cpp runs on.
                }
            },
            ct);
        await inPass.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await Task.Delay(100, ct);
        clock.Restart();
        await stopping.CancelAsync();
        await running;
        await stopped.DisposeAsync();
        var letGo = clock.Elapsed;

        // The ten minutes, up to the first window and no further: the encoder-begin handler says no, which
        // whisper.cpp reads as the end of the call.
        var mel = TimeSpan.Zero;
        await using (var first = Processor(factory, () => mel = clock.Elapsed, carryOn: false))
        {
            clock.Restart();
            await foreach (var _ in first.ProcessAsync(tenMinutes, ct))
            {
            }
        }

        Measurements.Record(string.Create(
            CultureInfo.InvariantCulture,
            $"Stop inside an encoder pass ({Model.Name}, {WhisperRecogniser.Threads} thread, full context, {longest} s of noise, the longest segment the service hands over): "
            + $"the encoder pass began {begun.TotalMilliseconds:F0} ms into the call and the call took {wholeCall.TotalMilliseconds:F0} ms; "
            + $"stopped 100 ms into the pass, whisper.cpp let go **{letGo.TotalMilliseconds:F0} ms** after the stop (bound {StopBound.Default.TotalMilliseconds:F0} ms; reported); "
            + $"the mel spectrogram of ten minutes, before the first window, took {mel.TotalMilliseconds:F0} ms (reported)"));
    }

    /// <summary>A processor built as <see cref="WhisperRecogniser"/> builds its full-context one, which says when each encoder pass begins.</summary>
    private static Whisper.net.WhisperProcessor Processor(Whisper.net.WhisperFactory factory, Action begins, bool carryOn = true) =>
        factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(WhisperRecogniser.Threads)
            .WithProbabilities()
            .WithEncoderBeginHandler(_ =>
            {
                begins();
                return carryOn;
            })
            .Build();

    /// <summary>Quiet noise as samples whisper.cpp takes. What is said does not matter here, only how long it is.</summary>
    private static float[] Noise(int seconds, int seed)
    {
        var random = new Random(seed);
        var samples = new float[16_000 * seconds];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = random.Next(-200, 200) / 32768f;
        }

        return samples;
    }

    /// <summary>Waits, up to <paramref name="within"/>, until no recogniser in this process holds a model. Returns how many still do.</summary>
    private static async Task<int> NoModelHeldAsync(TimeSpan within, CancellationToken ct)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (WhisperRecogniser.ModelsInMemory > 0 && waited.Elapsed < within)
        {
            await Task.Delay(100, ct);
        }

        return WhisperRecogniser.ModelsInMemory;
    }

    /// <summary>What the stop's disposal may take beyond <see cref="StopBound.Default"/>: the scheduler, not the model.</summary>
    private static TimeSpan SchedulingMargin { get; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How soon after the stop a transcription in flight must have let go of the model, aborted. It has to
    /// cover what whisper.cpp does not abort: a stop that lands as an encoder pass begins waits all of it,
    /// most of the 4.9 s a segment takes at the full context on one thread on the laptop (run
    /// 44027063937), or the rest of the mel spectrogram before the first window. And it has to be far short
    /// of the work in flight, twenty windows at about five seconds each, so it tells an answered abort from
    /// a call that ran on. It was 3 s until 2026-11-17, timed on a task that ended with the disposal
    /// whatever the native call did, so it held nothing to it.
    /// </summary>
    private static TimeSpan AbortAnsweredWithin { get; } = TimeSpan.FromSeconds(8);

    /// <summary>How long the memory measurement waits for another test's release to finish before it measures.</summary>
    private static TimeSpan OthersReleasedWithin { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The tests' own copy of the model, kept between runs. Seeded from the service's folder when the
    /// demo has already downloaded it there; the hash is checked either way before it is loaded.
    /// </summary>
    internal static string ModelCache()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cache = Path.Combine(local, "ScreenTrail.Tests", "models");
        Directory.CreateDirectory(cache);
        var file = $"ggml-{Model.Name}.bin";
        var ours = Path.Combine(cache, file);
        var theService = Path.Combine(local, "ScreenTrail", "models", file);
        if (!File.Exists(ours) && File.Exists(theService))
        {
            File.Copy(theService, ours);
        }

        return cache;
    }
}
