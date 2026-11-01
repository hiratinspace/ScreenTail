<#
.SYNOPSIS
    TEMPORARY diagnostic for the uninstall flake (exit 3010, ScreenTail.Service.exe in use). Not part of
    the product; removed before the fix merges.

.DESCRIPTION
    Installs the package, then measures, many times over, whether ScreenTail.Service.exe can be opened
    for deletion right after a process that ran from it has exited, and if not, which processes have it
    open (NtQueryInformationFile, FileProcessIdsUsingFileInformation) and for how long. Prints counts,
    process names and timings only.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [string]$Logs = (Join-Path ([IO.Path]::GetTempPath()) 'screentail-diag'),
    [int]$Iterations = 40,
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
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class LockProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status, IntPtr info, int length, int infoClass);

    // 0 when the file can be opened for deletion the way Windows Installer needs; otherwise the Win32 error.
    public static int OpenForDelete(string path)
    {
        using (var h = CreateFileW(path, 0x00010000 /* DELETE */, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            return h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }
    }

    // The ids of every process with the file open (a handle or a mapping), as the kernel reports them.
    public static long[] Holders(string path)
    {
        using (var h = CreateFileW(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) { return new long[] { -Marshal.GetLastWin32Error() }; }
            int size = 64 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                IoStatusBlock status;
                int nt = NtQueryInformationFile(h, out status, buffer, size, 47 /* FileProcessIdsUsingFileInformation */);
                if (nt != 0) { return new long[] { -100000 - (nt & 0xFFFF) }; }
                int count = Marshal.ReadInt32(buffer);
                var ids = new List<long>();
                for (int i = 0; i < count; i++)
                {
                    ids.Add(Marshal.ReadIntPtr(buffer, IntPtr.Size + i * IntPtr.Size).ToInt64());
                }
                return ids.ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
'@

function Describe-Holders([string]$Path) {
    $ids = [LockProbe]::Holders($Path)
    ($ids | ForEach-Object {
            $id = $_
            if ($id -lt 0) { "query-failed($id)" } else {
                $p = Get-Process -Id $id -ErrorAction SilentlyContinue
                if ($p) { "$($p.ProcessName)#$id" } else { "gone#$id" }
            }
        }) -join ','
}

# After a process that ran from $Path exits: how long the file stays unopenable for deletion, and who has it.
function Measure-Lock([string]$Path, [string]$Label) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $first = [LockProbe]::OpenForDelete($Path)
    if ($first -eq 0) {
        return [pscustomobject]@{ Label = $Label; Locked = $false; Error = 0; Ms = 0; Holders = '' }
    }
    $holders = Describe-Holders $Path
    $seen = @{}
    $seen[$holders] = 1
    while ($watch.ElapsedMilliseconds -lt 20000) {
        if ([LockProbe]::OpenForDelete($Path) -eq 0) { break }
        $h = Describe-Holders $Path
        $seen[$h] = 1
        Start-Sleep -Milliseconds 5
    }
    return [pscustomobject]@{ Label = $Label; Locked = $true; Error = $first; Ms = $watch.ElapsedMilliseconds; Holders = ($seen.Keys -join ' | ') }
}

function Run-And-Wait([string]$File, [string]$Arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo($File, $Arguments)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($info)
    $null = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    $code = $p.ExitCode
    $p.Dispose()
    return $code
}

function Start-Service-And-Wait {
    $token = Join-Path $dataDir 'ipc.token'
    Remove-Item $token -Force -ErrorAction SilentlyContinue
    $null = Start-Process -FilePath $serviceExe -WorkingDirectory $installDir -PassThru
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-Path $token) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Write-Host '== Defender on this runner'
try {
    Get-MpComputerStatus | Select-Object AMRunningMode, AntivirusEnabled, RealTimeProtectionEnabled, OnAccessProtectionEnabled, BehaviorMonitorEnabled, IoavProtectionEnabled, AMProductVersion | Format-List | Out-String | Write-Host
    $pref = Get-MpPreference
    Write-Host "DisableRealtimeMonitoring=$($pref.DisableRealtimeMonitoring) ExclusionPath=$($pref.ExclusionPath -join ';') ExclusionProcess=$($pref.ExclusionProcess -join ';')"
} catch {
    Write-Host "Defender status unavailable: $($_.Exception.Message)"
}
$mp = Get-Process -Name MsMpEng -ErrorAction SilentlyContinue
Write-Host "MsMpEng running: $([bool]$mp)"

Write-Host '== Install'
$code = Run-And-Wait 'msiexec.exe' "/i `"$Msi`" /qn /norestart /l*v `"$(Join-Path $Logs 'diag-install.log')`""
Write-Host "install exit $code"

$results = New-Object System.Collections.Generic.List[object]

Write-Host "== A: --uninstall-cleanup with nothing running, then probe ($Iterations times)"
for ($i = 0; $i -lt $Iterations; $i++) {
    $code = Run-And-Wait $serviceExe '--uninstall-cleanup'
    $r = Measure-Lock $serviceExe 'A cleanup alone'
    $results.Add($r)
    if ($r.Locked) { Write-Host ("A[{0}] exit {1} LOCKED error {2} for {3} ms by {4}" -f $i, $code, $r.Error, $r.Ms, $r.Holders) }
}

Write-Host "== B: service running, --uninstall-cleanup stops it, then probe ($Iterations times)"
for ($i = 0; $i -lt $Iterations; $i++) {
    $started = Start-Service-And-Wait
    $code = Run-And-Wait $serviceExe '--uninstall-cleanup'
    $r = Measure-Lock $serviceExe 'B cleanup stops service'
    $results.Add($r)
    if ($r.Locked -or -not $started) { Write-Host ("B[{0}] started {1} exit {2} locked {3} error {4} for {5} ms by {6}" -f $i, $started, $code, $r.Locked, $r.Error, $r.Ms, $r.Holders) }
}

Write-Host "== C: --capabilities, then probe ($Iterations times)"
for ($i = 0; $i -lt $Iterations; $i++) {
    $code = Run-And-Wait $serviceExe '--capabilities'
    $r = Measure-Lock $serviceExe 'C capabilities'
    $results.Add($r)
    if ($r.Locked) { Write-Host ("C[{0}] exit {1} LOCKED error {2} for {3} ms by {4}" -f $i, $code, $r.Error, $r.Ms, $r.Holders) }
}

Write-Host "== D: a small system program copied into Program Files, then probe ($Iterations times)"
$small = Join-Path $env:ProgramFiles 'ScreenTailDiag\where.exe'
New-Item -ItemType Directory -Force -Path (Split-Path $small) | Out-Null
for ($i = 0; $i -lt $Iterations; $i++) {
    Copy-Item (Join-Path $env:SystemRoot 'System32\where.exe') $small -Force
    $code = Run-And-Wait $small 'where.exe'
    $r = Measure-Lock $small 'D small exe'
    $results.Add($r)
    if ($r.Locked) { Write-Host ("D[{0}] exit {1} LOCKED error {2} for {3} ms by {4}" -f $i, $code, $r.Error, $r.Ms, $r.Holders) }
}
Remove-Item (Split-Path $small) -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '== Uninstall after the probes'
$code = Run-And-Wait 'msiexec.exe' "/x `"$Msi`" /qn /norestart /l*v `"$(Join-Path $Logs 'diag-uninstall-0.log')`""
Write-Host "uninstall exit $code"

Write-Host "== E: install, start the service, msiexec /x ($MsiCycles cycles); F: install, msiexec /x with nothing running"
$cycles = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $MsiCycles; $i++) {
    foreach ($withService in @($true, $false)) {
        $tag = if ($withService) { 'E' } else { 'F' }
        $null = Run-And-Wait 'msiexec.exe' "/i `"$Msi`" /qn /norestart"
        if ($withService) { $null = Start-Service-And-Wait }
        $log = Join-Path $Logs ("diag-{0}-{1}.log" -f $tag, $i)
        $code = Run-And-Wait 'msiexec.exe' "/x `"$Msi`" /qn /norestart /l*v `"$log`""
        $inUse = @(Get-Content $log | Where-Object { $_ -match 'Info 1903|2329' })
        $cycles.Add([pscustomobject]@{ Tag = $tag; Code = $code; Folder = (Test-Path $installDir) })
        Write-Host ("{0}[{1}] exit {2}, folder left {3}{4}" -f $tag, $i, $code, (Test-Path $installDir), $(if ($inUse) { ': ' + ($inUse -join ' / ') } else { '' }))
        if (Test-Path $installDir) {
            # Reboot pending removal; clear it by hand so the next cycle starts clean.
            for ($w = 0; $w -lt 100 -and (Test-Path $installDir); $w++) {
                Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 200
            }
        }
    }
}

Write-Host ''
Write-Host '== Summary'
$results | Group-Object Label | ForEach-Object {
    $locked = @($_.Group | Where-Object Locked)
    $ms = ($locked | Measure-Object Ms -Maximum).Maximum
    Write-Host ("{0}: {1} of {2} locked right after exit (longest {3} ms)" -f $_.Name, $locked.Count, $_.Count, $ms)
}
$cycles | Group-Object Tag | ForEach-Object {
    Write-Host ("msiexec /x cycles {0}: {1} of {2} not 0 ({3})" -f $_.Name, @($_.Group | Where-Object { $_.Code -ne 0 }).Count, $_.Count, (($_.Group | ForEach-Object { $_.Code }) -join ','))
}
