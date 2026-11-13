<#
.SYNOPSIS
    Runs check-demo-stopped.ps1 against made-up demo states and fails if it gives the wrong answer.

.DESCRIPTION
    The hardware jobs start with check-demo-stopped.ps1, and a check that passes everything looks the
    same as one that works until the demo is up during a run. Each case writes a state file the way
    start.ps1 writes one, runs the check in a child process (it exits, so it cannot be dot-sourced) and
    checks the exit code and the line. A live process is this script's own: its id, name and start time
    are what start.ps1 would have recorded for it. No Pester, no network: Windows PowerShell 5.1 and a
    temp folder.

    The cases pass -NoReadiness, so each stays quick, except two: one runs the readiness block for real
    on this machine and checks that the block prints and the exit code is still the demo check's, and
    one checks that a laptop on battery stops before the block. Every case gives the power source
    (-PowerSource, -BatteryPercent) rather than reading this machine's, so the battery stop answers the
    same here as on the laptop.
    The block's decisions are tested on made-up readings in laptop-readiness.Tests.ps1, run from here.

    ASCII only, for the same reason as the check itself.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$check = Join-Path $PSScriptRoot 'check-demo-stopped.ps1'
if (-not (Test-Path $check)) { Write-Host "::error::No check at $check."; exit 1 }

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("check-demo-stopped-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null

# A name no process has, so the process half of the check is quiet unless a case says otherwise.
$nobody = 'ScreenTail.NoSuchProcess'
$self = Get-Process -Id $PID
$selfTicks = $self.StartTime.ToUniversalTime().Ticks
$busy = 'The ScreenTail demo is running on this laptop. Run scripts\windows\demo\stop.cmd, then re-run this job.'
$idle = 'The ScreenTail demo is not running.'

function Write-State {
    param([string]$Path, [object[]]$Entries)
    $document = [ordered]@{ Written = (Get-Date).ToString('o'); Entries = @($Entries) }
    [System.IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $document -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
}

function Entry {
    param([string]$Role, [int]$Id, [string]$Name, [long]$Ticks)
    [pscustomobject]@{ Role = $Role; Kind = 'process'; Id = $Id; Name = $Name; StartTicks = $Ticks }
}

# A case's session: -1 leaves -SessionId off, so the check uses its own (the job's); the other is any other.
$here = -1
$elsewhere = $self.SessionId + 1000
# The process line names each process and its id: this script's own powershell is one of them.
$unrecorded = "$($self.ProcessName) (pid $PID)"
$closeIt = 'was not started by the demo, so stop.cmd will not stop it. Close it, then re-run this job.'
# The battery stop's lines (laptop-readiness.ps1, Get-ReadinessStop).
$onBattery = '::error::This laptop is running on battery (42% left), which can run out partway through this job: plug it in, then re-run this job.'
$lowCharge = '::error::This laptop''s battery is at 18%, too low to be sure it lasts this job even on the charger: let it charge to at least 30%, then re-run this job.'

$cases = @(
    [pscustomobject]@{ Name = 'no state file and no ScreenTail process passes'; Entries = $null; Names = @($nobody); Session = $here; ExpectExit = 0; MustSay = @($idle) },
    [pscustomobject]@{ Name = 'a state file with nothing in it passes'; Entries = @(); Names = @($nobody); Session = $here; ExpectExit = 0; MustSay = @($idle) },
    [pscustomobject]@{ Name = 'a live capture service that start.cmd launched fails'; Entries = @((Entry 'service' $PID $self.ProcessName $selfTicks)); Names = @($nobody); Session = $here; ExpectExit = 1; MustSay = @($busy) },
    [pscustomobject]@{ Name = 'a recorded process that has since exited passes'; Entries = @((Entry 'service' $PID $self.ProcessName ($selfTicks - 36000000000))); Names = @($nobody); Session = $here; ExpectExit = 0; MustSay = @($idle) },
    [pscustomobject]@{ Name = 'a recorded id that now belongs to another program passes'; Entries = @((Entry 'service' $PID 'ScreenTail.Service' $selfTicks)); Names = @($nobody); Session = $here; ExpectExit = 0; MustSay = @($idle) },
    [pscustomobject]@{ Name = 'a ScreenTail process the demo did not start is named with its id and is to be closed'; Entries = $null; Names = @($self.ProcessName); Session = $here; ExpectExit = 1; MustSay = @($unrecorded, $closeIt) },
    [pscustomobject]@{ Name = 'a ScreenTail process in another desktop session passes'; Entries = $null; Names = @($self.ProcessName); Session = $elsewhere; ExpectExit = 0; MustSay = @($idle) },
    [pscustomobject]@{ Name = 'the demo running says stop.cmd, even with a process beside it'; Entries = @((Entry 'service' $PID $self.ProcessName $selfTicks)); Names = @($self.ProcessName); Session = $here; ExpectExit = 1; MustSay = @($busy) },
    [pscustomobject]@{ Name = 'a passing check prints the readiness block and still exits 0, whatever it finds'; Entries = $null; Names = @($nobody); Session = $here; ExpectExit = 0; MustSay = @($idle, 'Laptop readiness (warns only, never fails the job):', '  Power source: AC (no battery)', '  CPU busy:     '); Readiness = $true },
    [pscustomobject]@{ Name = 'a laptop on battery stops with one line saying plug it in'; Entries = $null; Names = @($nobody); Session = $here; ExpectExit = 1; MustSay = @($idle, $onBattery); Power = @('-PowerSource', 'Battery', '-BatteryPercent', '42') },
    [pscustomobject]@{ Name = 'a laptop on AC with too little charge stops with one line saying let it charge'; Entries = $null; Names = @($nobody); Session = $here; ExpectExit = 1; MustSay = @($idle, $lowCharge); Power = @('-PowerSource', 'AC', '-BatteryPercent', '18') },
    [pscustomobject]@{ Name = 'a laptop on AC with enough charge passes'; Entries = $null; Names = @($nobody); Session = $here; ExpectExit = 0; MustSay = @($idle); MustNotSay = @('::error::'); Power = @('-PowerSource', 'AC', '-BatteryPercent', '64') },
    [pscustomobject]@{ Name = 'a desktop with no battery passes'; Entries = $null; Names = @($nobody); Session = $here; ExpectExit = 0; MustSay = @($idle); MustNotSay = @('::error::'); Power = @('-PowerSource', 'NoBattery') },
    [pscustomobject]@{ Name = 'a laptop on battery stops before the readiness block, which it does not need'; Entries = $null; Names = @($nobody); Session = $here; ExpectExit = 1; MustSay = @($onBattery); MustNotSay = @('Laptop readiness'); Power = @('-PowerSource', 'Battery', '-BatteryPercent', '42'); Readiness = $true },
    [pscustomobject]@{ Name = 'the demo running is said first, even on battery'; Entries = @((Entry 'service' $PID $self.ProcessName $selfTicks)); Names = @($nobody); Session = $here; ExpectExit = 1; MustSay = @($busy); MustNotSay = @('on battery'); Power = @('-PowerSource', 'Battery', '-BatteryPercent', '42') }
)

$bad = 0
$n = 0
foreach ($case in $cases) {
    $n++
    $state = Join-Path $root "state-$n.json"
    if ($null -ne $case.Entries) {
        Write-State -Path $state -Entries $case.Entries
    }

    # The session is passed only when it is not the check's own, which is how the workflow calls it.
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $check, '-StateFile', $state, '-Names', ($case.Names -join ','))
    if ($case.Session -ge 0) {
        $arguments += @('-SessionId', [string]$case.Session)
    }
    if (-not $case.Readiness) {
        $arguments += '-NoReadiness'
    }
    # The power is given, never measured, so a case answers the same on a laptop on battery as on the
    # hosted runner, which has none.
    if ($case.Power) {
        $arguments += $case.Power
    }
    else {
        $arguments += @('-PowerSource', 'NoBattery')
    }
    $output = & powershell.exe @arguments 2>&1 | Out-String
    $exit = $LASTEXITCODE

    $missing = @($case.MustSay | Where-Object { -not $output.Contains($_) })
    $unwanted = @(@($case.MustNotSay) | Where-Object { $_ -and $output.Contains($_) })
    $ok = ($exit -eq $case.ExpectExit) -and ($missing.Count -eq 0) -and ($unwanted.Count -eq 0)
    if ($ok) {
        Write-Host "ok   $($case.Name) (exit $exit)"
    } else {
        $bad++
        Write-Host "::error file=scripts/windows/check-demo-stopped.ps1::$($case.Name): expected exit $($case.ExpectExit) saying '$($case.MustSay -join ''' and ''')', got exit $exit."
        Write-Host $output
    }
}

Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue

# The demo report's cases (demo\report.Tests.ps1) run from here too: the scripts job runs this file, and
# a case nobody runs proves nothing. A child process, so the report's functions stay out of this one.
$reportTests = Join-Path $PSScriptRoot 'demo\report.Tests.ps1'
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $reportTests
$reportExit = $LASTEXITCODE

# The readiness block's decisions, on made-up readings (laptop-readiness.Tests.ps1).
$readinessTests = Join-Path $PSScriptRoot 'laptop-readiness.Tests.ps1'
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $readinessTests
$readinessExit = $LASTEXITCODE

if ($bad -gt 0) {
    Write-Host "$bad of $n demo check cases gave the wrong answer."
}
else {
    Write-Host "All $n demo check cases gave the right answer."
}
if ($reportExit -ne 0) {
    Write-Host "::error file=scripts/windows/demo/report.Tests.ps1::The demo report's cases failed (exit $reportExit)."
}
if ($readinessExit -ne 0) {
    Write-Host "::error file=scripts/windows/laptop-readiness.Tests.ps1::The readiness cases failed (exit $readinessExit)."
}
if ($bad -gt 0 -or $reportExit -ne 0 -or $readinessExit -ne 0) { exit 1 }
exit 0
