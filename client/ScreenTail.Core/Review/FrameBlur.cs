using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>
/// Applying a blur, in the order that makes it safe (ST-075).
///
/// In Core rather than in the view model because the ordering <em>is</em> the guarantee, and a rule that
/// can only be checked on Windows is a rule that is checked when someone remembers. The painting is the
/// frames implementation's: in the running application that is the service, over the pipe, with the
/// same masker redaction uses — the pane used to flatten in WPF and hand the bytes back, which re-encoded
/// a JPEG as PNG on the UI thread and made the stored name a lie (weaknesses P2-9).
/// </summary>
public sealed class FrameBlur(IReviewFrames frames)
{
    private readonly IReviewFrames _frames = frames ?? throw new ArgumentNullException(nameof(frames));

    /// <summary>
    /// Flattens the rectangle and writes it, returning the region recorded — or null when there was
    /// nothing to do.
    ///
    /// The store is written before this returns, so the caller cannot show a covered password over an
    /// uncovered one on disk. The one <see cref="MaskedRegion"/> computed here is what is painted and what
    /// is recorded: if those two could differ, <c>masked_regions</c> would be a false statement about a
    /// picture nobody can check any more, because the original is gone.
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
        var flattened = await _frames.BlurAsync(frame.Id, region, ct).ConfigureAwait(false);
        if (flattened is null)
        {
            // Gone between the load and the blur. Nothing was painted and nothing is claimed.
            return null;
        }

        return new BlurredFrame(
            frame with { MaskedRegions = [.. frame.MaskedRegions, region] },
            flattened,
            region);
    }
}

/// <param name="Frame">The frame as it now is, with the region appended to what redaction already found.</param>
public sealed record BlurredFrame(Frame Frame, byte[] Image, MaskedRegion Region);
