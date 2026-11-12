<#
.SYNOPSIS
    Fails at once, with one line, when the ScreenTail demo or another ScreenTail process is running here.

.DESCRIPTION
    The laptop that runs the hardware jobs is also where the owner runs the demo. While the demo is up
    its capture service holds Ctrl+Alt+R, P, S and M and owns the pipe, so the hardware tests fail one
    by one with messages such as "Another application is already using Ctrl+Alt+R", which read like a
    regression rather than a machine that is busy. This runs first and says what is going on instead.

    Read-only. It looks at two things and changes neither:
      - the demo's state file (%LOCALAPPDATA%\ScreenTail.Dev\demo\state.json), for a process start.cmd
        launched that is still alive, judged the way stop.cmd judges it: id, name and start time. That
        is the demo, and stop.cmd stops it;
      - a ScreenTail.Service or ScreenTail.UI process in this desktop session, the one the jobs use.
        One the state file does not record is not something stop.cmd will stop, so the line names it
        by process and id and says to close it.

    It never skips anything: a busy laptop is a failed job, not a quiet one.

    When the demo is not running, it then prints a short readiness block (laptop-readiness.ps1): power
    source and battery, power plan, power mode and energy saver, the CPU clock against its maximum, CPU
    busy over a 3 s sample, the five busiest processes, and whether Windows Update or a Defender scan is
    busy. If the laptop is on battery, a saver is on, the clock is held below 70% of maximum or more
    than 25% of the CPU is busy before the tests start, it adds one ::warning:: naming the likely fix
    (plug in, set power mode to Best performance, wait for Windows Update to finish) and copies the
    block to the job summary. That is a warning only: a slow laptop still runs the job, and the exit
    code is the demo check's alone. A probe that fails prints "unknown". The block takes about 5 s.
    docs/dev/windows-test-loop.md ("Before a laptop run") says what to do about each warning.

    ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.

.PARAMETER StateFile
    The demo's state file. The tests point it at a scratch file.

.PARAMETER Names
    The process names that mean the product is running. The tests pass a name of their own.

.PARAMETER SessionId
    The desktop session whose processes count. Defaults to this script's own session, which is the
    job's. Only the tests pass it, to show that a process in another session is not counted; the
    workflow never does.

.PARAMETER NoReadiness
    Skips the readiness block. The tests pass it, so their cases stay quick; the workflow never does.
#>
[CmdletBinding()]
param(
    [string]$StateFile,
    [string[]]$Names = @('ScreenTail.Service', 'ScreenTail.UI'),
    [int]$SessionId = -1,
    [switch]$NoReadiness
)

# The demo's own reading of its state file, so "still running" means here what it means to stop.cmd.
. (Join-Path $PSScriptRoot 'demo\common.ps1')
if ($StateFile) {
    $script:DemoStateFile = $StateFile
}

$session = $SessionId
if ($session -lt 0) {
    $session = (Get-Process -Id $PID).SessionId
}

$started = @(Get-DemoState | Where-Object { Get-EntryProcess $_ })
if ($started.Count -gt 0) {
    Write-Host '::error::The ScreenTail demo is running on this laptop. Run scripts\windows\demo\stop.cmd, then re-run this job.'
    exit 1
}

$running = @(Get-Process -Name $Names -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
if ($running.Count -gt 0) {
    $named = ($running | Sort-Object Id | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
    Write-Host "::error::$named is running on this laptop and was not started by the demo, so stop.cmd will not stop it. Close it, then re-run this job."
    exit 1
}

Write-Host 'The ScreenTail demo is not running.'

# How fast this laptop is likely to be, so a slow run explains itself here rather than in a timeout.
# Warn only: nothing below changes the exit code.
if (-not $NoReadiness) {
    try {
        . (Join-Path $PSScriptRoot 'laptop-readiness.ps1')
        Invoke-LaptopReadiness
    }
    catch {
        Write-Host "Laptop readiness: unknown ($($_.Exception.Message))"
    }
}
exit 0
