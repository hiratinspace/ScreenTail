<#
.SYNOPSIS
    Asks every running copy of an installed ScreenTail program to close, the way Windows asks, and says
    whether it closed by itself (ST-112). For the installer smoke test.

.DESCRIPTION
    -Mode RestartManager: what Windows Installer does in InstallValidate. A Restart Manager session, the
    type Restart Manager gives each running copy, then a shutdown without force, which only asks. The
    copies are registered by process, not by file as Windows Installer registers them: by file, Restart
    Manager would also ask whatever else had the executable open at that moment, a scanner or the CI
    runner's own monitor, to close, and a runner asked to close wedges the job with no log.
    -Mode CloseMessage: WM_CLOSE posted to the service's session-end window, as taskkill without /f does.
    -Restart (RestartManager only): once every process has ended, Restart Manager's restart in the same
    session, as Windows Installer makes it when the installation is done; then it looks for a new copy.

    Then it waits up to -WaitSeconds for each process to end, and prints one line per fact, for the smoke
    test to check:
        processes <n>
        listed <n>                            (RestartManager: processes it would ask, only ours)
        app-type <pid> <RM_APP_TYPE>          (RestartManager)
        restartable <pid> <True|False>        (RestartManager: registered for restart)
        shutdown <win32 result> <ms>          (RestartManager)
        posted <pid> <windows>                (CloseMessage)
        exited <pid> <ms> <exit code>   or   running <pid>
        store-left <names|none>               (RestartManager with -StoreFolder: store files once it ended)
        restart <win32 result>                (-Restart)
        restarted <pid>   or   not-restarted  (-Restart)
    Process ids and numbers only: no window title, no path, nothing from the profile.

    The smoke test runs this in a process of its own and ends it if it overstays: a Restart Manager call
    has no timeout of its own.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][ValidateSet('RestartManager', 'CloseMessage')][string]$Mode,
    [int]$WaitSeconds = 15,
    [switch]$Restart,
    [string]$StoreFolder = ''
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
    [DllImport("rstrtmgr.dll")] static extern int RmRestart(uint handle, uint reserved, IntPtr callback);

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static List<string> RestartManager(string file, int[] processIds, int waitMs, bool restart, string storeFolder)
    {
        var processes = new List<System.Diagnostics.Process>();
        foreach (var id in processIds)
        {
            try { var p = System.Diagnostics.Process.GetProcessById(id); var unused = p.Handle; processes.Add(p); } catch (ArgumentException) { }
        }
        var lines = new List<string>();
        uint handle;
        var rc = RmStartSession(out handle, 0, new StringBuilder(64));
        if (rc != 0) { lines.Add("session " + rc); return lines; }
        try
        {
            // These processes and nothing else (see the description).
            var unique = new RM_UNIQUE_PROCESS[processes.Count];
            for (var i = 0; i < processes.Count; i++)
            {
                var started = processes[i].StartTime.ToFileTime();
                unique[i].dwProcessId = processes[i].Id;
                unique[i].ProcessStartTime.dwLowDateTime = (int)(started & 0xFFFFFFFF);
                unique[i].ProcessStartTime.dwHighDateTime = (int)(started >> 32);
            }
            rc = RmRegisterResources(handle, 0, null, (uint)unique.Length, unique, 0, null);
            if (rc != 0) { lines.Add("register " + rc); return lines; }
            uint needed, count = 0, reasons = 0;
            RmGetList(handle, out needed, ref count, null, ref reasons);
            var info = new RM_PROCESS_INFO[needed + 1];
            count = (uint)info.Length;
            rc = RmGetList(handle, out needed, ref count, info, ref reasons);
            lines.Add("listed " + (rc == 0 ? count : 0));
            for (var i = 0; rc == 0 && i < count; i++)
            {
                lines.Add("app-type " + info[i].Process.dwProcessId + " " + info[i].ApplicationType);
                lines.Add("restartable " + info[i].Process.dwProcessId + " " + info[i].bRestartable);
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            rc = RmShutdown(handle, 0, IntPtr.Zero);
            lines.Add("shutdown " + rc + " " + watch.ElapsedMilliseconds);
            var allExited = true;
            foreach (var p in processes)
            {
                if (p.WaitForExit(waitMs)) { lines.Add("exited " + p.Id + " " + watch.ElapsedMilliseconds + " " + p.ExitCode); }
                else { lines.Add("running " + p.Id); allExited = false; }
            }
            if (allExited && !string.IsNullOrEmpty(storeFolder) && System.IO.Directory.Exists(storeFolder))
            {
                // Between the end and the restart, which opens the store again.
                var left = new List<string>();
                foreach (var path in System.IO.Directory.GetFiles(storeFolder, "store.db-*")) { left.Add(System.IO.Path.GetFileName(path)); }
                lines.Add("store-left " + (left.Count == 0 ? "none" : string.Join(",", left.ToArray())));
            }
            if (restart && allExited)
            {
                lines.Add("restart " + RmRestart(handle, 0, IntPtr.Zero));
            }
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

$ids = [int[]]@($processes | ForEach-Object { $_.Id })
if ($Mode -eq 'RestartManager') {
    # The waits happen inside, because Restart Manager's restart must come in the same session, after the end.
    [ScreenTailClose]::RestartManager($Exe, $ids, $WaitSeconds * 1000, [bool]$Restart, $StoreFolder) | ForEach-Object { Write-Output $_ }
} else {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    foreach ($process in $processes) {
        Write-Output "posted $($process.Id) $([ScreenTailClose]::PostClose($process.Id, 'ScreenTail.Service.SessionEnd'))"
    }
    foreach ($process in $processes) {
        if ($process.WaitForExit($WaitSeconds * 1000)) {
            Write-Output "exited $($process.Id) $($watch.ElapsedMilliseconds) $($process.ExitCode)"
        } else {
            Write-Output "running $($process.Id)"
        }
    }
}

if ($Restart) {
    # A copy that started after the request, from the same file.
    $again = $null
    for ($i = 0; $i -lt 40 -and -not $again; $i++) {
        Start-Sleep -Milliseconds 500
        $again = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ieq $Exe -and $ids -notcontains $_.Id }) | Select-Object -First 1
    }
    if ($again) { Write-Output "restarted $($again.Id)" } else { Write-Output 'not-restarted' }
}
