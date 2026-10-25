<#
.SYNOPSIS
    Runs check-skips.ps1 against synthetic TRX reports and fails if the gate gives the wrong answer.

.DESCRIPTION
    The scripts job used to parse the gate and nothing ran it, so a gate that passed everything was
    indistinguishable from one that worked (2026-10-12 review, T-232). Each case below writes a small
    baseline and one or two TRX reports the way xUnit writes them, runs the gate in a child process
    (it exits, so it cannot be dot-sourced) and checks the exit code. No test project, no network, no
    Pester: Windows PowerShell 5.1 and a temp folder.

    ASCII only, for the same reason as the gate itself.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot 'check-skips.ps1'
if (-not (Test-Path $gate)) { Write-Host "::error::No gate at $gate."; exit 1 }

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("check-skips-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null

# The gate appends to the job summary when the variable is set. Point it at a scratch file so the cases
# below do not write twelve headings into the real one.
$env:GITHUB_STEP_SUMMARY = Join-Path $root 'summary.md'

function Write-Trx {
    param([string]$Path, [object[]]$Results)
    $rows = foreach ($r in $Results) {
        $name = $r.Name
        $outcome = $r.Outcome
        if ($outcome -eq 'NotExecuted') {
            "    <UnitTestResult testName=`"$name`" outcome=`"$outcome`"><Output><StdOut>$($r.Reason)</StdOut></Output></UnitTestResult>"
        } else {
            "    <UnitTestResult testName=`"$name`" outcome=`"$outcome`" />"
        }
    }
    $xml = @(
        '<?xml version="1.0" encoding="utf-8"?>',
        '<TestRun id="00000000-0000-0000-0000-000000000000" name="synthetic" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">',
        '  <Results>'
    ) + @($rows) + @(
        '  </Results>',
        '</TestRun>'
    )
    [System.IO.File]::WriteAllLines($Path, [string[]]$xml, (New-Object System.Text.UTF8Encoding($false)))
}

function Write-Baseline {
    param([string]$Path, [int]$MaxSkipped, [string[]]$MustRun)
    $environment = [ordered]@{ maxSkipped = $MaxSkipped; why = 'synthetic' }
    if ($MustRun) { $environment.mustRun = $MustRun }
    $json = [ordered]@{ environments = [ordered]@{ hosted = $environment } } | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Passed { param([string]$Name) [pscustomobject]@{ Name = $Name; Outcome = 'Passed' } }
function Skipped { param([string]$Name, [string]$Reason = 'no desktop') [pscustomobject]@{ Name = $Name; Outcome = 'NotExecuted'; Reason = $Reason } }
function FailedTest { param([string]$Name) [pscustomobject]@{ Name = $Name; Outcome = 'Failed' } }

$cases = @()
function Case {
    param([string]$Name, [int]$ExpectExit, [int]$MaxSkipped, [string[]]$MustRun, [object[][]]$Reports, [string]$MustSay)
    $script:cases += [pscustomobject]@{
        Name = $Name; ExpectExit = $ExpectExit; MaxSkipped = $MaxSkipped; MustRun = $MustRun; Reports = $Reports; MustSay = $MustSay
    }
}

$proof = 'ScreenTail.Tests.Windows.Input.InputHookTests.TypingAPasswordRecordsOnlyHowManyKeys'
$other = 'ScreenTail.Tests.Windows.Input.InputHookTests.EnterIsItsOwnCategory'
$third = 'ScreenTail.Tests.Windows.Capture.SomethingElse'

Case -Name 'a run within its budget passes' -ExpectExit 0 -MaxSkipped 1 `
    -Reports @(, @((Passed $proof), (Passed $other), (Skipped $third))) -MustSay 'Skip gate passed: 1 of 3 skipped'
Case -Name 'skipping more than the budget fails' -ExpectExit 1 -MaxSkipped 1 `
    -Reports @(, @((Passed $proof), (Skipped $other), (Skipped $third))) -MustSay 'against a budget of 1'
Case -Name 'skipped tests are counted across every report' -ExpectExit 1 -MaxSkipped 1 `
    -Reports @(@((Passed $proof), (Skipped $other)), @((Skipped $third))) -MustSay 'against a budget of 1'
Case -Name 'a run with no tests at all fails, however generous the budget' -ExpectExit 1 -MaxSkipped 10 `
    -Reports @(, @()) -MustSay 'hold no tests at all'
Case -Name 'a must-run test that is not in the run fails the gate' -ExpectExit 1 -MaxSkipped 0 -MustRun @('InputHookTests.TypingAPasswordRecordsOnlyHowManyKeys') `
    -Reports @(, @((Passed $other))) -MustSay 'must run and pass'
Case -Name 'a must-run test that skipped fails the gate even inside the budget' -ExpectExit 1 -MaxSkipped 5 -MustRun @('InputHookTests.TypingAPasswordRecordsOnlyHowManyKeys') `
    -Reports @(, @((Skipped $proof 'Input injection not permitted on this runner.'), (Passed $other))) -MustSay 'must run and pass'
Case -Name 'a must-run test that failed does not count as having run' -ExpectExit 1 -MaxSkipped 5 -MustRun @('InputHookTests.TypingAPasswordRecordsOnlyHowManyKeys') `
    -Reports @(, @((FailedTest $proof), (Passed $other))) -MustSay 'must run and pass'
Case -Name 'a must-run test that passed satisfies the gate' -ExpectExit 0 -MaxSkipped 0 -MustRun @('InputHookTests.TypingAPasswordRecordsOnlyHowManyKeys') `
    -Reports @(, @((Passed $proof), (Passed $other))) -MustSay 'Skip gate passed: 0 of 2 skipped'
Case -Name 'a budget nobody has reached is pointed out' -ExpectExit 0 -MaxSkipped 3 `
    -Reports @(, @((Passed $proof), (Passed $other))) -MustSay 'Lower ''hosted'''

$bad = 0
$n = 0
foreach ($case in $cases) {
    $n++
    $dir = Join-Path $root "case-$n"
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'trx') | Out-Null
    $baseline = Join-Path $dir 'skip-baseline.json'
    Write-Baseline -Path $baseline -MaxSkipped $case.MaxSkipped -MustRun $case.MustRun
    $i = 0
    foreach ($report in $case.Reports) {
        $i++
        Write-Trx -Path (Join-Path $dir "trx\report-$i.trx") -Results @($report)
    }

    $output = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $gate `
        -Environment hosted -TrxGlob (Join-Path $dir 'trx\*.trx') -BaselinePath $baseline 2>&1 | Out-String
    $exit = $LASTEXITCODE

    $ok = ($exit -eq $case.ExpectExit) -and ($output.Contains($case.MustSay))
    if ($ok) {
        Write-Host "ok   $($case.Name) (exit $exit)"
    } else {
        $bad++
        Write-Host "::error file=scripts/windows/check-skips.ps1::$($case.Name): expected exit $($case.ExpectExit) saying '$($case.MustSay)', got exit $exit."
        Write-Host $output
    }
}

# No reports at all is its own failure, before any budget is read.
$n++
$dir = Join-Path $root "case-$n"
New-Item -ItemType Directory -Force -Path (Join-Path $dir 'trx') | Out-Null
$baseline = Join-Path $dir 'skip-baseline.json'
Write-Baseline -Path $baseline -MaxSkipped 10
$output = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $gate `
    -Environment hosted -TrxGlob (Join-Path $dir 'trx\*.trx') -BaselinePath $baseline 2>&1 | Out-String
if ($LASTEXITCODE -eq 1 -and $output.Contains('No TRX reports matched')) {
    Write-Host 'ok   a run that wrote no report fails (exit 1)'
} else {
    $bad++
    Write-Host "::error file=scripts/windows/check-skips.ps1::a run that wrote no report: expected exit 1 saying 'No TRX reports matched', got exit $LASTEXITCODE."
    Write-Host $output
}

Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
if ($bad -gt 0) {
    Write-Host "$bad of $n skip gate cases gave the wrong answer."
    exit 1
}
Write-Host "All $n skip gate cases gave the right answer."
