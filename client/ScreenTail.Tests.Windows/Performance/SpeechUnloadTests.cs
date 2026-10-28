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
    /// The tests' own copy of the model, kept between runs. Seeded from the service's folder when the
    /// demo has already downloaded it there; the hash is checked either way before it is loaded.
    /// </summary>
    private static string ModelCache()
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
