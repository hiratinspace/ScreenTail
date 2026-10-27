<#
.SYNOPSIS
    Stops what start.ps1 started, and nothing else.

.DESCRIPTION
    start.ps1 records each process it launches (by id, name and start time) in
    %LOCALAPPDATA%\ScreenTail.Dev\demo\state.json. This script stops exactly those, in the order that
    leaves nothing waiting on something already gone: the UI, the capture service and its window, then
    the backend and its window, then the database container if start had to start it.

    A process whose id now belongs to something else (Windows reuses ids) is left alone. ScreenTail
    processes that start did not launch are listed and left alone.

    The capture service is closed forcefully. For a clean finish press Ctrl+C in its window first; a
    session cut off mid-recording is recovered the next time the service starts.

.EXAMPLE
    scripts\windows\demo\stop.cmd
#>
[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')

$labels = @{
    'ui' = 'the UI'
    'service' = 'the capture service'
    'service-window' = 'the capture service window'
    'backend' = 'the backend'
    'backend-window' = 'the backend window'
    'database' = "the database container ($script:DemoDbContainer)"
}
$order = @('ui', 'service', 'service-window', 'backend', 'backend-window', 'database')

Write-Host 'Stopping the ScreenTail demo.'
Assert-Windows

$entries = @(Get-DemoState)
if ($entries.Count -eq 0) {
    Write-Ok 'Nothing to stop: there is no record of a demo started by start.cmd.'
    $others = @(Get-Process -Name 'ScreenTail.Service', 'ScreenTail.UI' -ErrorAction SilentlyContinue)
    if ($others.Count -gt 0) {
        Write-Note 'These ScreenTail processes are running but were not started by start.cmd, so they are left alone:'
        foreach ($process in $others) {
            Write-Note "  pid $($process.Id)  $($process.ProcessName)"
        }
    }
    exit 0
}

Write-Step 'Stopping'
$failed = @()
$keep = @()
foreach ($role in $order) {
    foreach ($entry in @($entries | Where-Object { $_.Role -eq $role })) {
        $label = $labels[$role]
        if (-not $label) { $label = $role }

        if ($entry.Kind -eq 'container') {
            if ((Get-DockerState) -ne 'running') {
                Write-Note "$label`: Docker is not running, so there is nothing to stop."
                continue
            }
            $stopped = Invoke-Native -FilePath 'docker' -ArgumentList @('stop', $script:DemoDbContainer)
            if ($stopped.ExitCode -eq 0) {
                Write-Ok "Stopped $label."
            }
            else {
                Write-Caution "Could not stop $label. Stop it in Docker Desktop if it matters."
                $failed += $label
                $keep += $entry
            }
            continue
        }

        $process = Get-EntryProcess $entry
        if (-not $process) {
            Write-Note "$label`: already gone."
            continue
        }

        if ($role -like '*-window') {
            # The window and whatever it launched, which is what start ran in it. /T follows parent ids
            # from this one process, which was checked above to be the one start opened.
            $null = Invoke-Native -FilePath 'taskkill.exe' -ArgumentList @('/PID', [string]$process.Id, '/T', '/F')
        }
        else {
            if ($role -eq 'ui') {
                # The UI is a tray application; asking first costs three seconds at most.
                try {
                    if ($process.CloseMainWindow()) { $null = $process.WaitForExit(3000) }
                }
                catch { }
            }
            if (-not $process.HasExited) {
                try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
            }
        }

        try { $null = $process.WaitForExit(10000) } catch { }
        $process.Refresh()
        if ($process.HasExited) {
            Write-Ok "Stopped $label (pid $($process.Id))."
        }
        else {
            Write-Caution "$label (pid $($process.Id)) did not stop."
            $failed += "$label (pid $($process.Id))"
        }
    }
}

if ($failed.Count -gt 0) {
    # What is still running, and a container that would not stop, so that stop.cmd can try again.
    Save-DemoState (@($entries | Where-Object { Get-EntryProcess $_ }) + @($keep))
    Stop-Demo -Problem ('Some of the demo is still running: ' + ($failed -join '; ') + '.') -Fix @(
        'Close it from Task Manager, or run stop.cmd again.')
}

Remove-Item -LiteralPath $script:DemoStateFile -Force -ErrorAction SilentlyContinue

$others = @(Get-Process -Name 'ScreenTail.Service', 'ScreenTail.UI' -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) {
    Write-Note 'Still running, and not started by start.cmd (left alone):'
    foreach ($process in $others) {
        Write-Note "  pid $($process.Id)  $($process.ProcessName)"
    }
}

Write-Host ''
Write-Host 'Stopped. Your sessions and drafts are kept; start.cmd brings everything back.' -ForegroundColor Green
