using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenTail.UI.Review;

/// <summary>
/// Decodes a frame for the strip at the size the strip draws it (weaknesses P2-3).
///
/// Bound as bytes, WPF decoded every screenshot at its native size to draw a 160×90 thumbnail: about
/// 5.8 MB of pixels per frame, 860 MB for a 150-frame session, and an out-of-memory before the
/// technician had scrolled. Decoding at a fixed width makes a thumbnail a thumbnail — a few hundred
/// kilobytes — and the full image is fetched only when a frame is enlarged, one at a time.
/// </summary>
internal static class Thumbnails
{
    /// <summary>Twice the strip's 160 px, so a 200% display gets real pixels rather than a blur.</summary>
    public const int PixelWidth = 320;

    public static ImageSource Decode(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var stream = new MemoryStream(image, writable: false);
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.DecodePixelWidth = PixelWidth;
        decoded.StreamSource = stream;
        decoded.EndInit();
        decoded.Freeze();
        return decoded;
    }
}
