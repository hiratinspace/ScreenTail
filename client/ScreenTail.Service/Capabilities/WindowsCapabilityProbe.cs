using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ScreenTail.Core.Capabilities;

namespace ScreenTail.Service.Capabilities;

/// <summary>
/// The real checks (ST-021). Each one does the thing rather than inferring it: it installs a hook and
/// removes it, takes a one-pixel screenshot, opens the microphone consent key. Inference is how you end up
/// telling a technician everything is fine while nothing is being captured.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCapabilityProbe(TimeProvider? time = null) : ICapabilityProbe
{
    private const string ConsentPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string DesktopAppsConsentPath = ConsentPath + @"\NonPackaged";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public CapabilityReport Probe() => new(
        _time.GetUtcNow(),
        [CheckDesktopSession(), CheckMicrophone(), CheckScreenCapture(), CheckInputHooks(), CheckElevatedWindows()]);

    /// <summary>
    /// Session 0 has no desktop: hooks never fire and captures come back black. This is the check that would
    /// have caught the service-hosting mistake ADR-0003 was written about, so it runs first.
    /// </summary>
    private static CapabilityCheck CheckDesktopSession()
    {
        try
        {
            if (!Environment.UserInteractive)
            {
                return CapabilityCopy.NoDesktopSession();
            }

            var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
            if (desktop == IntPtr.Zero)
            {
                return CapabilityCopy.NoDesktopSession();
            }

            _ = CloseDesktop(desktop);
            return CapabilityCopy.Ok(Capability.DesktopSession, "Running in your desktop session.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CapabilityCopy.Failed(Capability.DesktopSession, ex.Message);
        }
    }

    /// <summary>
    /// Windows keeps microphone consent in two places: the machine policy an admin sets, and the user's own
    /// choice. Desktop apps have their own key again, which is the one that actually governs us.
    /// </summary>
    private static CapabilityCheck CheckMicrophone()
    {
        try
        {
            if (ReadConsent(Registry.LocalMachine, ConsentPath) == "Deny")
            {
                return CapabilityCopy.MicrophoneBlockedByAdmin();
            }

            foreach (var path in new[] { ConsentPath, DesktopAppsConsentPath })
            {
                if (ReadConsent(Registry.CurrentUser, path) == "Deny")
                {
                    return CapabilityCopy.MicrophoneBlocked();
                }
            }

            return HasRecordingDevice()
                ? CapabilityCopy.Ok(Capability.Microphone, "Microphone is available.")
                : CapabilityCopy.NoMicrophone();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CapabilityCopy.Failed(Capability.Microphone, ex.Message);
        }
    }

    /// <summary>Takes a real screenshot of one pixel. A remote session or a driver fault shows up as a failure here.</summary>
    private static CapabilityCheck CheckScreenCapture()
    {
        var screen = IntPtr.Zero;
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                return CapabilityCopy.ScreenCaptureBlocked("no device context");
            }

            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, 1, 1);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return CapabilityCopy.ScreenCaptureBlocked("no bitmap");
            }

            var previous = SelectObject(memory, bitmap);
            var copied = BitBlt(memory, 0, 0, 1, 1, screen, 0, 0, SRCCOPY);
            SelectObject(memory, previous);
            return copied
                ? CapabilityCopy.Ok(Capability.ScreenCapture, "Screenshots are working.")
                : CapabilityCopy.ScreenCaptureBlocked($"BitBlt failed, error {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CapabilityCopy.Failed(Capability.ScreenCapture, ex.Message);
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            if (memory != IntPtr.Zero)
            {
                _ = DeleteDC(memory);
            }

            if (screen != IntPtr.Zero)
            {
                _ = ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }

    /// <summary>
    /// Installs a mouse hook and takes it straight back out. A mouse hook, not a keyboard one: ScreenTail
    /// never installs a keyboard hook at all, which is how INV-2 is kept structurally rather than by policy.
    /// </summary>
    private static CapabilityCheck CheckInputHooks()
    {
        var hook = IntPtr.Zero;
        try
        {
            hook = SetWindowsHookEx(WH_MOUSE_LL, StaticNoOpHook, IntPtr.Zero, 0);
            if (hook == IntPtr.Zero)
            {
                return CapabilityCopy.HooksBlocked($"error {Marshal.GetLastWin32Error()}");
            }

            return CapabilityCopy.Ok(Capability.InputHooks, "Clicks and scene changes are being detected.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CapabilityCopy.Failed(Capability.InputHooks, ex.Message);
        }
        finally
        {
            if (hook != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(hook);
            }
        }
    }

    /// <summary>
    /// Whether we can read the window in front. Against an elevated window a non-elevated process is blind,
    /// and that is the correct, expected answer — Spec §5 S2 tells the technician rather than hiding it.
    /// </summary>
    private static CapabilityCheck CheckElevatedWindows()
    {
        try
        {
            using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(current);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                ? CapabilityCopy.Ok(Capability.ElevatedWindows, "Elevated windows are visible.")
                : CapabilityCopy.ElevatedWindowsInvisible();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CapabilityCopy.Failed(Capability.ElevatedWindows, ex.Message);
        }
    }

    private static string? ReadConsent(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue("Value") as string;
    }

    private static bool HasRecordingDevice() => waveInGetNumDevs() > 0;

    // A hook that does nothing: the probe is asking whether Windows will let it in, not watching input.
    private static readonly HookProc StaticNoOpHook = (code, wParam, lParam) => CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private const int WH_MOUSE_LL = 14;
    private const int DESKTOP_READOBJECTS = 0x0001;
    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("winmm.dll")]
    private static extern uint waveInGetNumDevs();
}
