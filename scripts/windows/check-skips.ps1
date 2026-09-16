<#
.SYNOPSIS
    Fails a Windows test run that skipped more than its machine is allowed to (ST-018).

.DESCRIPTION
    `dotnet test` exits 0 when every test skips. That makes a green tick mean "nothing went wrong",
    not "the checks ran", and on hardware that is the whole point of the run, the difference matters.
    Of 46 hardware facts, 18 always skipped on the hosted runner and up to 29 could skip, including
    TypingAPasswordRecordsOnlyHowManyKeys, which is INV-2's only end-to-end proof
    (docs/review/weaknesses.md P1-4).

    This reads the TRX reports a run produced, counts what did not execute, and compares that with the
    budget in client/ScreenTail.Tests.Windows/skip-baseline.json. Every skipped test is named in the log
    and in the job summary, so a run that skipped something says which fact went unchecked.

.PARAMETER Environment
    Which budget applies: 'hosted' (windows-latest) or 'laptop' (the self-hosted runner).

.PARAMETER TrxGlob
    Where to find the TRX reports.

.PARAMETER BaselinePath
    The committed budget file.

    Kept to ASCII on purpose. Windows PowerShell 5.1 reads a .ps1 with no byte-order mark as ANSI, so a
    single non-ASCII character several lines up turns into mojibake and the parser then fails somewhere
    else entirely with "the string is missing the terminator". spike-windows.yml parse-checks this file
    against 5.1 and refuses non-ASCII in it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('hosted', 'laptop')][string]$Environment,
    [Parameter(Mandatory)][string]$TrxGlob,
    [string]$BaselinePath = 'client/ScreenTail.Tests.Windows/skip-baseline.json'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $BaselinePath)) {
    Write-Host "::error::No skip baseline at $BaselinePath. ST-018 requires one."
    exit 1
}

$baseline = Get-Content $BaselinePath -Raw | ConvertFrom-Json
$budget = $baseline.environments.$Environment
if ($null -eq $budget) {
    Write-Host "::error::$BaselinePath has no budget for '$Environment'."
    exit 1
}

$reports = @(Get-ChildItem -Path $TrxGlob -Recurse -ErrorAction SilentlyContinue)
if ($reports.Count -eq 0) {
    # A missing report is the failure mode this script exists to catch, wearing a different hat: no
    # report means no evidence the tests ran at all.
    Write-Host "::error::No TRX reports matched '$TrxGlob'. The test step must pass --report-trx."
    exit 1
}

$skipped = [System.Collections.Generic.List[string]]::new()
$total = 0
$failed = 0

foreach ($report in $reports) {
    [xml]$trx = Get-Content $report.FullName -Raw
    foreach ($result in $trx.TestRun.Results.UnitTestResult) {
        if ($null -eq $result) { continue }
        $total++
        switch ($result.outcome) {
            'NotExecuted' {
                $reason = $result.Output.ErrorInfo.Message
                if ([string]::IsNullOrWhiteSpace($reason)) { $reason = '(no reason recorded)' }
                $skipped.Add("$($result.testName): $($reason -replace '\s+', ' ')")
            }
            'Failed' { $failed++ }
        }
    }
}

$lines = @(
    "## Windows skip gate: $Environment",
    '',
    "Ran **$total** tests from $($reports.Count) report(s): $failed failed, **$($skipped.Count) skipped**, budget **$($budget.maxSkipped)**.",
    ''
)

if ($skipped.Count -gt 0) {
    $lines += @('Skipped:', '')
    $lines += ($skipped | ForEach-Object { "- $_" })
    $lines += ''
}

if ($env:GITHUB_STEP_SUMMARY) {
    [System.IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, ($lines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
}
$lines | ForEach-Object { Write-Host $_ }

foreach ($line in $skipped) {
    Write-Host "::warning::Skipped on $($Environment): $line"
}

if ($skipped.Count -gt $budget.maxSkipped) {
    Write-Host "::error::$($skipped.Count) tests skipped on $Environment against a budget of $($budget.maxSkipped). Every one of them is a fact nobody checked. Fix the machine, or make the case in review and lower the count another way. Raising the budget is refused by the baseline check."
    exit 1
}

if ($skipped.Count -lt $budget.maxSkipped) {
    # Not a failure: a run that skips less than its budget is good news. It is worth saying out loud,
    # because a budget nobody tightens stops being a ratchet.
    Write-Host "::notice::Only $($skipped.Count) tests skipped against a budget of $($budget.maxSkipped). Lower '$Environment' in $BaselinePath to $($skipped.Count) to keep the gate tight."
}

Write-Host "Skip gate passed: $($skipped.Count) of $total skipped, budget $($budget.maxSkipped)."
