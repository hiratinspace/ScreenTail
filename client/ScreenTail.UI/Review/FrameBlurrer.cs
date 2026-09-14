using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTail.Shared.Schema;

namespace ScreenTail.UI.Review;

/// <summary>
/// Replaces a rectangle of a PNG with flat colour (ST-075).
///
/// Flat, not blurred, despite the button saying Blur. A Gaussian of a readable password is still a
/// picture of that password: the strength that defeats a human eye does not defeat an upscaler, and this
/// is the one operation in the product whose entire purpose is that the pixels underneath stop existing.
/// The word on the button is the technician's word for it; what happens is redaction, the same thing
/// <c>MaskKind</c> has always meant.
///
/// Windows-only because it decodes and re-encodes through WIC. The arithmetic that decides *which*
/// rectangle lives in <see cref="Core.Review.BlurRegion"/>, where it is tested without a machine.
/// </summary>
internal static class FrameBlurrer
{
    public static byte[] Apply(ReadOnlySpan<byte> png, MaskedRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);

        var source = Decode(png);
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawImage(source, new System.Windows.Rect(0, 0, source.PixelWidth, source.PixelHeight));
            draw.DrawRectangle(
                Brushes.Black,
                pen: null,
                new System.Windows.Rect(region.X, region.Y, region.Width, region.Height));
        }

        // Rendered at the frame's own pixel size, not the screen's. A frame captured on a 4K monitor and
        // reviewed on a laptop would otherwise come back the size of the laptop's pixels, and the stored
        // width and height would start lying about the image they describe.
        var target = new RenderTargetBitmap(
            source.PixelWidth,
            source.PixelHeight,
            source.DpiX,
            source.DpiY,
            PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapImage Decode(ReadOnlySpan<byte> png)
    {
        using var stream = new MemoryStream(png.ToArray());

        // OnLoad, so the stream can be disposed here rather than being held for the life of the bitmap —
        // and so nothing keeps a decoder pointed at the bytes we are about to stop keeping.
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.StreamSource = stream;
        decoded.EndInit();
        decoded.Freeze();
        return decoded;
    }
}
