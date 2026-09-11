using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using ScreenTail.Spike.Core;

namespace ScreenTail.Spike.Capture;

/// <summary>AC3: one full-screen capture, then count the overlay's marker pixels where the overlay sits.</summary>
internal static class OverlayCheck
{
    public const string OverlayTitle = "ScreenTail.Spike.Overlay";
    public const byte MarkerR = 0xFF;
    public const byte MarkerG = 0x00;
    public const byte MarkerB = 0xFF;

    public static Rectangle? FindOverlayBounds()
    {
        var hwnd = Native.FindWindow(null, OverlayTitle);
        return hwnd == IntPtr.Zero ? null : ScreenGrab.WindowBounds(hwnd);
    }

    public static int Execute(string outDir, string label)
    {
        if (FindOverlayBounds() is not { } overlay)
        {
            Console.Error.WriteLine("Overlay window not found. Start Spike.Overlay first.");
            return 2;
        }

        var screen = ScreenGrab.VirtualScreen();
        using var capture = ScreenGrab.Capture(screen);
        var pngPath = Path.Combine(outDir, $"overlay-check-{label}.png");
        capture.Save(pngPath, ImageFormat.Png);

        var region = overlay;
        region.Offset(-screen.X, -screen.Y);
        var fraction = MarkerPixelProbe.MatchFraction(ScreenGrab.ReadBgra(capture, region), MarkerR, MarkerG, MarkerB);
        var verdict = fraction < MarkerPixelProbe.ExcludedBelow
            ? "EXCLUDED - overlay absent from capture"
            : "CAPTURED - overlay visible in capture";

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"Overlay bounds {overlay}; marker pixels in that region {fraction:P2}; {verdict}");
        File.WriteAllText(Path.Combine(outDir, $"overlay-check-{label}.txt"), summary + Environment.NewLine);

        Console.WriteLine(summary);
        Console.WriteLine($"Saved {pngPath}");
        return 0;
    }
}
