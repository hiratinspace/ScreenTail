<#
.SYNOPSIS
    Says, in the first seconds of a laptop job, whether this machine is in a state to run it at full
    speed: power source, power plan and saver, CPU clock, background CPU and busy installers.
    Dot-sourced by check-demo-stopped.ps1 (and its tests), never run alone.

.DESCRIPTION
    In November 2026 the capabilities job timed out because the laptop itself was about 2.3x slow from the
    first run of the session (OCR median 547 to 626 ms against 240 ms, the service build 68 s against
    28 s), and nothing in the log said why. That was the machine's state (power, heat or background
    work), not the code. This prints a short block saying what the machine is doing, and a ::warning::
    naming the likely fix when it looks slow. The block never fails a job: a slow laptop still runs.

    One thing does fail it, before the block: the battery stop (Get-ReadinessStop). A laptop job on
    battery runs slow and can run out partway, and the laptop drains even on the charger under load, so
    a run that starts on battery or low on charge dies midway and leaves a pull request red for hours.
    The job stops at its first step instead, with one sentence saying what to do, when:
      - the laptop is on battery, whatever its charge; or
      - it is on the charger with less than 30% charge ($script:ReadinessMinBatteryPercent).
    A machine with no battery, or a power source or charge that cannot be read, is never stopped.

    The warning is raised when any of these hold:
      - energy saver (battery saver) is on, or the power mode is Best power efficiency;
      - the CPU is held below 70% of its maximum clock (the "% Performance Limit" counter, which power
        policy and heat lower; the current clock is used only when that counter is missing, and an
        idle CPU clocks down, so that fallback can warn on a laptop that is fine);
      - more than 25% of the CPU is busy before the tests start.

    Rules for this file:
      - Windows PowerShell 5.1 syntax, and nothing that needs elevation: the runner account is not an
        administrator.
      - Every probe runs through Invoke-ReadinessProbe, so one that fails reads "unknown" and never
        throws. The whole report takes about 4 to 5 seconds, most of it the 3 s CPU sample.
      - The decisions (Get-ReadinessStop, Get-ReadinessWarnings) and the text (Get-ReadinessReport)
        take measured values and touch nothing, so laptop-readiness.Tests.ps1 runs them on fake readings, on any OS.
      - ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.

    docs/dev/windows-test-loop.md ("Before a laptop run") says what to do about each warning.
#>

# --- Thresholds -------------------------------------------------------------------------------------

# Below this charge on the charger, the job stops before it starts. Why 30: on 2026-11-12 the laptop's
# jobs that started on the charger began at 33% to 82% and gained 7 to 9 points each, so 30 stops none
# of them; but under load it can also lose charge on the charger, and a job that starts below 30 and
# loses ten points over its 17 to 30 minutes reaches 20%, where Windows turns energy saver on and the
# laptop slows to a timeout, and then lower, where it sleeps. Raise it if runs still die on the charger.
$script:ReadinessMinBatteryPercent = 30
$script:ReadinessMinClockPercent = 70
$script:ReadinessMaxBusyPercent = 25
$script:ReadinessSampleSeconds = 3
# An installer process using at least this share of the whole machine counts as busy.
$script:ReadinessUpdateBusyPercent = 5
$script:ReadinessDefenderBusyPercent = 10
$script:ReadinessUpdateNames = @('tiworker', 'wuauclt', 'usocoreworker')
$script:ReadinessDefenderNames = @('msmpeng')

# Windows 10 and 11's power mode slider, stored as an overlay on the active plan.
$script:ReadinessPowerModes = @{
    '961cc777-2547-4f9d-8174-7d86181b8a7a' = 'Best power efficiency'
    '00000000-0000-0000-0000-000000000000' = 'Balanced'
    '3af9b8d9-7c97-431d-ad78-34a8bfea439f' = 'Better performance'
    'ded574b5-45a0-4f42-8737-46345c09c238' = 'Best performance'
}

# --- Probes -----------------------------------------------------------------------------------------

# Runs one probe. Whatever it throws, the answer is $null, which the report prints as "unknown".
function Invoke-ReadinessProbe {
    param([scriptblock]$Probe)
    try {
        return (& $Probe)
    }
    catch {
        return $null
    }
}

# A new, empty reading: every value unknown.
function New-ReadinessReading {
    return [pscustomobject]@{
        OnBattery         = $null
        HasBattery        = $null
        BatteryPercent    = $null
        PlanName          = $null
        PowerMode         = $null
        SaverOn           = $null
        ClockPercent      = $null
        ClockLimitPercent = $null
        BusyPercent       = $null
        SampleSeconds     = $null
        Processes         = $null
    }
}

function Get-ReadinessPower {
    $status = Invoke-ReadinessProbe {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $p = [System.Windows.Forms.SystemInformation]::PowerStatus
        $line = [string]$p.PowerLineStatus
        if ($line -ne 'Online' -and $line -ne 'Offline') { return $null }
        $noBattery = (([int]$p.BatteryChargeStatus) -band 128) -ne 0
        $percent = $null
        if (-not $noBattery -and $p.BatteryLifePercent -ge 0 -and $p.BatteryLifePercent -le 1) {
            $percent = [int][math]::Round($p.BatteryLifePercent * 100)
        }
        [pscustomobject]@{ OnBattery = ($line -eq 'Offline'); HasBattery = (-not $noBattery); Percent = $percent }
    }
    if ($null -ne $status) { return $status }

    return Invoke-ReadinessProbe {
        $batteries = @(Get-CimInstance -ClassName Win32_Battery -ErrorAction Stop)
        if ($batteries.Count -eq 0) {
            return [pscustomobject]@{ OnBattery = $false; HasBattery = $false; Percent = $null }
        }
        $b = $batteries[0]
        # BatteryStatus 1 is "discharging"; the others mean the charger is in.
        [pscustomobject]@{ OnBattery = ([int]$b.BatteryStatus -eq 1); HasBattery = $true; Percent = $b.EstimatedChargeRemaining }
    }
}

# A power reading given rather than measured, shaped as Get-ReadinessPower's: Source is Battery, AC or
# NoBattery, Percent the charge (-1 or absent for unknown). check-demo-stopped.ps1's -PowerSource uses
# it, so its tests never depend on the machine running them.
function New-ReadinessPower {
    param([string]$Source, [int]$Percent = -1)
    $p = $null
    if ($Percent -ge 0 -and $Source -ne 'NoBattery') { $p = $Percent }
    switch ($Source) {
        'Battery' { return [pscustomobject]@{ OnBattery = $true; HasBattery = $true; Percent = $p } }
        'AC' { return [pscustomobject]@{ OnBattery = $false; HasBattery = $true; Percent = $p } }
        'NoBattery' { return [pscustomobject]@{ OnBattery = $false; HasBattery = $false; Percent = $null } }
    }
    throw "Unknown power source '$Source': use Battery, AC or NoBattery."
}

# Copies a power reading (Get-ReadinessPower's or New-ReadinessPower's) into a readiness reading.
function Set-ReadinessPower {
    param($Reading, $Power)
    if ($null -eq $Power) { return }
    $Reading.OnBattery = $Power.OnBattery
    $Reading.HasBattery = $Power.HasBattery
    $Reading.BatteryPercent = $Power.Percent
}

function Get-ReadinessPlanName {
    return Invoke-ReadinessProbe {
        $text = (& powercfg.exe /getactivescheme 2>$null | Out-String)
        if ($text -match '\(([^)]+)\)\s*$') { return $Matches[1].Trim() }
        return $null
    }
}

function Get-ReadinessPowerMode {
    param($OnBattery)
    return Invoke-ReadinessProbe {
        $key = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes' -ErrorAction Stop
        $name = 'ActiveOverlayAcPowerScheme'
        if ($OnBattery -eq $true) { $name = 'ActiveOverlayDcPowerScheme' }
        $guid = [string]$key.$name
        if (-not $guid) { return $null }
        $mode = $script:ReadinessPowerModes[$guid.ToLowerInvariant()]
        if ($mode) { return $mode }
        return $null
    }
}

# Energy saver (battery saver before Windows 11) through the WinRT PowerManager, which needs no admin.
# $true, $false, or $null when this Windows cannot say.
function Get-ReadinessSaver {
    return Invoke-ReadinessProbe {
        $manager = [Windows.System.Power.PowerManager, Windows.System.Power, ContentType = WindowsRuntime]
        $status = [string]$manager::EnergySaverStatus
        if ($status -eq 'On') { return $true }
        if ($status -eq 'Off' -or $status -eq 'Disabled') { return $false }
        return $null
    }
}

# One CPU sample over $Seconds: busy %, clock % of maximum, the clock limit, and every process's share
# of the whole machine. The counter names are the English ones; on another display language the
# sample fails and Win32_Processor's coarser figures stand in.
function Get-ReadinessCpu {
    param([int]$Seconds = $script:ReadinessSampleSeconds)

    $result = [pscustomobject]@{ ClockPercent = $null; ClockLimitPercent = $null; BusyPercent = $null; Processes = $null; SampleSeconds = $null }
    $cores = [Environment]::ProcessorCount
    if ($cores -lt 1) { $cores = 1 }

    $samples = Invoke-ReadinessProbe {
        $paths = @(
            '\Processor Information(_Total)\% Processor Performance',
            '\Processor Information(_Total)\% Performance Limit',
            '\Processor(_Total)\% Processor Time',
            '\Process(*)\% Processor Time'
        )
        $set = Get-Counter -Counter $paths -SampleInterval $Seconds -MaxSamples 1 -ErrorAction SilentlyContinue
        if ($null -eq $set) { return $null }
        return @($set.CounterSamples)
    }

    if ($null -ne $samples -and @($samples).Count -gt 0) {
        $result.SampleSeconds = $Seconds
        $byName = @{}
        foreach ($s in @($samples)) {
            $path = ([string]$s.Path).ToLowerInvariant()
            if ($path.EndsWith('processor information(_total)\% processor performance')) {
                $result.ClockPercent = [double]$s.CookedValue
            }
            elseif ($path.EndsWith('processor information(_total)\% performance limit')) {
                $result.ClockLimitPercent = [double]$s.CookedValue
            }
            elseif ($path.EndsWith('\processor(_total)\% processor time')) {
                $result.BusyPercent = [double]$s.CookedValue
            }
            elseif ($path.Contains('\process(')) {
                $name = ([string]$s.InstanceName).ToLowerInvariant() -replace '#\d+$', ''
                if ($name -eq '_total' -or $name -eq 'idle' -or $name -eq '') { continue }
                $share = [double]$s.CookedValue / $cores
                if ($byName.ContainsKey($name)) { $byName[$name] += $share } else { $byName[$name] = $share }
            }
        }
        if ($byName.Count -gt 0) {
            $result.Processes = @($byName.GetEnumerator() | ForEach-Object { [pscustomobject]@{ Name = $_.Key; Percent = $_.Value } })
        }
    }

    if ($null -eq $result.ClockPercent -or $null -eq $result.BusyPercent) {
        $cpu = Invoke-ReadinessProbe { @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop)[0] }
        if ($null -ne $cpu) {
            if ($null -eq $result.ClockPercent -and $cpu.MaxClockSpeed -gt 0) {
                $result.ClockPercent = 100.0 * $cpu.CurrentClockSpeed / $cpu.MaxClockSpeed
            }
            if ($null -eq $result.BusyPercent -and $null -ne $cpu.LoadPercentage) {
                $result.BusyPercent = [double]$cpu.LoadPercentage
            }
        }
    }
    return $result
}

# Every probe, into one reading. Windows only; each value it cannot get stays $null. A power reading
# already taken (the battery stop's) is used as it is rather than read again.
function Measure-LaptopReadiness {
    param($Power)
    $reading = New-ReadinessReading
    if ($null -eq $Power) { $Power = Get-ReadinessPower }
    Set-ReadinessPower -Reading $reading -Power $Power
    $reading.PlanName = Get-ReadinessPlanName
    $reading.PowerMode = Get-ReadinessPowerMode -OnBattery $reading.OnBattery
    $reading.SaverOn = Get-ReadinessSaver
    $cpu = Invoke-ReadinessProbe { Get-ReadinessCpu }
    if ($null -ne $cpu) {
        $reading.ClockPercent = $cpu.ClockPercent
        $reading.ClockLimitPercent = $cpu.ClockLimitPercent
        $reading.BusyPercent = $cpu.BusyPercent
        $reading.SampleSeconds = $cpu.SampleSeconds
        $reading.Processes = $cpu.Processes
    }
    return $reading
}

# --- Decision and text (pure: measured values in, words out) ----------------------------------------

function Format-ReadinessPercent {
    param($Value)
    if ($null -eq $Value) { return 'unknown' }
    return ('{0}%' -f [int][math]::Round([double]$Value))
}

# The busiest processes, most first.
function Get-ReadinessTopProcesses {
    param($Processes, [int]$Count = 5)
    if ($null -eq $Processes) { return @() }
    return @(@($Processes) | Sort-Object -Property @{ Expression = { [double]$_.Percent }; Descending = $true }, Name | Select-Object -First $Count)
}

# The busiest process among Names using at least Threshold % of the machine, or $null.
function Get-ReadinessBusyProcess {
    param($Processes, [string[]]$Names, [double]$Threshold)
    if ($null -eq $Processes) { return $null }
    $busy = @(@($Processes) | Where-Object { ($Names -contains ([string]$_.Name).ToLowerInvariant()) -and [double]$_.Percent -ge $Threshold })
    if ($busy.Count -eq 0) { return $null }
    return (Get-ReadinessTopProcesses -Processes $busy -Count 1)[0]
}

# The one sentence that stops the job, or $null to let it run. Plain words about the power only, never
# anything else on the machine (INV-10). An unknown is never a stop, and neither is a machine with no
# battery.
function Get-ReadinessStop {
    param($Reading)
    if ($Reading.HasBattery -eq $false) { return $null }
    if ($Reading.OnBattery -eq $true) {
        $left = ''
        if ($null -ne $Reading.BatteryPercent) { $left = " ($(Format-ReadinessPercent $Reading.BatteryPercent) left)" }
        return "This laptop is running on battery$left, which can run out partway through this job: plug it in, then re-run this job."
    }
    if ($Reading.OnBattery -eq $false -and $null -ne $Reading.BatteryPercent -and [double]$Reading.BatteryPercent -lt $script:ReadinessMinBatteryPercent) {
        return "This laptop's battery is at $(Format-ReadinessPercent $Reading.BatteryPercent), too low to be sure it lasts this job even on the charger: let it charge to at least $($script:ReadinessMinBatteryPercent)%, then re-run this job."
    }
    return $null
}

# Reads the power (unless it is given) and returns Get-ReadinessStop's sentence, or $null. Never throws:
# a reading that fails is unknown, and an unknown lets the job run.
function Invoke-LaptopPowerCheck {
    param($Power)
    try {
        if ($null -eq $Power) { $Power = Get-ReadinessPower }
        $reading = New-ReadinessReading
        Set-ReadinessPower -Reading $reading -Power $Power
        return (Get-ReadinessStop -Reading $reading)
    }
    catch {
        return $null
    }
}

# What looks wrong with this reading, each with its likely fix. Empty when nothing does, or when the
# values that would say are unknown: an unknown is never a warning.
function Get-ReadinessWarnings {
    param($Reading)

    $warnings = New-Object System.Collections.ArrayList
    $update = Get-ReadinessBusyProcess -Processes $Reading.Processes -Names $script:ReadinessUpdateNames -Threshold $script:ReadinessUpdateBusyPercent
    $defender = Get-ReadinessBusyProcess -Processes $Reading.Processes -Names $script:ReadinessDefenderNames -Threshold $script:ReadinessDefenderBusyPercent

    # On battery is not a warning: Get-ReadinessStop stops the job before this block is printed.

    if ($Reading.SaverOn -eq $true) {
        [void]$warnings.Add([pscustomobject]@{ Rule = 'saver'; Problem = 'energy saver is on'; Fix = 'turn energy saver off' })
    }
    if ($Reading.PowerMode -eq 'Best power efficiency') {
        [void]$warnings.Add([pscustomobject]@{ Rule = 'saver'; Problem = 'power mode is Best power efficiency'; Fix = 'set power mode to Best performance' })
    }

    $limit = $Reading.ClockLimitPercent
    if ($null -ne $limit) {
        if ([double]$limit -lt $script:ReadinessMinClockPercent) {
            [void]$warnings.Add([pscustomobject]@{ Rule = 'clock'; Problem = "the CPU is held to $(Format-ReadinessPercent $limit) of its maximum clock"; Fix = 'set power mode to Best performance and let it cool (lid open, vents clear)' })
        }
    }
    elseif ($null -ne $Reading.ClockPercent -and [double]$Reading.ClockPercent -lt $script:ReadinessMinClockPercent) {
        [void]$warnings.Add([pscustomobject]@{ Rule = 'clock'; Problem = "the CPU runs at $(Format-ReadinessPercent $Reading.ClockPercent) of its maximum clock"; Fix = 'set power mode to Best performance and let it cool (lid open, vents clear)' })
    }

    if ($null -ne $Reading.BusyPercent -and [double]$Reading.BusyPercent -gt $script:ReadinessMaxBusyPercent) {
        $fixes = @()
        if ($null -ne $update) { $fixes += 'wait for Windows Update to finish' }
        if ($null -ne $defender) { $fixes += 'wait for the Defender scan to finish' }
        if ($fixes.Count -eq 0) {
            $top = @(Get-ReadinessTopProcesses -Processes $Reading.Processes -Count 1)
            if ($top.Count -gt 0) { $fixes += "close $($top[0].Name) or wait for it to finish" }
            else { $fixes += 'wait for the background work to finish' }
        }
        [void]$warnings.Add([pscustomobject]@{ Rule = 'busy'; Problem = "$(Format-ReadinessPercent $Reading.BusyPercent) of the CPU is busy before the tests start"; Fix = ($fixes -join ', ') })
    }

    return @($warnings.ToArray())
}

# The block to print, and the one ::warning:: line (or $null). Lines are plain ASCII.
function Get-ReadinessReport {
    param($Reading)

    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add('Laptop readiness (warns only, never fails the job):')

    $power = 'unknown'
    if ($Reading.OnBattery -eq $true) { $power = 'battery' }
    elseif ($Reading.OnBattery -eq $false) { $power = 'AC' }
    if ($null -ne $Reading.OnBattery) {
        if ($Reading.HasBattery -eq $false) { $power += ' (no battery)' }
        elseif ($null -ne $Reading.BatteryPercent -and $Reading.OnBattery -eq $true) { $power += " ($(Format-ReadinessPercent $Reading.BatteryPercent) left)" }
        elseif ($null -ne $Reading.BatteryPercent) { $power += " (battery $(Format-ReadinessPercent $Reading.BatteryPercent))" }
    }
    [void]$lines.Add("  Power source: $power")

    $plan = 'unknown'
    if ($Reading.PlanName) { $plan = [string]$Reading.PlanName }
    $mode = 'unknown'
    if ($Reading.PowerMode) { $mode = [string]$Reading.PowerMode }
    $saver = 'unknown'
    if ($Reading.SaverOn -eq $true) { $saver = 'on' } elseif ($Reading.SaverOn -eq $false) { $saver = 'off' }
    [void]$lines.Add("  Power plan:   $plan; power mode $mode; energy saver $saver")

    $clock = 'unknown'
    if ($null -ne $Reading.ClockPercent) { $clock = "$(Format-ReadinessPercent $Reading.ClockPercent) of maximum now" }
    if ($null -ne $Reading.ClockLimitPercent) { $clock += ", held to $(Format-ReadinessPercent $Reading.ClockLimitPercent)" }
    [void]$lines.Add("  CPU clock:    $clock")

    $busy = Format-ReadinessPercent $Reading.BusyPercent
    if ($null -ne $Reading.BusyPercent -and $null -ne $Reading.SampleSeconds) { $busy += " over $($Reading.SampleSeconds) s" }
    [void]$lines.Add("  CPU busy:     $busy")

    $top = @(Get-ReadinessTopProcesses -Processes $Reading.Processes -Count 5)
    $busiest = 'unknown'
    if ($top.Count -gt 0) { $busiest = (($top | ForEach-Object { "$($_.Name) $(Format-ReadinessPercent $_.Percent)" }) -join ', ') }
    [void]$lines.Add("  Busiest:      $busiest")

    $installers = 'unknown'
    if ($null -ne $Reading.Processes) {
        $update = Get-ReadinessBusyProcess -Processes $Reading.Processes -Names $script:ReadinessUpdateNames -Threshold $script:ReadinessUpdateBusyPercent
        $defender = Get-ReadinessBusyProcess -Processes $Reading.Processes -Names $script:ReadinessDefenderNames -Threshold $script:ReadinessDefenderBusyPercent
        $u = 'Windows Update quiet'
        if ($null -ne $update) { $u = "Windows Update busy ($($update.Name) $(Format-ReadinessPercent $update.Percent))" }
        $d = 'Defender quiet'
        if ($null -ne $defender) { $d = "Defender scanning ($($defender.Name) $(Format-ReadinessPercent $defender.Percent))" }
        $installers = "$u; $d"
    }
    [void]$lines.Add("  Installers:   $installers")

    $warnings = @(Get-ReadinessWarnings -Reading $Reading)
    $warning = $null
    if ($warnings.Count -gt 0) {
        $problems = ($warnings | ForEach-Object { $_.Problem }) -join '; '
        $fixes = @()
        foreach ($w in $warnings) { if ($fixes -notcontains $w.Fix) { $fixes += $w.Fix } }
        $warning = "This laptop looks slow, so the job may run long or time out: $problems. Likely fix: $($fixes -join '; '). Then re-run the job. (Warning only: the job runs anyway.)"
    }

    return [pscustomobject]@{ Lines = @($lines.ToArray()); Warning = $warning; Warnings = $warnings }
}

# Appends the block and the warning to the job summary as Markdown. UTF-8 without a BOM.
function Add-ReadinessSummary {
    param($Report, [string]$Path)
    if (-not $Path) { return }
    $text = "### Laptop readiness`n`n" + $Report.Warning + "`n`n" + '```' + "`n" + ($Report.Lines -join "`n") + "`n" + '```' + "`n"
    [System.IO.File]::AppendAllText($Path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# Measures (unless a reading is given), prints the block, and when anything looks slow prints one
# ::warning:: and adds the block to the job summary. Never throws. Power, when given, is the battery
# stop's reading, used rather than read again.
function Invoke-LaptopReadiness {
    param($Reading, $Power, [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY)
    try {
        if ($null -eq $Reading) { $Reading = Measure-LaptopReadiness -Power $Power }
        $report = Get-ReadinessReport -Reading $Reading
        foreach ($line in $report.Lines) { Write-Host $line }
        if ($report.Warning) {
            Write-Host "::warning::$($report.Warning)"
            Invoke-ReadinessProbe { Add-ReadinessSummary -Report $report -Path $SummaryPath } | Out-Null
        }
    }
    catch {
        Write-Host "Laptop readiness: unknown ($($_.Exception.Message))"
    }
}
