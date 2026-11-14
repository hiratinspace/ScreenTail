using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>
/// Applying a blur, in the order that makes it safe (ST-075).
///
/// In Core rather than in the view model because the ordering <em>is</em> the guarantee, and a rule that
/// can only be checked on Windows is a rule that is checked when someone remembers. The painting is the
/// frames implementation's: in the running application that is the service, over the pipe, with the
/// same masker redaction uses; the pane used to flatten in WPF and hand the bytes back, which re-encoded
/// a JPEG as PNG on the UI thread and made the stored name a lie (weaknesses P2-9).
/// </summary>
public sealed class FrameBlur(IReviewFrames frames)
{
    private readonly IReviewFrames _frames = frames ?? throw new ArgumentNullException(nameof(frames));

    /// <summary>
    /// Flattens the rectangle and writes it, returning the region recorded, or null when there was
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
        // frame with a rectangle drawn over nothing, destroying it to hide something that may not have
        // been there.
        if (image is null || !BlurRegion.IsMeaningful(drawn))
        {
            return null;
        }

        var region = BlurRegion.From(drawn, displayed, new Size(frame.Width, frame.Height));
        var reply = await _frames.BlurWithReplyAsync(frame.Id, region, ct).ConfigureAwait(false);
        if (reply.Unconfirmed)
        {
            // Neither "nothing happened" nor a blur: the caller must stop showing what it holds and ask again.
            throw new BlurNotConfirmedException();
        }

        if (!reply.Written)
        {
            // Gone between the load and the blur, refused, or no service. Nothing was painted and nothing
            // is claimed.
            return null;
        }

        // The text goes with the pixels, as it does in the store: it was read from what was just destroyed.
        // Written without an image to show is still written: the record says so, and the caller must stop
        // showing the picture it has, which is the one from before.
        return new BlurredFrame(
            frame with { MaskedRegions = [.. frame.MaskedRegions, region], OcrText = null },
            reply.Image,
            region);
    }
}

/// <param name="Frame">The frame as it now is: the region appended to what redaction already found, and no OCR text.</param>
/// <param name="Image">The image as it now is, or null when it was written but could not be sent back.</param>
public sealed record BlurredFrame(Frame Frame, byte[]? Image, MaskedRegion Region);

/// <summary>What a blur came to, as the pane shows it.</summary>
public enum BlurOutcome
{
    /// <summary>Not written: the frame is gone, the service refused or is not there. Nothing changed.</summary>
    NotBlurred,

    /// <summary>Written, and the new image is on screen.</summary>
    Blurred,

    /// <summary>Written, but the new image could not be sent back, so the old one is no longer shown.</summary>
    BlurredNotShown,

    /// <summary>The service went away after it was asked; the blur may or may not be on disk.</summary>
    Unknown,
}

/// <summary>
/// A blur was asked for and the service went away before saying whether it was written (ST-084 follow-up
/// review of #257). Thrown rather than returned as "not blurred", which would be a false statement about a
/// frame that may now be covered, and the pane would go on showing the picture from before.
/// </summary>
public sealed class BlurNotConfirmedException : Exception
{
    public BlurNotConfirmedException()
        : base("The capture service went away before saying whether the blur was written.")
    {
    }

    public BlurNotConfirmedException(string message)
        : base(message)
    {
    }

    public BlurNotConfirmedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
