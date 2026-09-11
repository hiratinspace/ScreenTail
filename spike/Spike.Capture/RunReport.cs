using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ScreenTail.Spike.Core;

namespace ScreenTail.Spike.Capture;

internal sealed record FrameRecord(
    int NativeWidth,
    int NativeHeight,
    int OutWidth,
    int OutHeight,
    int JpegBytes,
    double CaptureMs,
    double EncodeMs);

internal sealed record UiaTransition(DateTime At, string Process, UiaFocusSnapshot Snapshot, SubtreeVerdict Verdict)
{
    public bool SameStateAs(UiaTransition other) =>
        Process == other.Process
        && Snapshot.WindowClass == other.Snapshot.WindowClass
        && Snapshot.FocusedClass == other.Snapshot.FocusedClass
        && Snapshot.FocusedControlType == other.Snapshot.FocusedControlType
        && Verdict.Kind == other.Verdict.Kind;
}

/// <summary>
/// Everything a run measured. No window titles, key identities, OCR text or transcript text are recorded.
/// Each counter has a single writer; the report is only read after every worker has finished.
/// </summary>
internal sealed class RunReport(SpikeOptions options)
{
    public const double HookBudgetMs = 5.0;
    private const int JpegBudgetBytes = 400 * 1024;
    private const int MinTypedChars = 300;

    private static readonly string[] RdpProcesses = ["mstsc", "msrdc"];

    private readonly List<FrameRecord> _frames = [];
    private readonly List<UiaTransition> _uiaTransitions = [];
    private readonly Dictionary<string, int> _uiaErrors = new(StringComparer.Ordinal);
    private readonly LatencyStats _uiaQueryMs = new(100_000);
    private readonly LatencyStats _ocrMs = new(10_000);
    private readonly LatencyStats _whisperChunkMs = new(10_000);
    private readonly LatencyStats _cpuPercent = new(100_000);
    private readonly LatencyStats _captureMs = new(100_000);

    private DateTime _startedAt;
    private DateTime _endedAt;
    private LatencySummary? _hookCallback;
    private LatencySummary? _dispatchDelay;
    private long _ringDropped;
    private bool _unhookSuspected;
    private int _watchdogStrikes;
    private int _typingBursts;
    private int _typedChars;
    private int _shortcuts;
    private int _enters;
    private int _overlayProbes;
    private double _overlayMaxFraction;
    private double _ocrConfidenceSum;
    private int _ocrWords;
    private int _whisperWords;
    private long _maxWorkingSet;

    public string LowLevelHooksTimeout { get; init; } = string.Empty;

    public string? OcrUnavailable { get; set; }

    public string? WhisperUnavailable { get; set; }

    public string AudioSource { get; set; } = "not started";

    public int Clicks { get; set; }

    public int Debounced { get; set; }

    public int ScreenshotsDroppedBusy { get; set; }

    public int ScreenshotErrors { get; set; }

    public int OcrSkippedBusy { get; set; }

    public int WhisperChunksDropped { get; set; }

    public void MarkStarted() => _startedAt = DateTime.Now;

    public void CountKeyboard(KeyboardEvent keyboardEvent)
    {
        switch (keyboardEvent.Type)
        {
            case KeyboardEvent.TypingBurst:
                _typingBursts++;
                _typedChars += keyboardEvent.Count;
                break;
            case KeyboardEvent.Shortcut:
                _shortcuts++;
                break;
            case KeyboardEvent.Enter:
                _enters++;
                break;
        }
    }

    public void AddFrame(FrameRecord frame)
    {
        _frames.Add(frame);
        _captureMs.Add(frame.CaptureMs + frame.EncodeMs);
    }

    public void RecordOverlayProbe(double fraction)
    {
        _overlayProbes++;
        _overlayMaxFraction = Math.Max(_overlayMaxFraction, fraction);
    }

    public void RecordOcr(double ms, float confidence, int words)
    {
        _ocrMs.Add(ms);
        _ocrConfidenceSum += confidence;
        _ocrWords += words;
    }

    public void RecordUiaQuery(double ms) => _uiaQueryMs.Add(ms);

    public void AddUiaTransition(UiaTransition transition) => _uiaTransitions.Add(transition);

    public void CountUiaError(string kind) => _uiaErrors[kind] = _uiaErrors.GetValueOrDefault(kind) + 1;

    public void RecordWhisperChunk(double ms, int words)
    {
        _whisperChunkMs.Add(ms);
        _whisperWords += words;
    }

    public void RecordResources(double cpuPercent, long workingSet)
    {
        _cpuPercent.Add(cpuPercent);
        _maxWorkingSet = Math.Max(_maxWorkingSet, workingSet);
    }

    public string ProgressLine(int seconds) => Inv(
        $"[{seconds / 60:00}:{seconds % 60:00}] typed chars {_typedChars}, clicks {Clicks}, frames {_frames.Count}, whisper chunks {_whisperChunkMs.Offered}, UIA polls {_uiaQueryMs.Offered}");

    public void Finish(LatencySummary hookCallback, LatencySummary dispatchDelay, long ringDropped, HookHealthMonitor monitor)
    {
        _endedAt = DateTime.Now;
        _hookCallback = hookCallback;
        _dispatchDelay = dispatchDelay;
        _ringDropped = ringDropped;
        _unhookSuspected = monitor.SuspectedUnhooked;
        _watchdogStrikes = monitor.TotalStrikes;
    }

    public string Write(string outDir)
    {
        var path = Path.Combine(outDir, "report.md");
        File.WriteAllText(path, ToMarkdown());
        return path;
    }

    private string ToMarkdown()
    {
        var md = new StringBuilder();
        var minutes = (_endedAt - _startedAt).TotalMinutes;

        md.AppendLine(Inv($"# ST-001 spike run: {_startedAt:yyyy-MM-dd HH:mm}"));
        md.AppendLine();
        md.AppendLine(Inv($"- Load: whisper {On(options.Whisper && WhisperUnavailable is null)}, UIA {On(options.Uia)}, OCR {On(options.Ocr && OcrUnavailable is null)}; ran {minutes:0.0} min"));
        md.AppendLine(Inv($"- Machine: {Environment.ProcessorCount} logical cores, {RuntimeInformation.OSDescription}, OS arch {RuntimeInformation.OSArchitecture}, process arch {RuntimeInformation.ProcessArchitecture}"));
        md.AppendLine(Inv($"- Audio: {AudioSource}"));
        if (WhisperUnavailable is not null)
        {
            md.AppendLine(Inv($"- Whisper unavailable: {WhisperUnavailable}"));
        }

        if (OcrUnavailable is not null)
        {
            md.AppendLine(Inv($"- OCR unavailable: {OcrUnavailable}"));
        }

        AppendAc1(md, minutes);
        AppendAc2(md);
        AppendAc3(md);
        AppendAc4(md);
        return md.ToString();
    }

    private void AppendAc1(StringBuilder md, double minutes)
    {
        var hook = _hookCallback!;
        var dispatch = _dispatchDelay!;
        string verdict;
        if (hook.Count == 0)
        {
            verdict = "INCONCLUSIVE - no hook callbacks recorded";
        }
        else if (!hook.Passes || _unhookSuspected)
        {
            verdict = "FAIL";
        }
        else if (minutes < 4.9 || _typedChars < MinTypedChars || !options.FullLoad || WhisperUnavailable is not null)
        {
            verdict = Inv($"PASS for this run, but INCONCLUSIVE for AC1 (needs >= 5 min, >= {MinTypedChars} chars typed, full load)");
        }
        else
        {
            verdict = "PASS";
        }

        md.AppendLine();
        md.AppendLine("## AC1: added input latency < 5 ms under load, no hook silently removed");
        md.AppendLine();
        md.AppendLine($"**Verdict: {verdict}**");
        md.AppendLine();
        md.AppendLine("| Metric | Value |");
        md.AppendLine("|---|---|");
        md.AppendLine(Inv($"| Hook callbacks timed (mouse + keyboard) | {hook.Count} |"));
        md.AppendLine(Inv($"| Callback time p50 / p95 / p99 / max (ms) | {hook.P50:0.000} / {hook.P95:0.000} / {hook.P99:0.000} / {hook.Max:0.000} |"));
        md.AppendLine(Inv($"| Callbacks >= {HookBudgetMs} ms | {hook.OverThreshold} |"));
        md.AppendLine(Inv($"| Dispatch delay p50 / p99 / max (ms, ~15.6 ms tick resolution) | {dispatch.P50:0} / {dispatch.P99:0} / {dispatch.Max:0} |"));
        md.AppendLine(Inv($"| Silent unhook suspected | {(_unhookSuspected ? "YES" : "no")} ({_watchdogStrikes} watchdog strikes) |"));
        md.AppendLine(Inv($"| LowLevelHooksTimeout | {LowLevelHooksTimeout} |"));
        md.AppendLine(Inv($"| Input samples dropped (ring full) | {_ringDropped} |"));
        md.AppendLine(Inv($"| Typed chars / bursts / shortcuts / enters | {_typedChars} / {_typingBursts} / {_shortcuts} / {_enters} |"));
        md.AppendLine(Inv($"| Clicks / debounced / screenshots | {Clicks} / {Debounced} / {_frames.Count} |"));
        md.AppendLine(Inv($"| Whisper 5 s chunks / p50 / max ms per chunk / dropped | {Stat(_whisperChunkMs, 50)} / {Max(_whisperChunkMs)} ({_whisperChunkMs.Offered} chunks, {WhisperChunksDropped} dropped, {_whisperWords} words) |"));
        md.AppendLine(Inv($"| UIA polls / p95 / max ms | {_uiaQueryMs.Offered} / {Stat(_uiaQueryMs, 95)} / {Max(_uiaQueryMs)} (errors: {ErrorsText()}) |"));
        md.AppendLine(Inv($"| Screenshot capture+encode p95 / max ms | {Stat(_captureMs, 95)} / {Max(_captureMs)} ({ScreenshotErrors} errors, {ScreenshotsDroppedBusy} dropped busy) |"));
        md.AppendLine(Inv($"| OCR frames / p50 ms / mean confidence | {_ocrMs.Offered} / {Stat(_ocrMs, 50)} / {(_ocrMs.Offered == 0 ? 0 : _ocrConfidenceSum / _ocrMs.Offered):0.00} ({OcrSkippedBusy} skipped busy) |"));
        md.AppendLine(Inv($"| Process CPU avg-ish p50 / max (% of all cores) | {Stat(_cpuPercent, 50)} / {Max(_cpuPercent)} |"));
        md.AppendLine(Inv($"| Working set max (MB) | {_maxWorkingSet / (1024.0 * 1024):0} |"));
        md.AppendLine(Inv($"| GC gen0 / gen1 / gen2, total pause (ms) | {GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}, {GC.GetTotalPauseDuration().TotalMilliseconds:0.0} |"));
    }

    private void AppendAc2(StringBuilder md)
    {
        var rdp = _uiaTransitions.Where(t => RdpProcesses.Contains(t.Process, StringComparer.OrdinalIgnoreCase)).ToList();
        var verdict = rdp.Count == 0
            ? "NOT EXERCISED - no RDP (mstsc/msrdc) window was focused"
            : rdp.Any(t => t.Verdict.Kind == SubtreeKind.Opaque) ? "PASS" : "FAIL";

        md.AppendLine();
        md.AppendLine("## AC2: RDP focused -> FlaUI reports an opaque subtree");
        md.AppendLine();
        md.AppendLine($"**Verdict: {verdict}**");
        md.AppendLine();
        md.AppendLine("Focus transitions (process, classes and control types only):");
        md.AppendLine();
        md.AppendLine("| Time | Process | Window class | Focused class | Control type | Children | Area | Verdict |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var t in _uiaTransitions.Take(200))
        {
            md.AppendLine(Inv(
                $"| {t.At:HH:mm:ss} | {t.Process} | {t.Snapshot.WindowClass} | {t.Snapshot.FocusedClass} | {t.Snapshot.FocusedControlType} | {t.Snapshot.FocusedChildCount} | {t.Snapshot.FocusedAreaFraction:P0} | {t.Verdict.Kind} ({t.Verdict.Reason}) |"));
        }
    }

    private void AppendAc3(StringBuilder md)
    {
        var verdict = _overlayProbes == 0
            ? "NOT EXERCISED in this run - no captured window overlapped the overlay (use overlay-check)"
            : _overlayMaxFraction < MarkerPixelProbe.ExcludedBelow ? "PASS" : "FAIL";

        md.AppendLine();
        md.AppendLine("## AC3: overlay with WDA_EXCLUDEFROMCAPTURE is absent from captured frames");
        md.AppendLine();
        md.AppendLine($"**Verdict: {verdict}**");
        md.AppendLine();
        md.AppendLine(Inv($"Frames overlapping the overlay: {_overlayProbes}; highest marker-pixel share inside the overlay rectangle: {_overlayMaxFraction:P2} (excluded if < {MarkerPixelProbe.ExcludedBelow:P0})."));
    }

    private void AppendAc4(StringBuilder md)
    {
        var fourK = _frames.Where(f => Math.Max(f.NativeWidth, f.NativeHeight) >= 3840).ToList();
        var verdict = fourK.Count == 0
            ? "NOT EXERCISED in this run - no 4K-wide frame captured (use the legibility command)"
            : fourK.Max(f => f.JpegBytes) < JpegBudgetBytes ? "SIZE PASS (legibility: see legibility.md and the saved frames)" : "SIZE FAIL";

        md.AppendLine();
        md.AppendLine("## AC4: 4K frame -> <= 1600 px JPEG under 400 KB with legible 9 pt text");
        md.AppendLine();
        md.AppendLine($"**Verdict: {verdict}**");
        md.AppendLine();
        md.AppendLine("| Frames | Count | Native (max) | Output (max) | JPEG avg / max (KB) |");
        md.AppendLine("|---|---|---|---|---|");
        AppendFrameRow(md, "all", _frames);
        AppendFrameRow(md, "native long edge >= 3840", fourK);
    }

    private static void AppendFrameRow(StringBuilder md, string label, List<FrameRecord> frames)
    {
        if (frames.Count == 0)
        {
            md.AppendLine($"| {label} | 0 | - | - | - |");
            return;
        }

        var widest = frames.MaxBy(f => f.NativeWidth * f.NativeHeight)!;
        md.AppendLine(Inv(
            $"| {label} | {frames.Count} | {widest.NativeWidth}x{widest.NativeHeight} | {widest.OutWidth}x{widest.OutHeight} | {frames.Average(f => f.JpegBytes) / 1024:0} / {frames.Max(f => f.JpegBytes) / 1024.0:0} |"));
    }

    private string ErrorsText() =>
        _uiaErrors.Count == 0 ? "none" : string.Join(", ", _uiaErrors.Select(e => Inv($"{e.Key} x{e.Value}")));

    private static string Stat(LatencyStats stats, double percentile)
    {
        var summary = stats.Summarize(double.MaxValue);
        if (summary.Count == 0)
        {
            return "-";
        }

        var value = percentile switch
        {
            50 => summary.P50,
            95 => summary.P95,
            99 => summary.P99,
            _ => throw new ArgumentOutOfRangeException(nameof(percentile)),
        };
        return value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string Max(LatencyStats stats)
    {
        var summary = stats.Summarize(double.MaxValue);
        return summary.Count == 0 ? "-" : summary.Max.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string On(bool on) => on ? "on" : "off";

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
