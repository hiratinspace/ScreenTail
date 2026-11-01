<#
.SYNOPSIS
    Asks every running copy of an installed ScreenTail program to close, the way Windows asks, and says
    whether it closed by itself (ST-112). For the installer smoke test.

.DESCRIPTION
    -Mode RestartManager: what Windows Installer does in InstallValidate. A Restart Manager session with
    the executable registered, the type Restart Manager gives each process holding it, then a shutdown
    without force, which only asks.
    -Mode CloseMessage: WM_CLOSE posted to the service's session-end window, as taskkill without /f does.

    Then it waits up to -WaitSeconds for each process to end, and prints one line per fact, for the smoke
    test to check:
        processes <n>
        app-type <pid> <RM_APP_TYPE>          (RestartManager)
        shutdown <win32 result> <ms>          (RestartManager)
        posted <pid> <windows>                (CloseMessage)
        exited <pid> <ms> <exit code>   or   running <pid>
    Process ids and numbers only: no window title, no path, nothing from the profile.

    The smoke test runs this in a process of its own and ends it if it overstays: a Restart Manager call
    has no timeout of its own.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][ValidateSet('RestartManager', 'CloseMessage')][string]$Mode,
    [int]$WaitSeconds = 15
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class ScreenTailClose
{
    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint handle, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint handle);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint handle, uint files, string[] names, uint apps, RM_UNIQUE_PROCESS[] processes, uint services, string[] serviceNames);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[] info, ref uint reasons);
    [DllImport("rstrtmgr.dll")] static extern int RmShutdown(uint handle, uint flags, IntPtr callback);

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static List<string> RestartManager(string file)
    {
        var lines = new List<string>();
        uint handle;
        var rc = RmStartSession(out handle, 0, new StringBuilder(64));
        if (rc != 0) { lines.Add("session " + rc); return lines; }
        try
        {
            rc = RmRegisterResources(handle, 1, new[] { file }, 0, null, 0, null);
            if (rc != 0) { lines.Add("register " + rc); return lines; }
            uint needed, count = 0, reasons = 0;
            RmGetList(handle, out needed, ref count, null, ref reasons);
            var info = new RM_PROCESS_INFO[needed + 1];
            count = (uint)info.Length;
            rc = RmGetList(handle, out needed, ref count, info, ref reasons);
            for (var i = 0; rc == 0 && i < count; i++)
            {
                lines.Add("app-type " + info[i].Process.dwProcessId + " " + info[i].ApplicationType);
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            rc = RmShutdown(handle, 0, IntPtr.Zero);
            lines.Add("shutdown " + rc + " " + watch.ElapsedMilliseconds);
        }
        finally { RmEndSession(handle); }
        return lines;
    }

    public static int PostClose(int pid, string windowClass)
    {
        var posted = 0;
        EnumWindows((window, unused) =>
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == pid)
            {
                var name = new StringBuilder(256);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() == windowClass && PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero)) { posted++; }
            }
            return true;
        }, IntPtr.Zero);
        return posted;
    }
}
'@

$name = [IO.Path]::GetFileNameWithoutExtension($Exe)
$processes = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ieq $Exe })
foreach ($process in $processes) {
    # Windows PowerShell reports ExitCode only for a process whose handle was taken while it ran.
    $null = $process.Handle
}
Write-Output "processes $($processes.Count)"

$watch = [Diagnostics.Stopwatch]::StartNew()
if ($Mode -eq 'RestartManager') {
    [ScreenTailClose]::RestartManager($Exe) | ForEach-Object { Write-Output $_ }
} else {
    foreach ($process in $processes) {
        Write-Output "posted $($process.Id) $([ScreenTailClose]::PostClose($process.Id, 'ScreenTail.Service.SessionEnd'))"
    }
}

foreach ($process in $processes) {
    if ($process.WaitForExit($WaitSeconds * 1000)) {
        Write-Output "exited $($process.Id) $($watch.ElapsedMilliseconds) $($process.ExitCode)"
    } else {
        Write-Output "running $($process.Id)"
    }
}
