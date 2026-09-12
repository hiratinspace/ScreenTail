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
        var hwnd = Native.FindWindow(null, OverlayTitle);
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("Overlay window not found. Start Spike.Overlay first.");
            return 2;
        }

        var overlay = ScreenGrab.WindowBounds(hwnd);
        Native.GetWindowThreadProcessId(hwnd, out var pid);

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

        // The pill is 360x44 logical, so at most about 720x88 at 200% scaling. Anything much larger means
        // this measured the wrong rectangle, and the verdict says nothing about the overlay.
        var plausible = overlay.Width is > 0 and <= 800 && overlay.Height is > 0 and <= 140;
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"Overlay window 0x{hwnd.ToInt64():X} (pid {pid}) bounds {overlay}; marker pixels in that region {fraction:P2}; {verdict}");
        if (!plausible)
        {
            summary += Environment.NewLine
                + "WARNING: that rectangle is not the size of the HUD pill (about 360x44 logical), so this run measured the wrong area.";
        }
        File.WriteAllText(Path.Combine(outDir, $"overlay-check-{label}.txt"), summary + Environment.NewLine);

        Console.WriteLine(summary);
        Console.WriteLine($"Saved {pngPath}");
        return 0;
    }
}
