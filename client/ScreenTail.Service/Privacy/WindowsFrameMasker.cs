using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;
using Encoder = System.Drawing.Imaging.Encoder;

namespace ScreenTail.Service.Privacy;

/// <summary>
/// Paints over the regions the pattern engine found, then shrinks the frame for storage (ST-041).
///
/// Order first, because it is the whole correctness argument: the regions were found in the native image's
/// coordinates, so they are painted there. Shrinking first would move every box by a rounding error and
/// leave a sliver of what was meant to be covered — a strip of a card number is still a card number.
///
/// Painted, not blurred. A blur is reversible enough to be an argument, and a technician looking at a
/// screenshot should be able to see at a glance that something was removed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFrameMasker(int jpegQuality = 82) : IFrameMasker
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge)
    {
        ArgumentNullException.ThrowIfNull(regions);

        using var source = new MemoryStream(image.ToArray(), writable: false);
        using var native = new Bitmap(source);

        Paint(native, regions);

        var plan = Downscale.For(native.Width, native.Height, maxEdge);
        if (!plan.Resamples)
        {
            return new MaskedImage(Encode(native), native.Width, native.Height);
        }

        using var stored = new Bitmap(plan.Width, plan.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(stored))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.DrawImage(native, 0, 0, plan.Width, plan.Height);
        }

        return new MaskedImage(Encode(stored), stored.Width, stored.Height);
    }

    private static void Paint(Bitmap target, IReadOnlyList<MaskedRegion> regions)
    {
        if (regions.Count == 0)
        {
            return;
        }

        using var graphics = Graphics.FromImage(target);
        using var brush = new SolidBrush(Color.Black);
        foreach (var region in regions)
        {
            // Clamped to the image: a region that ran off the edge would throw, and losing the frame over
            // an off-by-one would be a worse outcome than painting a slightly smaller rectangle.
            var x = Math.Clamp((int)region.X, 0, target.Width);
            var y = Math.Clamp((int)region.Y, 0, target.Height);
            var width = Math.Clamp((int)region.Width, 0, target.Width - x);
            var height = Math.Clamp((int)region.Height, 0, target.Height - y);
            if (width > 0 && height > 0)
            {
                graphics.FillRectangle(brush, x, y, width, height);
            }
        }
    }

    private byte[] Encode(Bitmap bitmap)
    {
        using var parameters = new EncoderParameters(1);
        using var quality = new EncoderParameter(Encoder.Quality, (long)jpegQuality);
        parameters.Param[0] = quality;

        using var buffer = new MemoryStream(128 * 1024);
        bitmap.Save(buffer, JpegCodec, parameters);
        return buffer.ToArray();
    }
}
