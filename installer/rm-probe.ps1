<#
    TEMPORARY PROBE (ST-112 graceful close). Removed before the branch's final head.

    Reports, for every process running the given executable: its top-level and message-only windows (class
    names and visibility only, never a title), what Restart Manager says about it, and what happens when
    Restart Manager is asked to shut it down without force, as Windows Installer asks in InstallValidate.
#>
param([Parameter(Mandatory = $true)][string]$Exe, [int]$WaitSeconds = 8)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class StRmProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_PROCESS_INFO
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
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    static string ClassOf(IntPtr hwnd) { var b = new StringBuilder(256); GetClassName(hwnd, b, b.Capacity); return b.ToString(); }

    public static List<string> Windows(int pid)
    {
        var found = new List<string>();
        EnumWindows((h, l) =>
        {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner == pid) found.Add("top-level " + (IsWindowVisible(h) ? "visible" : "hidden") + " class=" + ClassOf(h));
            return true;
        }, IntPtr.Zero);
        var after = IntPtr.Zero;
        while ((after = FindWindowEx(new IntPtr(-3), after, null, null)) != IntPtr.Zero)
        {
            uint owner; GetWindowThreadProcessId(after, out owner);
            if (owner == pid) found.Add("message-only class=" + ClassOf(after));
        }
        return found;
    }

    public static List<string> Shutdown(string file, bool shutdown)
    {
        var lines = new List<string>();
        uint handle;
        var key = new StringBuilder(64);
        var rc = RmStartSession(out handle, 0, key);
        if (rc != 0) { lines.Add("RmStartSession " + rc); return lines; }
        try
        {
            rc = RmRegisterResources(handle, 1, new[] { file }, 0, null, 0, null);
            lines.Add("RmRegisterResources " + rc);
            List(handle, lines, "before");
            if (shutdown)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                rc = RmShutdown(handle, 0, IntPtr.Zero);
                lines.Add("RmShutdown(no force) returned " + rc + " after " + watch.ElapsedMilliseconds + " ms");
                List(handle, lines, "after");
            }
        }
        finally { RmEndSession(handle); }
        return lines;
    }

    static void List(uint handle, List<string> lines, string when)
    {
        uint needed, count = 0, reasons = 0;
        var rc = RmGetList(handle, out needed, ref count, null, ref reasons);
        var info = new RM_PROCESS_INFO[needed + 1];
        count = (uint)info.Length;
        rc = RmGetList(handle, out needed, ref count, info, ref reasons);
        lines.Add("RmGetList " + when + ": rc=" + rc + " apps=" + count + " reasons=0x" + reasons.ToString("X"));
        for (var i = 0; i < count; i++)
        {
            lines.Add("  pid=" + info[i].Process.dwProcessId + " type=" + info[i].ApplicationType + " status=0x" + info[i].AppStatus.ToString("X") + " session=" + info[i].TSSessionId + " restartable=" + info[i].bRestartable);
        }
    }
}
'@

$name = [IO.Path]::GetFileNameWithoutExtension($Exe)
$processes = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $Exe })
Write-Host "PROBE: $($processes.Count) process(es) running $Exe"
foreach ($p in $processes) {
    $null = $p.Handle
    Write-Host "PROBE: pid $($p.Id) session $($p.SessionId)"
    [StRmProbe]::Windows($p.Id) | ForEach-Object { Write-Host "PROBE:   $_" }
}
[StRmProbe]::Shutdown($Exe, $true) | ForEach-Object { Write-Host "PROBE: $_" }
foreach ($p in $processes) {
    $exited = $p.WaitForExit($WaitSeconds * 1000)
    if ($exited) {
        Write-Host "PROBE: pid $($p.Id) exited by itself within $WaitSeconds s, code $($p.ExitCode)"
    } else {
        Write-Host "PROBE: pid $($p.Id) still running after $WaitSeconds s"
    }
}
