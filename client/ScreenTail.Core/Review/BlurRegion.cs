using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>
/// Turning a rectangle the technician dragged over a displayed image into one in the frame's own pixels
/// (ST-075, Spec §5 S3).
///
/// Separate from the drawing because getting it wrong is silent and unrecoverable: the region is what the
/// blur is applied to and what <c>masked_regions</c> then claims was hidden. A rectangle scaled from the
/// wrong axis blurs the wrong part of the picture, writes a record saying the right part was covered, and
/// destroys the original in the same breath.
/// </summary>
public static class BlurRegion
{
    /// <param name="drawn">The rectangle in the coordinates of the image as displayed.</param>
    /// <param name="displayed">The size the image was drawn at.</param>
    /// <param name="frame">The frame's own width and height.</param>
    public static MaskedRegion From(Rect drawn, Size displayed, Size frame)
    {
        if (displayed.Width <= 0 || displayed.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displayed), displayed, "The image was not drawn.");
        }

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), frame, "The frame has no size.");
        }

        // Both axes, from their own dimension. Uniform scaling makes one factor look sufficient, and it is
        // — right up until the pane is resized to a different aspect and every blur lands slightly wrong,
        // in a way nobody notices because the picture underneath is already gone.
        var scaleX = frame.Width / displayed.Width;
        var scaleY = frame.Height / displayed.Height;

        var left = Math.Clamp(drawn.Left * scaleX, 0, frame.Width);
        var top = Math.Clamp(drawn.Top * scaleY, 0, frame.Height);
        var right = Math.Clamp(drawn.Right * scaleX, 0, frame.Width);
        var bottom = Math.Clamp(drawn.Bottom * scaleY, 0, frame.Height);

        // Rounded outward. A blur that covers one pixel less than asked for is a blur that leaves the top
        // row of a password on the screen, and the schema's minimum is 1 either way.
        var x = (long)Math.Floor(left);
        var y = (long)Math.Floor(top);
        return new MaskedRegion
        {
            X = x,
            Y = y,
            Width = Math.Max(1, (long)Math.Ceiling(right) - x),
            Height = Math.Max(1, (long)Math.Ceiling(bottom) - y),
            Kind = MaskKind.UserBlur,
        };
    }

    /// <summary>Whether the drag was big enough to mean anything, or just a click that wobbled.</summary>
    public static bool IsMeaningful(Rect drawn) => drawn.Width >= 4 && drawn.Height >= 4;
}

/// <summary>A rectangle, without taking a dependency on WPF so the arithmetic stays testable (ADR-0002).</summary>
public readonly record struct Rect(double Left, double Top, double Width, double Height)
{
    /// <summary>From two corners in any order, which is what a drag gives you.</summary>
    public static Rect Between(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

public readonly record struct Size(double Width, double Height);
