using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
// Both namespaces define OcrWord: ours carries a box we control, theirs is the engine's own shape.
using FoundWord = ScreenTail.Core.Privacy.OcrWord;

namespace ScreenTail.Service.Privacy;

/// <summary>
/// Reads the text in a frame with the OCR engine built into Windows (ST-041).
///
/// ADR-0001 measured Tesseract at 1.19 s per frame against a 700 ms budget and listed three ways out:
/// smaller regions, more threads, or this. This one is chosen because it wins on every axis that matters
/// here — it is hardware-accelerated, it ships no native binaries and no language data of its own, and it
/// runs on ARM, where Tesseract's x64-only binaries would have forced the capture process into emulation
/// (finding 7). Shipping fewer third-party binaries is also less attack surface in a process that reads
/// everything on a technician's screen.
///
/// One thing it does not give us: a confidence score. Tesseract reports per-word confidence and the
/// worker uses it to throw away frames it could not read; this engine reports none, so that gate is inert
/// here and says so rather than pretending to a number it does not have.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsOcrRecogniser : IFrameTextRecogniser
{
    /// <summary>What this engine reports when it cannot say. Documented rather than invented.</summary>
    public const double UnknownConfidence = 1.0;

    private readonly OcrEngine? _engine;
    private readonly double _scale;

    /// <param name="language">The language to read, or null for the user's own.</param>
    /// <param name="scale">
    /// How much larger the engine is given the frame than it was captured, with every box brought back
    /// to the frame's own pixels. One reads it as it is.
    /// </param>
    public WindowsOcrRecogniser(string? language = null, double scale = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1.0);
        _scale = scale;
        // The user's own languages first: a technician working in German reads German dialogs, and an
        // engine that only knows English would quietly miss the words a secret sits beside.
        _engine = language is null
            ? OcrEngine.TryCreateFromUserProfileLanguages()
            : OcrEngine.TryCreateFromLanguage(new Language(language));

        _engine ??= OcrEngine.TryCreateFromLanguage(new Language("en-US"));
    }

    /// <summary>False when Windows has no OCR language pack installed, which is rare but possible.</summary>
    public bool Available => _engine is not null;

    public string? Language => _engine?.RecognizerLanguage.LanguageTag;

    /// <summary>The largest width or height, in pixels, the engine reads.</summary>
    public static uint MaxImageDimension => OcrEngine.MaxImageDimension;

    public async Task<RecognisedText> ReadAsync(ReadOnlyMemory<byte> image, CancellationToken ct = default)
    {
        if (_engine is null)
        {
            // No engine means no reading, and the worker treats that as a frame it cannot check.
            throw new InvalidOperationException("Windows has no OCR language pack installed.");
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(image.ToArray().AsBuffer()).AsTask(ct).ConfigureAwait(false);
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);

        // Never larger than the engine takes: a frame it refuses is a frame it did not read.
        var scale = Math.Min(_scale, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        scale = Math.Max(1.0, scale);
        using var bitmap = scale == 1.0
            ? await decoder.GetSoftwareBitmapAsync().AsTask(ct).ConfigureAwait(false)
            : await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform
                {
                    ScaledWidth = (uint)Math.Round(decoder.PixelWidth * scale),
                    ScaledHeight = (uint)Math.Round(decoder.PixelHeight * scale),
                    InterpolationMode = BitmapInterpolationMode.Cubic,
                },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);

        var result = await _engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        var words = new List<FoundWord>();
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                var box = word.BoundingRect;
                if (scale == 1.0)
                {
                    words.Add(new FoundWord(
                        word.Text,
                        (int)Math.Round(box.X),
                        (int)Math.Round(box.Y),
                        (int)Math.Round(box.Width),
                        (int)Math.Round(box.Height)));
                    continue;
                }

                // Outward to whole pixels of the frame, so a box brought back from a larger reading
                // covers no less than the engine said.
                var left = (int)Math.Floor(box.X / scale);
                var top = (int)Math.Floor(box.Y / scale);
                words.Add(new FoundWord(
                    word.Text,
                    left,
                    top,
                    (int)Math.Ceiling((box.X + box.Width) / scale) - left,
                    (int)Math.Ceiling((box.Y + box.Height) / scale) - top));
            }
        }

        return new RecognisedText(words, UnknownConfidence);
    }
}
