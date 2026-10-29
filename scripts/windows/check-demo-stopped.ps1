<#
.SYNOPSIS
    Fails at once, with one line, when the ScreenTail demo is running on this machine.

.DESCRIPTION
    The laptop that runs the hardware jobs is also where the owner runs the demo. While the demo is up
    its capture service holds Ctrl+Alt+R, P, S and M and owns the pipe, so the hardware tests fail one
    by one with messages such as "Another application is already using Ctrl+Alt+R", which read like a
    regression rather than a machine that is busy. This runs first and says what is going on instead.

    Read-only. It looks at two things and changes neither:
      - a ScreenTail.Service or ScreenTail.UI process in this desktop session, the one the jobs use;
      - the demo's state file (%LOCALAPPDATA%\ScreenTail.Dev\demo\state.json), for a process start.cmd
        launched that is still alive, judged the way stop.cmd judges it: id, name and start time.

    It never skips anything: a busy laptop is a failed job, not a quiet one.

    ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.

.PARAMETER StateFile
    The demo's state file. The tests point it at a scratch file.

.PARAMETER Names
    The process names that mean the product is running. The tests pass a name of their own.
#>
[CmdletBinding()]
param(
    [string]$StateFile,
    [string[]]$Names = @('ScreenTail.Service', 'ScreenTail.UI')
)

# The demo's own reading of its state file, so "still running" means here what it means to stop.cmd.
. (Join-Path $PSScriptRoot 'demo\common.ps1')
if ($StateFile) {
    $script:DemoStateFile = $StateFile
}

$session = (Get-Process -Id $PID).SessionId
$running = @(Get-Process -Name $Names -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
$started = @(Get-DemoState | Where-Object { Get-EntryProcess $_ })

if ($running.Count -gt 0 -or $started.Count -gt 0) {
    Write-Host '::error::The ScreenTail demo is running on this laptop. Run scripts\windows\demo\stop.cmd, then re-run this job.'
    exit 1
}

Write-Host 'The ScreenTail demo is not running.'
exit 0
