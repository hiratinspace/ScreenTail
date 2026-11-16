<#
.SYNOPSIS
    Prints how the shipped remote tools' and browsers' windows are drawn, so the owner can check that
    ScreenTail's see-through rule will not skip their frames.

.DESCRIPTION
    ScreenTail skips a frame when the window in front is a layered window drawn below full opacity
    (WS_EX_LAYERED with LWA_ALPHA under 255), because what lies behind it would be in the frame. A colour
    key (LWA_COLORKEY) is skipped too, except for a remote tool in the shipped registry, which may use one
    for shaped corners. Per-pixel alpha (UpdateLayeredWindow, where GetLayeredWindowAttributes fails) is
    allowed. If a remote tool's main window turns out to be drawn below full opacity, its frames are
    being skipped and the capture service log says so once per window. A colour-keyed browser or admin
    tool is skipped under every scope; the probe marks those lines too.

    Open the tools first (a ScreenConnect session, Remote Desktop, AnyDesk, TeamViewer, Splashtop, Chrome,
    Edge), then run:

        powershell -ExecutionPolicy Bypass -File scripts\windows\probe-layered-windows.ps1

    Read-only. Prints one line per visible top-level window of those processes: process, window class,
    size, and the layered flags. Window titles are never printed: they can name customers and machines,
    and this output may be pasted into an issue.

    A line reading "layered alpha=NNN" with NNN under 255 is a window ScreenTail will not photograph.
#>

[CmdletBinding()]
param(
    # Process names to look at, without .exe. The default is the shipped registry's remote tools plus the
    # common browsers.
    [string[]] $Processes = @(
        'ScreenConnect.WindowsClient', 'Connectwisecontrol.client',
        'mstsc', 'msrdc', 'RdClient',
        'SRManager', 'strwinclt', 'SplashtopBusiness',
        'TeamViewer', 'tv_w32', 'tv_x64',
        'AnyDesk',
        'chrome', 'msedge', 'firefox', 'brave'
    )
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class LayeredProbe
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    public static List<IntPtr> Visible()
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) { found.Add(h); } return true; }, IntPtr.Zero);
        return found;
    }
}
'@

$WS_EX_LAYERED = 0x00080000
$LWA_COLORKEY = 0x1
$LWA_ALPHA = 0x2

$wanted = @{}
foreach ($name in $Processes) { $wanted[$name.ToLowerInvariant()] = $true }

$rows = 0
foreach ($window in [LayeredProbe]::Visible()) {
    $processId = [uint32]0
    [void][LayeredProbe]::GetWindowThreadProcessId($window, [ref]$processId)
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process -or -not $wanted.ContainsKey($process.ProcessName.ToLowerInvariant())) { continue }

    $class = New-Object System.Text.StringBuilder 256
    [void][LayeredProbe]::GetClassName($window, $class, $class.Capacity)
    $rect = New-Object LayeredProbe+Rect
    [void][LayeredProbe]::GetWindowRect($window, [ref]$rect)
    $size = '{0}x{1}' -f ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)

    $exStyle = [LayeredProbe]::GetWindowLongPtr($window, -20).ToInt64()
    if (($exStyle -band $WS_EX_LAYERED) -eq 0) {
        $layering = 'not layered'
    } else {
        $key = [uint32]0; $alpha = [byte]0; $flags = [uint32]0
        if ([LayeredProbe]::GetLayeredWindowAttributes($window, [ref]$key, [ref]$alpha, [ref]$flags)) {
            $parts = @('layered')
            if ($flags -band $LWA_ALPHA) { $parts += "alpha=$alpha" }
            if ($flags -band $LWA_COLORKEY) { $parts += 'colour-key' }
            if (($flags -band ($LWA_ALPHA -bor $LWA_COLORKEY)) -eq 0) { $parts += 'no attributes set' }
            $layering = $parts -join ' '
        } else {
            $layering = 'layered per-pixel alpha (attributes unreadable)'
        }
    }

    $browsers = @('chrome', 'msedge', 'firefox', 'brave')
    $isBrowser = $browsers -contains $process.ProcessName.ToLowerInvariant()
    $skipped = ''
    if ($layering -match 'alpha=(\d+)' -and [int]$Matches[1] -lt 255) {
        $skipped = '  <-- frames skipped (translucent)'
    } elseif ($layering -match 'colour-key') {
        # Matched here by process name; the service also matches remote tools by window class.
        $skipped = if ($isBrowser) { '  <-- frames skipped (colour key, not a remote tool)' } else { '  (colour key, allowed for a remote tool)' }
    }
    Write-Output ('{0,-28} class={1,-36} {2,-11} {3}{4}' -f $process.ProcessName, $class.ToString(), $size, $layering, $skipped)
    $rows++
}

if ($rows -eq 0) {
    Write-Output 'No visible window of the listed processes. Open the remote tools and browsers first.'
}
