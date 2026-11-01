<#
.SYNOPSIS
    TEMPORARY diagnostic for the uninstall flake (exit 3010, ScreenTail.Service.exe in use). Not part of
    the product; removed before the fix merges.

.DESCRIPTION
    Watches who has ScreenTail.Service.exe open (NtQueryInformationFile, FileProcessIdsUsingFileInformation),
    sampled every few milliseconds, while a fresh copy of it runs and after it exits, and while
    msiexec uninstalls a fresh install. Prints process names and timings only.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [string]$Logs = (Join-Path ([IO.Path]::GetTempPath()) 'screentail-diag'),
    [int]$Iterations = 25,
    [int]$MsiCycles = 12
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Logs | Out-Null
$installDir = Join-Path $env:ProgramFiles 'ScreenTail'
$serviceExe = Join-Path $installDir 'ScreenTail.Service.exe'
$dataDir = Join-Path $env:LOCALAPPDATA 'ScreenTail'
$Msi = (Resolve-Path $Msi).Path

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public static class LockProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status, IntPtr info, int length, int infoClass);

    public static int OpenForDelete(string path)
    {
        using (var h = CreateFileW(path, 0x00010000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            return h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }
    }

    public static long[] Holders(string path)
    {
        using (var h = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) { return new long[0]; }
            int size = 64 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                IoStatusBlock status;
                int nt = NtQueryInformationFile(h, out status, buffer, size, 47);
                if (nt != 0) { return new long[] { -1 }; }
                int count = Marshal.ReadInt32(buffer);
                var ids = new List<long>();
                for (int i = 0; i < count; i++) { ids.Add(Marshal.ReadIntPtr(buffer, IntPtr.Size + i * IntPtr.Size).ToInt64()); }
                return ids.ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}

// Samples the holders of one path on a thread of its own until stopped. Records, per process name,
// the first and last time (ms on its own clock) it was seen holding the file.
public sealed class HolderSampler
{
    private readonly string _path;
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<long, string> _names = new Dictionary<long, string>();
    public readonly Dictionary<string, long[]> Seen = new Dictionary<string, long[]>();
    public long Samples;
    public long ExcludePid = -1;

    public HolderSampler(string path)
    {
        _path = path;
        _thread = new Thread(Run);
        _thread.IsBackground = true;
        _thread.Priority = ThreadPriority.AboveNormal;
        _thread.Start();
    }

    public long Now { get { return _clock.ElapsedMilliseconds; } }

    private string NameOf(long pid)
    {
        string name;
        if (_names.TryGetValue(pid, out name)) { return name; }
        try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "gone"; }
        _names[pid] = name;
        return name;
    }

    private void Run()
    {
        while (!_stop)
        {
            long[] ids;
            try { ids = LockProbe.Holders(_path); } catch { ids = new long[0]; }
            long t = _clock.ElapsedMilliseconds;
            Samples++;
            foreach (var id in ids)
            {
                string key = (id == ExcludePid ? "self" : NameOf(id)) + "#" + id;
                lock (Seen)
                {
                    long[] span;
                    if (Seen.TryGetValue(key, out span)) { span[1] = t; } else { Seen[key] = new long[] { t, t }; }
                }
            }
            Thread.Sleep(1);
        }
    }

    public void Stop() { _stop = true; _thread.Join(); }
}
'@

function Format-Seen($Sampler, [long]$Zero) {
    $parts = foreach ($k in $Sampler.Seen.Keys) {
        $s = $Sampler.Seen[$k]
        "{0} {1}..{2} ms" -f $k, ($s[0] - $Zero), ($s[1] - $Zero)
    }
    ($parts -join '; ')
}

function Run-And-Wait([string]$File, [string]$Arguments, $Sampler) {
    $info = New-Object Diagnostics.ProcessStartInfo($File, $Arguments)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($info)
    if ($Sampler) { $Sampler.ExcludePid = $p.Id }
    $null = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    $code = $p.ExitCode
    $p.Dispose()
    return $code
}

function Start-Service-And-Wait {
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    $token = Join-Path $dataDir 'ipc.token'
    Remove-Item $token -Force -ErrorAction SilentlyContinue
    $null = Start-Process -FilePath $serviceExe -WorkingDirectory $installDir -PassThru
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-Path $token) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Write-Host '== Machine'
Get-Process | Where-Object { $_.ProcessName -match 'MsMpEng|MsSense|SenseIR|SenseCnc|NisSrv|SearchIndexer|SearchProtocolHost|CompatTelRunner|MpDefenderCoreService|elastic|crowd|falcon|sentinel|cb' } |
    ForEach-Object { Write-Host "  running: $($_.ProcessName)" }
try {
    $s = Get-MpComputerStatus
    Write-Host "  Defender AMRunningMode=$($s.AMRunningMode) RealTime=$($s.RealTimeProtectionEnabled)"
} catch { Write-Host "  Defender status unavailable" }
foreach ($svc in @('Sense', 'WinDefend', 'WSearch', 'SysMain', 'PcaSvc', 'DiagTrack', 'WerSvc')) {
    $x = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($x) { Write-Host "  service $svc $($x.Status)" } else { Write-Host "  service $svc absent" }
}

Write-Host '== Install'
$code = Run-And-Wait 'msiexec.exe' "/i `"$Msi`" /qn /norestart /l*v `"$(Join-Path $Logs 'diag-install.log')`"" $null
Write-Host "install exit $code"

Write-Host "== A: a fresh copy of the service runs --uninstall-cleanup; who holds it, during and after ($Iterations times)"
$lockedAfter = 0
for ($i = 0; $i -lt $Iterations; $i++) {
    $copy = Join-Path $installDir ("diag-$i.exe")
    Copy-Item $serviceExe $copy -Force
    $sampler = New-Object HolderSampler($copy)
    $code = Run-And-Wait $copy '--uninstall-cleanup' $sampler
    $exit = $sampler.Now
    $first = [LockProbe]::OpenForDelete($copy)
    $free = $exit
    while ([LockProbe]::OpenForDelete($copy) -ne 0 -and ($sampler.Now - $exit) -lt 20000) { Start-Sleep -Milliseconds 2 }
    $free = $sampler.Now
    Start-Sleep -Milliseconds 1500
    $sampler.Stop()
    if ($first -ne 0) { $lockedAfter++ }
    Write-Host ("A[{0}] exit {1}; right after exit open-for-delete error {2}, free {3} ms after exit; holders (ms from exit): {4}" -f $i, $code, $first, ($free - $exit), (Format-Seen $sampler $exit))
    Remove-Item $copy -Force -ErrorAction SilentlyContinue
}
Write-Host "A: $lockedAfter of $Iterations still held right after the process exited"

Write-Host '== Uninstall after A'
$null = Run-And-Wait 'msiexec.exe' "/x `"$Msi`" /qn /norestart" $null

Write-Host "== E: install, service running, msiexec /x; F: install, msiexec /x with nothing running ($MsiCycles cycles each); holders of the service executable through the uninstall"
$codes = @{ E = @(); F = @() }
for ($i = 0; $i -lt $MsiCycles; $i++) {
    foreach ($tag in @('E', 'F')) {
        $null = Run-And-Wait 'msiexec.exe' "/i `"$Msi`" /qn /norestart" $null
        if ($tag -eq 'E') { $null = Start-Service-And-Wait }
        $sampler = New-Object HolderSampler($serviceExe)
        $log = Join-Path $Logs ("diag-{0}-{1}.log" -f $tag, $i)
        $start = $sampler.Now
        $code = Run-And-Wait 'msiexec.exe' "/x `"$Msi`" /qn /norestart /l*v `"$log`"" $null
        $sampler.Stop()
        $codes[$tag] += $code
        $inUse = @(Get-Content $log | Where-Object { $_ -match 'Info 1903' })
        Write-Host ("{0}[{1}] exit {2}{3}; holders (ms from msiexec start): {4}" -f $tag, $i, $code, $(if ($inUse) { ' IN USE: ' + ($inUse -join ' / ') } else { '' }), (Format-Seen $sampler $start))
        for ($w = 0; $w -lt 100 -and (Test-Path $installDir); $w++) {
            Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 200
        }
    }
}
Write-Host ''
Write-Host "== Summary: A $lockedAfter of $Iterations held after exit; E exits $($codes['E'] -join ','); F exits $($codes['F'] -join ',')"
