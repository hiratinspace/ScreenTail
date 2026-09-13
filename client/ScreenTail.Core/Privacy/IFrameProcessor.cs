using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

/// <param name="Words">Every word found, with where it sits in the image.</param>
/// <param name="MeanConfidence">0–1. Low confidence means the image was hard to read, which matters: a
/// secret the recogniser could not read is a secret the pattern engine cannot mask.</param>
public sealed record RecognisedText(IReadOnlyList<OcrWord> Words, double MeanConfidence)
{
    public static readonly RecognisedText Nothing = new([], 0);

    public bool IsEmpty => Words.Count == 0;
}

/// <summary>Reads the text in a frame (ST-041). The implementation is per-platform; the rules are not.</summary>
public interface IFrameTextRecogniser
{
    Task<RecognisedText> ReadAsync(ReadOnlyMemory<byte> image, CancellationToken ct = default);
}

/// <param name="Image">The image with every region painted over, and downscaled for storage.</param>
public sealed record MaskedImage(byte[] Image, int Width, int Height);

/// <summary>
/// Paints the masked regions over a frame and shrinks it for storage (ST-041).
///
/// The order is the point: masking happens at native resolution, where the regions were found, and the
/// downscale happens afterwards. Shrinking first would move every box a little and leave slivers of what
/// was meant to be covered.
/// </summary>
public interface IFrameMasker
{
    MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge);
}
