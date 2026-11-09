using System.Globalization;
using System.Net.Http;
using System.Runtime.Versioning;
using ScreenTail.Core.Net;
using ScreenTail.Core.Speech;
using ScreenTail.Service.Speech;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>
/// What the speech model holds while loaded, and how much of it the idle unload gives back (ST-031,
/// ST-027).
///
/// The service loads the model at the first session and lets it go after five quiet minutes, because
/// ADR-0001 measured the whole process at 505 MB with it, against 600 MB for all of ScreenTail. Whether
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
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (whisper.Loaded && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
        }

        Assert.False(whisper.Loaded, "the model was still loaded thirty seconds after its idle period ended");
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
        // encoder pass (about half a second each for base.en on the laptop, run 42524815671), so a call
        // that ignored the abort would run for well over AbortAnsweredWithin after the stop. A minute (two
        // windows) could finish inside it unaborted and prove nothing.
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

        // Dropped, not half-kept: the call ends cancelled or with nothing, never with words. Waited for
        // only until the abort's deadline, so a call that ignores the abort fails here rather than after
        // it has run through its ten minutes.
        var remaining = AbortAnsweredWithin - clock.Elapsed;
        _ = await Task.WhenAny(transcribing, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct));
        var ended = clock.Elapsed;
        var answered = transcribing.IsCompleted;
        Transcribed? heard = null;
        if (transcribing.IsCompletedSuccessfully)
        {
            heard = await transcribing;
        }

        Measurements.Record(string.Create(
            CultureInfo.InvariantCulture,
            $"Stop mid-transcription ({Model.Name}, ten minutes of audio in flight): the recogniser's disposal returned in **{disposed.TotalMilliseconds:F0} ms** "
            + $"(enforced under {StopBound.Default.TotalMilliseconds:F0} ms + {SchedulingMargin.TotalMilliseconds:F0} ms); "
            + $"the native call {(answered ? "ended" : "had not ended")} {ended.TotalMilliseconds:F0} ms after the stop (enforced under {AbortAnsweredWithin.TotalMilliseconds:F0} ms); "
            + $"left running past the stop's bound: {(abandoned > 0 ? "yes" : "no")} (reported)"));

        // 2026-11-08, owner proxy. The stop's bound stays StopBound.Default (500 ms), and what is enforced
        // is that the stop never waits past it, plus a little for the scheduler. Whether the native call
        // let go inside that bound (the recogniser's "abandoned", logged as "The speech model was still
        // busy at stop") is reported, not enforced: whisper.cpp reads its abort only between compute
        // steps, so the abort's latency is one step, 510 ms for base.en on the laptop (run 42524815671),
        // and it varies with the CPU and the model. When it is past the bound the release finishes by
        // itself and frees the model once nothing uses it. The abort itself is enforced: the call ends
        // within AbortAnsweredWithin of the stop, which a call running on through its ten minutes would
        // not. The narration segment it was working on is still dropped, by the owner's decision of
        // 2026-11-05: NarrationRecorder.AbandonedAtStop, a different count, one per segment in flight.
        Assert.True(
            disposed < StopBound.Default + SchedulingMargin,
            string.Create(CultureInfo.InvariantCulture, $"the stop waited on the native call: disposal with a transcription in flight took {disposed.TotalMilliseconds:F0} ms, bound {StopBound.Default.TotalMilliseconds:F0} ms + {SchedulingMargin.TotalMilliseconds:F0} ms"));
        Assert.True(
            answered,
            string.Create(CultureInfo.InvariantCulture, $"the abort was not answered: the native call was still running {ended.TotalMilliseconds:F0} ms after the stop, bound {AbortAnsweredWithin.TotalMilliseconds:F0} ms"));
        Assert.Null(heard);
    }

    /// <summary>What the stop's disposal may take beyond <see cref="StopBound.Default"/>: the scheduler, not the model.</summary>
    private static TimeSpan SchedulingMargin { get; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How soon after the stop a transcription in flight must have ended, aborted. Several of whisper.cpp's
    /// compute steps (510 ms for base.en on the laptop, run 42524815671), and far short of the work in
    /// flight, so it tells an answered abort from a call that ran on.
    /// </summary>
    private static TimeSpan AbortAnsweredWithin { get; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The tests' own copy of the model, kept between runs. Seeded from the service's folder when the
    /// demo has already downloaded it there; the hash is checked either way before it is loaded.
    /// </summary>
    internal static string ModelCache()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cache = Path.Combine(local, "ScreenTail.Tests", "models");
        Directory.CreateDirectory(cache);
        var file = $"ggml-{Model.Name}.bin";
        var ours = Path.Combine(cache, file);
        var theService = Path.Combine(local, "ScreenTail", "models", file);
        if (!File.Exists(ours) && File.Exists(theService))
        {
            File.Copy(theService, ours);
        }

        return cache;
    }
}
