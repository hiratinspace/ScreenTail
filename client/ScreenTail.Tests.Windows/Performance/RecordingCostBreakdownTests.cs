using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.Versioning;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Net;
using ScreenTail.Core.Perf;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Store;
using ScreenTail.Service.Capture;
using ScreenTail.Service.Privacy;
using ScreenTail.Service.Speech;
using ScreenTail.Shared.Schema;
using Whisper.net;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>
/// Where a recording session's processor time goes, and the ceiling a narrated one is held to (ST-031,
/// after M1).
///
/// M1 (2026-11-13) saw the capture service at about 25% of the laptop during a six-minute AnyDesk session
/// with the owner narrating. The throttle's budget on this eight-processor machine is 60% of one core,
/// 7.5% of the machine. The first breakdown (laptop run 44014246194) found speech about 80% of a narrated
/// session's processor time, because whisper.cpp encodes a full thirty-second window for every segment,
/// and OCR at twice the size most of the rest. Whisper now runs on one thread, and a smaller encoder
/// context per segment is built behind a switch that is off (<see cref="AudioContext"/>).
///
/// <list type="bullet">
/// <item>Each capture stage per unit of work, kept short: the first run's full table is in
/// docs/dev/performance.md.</item>
/// <item>Speech per segment with the service's recogniser, full context against sized, in every English
/// voice the machine has (up to three), with the word error of each and the memory each holds. Synthetic
/// speech only: real narration is ST-027's fixture, still to be recorded.</item>
/// <item>The composed service four ways: capturing with the room quiet; capturing with a technician
/// narrating, which is held to <see cref="NarratedCeilingPercentOfOneCore"/>; the same with the sized
/// context; and the same with the throttle acting, as the service runs, to say whether narration still
/// trips it.</item>
/// </list>
///
/// Nothing here writes an image, a window title, a transcript or the script's words; only numbers, thread
/// descriptions and module names. On a hosted runner it says it did not measure and stops, because a
/// shared VM's timings say nothing about a technician's laptop and the speech model is 148 MB.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class RecordingCostBreakdownTests
{
    /// <summary>
    /// The ceiling on a narrated recording, as the service ships it (full context, the throttle not
    /// acting, so the figure is of a session doing all its work), mean over the window, % of one core net
    /// of the test host. Set from the laptop after whisper moved to one thread, with a margin; see
    /// docs/dev/performance.md. It is a regression ceiling, not ST-031's budget of 60%: a narrated session
    /// is still over that until the sized context is switched on or the throttle's accounting changes, and
    /// that is recorded in ST-031's status rather than hidden by a ceiling set to pass.
    /// </summary>
    internal const double NarratedCeilingPercentOfOneCore = 160;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static string Folder { get; } = Path.Combine(Path.GetTempPath(), "screentail-tests", "breakdown");

    private static int Processors => Environment.ProcessorCount;

    /// <summary>The throttle's budget on this machine, as a percentage of it.</summary>
    private static double BudgetPercentOfMachine => CpuBudget.RecordingShareOf(Processors) * 100;

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
        window.FillScreen();
        window.ShowText(string.Join(Environment.NewLine, SceneText(0)));

        var idle = await IdleRateAsync(ct);
        using var capturer = new ScreenshotCapturer();
        var looks = await StageAsync("scene look (grid and hash)", 20, idle, n =>
        {
            var grid = capturer.CaptureSceneGrid(window.Handle) ?? throw new InvalidOperationException("no grid");
            _ = PerceptualHash.OfGrid(grid);
            return Task.CompletedTask;
        });
        CapturedFrame? captured = null;
        var capture = await StageAsync("frame capture (grab, convert, JPEG)", 5, idle, n =>
        {
            captured = capturer.CaptureForegroundWindow(window.Handle) ?? throw new InvalidOperationException("no frame");
            return Task.CompletedTask;
        });

        var dense = RenderDense(0);
        var dialog = RenderDialog(0);
        var service = new WindowsOcrRecogniser();
        var asCaptured = new WindowsOcrRecogniser(scale: 1.0);
        _ = await service.ReadAsync(dense, ct);
        var ocr = new List<(string Frame, StageResult Twice, StageResult Once)>();
        foreach (var (name, image) in new[] { ("dense 9 pt list", dense), ("settings dialog", dialog) })
        {
            var twice = await StageAsync($"OCR as the service reads it (up to 2x), {name}", 3, idle, n => service.ReadAsync(image, ct));
            var once = await StageAsync($"OCR as captured (1x), {name}", 3, idle, n => asCaptured.ReadAsync(image, ct));
            ocr.Add((name, twice, once));
        }

        var text = await service.ReadAsync(dense, ct);
        var engine = new RedactionEngine();
        FrameRedaction? redaction = null;
        var search = await StageAsync($"pattern search ({text.Words.Count} words)", 10, idle, n =>
        {
            redaction = engine.RedactFrame(text.Words);
            return Task.CompletedTask;
        });
        var regions = redaction!.Regions.ToList();
        var masker = new WindowsFrameMasker();
        var mask = await StageAsync($"mask, downscale and JPEG ({regions.Count} regions)", 5, idle, n =>
        {
            _ = masker.Mask(dense, regions, Downscale.MaxEdge);
            return Task.CompletedTask;
        });

        var ocrLine = string.Join(", ", ocr.Select(o => string.Create(Invariant, $"OCR {o.Frame} **{o.Twice.CpuMs:F0}** at up to 2x against {o.Once.CpuMs:F0} as captured")));
        Measurements.Record(string.Create(
            Invariant,
            $"Breakdown, per unit (processor ms, net of the idle process at {idle * 100:F2}% of one core): scene look **{looks.CpuMs:F1}**, frame capture at {captured!.SourceWidth}x{captured.SourceHeight} **{capture.CpuMs:F1}**, {ocrLine}, "
            + $"pattern search **{search.CpuMs:F1}**, mask **{mask.CpuMs:F1}**"));
    }

    [Fact]
    public async Task SpeechCostsThisMuchASegmentWithTheFullAndTheSizedContext()
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

        var voices = SpokenAudio.EnglishVoices(3);
        var idle = await IdleRateAsync(ct);

        // Memory first, each recogniser on its own: the full context holds one whisper state, the sized one
        // up to three (two sizes and the full window, for a segment longer than the larger size holds).
        var first = await ClipsAsync(voices[0], ct);
        var longest = SpokenAudio.WithRoomTone(SpokenAudio.Join([.. first.Spoken.Take(4)], 0.3, 7), 7);
        var memory = new List<string>();
        foreach (var sized in new[] { false, true })
        {
            ProcessCost.Settle();
            var unloaded = ProcessCost.Sample();
            await using var recogniser = Recogniser(sized);
            Assert.True(await recogniser.PrepareAsync(ct), $"the model could not be had: {recogniser.Unavailable}");
            foreach (var audio in new[] { first.Clips[0].Audio, first.Clips[2].Audio, longest })
            {
                _ = await recogniser.TranscribeAsync(Segment(audio), audio, ct);
            }

            ProcessCost.Settle();
            var loaded = ProcessCost.Sample();
            memory.Add(string.Create(Invariant, $"{(sized ? "sized (states for 512, 1024 and the full 1500)" : "full (one state)")} **+{ProcessCost.Megabytes(loaded.PrivateBytes - unloaded.PrivateBytes)}** private, working set +{ProcessCost.Megabytes(loaded.WorkingSet - unloaded.WorkingSet)}"));
        }

        Measurements.Record(string.Create(Invariant, $"Speech model memory, loaded and used on {first.Clips[0].Audio.Length / 16_000.0:F1} s, {first.Clips[2].Audio.Length / 16_000.0:F1} s and {longest.Length / 16_000.0:F1} s: ") + string.Join("; ", memory));

        await using var full = Recogniser(sized: false);
        await using var sizedContext = Recogniser(sized: true);
        Assert.True(await full.PrepareAsync(ct) && await sizedContext.PrepareAsync(ct), "the model could not be had");

        // Per voice, per clip: one call each way, its processor time and its word error.
        var rows = new List<string>();
        var totals = new Dictionary<bool, (double Errors, int Words, double Cpu, int Calls)> { [false] = (0, 0, 0, 0), [true] = (0, 0, 0, 0) };
        var byClip = new Dictionary<string, List<(double Full, double Sized)>>();
        for (var v = 0; v < voices.Count; v++)
        {
            var set = v == 0 ? first : await ClipsAsync(voices[v], ct);
            foreach (var (name, audio, said) in set.Clips)
            {
                var cells = new Dictionary<bool, (double Cpu, double Wer)>();
                foreach (var sized in new[] { false, true })
                {
                    var recogniser = sized ? sizedContext : full;
                    var wer = 0.0;
                    var cost = await StageAsync($"speech, voice {v + 1}, {name}, {(sized ? "sized" : "full")} context", 1, idle, async n =>
                    {
                        var heard = await recogniser.TranscribeAsync(Segment(audio), audio, ct);
                        wer = SpokenAudio.WordErrorRate(said, heard?.Text ?? string.Empty);
                    }, warm: v == 0 && name == set.Clips[0].Name);
                    cells[sized] = (cost.CpuMs / 1000, wer);
                    var words = said.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    var t = totals[sized];
                    totals[sized] = (t.Errors + (wer * words), t.Words + words, t.Cpu + (cost.CpuMs / 1000), t.Calls + 1);
                }

                byClip.TryAdd(name, []);
                byClip[name].Add((cells[false].Cpu, cells[true].Cpu));
                rows.Add(string.Create(Invariant, $"voice {v + 1} {name} ({audio.Length / 16_000.0:F1} s, context {AudioContext.For(TimeSpan.FromSeconds(audio.Length / 16_000.0))}): word error full {cells[false].Wer * 100:F0}%, sized {cells[true].Wer * 100:F0}%"));
            }
        }

        Measurements.Record(string.Create(Invariant, $"Speech word error, full against sized context, {voices.Count} synthetic voice(s): ") + string.Join("; ", rows));
        var clipLine = string.Join(", ", byClip.Select(c => string.Create(Invariant, $"{c.Key} {c.Value.Average(x => x.Full):F2} s / {c.Value.Average(x => x.Sized):F2} s")));
        Measurements.Record(string.Create(
            Invariant,
            $"Speech word error overall: full **{totals[false].Errors / Math.Max(1, totals[false].Words) * 100:F1}%**, sized **{totals[true].Errors / Math.Max(1, totals[true].Words) * 100:F1}%** over {totals[false].Words} words; "
            + $"processor per segment, mean: full **{totals[false].Cpu / Math.Max(1, totals[false].Calls):F2} s**, sized **{totals[true].Cpu / Math.Max(1, totals[true].Calls):F2} s**; by clip, full against sized: {clipLine}"));

        // Noise the energy gate would pass: what each invents, and what it costs.
        var noise = Noise(5, 9);
        var invented = new List<string>();
        foreach (var sized in new[] { false, true })
        {
            var recogniser = sized ? sizedContext : full;
            var words = 0;
            var cost = await StageAsync($"speech, noise, {(sized ? "sized" : "full")} context", 2, idle, async n =>
                words += (await recogniser.TranscribeAsync(Segment(noise), noise, ct))?.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0,
                warm: false);
            invented.Add(string.Create(Invariant, $"{(sized ? "sized" : "full")} {cost.CpuMs / 1000:F2} s, {words / 2.0:F1} words invented a call"));
        }

        // The configuration before this change, for the before and after: two threads, full context.
        var model = Path.Combine(SpeechUnloadTests.ModelCache(), $"ggml-{SpeechModels.BaseEnglish.Name}.bin");
        using var factory = WhisperFactory.FromPath(model);
        await using var twoThreads = factory.CreateBuilder().WithLanguage("en").WithThreads(2).WithProbabilities().Build();
        var two = Floats(first.Clips[2].Audio);
        var before = await StageAsync("speech, two threads and full context as before, two sentences", 2, idle, async n => _ = await HearAsync(twoThreads, two, ct));
        Measurements.Record(string.Create(Invariant, $"Speech, 5 s of noise: ") + string.Join("; ", invented)
            + string.Create(Invariant, $". Before (two threads, full context) on the two-sentence clip: **{before.CpuMs / 1000:F2} s** of processor in {before.WallMs / 1000:F2} s ({before.Busy:F1} processors busy)"));
    }

    [Fact]
    public async Task ANarratedRecordingStaysUnderItsCeiling()
    {
        if (!Hardware.PerformanceCounts)
        {
            Measurements.Record("Recording with narration: not measured on a hosted runner; the laptop measures and enforces it");
            return;
        }

        Hardware.RequireScreenCapture();
        var ct = TestContext.Current.CancellationToken;
        if (!SpokenAudio.Available)
        {
            Measurements.Record("Recording with narration: not measured, this machine has no Windows voice to speak the script");
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

        var narrating = spoken.Chunk(2).Select((pair, i) => SpokenAudio.WithRoomTone(SpokenAudio.Join(pair, 0.3, 10 + i), 20 + i)).ToArray();
        var dense = Enumerable.Range(0, 8).Select(RenderDense).ToList();

        using var window = DesktopWindow.Create("ScreenTail breakdown session");
        window.RequireForeground();
        window.FillScreen();
        window.ShowText(string.Join(Environment.NewLine, SceneText(0)));

        ProcessCost.Settle();
        var host = await ProcessCost.MeasureAsync(TimeSpan.FromSeconds(5), 3, ct);
        var baseline = host.CpuPercentOfOneCore;

        var quiet = await SessionAsync("capture, the room quiet", window, [], dense, new ServiceRigOptions(Speech: true, ApplyThrottle: false), TimeSpan.FromSeconds(30), baseline, ct);
        var narrated = await SessionAsync("capture and narration, as shipped (one thread, full context)", window, narrating, dense, new ServiceRigOptions(Speech: true, ApplyThrottle: false), TimeSpan.FromSeconds(45), baseline, ct);
        var sized = await SessionAsync("capture and narration, sized context (switched off in the service)", window, narrating, dense, new ServiceRigOptions(Speech: true, ApplyThrottle: false, SizeContext: true), TimeSpan.FromSeconds(45), baseline, ct);
        _ = await SessionAsync("capture and narration, the throttle acting as in the service", window, narrating, dense, new ServiceRigOptions(Speech: true, ApplyThrottle: true), TimeSpan.FromSeconds(75), baseline, ct);

        var cpu = narrated - baseline;
        Measurements.Record(string.Create(
            Invariant,
            $"Recording with narration, net of the test host's {baseline:F1}% of one core: quiet room {quiet - baseline:F1}, narrated **{cpu:F1}**, narrated with the sized context {sized - baseline:F1} (% of one core; "
            + $"{(quiet - baseline) / Processors:F1}%, {cpu / Processors:F1}%, {(sized - baseline) / Processors:F1}% of this machine's {Processors} logical processors). "
            + $"Ceiling on the narrated run {NarratedCeilingPercentOfOneCore:F0}% of one core ({NarratedCeilingPercentOfOneCore / Processors:F1}% of the machine), enforced; ST-031's budget is 60% of one core ({BudgetPercentOfMachine:F1}% of this machine)"));

        Assert.True(
            cpu < NarratedCeilingPercentOfOneCore,
            string.Create(Invariant, $"a narrated recording used {cpu:F1}% of one core net of the host, ceiling {NarratedCeilingPercentOfOneCore:F0}%"));
    }

    /// <summary>
    /// One composed session, measured in one-second slices (the throttle's own reading), and recorded.
    /// Returns the window's mean, % of one core, gross of the host.
    /// </summary>
    private static async Task<double> SessionAsync(
        string name, DesktopWindow window, short[][] clips, List<byte[]> dense, ServiceRigOptions options, TimeSpan measure, double baseline, CancellationToken ct)
    {
        var microphone = new ScriptedMicrophone(clips, pauseSeconds: 3);
        var scope = new ScopeDecision(CaptureScope.RemoteTool, "rdp", RemoteToolKind.Rdp, "Capturing", window.Handle);
        await using var rig = await ServiceRig.StartAsync(() => scope, options with { Microphone = microphone }, ct);
        window.RequireForeground();

        var clock = Stopwatch.StartNew();
        var moves = new List<(double At, CaptureLevelChange Move)>();
        rig.Throttle.Changed += move =>
        {
            lock (moves)
            {
                moves.Add((clock.Elapsed.TotalSeconds, move));
            }
        };
        Assert.True(await rig.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: true, ct: ct), "the session did not start");

        var staged = 0;
        using var working = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workload = Task.Run(
            async () =>
            {
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
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var callsBefore = rig.Speech?.Calls.Count ?? 0;
            var framesBefore = rig.Redaction.Progress.Frames;
            var scenesBefore = rig.Scenes.Counts;
            var measuredFrom = clock.Elapsed.TotalSeconds;
            var cost = await ProcessCost.MeasureAsync(TimeSpan.FromSeconds(1), (int)measure.TotalSeconds, ct);
            var calls = rig.Speech?.Calls.Skip(callsBefore).ToList() ?? [];
            var scenes = rig.Scenes.Counts;
            var elapsed = cost.Elapsed.TotalSeconds;
            var readings = cost.CpuBySlice.Select(c => c / Processors).ToList();
            List<(double At, CaptureLevelChange Move)> moved;
            lock (moves)
            {
                moved = [.. moves];
            }

            var throttle = options.ApplyThrottle
                ? (moved.Count == 0
                    ? $"acting, never moved; ended at {rig.Throttle.Level}"
                    : string.Create(Invariant, $"acting, ended at **{rig.Throttle.Level}**: ") + string.Join(", ", moved.Select(m => string.Create(Invariant, $"{m.Move.From} -> {m.Move.To} at {m.At - measuredFrom:F0} s ({m.Move.Share * 100:F1}%)"))))
                : (moved.Count == 0 ? "reading, not acting: would never have moved" : string.Create(Invariant, $"reading, not acting: would have moved {moved.Count} times, to {moved[^1].Move.To}"));

            Measurements.Record(string.Create(
                Invariant,
                $"Session, {name}: **{cost.CpuPercentOfOneCore - baseline:F1}%** of one core net of the host over {elapsed:F0} s (**{(cost.CpuPercentOfOneCore - baseline) / Processors:F1}%** of the machine); "
                + $"one-second readings as a share of the machine: median {Median(readings):F1}, peak **{readings.Max():F1}**, {readings.Count(r => r > BudgetPercentOfMachine)} of {readings.Count} over the {BudgetPercentOfMachine:F1}% budget; "
                + $"by thread: {cost.CpuByThreadNameLine(6)}. "
                + $"Work in the window: {scenes.Seen - scenesBefore.Seen} scene looks, {scenes.Kept - scenesBefore.Kept} scene frames kept, {rig.Redaction.Progress.Frames - framesBefore} frames redacted, "
                + $"{calls.Count} speech segments ({calls.Sum(c => c.AudioSeconds):F0} s of audio, the model busy {calls.Sum(c => c.WallSeconds):F0} s, {(calls.Count == 0 ? 0 : calls.Average(c => c.WallSeconds)):F1} s a segment), "
                + $"narration kept {rig.Narration.Kept}, refused {rig.Narration.Rejected}; the throttle {throttle}"));
            return cost.CpuPercentOfOneCore;
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

    /// <summary>Four clips in one voice: a short phrase, one sentence, two and three, each with room tone under it.</summary>
    private static async Task<(List<short[]> Spoken, (string Name, short[] Audio, string Said)[] Clips)> ClipsAsync(global::Windows.Media.SpeechSynthesis.VoiceInformation voice, CancellationToken ct)
    {
        var spoken = new List<short[]>();
        foreach (var sentence in SpokenAudio.Script)
        {
            spoken.Add(await SpokenAudio.SpeakAsync(sentence, Folder, ct, voice));
        }

        const string Phrase = "Restarting the spooler now.";
        var phrase = await SpokenAudio.SpeakAsync(Phrase, Folder, ct, voice);
        return (spoken,
        [
            ("short phrase", SpokenAudio.WithRoomTone(phrase, 1), Phrase),
            ("one sentence", SpokenAudio.WithRoomTone(spoken[5], 2), SpokenAudio.Script[5]),
            ("two sentences", SpokenAudio.WithRoomTone(SpokenAudio.Join(spoken.Take(2), 0.3, 3), 3), string.Join(' ', SpokenAudio.Script.Take(2))),
            ("three sentences", SpokenAudio.WithRoomTone(SpokenAudio.Join(spoken.Skip(2).Take(3), 0.3, 4), 4), string.Join(' ', SpokenAudio.Script.Skip(2).Take(3))),
        ]);
    }

    private static WhisperRecogniser Recogniser(bool sized) => new(
        new ModelDownload(new System.Net.Http.HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null))))),
        SpeechModels.BaseEnglish,
        SpeechUnloadTests.ModelCache(),
        sizeContext: sized);

    private static SpeechSegment Segment(short[] audio) => new(0, audio.Length * 1000L / SpokenAudio.SampleRate, false);

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

    private sealed record StageResult(string Stage, int Times, double CpuMs, double WallMs)
    {
        /// <summary>Processors kept busy while the stage ran: processor time over wall time.</summary>
        public double Busy => WallMs <= 0 ? 0 : CpuMs / WallMs;
    }

    /// <summary>
    /// Runs one stage <paramref name="times"/> times and records what each cost: the process's cycles over
    /// the run, less what it spends idle over the same time, divided by the count.
    /// </summary>
    private static async Task<StageResult> StageAsync(string stage, int times, double idlePerSecond, Func<int, Task> work, bool warm = true)
    {
        _ = CycleCounter.PerSecond;
        if (warm)
        {
            await work(0);
        }

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
