using System.Globalization;

namespace ScreenTail.Core.Review;

/// <summary>Which way an arrow key points.</summary>
public enum BlurBoxDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// The box a keyboard places over an enlarged screenshot to blur a region of it (ST-084 follow-up; Spec
/// §5 S3 draws the blur as a drag, which needs a mouse).
///
/// Held in whole percent of the picture, not in pixels as drawn: an arrow then moves it by the same share
/// of the screenshot whatever size the pane is, a resized pane leaves it over the same part of the
/// picture, and what a screen reader hears is exactly where it is. It never leaves the picture and never
/// shrinks below <see cref="MinimumSide"/>, so every box the technician can see is one that blurs.
///
/// Turned into a rectangle in the picture's drawn coordinates by <see cref="Drawn"/>, which is what a drag
/// produces too: from there both go through <see cref="FrameBlur"/> and <see cref="BlurRegion.From"/>, so the
/// keyboard and the mouse cannot disagree about scaling, rounding or the margin the service adds.
/// </summary>
public readonly record struct BlurBox
{
    /// <summary>An arrow: one percent of the picture.</summary>
    public const int Step = 1;

    /// <summary>Ctrl with an arrow: ten percent.</summary>
    public const int LargeStep = 10;

    /// <summary>The smallest side, in percent.</summary>
    public const int MinimumSide = 2;

    /// <summary>The side the box starts at, in percent: big enough to see at once, small enough to place.</summary>
    public const int DefaultSide = 20;

    private BlurBox(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>The left edge, in percent of the picture's width from its left.</summary>
    public int X { get; }

    /// <summary>The top edge, in percent of the picture's height from its top.</summary>
    public int Y { get; }

    /// <summary>In percent of the picture's width.</summary>
    public int Width { get; }

    /// <summary>In percent of the picture's height.</summary>
    public int Height { get; }

    /// <summary>Where <c>B</c> puts it: <see cref="DefaultSide"/> square, in the middle of the picture.</summary>
    public static BlurBox Centred { get; } = Of((100 - DefaultSide) / 2, (100 - DefaultSide) / 2, DefaultSide, DefaultSide);

    /// <summary>
    /// Any four numbers made into a box inside the picture: each side between <see cref="MinimumSide"/> and
    /// the whole picture, then the position moved as little as keeps it inside. In longs, so no value a
    /// caller passes overflows on the way.
    /// </summary>
    public static BlurBox Of(long x, long y, long width, long height)
    {
        var w = (int)Math.Clamp(width, MinimumSide, 100);
        var h = (int)Math.Clamp(height, MinimumSide, 100);
        return new BlurBox((int)Math.Clamp(x, 0, 100 - w), (int)Math.Clamp(y, 0, 100 - h), w, h);
    }

    /// <summary>Moved by whole percent, stopping at the picture's edges.</summary>
    public BlurBox Move(int dx, int dy) => Of((long)X + dx, (long)Y + dy, Width, Height);

    /// <summary>
    /// Wider or narrower, taller or shorter, from the top left corner. At the right or bottom edge it grows
    /// the other way rather than not at all, so a box can be widened to cover a strip along the edge.
    /// </summary>
    public BlurBox Resize(int dw, int dh) => Of(X, Y, (long)Width + dw, (long)Height + dh);

    /// <summary>
    /// One arrow key: a move, or with <paramref name="resize"/> (Shift) a resize, by <see cref="Step"/> or
    /// with <paramref name="large"/> (Ctrl) by <see cref="LargeStep"/>. Right and Down widen and heighten;
    /// Left and Up narrow and shorten.
    /// </summary>
    public BlurBox Nudge(BlurBoxDirection direction, bool resize, bool large)
    {
        var step = large ? LargeStep : Step;
        var (dx, dy) = direction switch
        {
            BlurBoxDirection.Left => (-step, 0),
            BlurBoxDirection.Right => (step, 0),
            BlurBoxDirection.Up => (0, -step),
            BlurBoxDirection.Down => (0, step),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
        };
        return resize ? Resize(dx, dy) : Move(dx, dy);
    }

    /// <summary>
    /// The box as a rectangle over the picture drawn at <paramref name="displayed"/>: what the view draws
    /// and what goes to <see cref="FrameBlur"/>, the one rectangle for both.
    ///
    /// Never smaller than <see cref="BlurRegion.MinimumDrawn"/> where the picture is that big, keeping
    /// inside it. On a small pane the smallest box can come to less than that, and a box the technician
    /// placed and confirmed must not be dropped as a wobbled click.
    /// </summary>
    public Rect Drawn(Size displayed)
    {
        var (left, width) = Span(X, Width, displayed.Width);
        var (top, height) = Span(Y, Height, displayed.Height);
        return new Rect(left, top, width, height);
    }

    /// <summary>"Blur box: 40% from the left, 40% from the top, 20% wide, 20% tall". Numbers only (INV-10).</summary>
    public string Spoken => string.Create(
        CultureInfo.InvariantCulture,
        $"Blur box: {X}% from the left, {Y}% from the top, {Width}% wide, {Height}% tall");

    private static (double Start, double Length) Span(int at, int length, double drawn)
    {
        if (drawn <= 0)
        {
            return (0, 0);
        }

        var size = Math.Min(Math.Max(length * drawn / 100, BlurRegion.MinimumDrawn), drawn);
        var start = Math.Min(at * drawn / 100, drawn - size);
        return (start, size);
    }
}
