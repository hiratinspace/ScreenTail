<#
.SYNOPSIS
    The ST-001 checks that need no human, shared by both CI jobs.

.DESCRIPTION
    Runs the overlay exclusion check with its control case (AC3) and synthetic legibility (AC4), then either:
      smoke           a 1-minute run with no input, to catch start-up crashes (hosted runner), or
      synthetic-load  a 5-minute full-load run with injected typing and clicks (AC1, real hardware).
    Each check is independent; the script exits 1 if any failed, after running them all.
    Expects a Release build in place. Appends results to the GitHub step summary when there is one.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Evidence,
    [ValidateSet('smoke', 'synthetic-load')][string]$RunMode = 'smoke'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$overlayExe = Join-Path $PSScriptRoot 'Spike.Overlay\bin\Release\net8.0-windows\Spike.Overlay.exe'
$failures = New-Object System.Collections.Generic.List[string]
New-Item -ItemType Directory -Force -Path $Evidence | Out-Null

function Invoke-Capture([string[]]$Arguments) {
    & dotnet run -c Release --no-build --project Spike.Capture -- @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Spike.Capture $($Arguments[0]) exited with $LASTEXITCODE" }
}

function Start-Overlay([string[]]$Arguments) {
    if ($Arguments) {
        $process = Start-Process -FilePath $overlayExe -ArgumentList $Arguments -PassThru
    }
    else {
        $process = Start-Process -FilePath $overlayExe -PassThru
    }
    Start-Sleep -Seconds 5
    return $process
}

function Stop-Overlay($Process) {
    if ($Process) { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue }
}

function Add-Summary([string[]]$Lines) {
    if ($env:GITHUB_STEP_SUMMARY) { $Lines | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
}

function Get-Verdict([string]$ReportPath, [string]$Section) {
    $lines = Get-Content $ReportPath
    $inSection = $false
    foreach ($line in $lines) {
        if ($line -like "## $Section*") { $inSection = $true; continue }
        if ($inSection -and $line -like '**Verdict:*') { return $line }
    }
    return $null
}

function Invoke-Check([string]$Name, [scriptblock]$Body) {
    Write-Host "== $Name"
    try { & $Body }
    catch {
        $failures.Add("${Name}: $_")
        Write-Host "::error::${Name}: $_"
    }
}

Invoke-Check 'AC3 overlay exclusion' {
    $out = Join-Path $Evidence 'overlay'
    $overlay = Start-Overlay
    try { Invoke-Capture @('overlay-check', '--label', 'excluded', '--out', $out) } finally { Stop-Overlay $overlay }
    $overlay = Start-Overlay @('--visible')
    try { Invoke-Capture @('overlay-check', '--label', 'visible', '--out', $out) } finally { Stop-Overlay $overlay }

    $excluded = (Get-Content (Join-Path $out 'overlay-check-excluded.txt') -Raw).Trim()
    $visible = (Get-Content (Join-Path $out 'overlay-check-visible.txt') -Raw).Trim()
    Add-Summary @('### AC3 overlay exclusion', '', "- excluded run: $excluded", "- control run: $visible", '')
    if ($excluded -notmatch 'EXCLUDED' -or $visible -notmatch 'CAPTURED') {
        throw 'expected EXCLUDED for the excluded run and CAPTURED for the control run'
    }
}

Invoke-Check 'AC4 legibility' {
    $out = Join-Path $Evidence 'legibility'
    Invoke-Capture @('legibility', '--out', $out)
    Add-Summary (Get-Content (Join-Path $out 'legibility.md'))
}

if ($RunMode -eq 'smoke') {
    Invoke-Check 'Smoke run (1 min, no input)' {
        $out = Join-Path $Evidence 'smoke-run'
        Invoke-Capture @('run', '--minutes', '1', '--out', $out)
        Add-Summary (Get-Content (Join-Path $out 'report.md'))
    }
}
else {
    Invoke-Check 'AC1 synthetic load (5 min, injected typing)' {
        $out = Join-Path $Evidence 'synthetic-load'
        $overlay = Start-Overlay @('--typing-target')
        try { Invoke-Capture @('run', '--minutes', '5', '--drive-input', '--out', $out) } finally { Stop-Overlay $overlay }

        $report = Join-Path $out 'report.md'
        Add-Summary (Get-Content $report)
        $verdict = Get-Verdict $report 'AC1'
        if ($verdict -like '*FAIL*') { throw "AC1 $verdict" }
    }
}

if ($failures.Count -gt 0) {
    Write-Host 'Failed checks:'
    $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}
