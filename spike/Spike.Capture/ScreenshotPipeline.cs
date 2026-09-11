using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScreenTail.Spike.Core;

namespace ScreenTail.Spike.Capture;

/// <summary>Click → debounce 400 ms → capture foreground window → overlay probe → downscale → JPEG → OCR.</summary>
internal sealed class ScreenshotPipeline
{
    private const uint DebounceMs = 400;
    private const int FramesToKeep = 25;
    private const long JpegQuality = 80;

    private readonly string _framesDir;
    private readonly OcrWorker? _ocr;
    private readonly RunReport _report;
    private readonly Channel<uint> _requests = Channel.CreateBounded<uint>(
        new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

    private bool _anyAccepted;
    private uint _lastAcceptedTick;
    private int _saved;

    public ScreenshotPipeline(string framesDir, OcrWorker? ocr, RunReport report)
    {
        _framesDir = framesDir;
        _ocr = ocr;
        _report = report;
        Directory.CreateDirectory(framesDir);
    }

    /// <summary>Called from the input pump only.</summary>
    public void OnClick(uint eventTick)
    {
        if (_anyAccepted && unchecked(eventTick - _lastAcceptedTick) < DebounceMs)
        {
            _report.Debounced++;
            return;
        }

        _anyAccepted = true;
        _lastAcceptedTick = eventTick;
        if (!_requests.Writer.TryWrite(eventTick))
        {
            _report.ScreenshotsDroppedBusy++;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var _ in _requests.Reader.ReadAllAsync(ct))
            {
                Shoot();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Shoot()
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var bounds = ScreenGrab.WindowBounds(Native.GetForegroundWindow());
            if (bounds.Width < 1 || bounds.Height < 1)
            {
                return;
            }

            using var native = ScreenGrab.Capture(bounds);
            var captureMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            ProbeOverlay(native, bounds);

            var encodeStarted = Stopwatch.GetTimestamp();
            using var small = ScreenGrab.Downscale(native);
            var jpeg = ScreenGrab.EncodeJpeg(small, JpegQuality);
            var encodeMs = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;

            _report.AddFrame(new FrameRecord(native.Width, native.Height, small.Width, small.Height, jpeg.Length, captureMs, encodeMs));
            if (_saved < FramesToKeep)
            {
                File.WriteAllBytes(Path.Combine(_framesDir, $"frame-{_saved++:D2}.jpg"), jpeg);
            }

            _ocr?.TrySubmit(jpeg);
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException)
        {
            // Secure desktop (UAC, lock screen) or a window that vanished mid-capture.
            _report.ScreenshotErrors++;
        }
    }

    private void ProbeOverlay(Bitmap native, Rectangle bounds)
    {
        if (OverlayCheck.FindOverlayBounds() is not { } overlay)
        {
            return;
        }

        overlay.Intersect(bounds);
        if (overlay.Width * overlay.Height < 100)
        {
            return;
        }

        overlay.Offset(-bounds.X, -bounds.Y);
        var fraction = MarkerPixelProbe.MatchFraction(
            ScreenGrab.ReadBgra(native, overlay),
            OverlayCheck.MarkerR,
            OverlayCheck.MarkerG,
            OverlayCheck.MarkerB);
        _report.RecordOverlayProbe(fraction);
    }
}
