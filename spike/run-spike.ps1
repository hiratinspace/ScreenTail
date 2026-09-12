<#
.SYNOPSIS
    Guided ST-001 spike run on a Windows machine: pull, build, run every check, collect the evidence.

.DESCRIPTION
    Writes everything to docs\adr\evidence\0001. The overlay check (AC3) runs by itself; the two
    5-minute runs (AC1, AC2) need you at the keyboard; legibility (AC4) runs by itself.
    Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Push
    Commit the evidence to the current branch and push it (asks you to confirm first).

.PARAMETER IncludeFrames
    Keep the up-to-25 JPEG frames each run saves. Off by default because they show your screen.

.PARAMETER SkipBaseline
    Skip the 5-minute hooks-only baseline run.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\run-spike.ps1 -Push
#>
[CmdletBinding()]
param(
    [switch]$Push,
    [switch]$IncludeFrames,
    [switch]$SkipBaseline
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = Join-Path $repoRoot 'docs\adr\evidence\0001'
$overlayExe = Join-Path $PSScriptRoot 'Spike.Overlay\bin\Release\net8.0-windows\Spike.Overlay.exe'

function Write-Step([string]$Title) {
    Write-Host ''
    Write-Host "== $Title" -ForegroundColor Cyan
}

function Assert-ExitCode([string]$What) {
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

function Invoke-Capture([string[]]$Arguments) {
    & dotnet run -c Release --no-build --project Spike.Capture -- @Arguments
    Assert-ExitCode "Spike.Capture $($Arguments[0])"
}

function Start-Overlay([switch]$Visible) {
    if ($Visible) {
        $process = Start-Process -FilePath $overlayExe -ArgumentList '--visible' -PassThru
    }
    else {
        $process = Start-Process -FilePath $overlayExe -PassThru
    }
    Start-Sleep -Seconds 3
    return $process
}

function Stop-Overlay($Process) {
    if ($Process) { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue }
}

function Initialize-Dotnet {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { return }
    # A shell opened before the SDK was installed still has the old PATH.
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { return }
    $installed = Join-Path $env:ProgramFiles 'dotnet'
    if (Test-Path (Join-Path $installed 'dotnet.exe')) {
        $env:Path = "$installed;$env:Path"
        return
    }
    throw 'dotnet was not found. Install the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8 --source winget), then open a new PowerShell window and re-run this script.'
}

function Get-ArchitectureName([int]$Code) {
    switch ($Code) {
        0 { 'x86' }
        5 { 'ARM' }
        9 { 'x64' }
        12 { 'ARM64' }
        default { "unknown ($Code)" }
    }
}

# --- Pull and build -------------------------------------------------------------------------

Write-Step 'Pull and build'
Initialize-Dotnet
$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'st-001-client-stack-spike') {
    Write-Warning "You are on '$branch'. The spike lives on 'st-001-client-stack-spike'."
}
& git pull --ff-only
Assert-ExitCode 'git pull'
& dotnet build ScreenTail.Spike.sln -c Release
Assert-ExitCode 'Build'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

# --- Machine info (no personal data: hardware, OS and display only) --------------------------

Write-Step 'Machine info'
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$system = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$gpu = Get-CimInstance Win32_VideoController | Select-Object -First 1
$dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction SilentlyContinue).AppliedDPI
if ($dpi) { $scaling = '{0:0}%' -f ($dpi / 96 * 100) } else { $scaling = 'unknown' }
if ($system.Model -match 'Virtual|VMware|Parallels|KVM|HVM|UTM') { $kind = 'virtual machine' } else { $kind = 'physical (best guess)' }

$machineFile = Join-Path $evidence 'machine.txt'
@(
    "CPU: $($cpu.Name.Trim()), $($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads, $(Get-ArchitectureName $cpu.Architecture)"
    ('RAM: {0:0} GB' -f ($system.TotalPhysicalMemory / 1GB))
    "Machine: $kind"
    "OS: $($os.Caption) $($os.Version) (build $($os.BuildNumber))"
    "Primary display: $($gpu.CurrentHorizontalResolution)x$($gpu.CurrentVerticalResolution), scaling $scaling"
) | Set-Content -Path $machineFile -Encoding UTF8
Get-Content $machineFile

# --- AC3: overlay exclusion, automatic --------------------------------------------------------

Write-Step 'AC3 overlay exclusion (automatic, about 20 seconds; do not touch the screen)'
$overlayOut = Join-Path $evidence 'overlay'
$overlay = Start-Overlay
try { Invoke-Capture @('overlay-check', '--label', 'excluded', '--out', $overlayOut) } finally { Stop-Overlay $overlay }
$overlay = Start-Overlay -Visible
try { Invoke-Capture @('overlay-check', '--label', 'visible', '--out', $overlayOut) } finally { Stop-Overlay $overlay }

# --- AC1 / AC2: the runs that need you -------------------------------------------------------

$overlay = Start-Overlay
try {
    if (-not $SkipBaseline) {
        Write-Step 'AC1 baseline: hooks only, 5 minutes'
        Write-Host 'Open Notepad now. After you press Enter, type continuously for 5 minutes and click now and then.'
        Read-Host 'Press Enter to start' | Out-Null
        Invoke-Capture @('run', '--minutes', '5', '--no-whisper', '--no-uia', '--no-ocr', '--out', (Join-Path $evidence 'baseline'))
    }

    Write-Step 'AC1 + AC2 + AC3: full load, 5 minutes'
    Write-Host 'Minutes 0-3: Notepad maximized. Type and click continuously, and talk while you work.'
    Write-Host 'Minutes 3-5: open mstsc to your RDP target, maximize it, then click and type inside the remote session.'
    Write-Host 'The magenta pill stays top-right; that is expected.'
    Read-Host 'Press Enter to start' | Out-Null
    Invoke-Capture @('run', '--minutes', '5', '--out', (Join-Path $evidence 'full-load'))
}
finally {
    Stop-Overlay $overlay
}

# --- AC4: legibility --------------------------------------------------------------------------

Write-Step 'AC4 legibility (about 2 minutes)'
$legibilityArgs = @('legibility', '--out', (Join-Path $evidence 'legibility'))
$answer = Read-Host 'Is your primary monitor 4K? If yes, put something text-heavy on it first (for example services.msc). [y/N]'
if ($answer -match '^[Yy]') { $legibilityArgs += '--from-screen' }
Invoke-Capture $legibilityArgs

# --- Tidy up and summarize --------------------------------------------------------------------

if (-not $IncludeFrames) {
    Get-ChildItem -Path $evidence -Directory -Recurse -Filter 'frames' | Remove-Item -Recurse -Force
    Write-Host 'Removed saved frames (frames\). Use -IncludeFrames to keep them.'
}

Write-Step 'Verdicts'
Get-ChildItem -Path $evidence -Recurse -Include 'report.md', 'overlay-check-*.txt' | ForEach-Object {
    Write-Host "-- $($_.Directory.Name)\$($_.Name)"
    Select-String -Path $_.FullName -Pattern 'Verdict|EXCLUDED|CAPTURED' | ForEach-Object { Write-Host "   $($_.Line)" }
}

if (-not $Push) {
    Write-Host ''
    Write-Host "Evidence saved to $evidence. Re-run with -Push, or commit it yourself."
    return
}

Write-Step 'Push'
Write-Warning "The overlay-check PNGs (and the legibility screen JPEG, if taken) are full screenshots of your screen. Review them in: $evidence"
if ((Read-Host 'Reviewed them and happy to push? [y/N]') -notmatch '^[Yy]') {
    Write-Host 'Not pushed.'
    return
}
& git add -- $evidence
Assert-ExitCode 'git add'
& git commit -m 'test(spike): add ST-001 Windows evidence' -m 'Refs: ST-001'
Assert-ExitCode 'git commit'
& git push
Assert-ExitCode 'git push'
Write-Host 'Pushed. The build agent can pick it up from the branch.'
