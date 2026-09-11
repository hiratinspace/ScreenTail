using System.Diagnostics;
using System.Threading.Channels;
using ScreenTail.Spike.Core;
using Tesseract;

namespace ScreenTail.Spike.Capture;

/// <summary>Single-threaded Tesseract worker. Recognized text is counted, never stored.</summary>
internal sealed class OcrWorker : IDisposable
{
    private readonly TesseractEngine _engine;
    private readonly RunReport _report;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

    private OcrWorker(TesseractEngine engine, RunReport report)
    {
        _engine = engine;
        _report = report;
    }

    public static async Task<OcrWorker?> CreateAsync(RunReport report)
    {
        var engine = await TryCreateEngineAsync(reason => report.OcrUnavailable = reason);
        return engine is null ? null : new OcrWorker(engine, report);
    }

    public static async Task<TesseractEngine?> TryCreateEngineAsync(Action<string> onUnavailable)
    {
        try
        {
            return new TesseractEngine(await Assets.EnsureTessdataAsync(), "eng", EngineMode.Default);
        }
        catch (Exception ex)
        {
            // Usually a missing VC++ redistributable or no network on first run.
            var reason = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"OCR disabled — {reason}");
            onUnavailable(reason);
            return null;
        }
    }

    public static (string Text, float Confidence) Recognize(TesseractEngine engine, byte[] image)
    {
        using var pix = Pix.LoadFromMemory(image);
        using var page = engine.Process(pix);
        return (page.GetText(), page.GetMeanConfidence());
    }

    public void TrySubmit(byte[] image)
    {
        if (!_queue.Writer.TryWrite(image))
        {
            _report.OcrSkippedBusy++;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var image in _queue.Reader.ReadAllAsync(ct))
            {
                var started = Stopwatch.GetTimestamp();
                var (text, confidence) = Recognize(_engine, image);
                _report.RecordOcr(Stopwatch.GetElapsedTime(started).TotalMilliseconds, confidence, OcrAgreement.Tokenize(text).Count);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _engine.Dispose();
}
