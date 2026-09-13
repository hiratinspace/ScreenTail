using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenTail.Service.Capture;

/// <summary>
/// Tells Windows we understand per-monitor scaling (ST-025).
///
/// Without this, a process is "DPI unaware" and Windows quietly reports virtualised coordinates: a
/// 3840-wide window on a 150%-scaled monitor comes back as 2560, and the capture would be taken from the
/// wrong rectangle, at the wrong size, with the cursor drawn in the wrong place. Set before any window is
/// created or queried, because it cannot be changed afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Dpi
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public static bool MakePerMonitorAware()
    {
        try
        {
            return SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 8.1 and older. Nothing this project supports, but failing to start over it would be
            // the wrong trade: capture degrades, it doesn't break.
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
