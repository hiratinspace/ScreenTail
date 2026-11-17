using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.Versioning;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Net;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using ScreenTail.Service.Capture;
using ScreenTail.Service.Privacy;
using ScreenTail.Service.Speech;
using Whisper.net;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>
/// Where a recording session's processor time goes, stage by stage (ST-031, after M1).
///
/// M1 (2026-11-13) saw the capture service at about 25% of the laptop during a six-minute AnyDesk session
/// with the owner narrating, against a budget of 7.5% of this eight-processor machine (60% of one core),
/// while <see cref="ServiceCostTests"/> measures recording at under 5% of one core. That test has no
/// speech, stages its frames rather than capturing them, and was last tuned before OCR began reading at
/// twice the size (#275). These three measurements say what each part costs on the laptop, so the fix is
/// aimed at what is actually expensive:
///
/// <list type="bullet">
/// <item>Each stage on its own, per unit of work: a scene look, a frame capture, OCR at the service's
/// enlargement and as captured, the pattern search, the mask, and a whole frame through the redaction
/// worker and the encrypted store.</item>
/// <item>Speech per segment, with the service's recogniser, at several lengths of spoken audio, and a few
/// configurations of the model beside it with their word error rate, so a cheaper setting is weighed
/// against what it costs the transcript.</item>
/// <item>The composed service three ways: capturing with the microphone hearing only the room, the same
/// with a technician narrating, and narration alone. The differences are what speech and capture each add,
/// and each run is broken down by thread.</item>
/// </list>
///
/// Reported, never enforced: this is a measurement for a plan, and the budget tests stay where they are.
/// Nothing here writes an image, a window title, a transcript or the script's words; only numbers, thread
/// descriptions and module names. On a hosted runner it says it did not measure and stops, because a
/// shared VM's timings say nothing about a technician's laptop and the speech model is 148 MB.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class RecordingCostBreakdownTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static string Folder { get; } = Path.Combine(Path.GetTempPath(), "screentail-tests", "breakdown");

    [Fact]
    public async Task EachStageOfARecordingHasAMeasuredCost()
    {
        if (!Hardware.PerformanceCounts)
        {
            Measurements.Record("Recording breakdown by stage: not measured on a hosted runner; the laptop measures it");
            return;
        }

        Hardware.RequireScreenCapture();
        var ct = TestContext.Current.CancellationToken;
        using var window = DesktopWindow.Create("ScreenTail breakdown target");
        window.RequireForeground();
        FillScreen(window);
        window.ShowText(string.Join(Environment.NewLine, SceneText(0)));

        var idle = await IdleRateAsync(ct);
        Measurements.Record(string.Create(Invariant, $"Breakdown: {Environment.ProcessorCount} logical processors; the test process idles at {idle * 100:F2}% of one core, taken off every figure below"));

        using var capturer = new ScreenshotCapturer();

        // A scene look: the 17x16 grid and its hash, once a second while a session records.
        var looks = await StageAsync("scene look (grid and hash)", 60, idle, n =>
        {
            var grid = capturer.CaptureSceneGrid(window.Handle) ?? throw new InvalidOperationException("no grid");
            _ = PerceptualHash.OfGrid(grid);
            return Task.CompletedTask;
        });

        // A frame capture: what a click or a kept scene costs before redaction.
        var timings = new List<CaptureTiming>();
        CapturedFrame? captured = null;
        var capture = await StageAsync("frame capture (grab, convert, JPEG)", 10, idle, n =>
        {
            captured = capturer.CaptureForegroundWindow(window.Handle) ?? throw new InvalidOperationException("no frame");
            timings.Add(captured.Timing);
            return Task.CompletedTask;
        });
        Measurements.Record(string.Create(
            Invariant,
            $"Breakdown, capture split: {captured!.SourceWidth}x{captured.SourceHeight}, {captured.Image.Length / 1024} KB JPEG; median grab {Median(timings.Select(t => t.Grab.TotalMilliseconds)):F1} ms, convert {Median(timings.Select(t => t.Convert.TotalMilliseconds)):F1} ms, encode {Median(timings.Select(t => t.Encode.TotalMilliseconds)):F1} ms"));

        // OCR, at the service's enlargement and as captured, on three kinds of frame.
        var dense = RenderDense(0);
        var dialog = RenderDialog(0);
        var service = new WindowsOcrRecogniser();
        var asCaptured = new WindowsOcrRecogniser(scale: 1.0);
        _ = await service.ReadAsync(dense, ct);
        var ocr = new List<(string Frame, StageResult Twice, StageResult Once, int Words)>();
        foreach (var (name, image) in new[] { ("dense 9 pt list", dense), ("settings dialog", dialog), ("captured test window", captured.Image) })
        {
            var words = (await service.ReadAsync(image, ct)).Words.Count;
            var twice = await StageAsync($"OCR as the service reads it (up to 2x), {name}", 6, idle, n => service.ReadAsync(image, ct));
            var once = await StageAsync($"OCR as captured (1x), {name}", 6, idle, n => asCaptured.ReadAsync(image, ct));
            ocr.Add((name, twice, once, words));
        }

        // The pattern search and the mask, on the dense frame's own words.
        var engine = new RedactionEngine();
        var text = await service.ReadAsync(dense, ct);
        FrameRedaction? redaction = null;
        var search = await StageAsync($"pattern search ({text.Words.Count} words)", 20, idle, n =>
        {
            redaction = engine.RedactFrame(text.Words);
            return Task.CompletedTask;
        });
        var regions = redaction!.Regions.ToList();
        var masker = new WindowsFrameMasker();
        var mask = await StageAsync($"mask, downscale and JPEG ({regions.Count} regions)", 10, idle, n =>
        {
            _ = masker.Mask(dense, regions, Downscale.MaxEdge);
            return Task.CompletedTask;
        });

        // A whole frame through the running worker: OCR, search, mask, the login check and the encrypted store.
        var scope = new ScopeDecision(CaptureScope.RemoteTool, "rdp", RemoteToolKind.Rdp, "Capturing", window.Handle);
        StageResult whole;
        await using (var rig = await ServiceRig.StartAsync(() => scope, new ServiceRigOptions(ApplyThrottle: false), ct))
        {
            Assert.True(await rig.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: true, ct: ct), "the session did not start");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            // The rig itself while recording a still window, so what is left is the frames' own cost.
            var rigIdle = await IdleRateAsync(ct);
            var frames = Enumerable.Range(1, 8).Select(RenderDense).ToList();
            var refused = 0;
            whole = await StageAsync("a staged frame through the redaction worker and the store", frames.Count, rigIdle, async i =>
            {
                var frame = new StagedFrame(Guid.NewGuid().ToString("N"), rig.Machine.NowMs, FrameTrigger.Click, 1920, 1080, null, frames[i]);
                if (!await rig.Machine.TryStageFrameAsync(frame, ct))
                {
                    refused++;
                    return;
                }

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (rig.Pending.Depth > 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(5, ct);
                }
            });
            Measurements.Record(string.Create(Invariant, $"Breakdown, the worker's own figure: {rig.Redaction.Progress.Frames} frames redacted, a mean of {rig.Redaction.Progress.PerFrame.TotalMilliseconds:F0} ms a frame, slowest {rig.Redaction.Progress.Slowest.TotalMilliseconds:F0} ms, {refused} refused by the session (state {rig.Machine.State}); the rig recording a still window idles at {rigIdle * 100:F2}% of one core"));
        }

        var denseOcr = ocr[0].Twice;
        var storeEstimate = whole.CpuMs - denseOcr.CpuMs - search.CpuMs - mask.CpuMs;
        var ocrLine = string.Join(", ", ocr.Select(o => string.Create(Invariant, $"OCR {o.Frame} ({o.Words} words) **{o.Twice.CpuMs:F0}** at up to 2x against {o.Once.CpuMs:F0} as captured")));
        Measurements.Record(string.Create(
            Invariant,
            $"Breakdown, per unit (processor ms, net of the idle process): scene look **{looks.CpuMs:F2}**, frame capture **{capture.CpuMs:F1}**, {ocrLine}, "
            + $"pattern search **{search.CpuMs:F1}**, mask **{mask.CpuMs:F1}**, a whole dense frame through the worker **{whole.CpuMs:F0}** "
            + $"(so the store write and the rest come to about {storeEstimate:F0}, by difference)"));
    }

    [Fact]
    public async Task SpeechCostsThisMuchASegment()
    {
        if (!Hardware.PerformanceCounts)
        {
            Measurements.Record("Speech per segment: not measured on a hosted runner, which would download base.en (141 MB); the laptop measures it");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        if (!SpokenAudio.Available)
        {
            Measurements.Record("Speech per segment: not measured, this machine has no Windows voice to speak the script");
            return;
        }

        var spoken = new List<short[]>();
        foreach (var sentence in SpokenAudio.Script)
        {
            spoken.Add(await SpokenAudio.SpeakAsync(sentence, Folder, ct));
        }

        var shortClip = await SpokenAudio.SpeakAsync("Restarting the spooler now.", Folder, ct);
        var clips = new (string Name, short[] Audio, string Said)[]
        {
            ("short phrase", SpokenAudio.WithRoomTone(shortClip, 1), "Restarting the spooler now."),
            ("one sentence", SpokenAudio.WithRoomTone(spoken[0], 2), SpokenAudio.Script[0]),
            ("two sentences", SpokenAudio.WithRoomTone(SpokenAudio.Join(spoken.Take(2), 0.3, 3), 3), string.Join(' ', SpokenAudio.Script.Take(2))),
            ("three sentences", SpokenAudio.WithRoomTone(SpokenAudio.Join(spoken.Skip(2).Take(3), 0.3, 4), 4), string.Join(' ', SpokenAudio.Script.Skip(2).Take(3))),
        };
        var noise = Noise(5, 9);

        var idle = await IdleRateAsync(ct);
        await using var whisper = new WhisperRecogniser(
            new ModelDownload(new System.Net.Http.HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null))))),
            SpeechModels.BaseEnglish,
            SpeechUnloadTests.ModelCache());
        Assert.True(await whisper.PrepareAsync(ct), $"the model could not be had: {whisper.Unavailable}");
        _ = await whisper.TranscribeAsync(new SpeechSegment(0, 1000, false), clips[0].Audio, ct);

        var lines = new List<string>();
        foreach (var (name, audio, said) in clips)
        {
            var wer = new List<double>();
            var cost = await StageAsync($"speech, {name}", 3, idle, async n =>
            {
                var heard = await whisper.TranscribeAsync(new SpeechSegment(0, audio.Length * 1000L / SpokenAudio.SampleRate, false), audio, ct);
                wer.Add(SpokenAudio.WordErrorRate(said, heard?.Text ?? string.Empty));
            });
            lines.Add(string.Create(Invariant, $"{name} ({audio.Length / (double)SpokenAudio.SampleRate:F1} s): **{cost.CpuMs / 1000:F2} s** of processor in {cost.WallMs / 1000:F2} s ({cost.Busy:F1} processors busy), word error {wer.Average() * 100:F0}%"));
        }

        var noiseWords = 0;
        var noiseCost = await StageAsync("speech, room noise", 3, idle, async n =>
        {
            var heard = await whisper.TranscribeAsync(new SpeechSegment(0, 5000, false), noise, ct);
            noiseWords += heard?.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
        });
        lines.Add(string.Create(Invariant, $"5 s of loud noise the energy gate would pass: **{noiseCost.CpuMs / 1000:F2} s** in {noiseCost.WallMs / 1000:F2} s, {noiseWords / 3.0:F1} words invented a call"));
        Measurements.Record("Speech per segment, the service's recogniser (base.en, 2 threads): " + string.Join("; ", lines));

        // The same model configured other ways, on the two-sentence clip and on the noise, for the plan.
        var model = Path.Combine(SpeechUnloadTests.ModelCache(), $"ggml-{SpeechModels.BaseEnglish.Name}.bin");
        using var factory = WhisperFactory.FromPath(model);
        var (twoName, twoAudio, twoSaid) = clips[2];
        var variants = new (string Name, Func<WhisperProcessorBuilder, WhisperProcessorBuilder> Configure)[]
        {
            ("2 threads (as the service)", b => b.WithThreads(2)),
            ("1 thread", b => b.WithThreads(1)),
            ("4 threads", b => b.WithThreads(4)),
            ("2 threads, audio context 768 (about 15 s)", b => b.WithThreads(2).WithAudioContextSize(768)),
            ("2 threads, no temperature fallback", b => b.WithThreads(2).WithTemperatureInc(0f)),
        };
        var rows = new List<string>();
        foreach (var (name, configure) in variants)
        {
            await using var processor = configure(factory.CreateBuilder().WithLanguage("en").WithProbabilities()).Build();
            var samples = Floats(twoAudio);
            var noiseSamples = Floats(noise);
            _ = await HearAsync(processor, samples, ct);
            var wer = new List<double>();
            var speech = await StageAsync($"variant {name}, two sentences", 2, idle, async n => wer.Add(SpokenAudio.WordErrorRate(twoSaid, await HearAsync(processor, samples, ct))));
            var room = await StageAsync($"variant {name}, noise", 2, idle, async n => _ = await HearAsync(processor, noiseSamples, ct));
            rows.Add(string.Create(Invariant, $"{name}: **{speech.CpuMs / 1000:F2} s** of processor in {speech.WallMs / 1000:F2} s, word error {wer.Average() * 100:F0}%; noise {room.CpuMs / 1000:F2} s in {room.WallMs / 1000:F2} s"));
        }

        Measurements.Record(string.Create(Invariant, $"Speech variants on {twoAudio.Length / (double)SpokenAudio.SampleRate:F1} s of two sentences and 5 s of noise (base.en): ") + string.Join("; ", rows));
    }

    [Fact]
    public async Task ARecordingWithNarrationIsBrokenDownByWhatItDoes()
    {
        if (!Hardware.PerformanceCounts)
        {
            Measurements.Record("Recording with narration, broken down: not measured on a hosted runner; the laptop measures it");
            return;
        }

        Hardware.RequireScreenCapture();
        var ct = TestContext.Current.CancellationToken;
        if (!SpokenAudio.Available)
        {
            Measurements.Record("Recording with narration, broken down: not measured, this machine has no Windows voice to speak the script");
            return;
        }

        // Two sentences at a time with a breath between them, then a pause: about one segment every
        // fourteen seconds, as M1's 26 segments in six minutes were, and speech for about three quarters
        // of the time, as narrating throughout is.
        var spoken = new List<short[]>();
        foreach (var sentence in SpokenAudio.Script)
        {
            spoken.Add(await SpokenAudio.SpeakAsync(sentence, Folder, ct));
        }

        var narrating = spoken.Chunk(2).Select((pair, i) => SpokenAudio.WithRoomTone(SpokenAudio.Join(pair, 0.3, 10 + i), 20 + i)).ToList();
        var dense = Enumerable.Range(0, 8).Select(RenderDense).ToList();

        using var window = DesktopWindow.Create("ScreenTail breakdown session");
        window.RequireForeground();
        FillScreen(window);
        window.ShowText(string.Join(Environment.NewLine, SceneText(0)));

        ProcessCost.Settle();
        var host = await ProcessCost.MeasureAsync(TimeSpan.FromSeconds(5), 4, ct);
        var runs = new List<(string Name, CostWindow Cost)>();
        foreach (var (name, clips, screen) in new[]
        {
            ("capture, the microphone hearing only the room", Array.Empty<short[]>(), true),
            ("capture and narration", narrating.ToArray(), true),
            ("narration alone, a still screen and no clicks", narrating.ToArray(), false),
        })
        {
            runs.Add((name, await SessionAsync(name, window, clips, screen, dense, ct)));
        }

        var baseline = host.CpuPercentOfOneCore;
        var capture = runs[0].Cost.CpuPercentOfOneCore - baseline;
        var both = runs[1].Cost.CpuPercentOfOneCore - baseline;
        var speechAlone = runs[2].Cost.CpuPercentOfOneCore - baseline;
        Measurements.Record(string.Create(
            Invariant,
            $"Recording broken down (mean over each window, % of one core net of the test host's {baseline:F1}): capture with a quiet room **{capture:F1}**, capture and narration **{both:F1}**, narration alone **{speechAlone:F1}**; "
            + $"so narration adds **{both - capture:F1}** to capture, and capture adds {both - speechAlone:F1} to narration. "
            + $"Of this machine's {Environment.ProcessorCount} logical processors: {capture / Environment.ProcessorCount:F1}%, {both / Environment.ProcessorCount:F1}%, {speechAlone / Environment.ProcessorCount:F1}% "
            + $"(budget 60% of one core, {60.0 / Environment.ProcessorCount:F1}% of this machine)"));
    }

    private static async Task<CostWindow> SessionAsync(string name, DesktopWindow window, short[][] clips, bool screen, List<byte[]> dense, CancellationToken ct)
    {
        var microphone = new ScriptedMicrophone(clips, pauseSeconds: 3);
        var scope = new ScopeDecision(CaptureScope.RemoteTool, "rdp", RemoteToolKind.Rdp, "Capturing", window.Handle);
        await using var rig = await ServiceRig.StartAsync(() => scope, new ServiceRigOptions(microphone, Speech: true, ApplyThrottle: false), ct);
        window.RequireForeground();
        Assert.True(await rig.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: true, ct: ct), "the session did not start");

        var staged = 0;
        using var working = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workload = Task.Run(
            async () =>
            {
                if (!screen)
                {
                    return;
                }

                // A remote screen that keeps changing (a new view every two seconds) and a click's frame
                // every six, the rate of a technician working through a console.
                using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
                for (var second = 1; await tick.WaitForNextTickAsync(working.Token); second++)
                {
                    if (second % 2 == 0)
                    {
                        window.ShowText(string.Join(Environment.NewLine, SceneText(second)));
                    }

                    if (second % 6 == 1)
                    {
                        var frame = new StagedFrame(Guid.NewGuid().ToString("N"), rig.Machine.NowMs, FrameTrigger.Click, 1920, 1080, null, dense[staged % dense.Count]);
                        if (await rig.Machine.TryStageFrameAsync(frame, working.Token))
                        {
                            staged++;
                        }
                    }
                }
            },
            CancellationToken.None);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), ct);
            var callsBefore = rig.Speech?.Calls.Count ?? 0;
            var framesBefore = rig.Redaction.Progress.Frames;
            var scenesBefore = rig.Scenes.Counts;
            var movesBefore = rig.ThrottleMoves.Count;
            // One-second slices, the throttle's own reading, so the spread is what it sees.
            var cost = await ProcessCost.MeasureAsync(TimeSpan.FromSeconds(1), 45, ct);
            var calls = rig.Speech?.Calls.Skip(callsBefore).ToList() ?? [];
            var scenes = rig.Scenes.Counts;
            var moves = rig.ThrottleMoves.Skip(movesBefore).ToList();
            var elapsed = cost.Elapsed.TotalSeconds;
            var played = microphone.Played;

            Measurements.Record(string.Create(
                Invariant,
                $"Session, {name}: **{cost.CpuPercentOfOneCore:F1}%** of one core over {elapsed:F0} s ({cost.CpuPercentOfMachine:F1}% of the machine; one-second readings as a share of the machine {CostWindow.Spread([.. cost.CpuBySlice.Select(c => c / Environment.ProcessorCount)], "F1")}, {cost.CpuBySlice.Count(c => c / Environment.ProcessorCount > 7.5)} of {cost.CpuBySlice.Count} over the 7.5% budget); "
                + $"by thread: {cost.CpuByThreadNameLine()}. "
                + $"Work in the window: {scenes.Seen - scenesBefore.Seen} scene looks, {scenes.Kept - scenesBefore.Kept} scene frames kept, {rig.Redaction.Progress.Frames - framesBefore} frames redacted (worker mean {rig.Redaction.Progress.PerFrame.TotalMilliseconds:F0} ms), "
                + $"{calls.Count} speech segments sent to the model ({calls.Sum(c => c.AudioSeconds):F0} s of audio, the model busy {calls.Sum(c => c.WallSeconds):F0} s of {elapsed:F0}, a mean of {(calls.Count == 0 ? 0 : calls.Average(c => c.WallSeconds)):F1} s a segment), "
                + $"narration heard {rig.Narration.Heard}, kept {rig.Narration.Kept}, refused as invented {rig.Narration.Rejected}; microphone played {played.Speech:F0} s of speech in {played.All:F0} s; "
                + $"the throttle, reading but not acting, would have moved: {(moves.Count == 0 ? "never" : string.Join(", ", moves.Select(m => string.Create(Invariant, $"{m.From} -> {m.To} at {m.Share * 100:F1}%"))))}"));
            return cost;
        }
        finally
        {
            await working.CancelAsync();
            try
            {
                await workload;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Processor seconds the process spends per wall second with nothing to do, from cycles: the quietest of
    /// several one-second slices after a pause. The first laptop run took the mean of three slices straight
    /// after the session test and read 108% of a core, which zeroed every stage; whatever was still winding
    /// down then is not what a stage costs.
    /// </summary>
    private static async Task<double> IdleRateAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        ProcessCost.Settle();
        var window = await ProcessCost.MeasureAsync(TimeSpan.FromSeconds(1), 5, ct);
        return window.CpuBySlice.Min() / 100;
    }

    /// <summary>The whole primary screen, as AnyDesk full screen is; <see cref="DesktopWindow.Maximize"/> left the first run's window at 420x220.</summary>
    private static void FillScreen(DesktopWindow window)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        window.MoveTo(screen.Left, screen.Top, screen.Width, screen.Height);
        window.RequireForeground();
    }

    private sealed record StageResult(string Stage, int Times, double CpuMs, double WallMs)
    {
        /// <summary>Processors kept busy while the stage ran: processor time over wall time.</summary>
        public double Busy => WallMs <= 0 ? 0 : CpuMs / WallMs;
    }

    /// <summary>
    /// Runs one stage <paramref name="times"/> times and records what each cost: the process's cycles over
    /// the run, less what it spends idle over the same time, divided by the count.
    /// </summary>
    private static async Task<StageResult> StageAsync(string stage, int times, double idlePerSecond, Func<int, Task> work)
    {
        _ = CycleCounter.PerSecond;
        await work(0);
        ProcessCost.Settle();
        var cyclesBefore = CycleCounter.OfThisProcess();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < times; i++)
        {
            await work(i);
        }

        var wall = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var cpu = (CycleCounter.OfThisProcess() - cyclesBefore) / CycleCounter.PerSecond;
        var result = new StageResult(stage, times, Math.Max(0, cpu - (idlePerSecond * wall)) * 1000 / times, wall * 1000 / times);
        Measurements.Record(string.Create(Invariant, $"Stage, {stage}: {result.CpuMs:F1} ms of processor and {result.WallMs:F1} ms wall each, over {times} ({result.Busy:F2} processors busy)"));
        return result;
    }

    private static async Task<string> HearAsync(WhisperProcessor processor, float[] samples, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var piece in processor.ProcessAsync(samples, ct))
        {
            _ = text.Append(piece.Text).Append(' ');
        }

        return text.ToString();
    }

    private static float[] Floats(short[] samples) => [.. samples.Select(s => s / 32768f)];

    /// <summary>Loud broadband noise with bursts, the kind an energy gate lets through: a fan, typing, a chair.</summary>
    private static short[] Noise(double seconds, int seed)
    {
        var random = new Random(seed);
        var samples = new short[(int)(seconds * SpokenAudio.SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            var burst = (i / 1600) % 3 == 0 ? 6000 : 1500;
            samples[i] = (short)random.Next(-burst, burst);
        }

        return samples;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    /// <summary>What the test window shows: a remote console's lines, different for each step so the sampler sees a change.</summary>
    private static string[] SceneText(int step) =>
    [
        string.Create(Invariant, $"Services ({step})"),
        string.Create(Invariant, $"Print Spooler    Running    Automatic    Local System    {step}"),
        string.Create(Invariant, $"Windows Update    Stopped    Manual    Local System    {step + 1}"),
        string.Create(Invariant, $"Event Log    Running    Automatic    Local Service    {step + 2}"),
        string.Create(Invariant, $"Task Scheduler    Running    Automatic    Local System    {step + 3}"),
        string.Create(Invariant, $"Backup volume D: {80 + (step % 15)}% used"),
    ];

    /// <summary>
    /// A 1920x1080 console list in 9 pt text, the densest thing a technician reads in a remote session,
    /// with a few strings shaped like secrets so the search and the mask have work to do. Made up here.
    /// No sign-in words: the first laptop run had "Password:" on it, the login heuristic took the frames
    /// for a credential prompt, and both capture sessions measured a suppressed recording.
    /// </summary>
    private static byte[] RenderDense(int index)
    {
        using var bitmap = new Bitmap(1920, 1080, PixelFormat.Format24bppRgb);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Segoe UI", 9);
        using var ink = new SolidBrush(Color.FromArgb(30, 30, 30));
        var names = new[] { "Print Spooler", "Windows Update", "Event Log", "Task Scheduler", "DHCP Client", "DNS Client", "Workstation", "Server", "Remote Registry", "Windows Time" };
        for (var row = 0; row < 62; row++)
        {
            var y = 8 + (row * 17);
            var name = names[(row + index) % names.Length];
            graphics.DrawString(string.Create(Invariant, $"{name} {row}"), font, ink, 10, y);
            graphics.DrawString(row % 3 == 0 ? "Stopped" : "Running", font, ink, 330, y);
            graphics.DrawString(row % 2 == 0 ? "Automatic" : "Manual", font, ink, 460, y);
            graphics.DrawString("Local System", font, ink, 600, y);
            graphics.DrawString(string.Create(Invariant, $"C:\\Windows\\System32\\svchost.exe -k netsvcs -p {index}{row}"), font, ink, 760, y);
            var tail = (row % 20) switch
            {
                4 => string.Create(Invariant, $"api_key=8f14e45fceea167a5a36dedd4bea25{index:D2}"),
                11 => string.Create(Invariant, $"pwd=Tr0ub4dor&3x{index}"),
                17 => string.Create(Invariant, $"token ghp_{index}aB3dE5fG7hJ9kL1mN3pQ5rS7tU9vW1xY3z"),
                _ => string.Create(Invariant, $"Started {row % 24:D2}:{(row * 7) % 60:D2} by NT AUTHORITY"),
            };
            graphics.DrawString(tail, font, ink, 1380, y);
        }

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Jpeg);
        return buffer.ToArray();
    }

    /// <summary>A printer's properties dialog at 1920x1080, as <see cref="ServiceCostTests"/> stages.</summary>
    private static byte[] RenderDialog(int index)
    {
        using var bitmap = new Bitmap(1920, 1080, PixelFormat.Format24bppRgb);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(243, 243, 243));
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var heading = new Font("Segoe UI", 16, FontStyle.Bold);
        using var body = new Font("Segoe UI", 11);
        using var ink = new SolidBrush(Color.FromArgb(28, 28, 28));
        graphics.DrawString(string.Create(Invariant, $"Printer Properties ({index})"), heading, ink, 60, 40);
        var y = 110;
        foreach (var label in new[] { "General", "Sharing", "Ports", "Advanced", "Colour Management", "Device Settings", "Location: Floor 2 east", "Comment: shared queue", "Model: LaserJet 4250", "Print Test Page", "Apply" })
        {
            graphics.DrawString(label, body, ink, 100, y);
            graphics.DrawString(string.Create(Invariant, $"{label} {index}"), body, ink, 1140, y);
            y += 34;
        }

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Jpeg);
        return buffer.ToArray();
    }
}
