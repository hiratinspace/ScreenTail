using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>
/// Applying a blur, in the order that makes it safe (ST-075).
///
/// In Core rather than in the view model because the ordering <em>is</em> the guarantee, and a rule that
/// can only be checked on Windows is a rule that is checked when someone remembers. The flattening itself
/// is injected: it needs WIC, and a test has no business encoding a PNG to find out whether the steps
/// happen in the right sequence.
/// </summary>
public sealed class FrameBlur(IReviewFrames frames, Func<byte[], MaskedRegion, byte[]> flatten)
{
    private readonly IReviewFrames _frames = frames ?? throw new ArgumentNullException(nameof(frames));
    private readonly Func<byte[], MaskedRegion, byte[]> _flatten = flatten ?? throw new ArgumentNullException(nameof(flatten));

    /// <summary>
    /// Flattens the rectangle and writes it, returning the region recorded — or null when there was
    /// nothing to do.
    ///
    /// The store is written before this returns, so the caller cannot show a covered password over an
    /// uncovered one on disk. The same <see cref="MaskedRegion"/> instance is both flattened and recorded:
    /// if those two could differ, <c>masked_regions</c> would be a false statement about a picture nobody
    /// can check any more, because the original is gone.
    /// </summary>
    /// <param name="image">The frame's current bytes. Null when the thumbnail never loaded.</param>
    public async Task<BlurredFrame?> ApplyAsync(
        Frame frame,
        byte[]? image,
        Rect drawn,
        Size displayed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // Nothing to flatten, and no way to check what was flattened. Writing anyway would replace a real
        // frame with a rectangle drawn over nothing — destroying it to hide something that may not have
        // been there.
        if (image is null || !BlurRegion.IsMeaningful(drawn))
        {
            return null;
        }

        var region = BlurRegion.From(drawn, displayed, new Size(frame.Width, frame.Height));
        var flattened = _flatten(image, region);
        await _frames.BlurAsync(frame.Id, flattened, region, ct).ConfigureAwait(false);

        return new BlurredFrame(
            frame with { MaskedRegions = [.. frame.MaskedRegions, region] },
            flattened,
            region);
    }
}

/// <param name="Frame">The frame as it now is, with the region appended to what redaction already found.</param>
public sealed record BlurredFrame(Frame Frame, byte[] Image, MaskedRegion Region);
