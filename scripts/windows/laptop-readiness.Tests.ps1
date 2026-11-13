<#
.SYNOPSIS
    Runs laptop-readiness.ps1's decision and text on made-up readings and fails if a slow laptop goes
    unwarned, a fine one is warned about, an unknown reads as anything but "unknown", or a probe throws;
    and if the battery stop lets a laptop on battery or too low on charge through, or stops one that
    has enough charge or no battery at all.

.DESCRIPTION
    laptop-readiness.ps1 is dot-sourced, which defines its functions and measures nothing. Every case
    builds a reading by hand, so no case depends on the machine running it: this file runs the same on
    Windows PowerShell 5.1, on the hosted runner, and under pwsh on any OS. No Pester, no network.

    The scripts job runs check-demo-stopped.Tests.ps1, which runs this file. ASCII only.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$library = Join-Path $PSScriptRoot 'laptop-readiness.ps1'
if (-not (Test-Path $library)) { Write-Host "::error::No readiness script at $library."; exit 1 }
. $library

# A laptop that is fine: on AC, Balanced, no saver, not held back, nearly idle.
function New-FineReading {
    $r = New-ReadinessReading
    $r.OnBattery = $false
    $r.HasBattery = $true
    $r.BatteryPercent = 100
    $r.PlanName = 'Balanced'
    $r.PowerMode = 'Best performance'
    $r.SaverOn = $false
    $r.ClockPercent = 45
    $r.ClockLimitPercent = 100
    $r.BusyPercent = 6
    $r.SampleSeconds = 3
    $r.Processes = @(
        [pscustomobject]@{ Name = 'svchost'; Percent = 2.4 },
        [pscustomobject]@{ Name = 'msmpeng'; Percent = 1.1 },
        [pscustomobject]@{ Name = 'explorer'; Percent = 0.6 },
        [pscustomobject]@{ Name = 'runner.listener'; Percent = 0.4 },
        [pscustomobject]@{ Name = 'dwm'; Percent = 0.3 },
        [pscustomobject]@{ Name = 'conhost'; Percent = 0.1 }
    )
    return $r
}

$bad = 0
$n = 0
function Test-Case {
    param([string]$Name, [scriptblock]$Check)
    $script:n++
    $ok = $false
    $detail = ''
    try {
        $ok = [bool](& $Check)
    }
    catch {
        $detail = " Threw: $($_.Exception.Message)"
    }
    if ($ok) {
        Write-Host "ok   $Name"
    }
    else {
        $script:bad++
        Write-Host "::error file=scripts/windows/laptop-readiness.ps1::$Name.$detail"
    }
}

function Rules($Reading) {
    return @(@(Get-ReadinessWarnings -Reading $Reading) | ForEach-Object { $_.Rule })
}

Test-Case 'a laptop that is fine gets no warning' {
    $report = Get-ReadinessReport -Reading (New-FineReading)
    (@(Rules (New-FineReading)).Count -eq 0) -and ($null -eq $report.Warning)
}

# --- The battery stop: the one thing here that fails the job ----------------------------------------

function Stop-Of($Reading) {
    return (Get-ReadinessStop -Reading $Reading)
}

Test-Case 'a laptop that is fine is not stopped' {
    $null -eq (Stop-Of (New-FineReading))
}

Test-Case 'a laptop on battery is stopped with its charge, told to plug in and re-run' {
    $r = New-FineReading; $r.OnBattery = $true; $r.BatteryPercent = 42
    $s = Stop-Of $r
    ($null -ne $s) -and $s.Contains('on battery (42% left)') -and $s.Contains('plug it in') -and $s.Contains('re-run this job')
}

Test-Case 'a laptop on battery is stopped even when it is full' {
    $r = New-FineReading; $r.OnBattery = $true; $r.BatteryPercent = 100
    $null -ne (Stop-Of $r)
}

Test-Case 'a laptop on battery whose charge is unknown is still stopped' {
    $r = New-FineReading; $r.OnBattery = $true; $r.BatteryPercent = $null
    $s = Stop-Of $r
    ($null -ne $s) -and $s.Contains('on battery') -and -not $s.Contains('unknown') -and $s.Contains('plug it in')
}

Test-Case 'a laptop on AC with too little charge is stopped and told to let it charge to the threshold' {
    $r = New-FineReading; $r.BatteryPercent = 18
    $s = Stop-Of $r
    ($null -ne $s) -and $s.Contains('18%') -and $s.Contains("let it charge to at least $($script:ReadinessMinBatteryPercent)%") -and $s.Contains('re-run this job')
}

Test-Case 'the low-charge threshold is 30%: 29% on AC is stopped, 30% is not' {
    $low = New-FineReading; $low.BatteryPercent = 29
    $edge = New-FineReading; $edge.BatteryPercent = 30
    ($script:ReadinessMinBatteryPercent -eq 30) -and ($null -ne (Stop-Of $low)) -and ($null -eq (Stop-Of $edge))
}

Test-Case 'a laptop on AC with enough charge is not stopped' {
    $r = New-FineReading; $r.BatteryPercent = 64
    $null -eq (Stop-Of $r)
}

Test-Case 'a desktop with no battery is never stopped' {
    $r = New-FineReading; $r.HasBattery = $false; $r.BatteryPercent = $null
    $odd = New-FineReading; $odd.HasBattery = $false; $odd.BatteryPercent = 0
    ($null -eq (Stop-Of $r)) -and ($null -eq (Stop-Of $odd))
}

Test-Case 'an unknown power source, or an unknown charge on AC, is never a stop' {
    $unknown = New-ReadinessReading
    $ac = New-FineReading; $ac.BatteryPercent = $null
    ($null -eq (Stop-Of $unknown)) -and ($null -eq (Stop-Of $ac))
}

Test-Case 'the stop is one plain ASCII sentence that names nothing but the power' {
    $r = New-FineReading; $r.OnBattery = $true; $r.BatteryPercent = 12
    $r.Processes = @([pscustomobject]@{ Name = 'Quarterly invoice - Excel'; Percent = 40 })
    $low = New-FineReading; $low.BatteryPercent = 12; $low.Processes = $r.Processes
    $all = @((Stop-Of $r), (Stop-Of $low))
    $ok = $true
    foreach ($s in $all) {
        if ($s.Contains("`n") -or $s.Contains('invoice') -or $s.Contains('::')) { $ok = $false }
        if ($s.ToCharArray() | Where-Object { [int]$_ -gt 127 }) { $ok = $false }
        if (-not $s.EndsWith('.')) { $ok = $false }
    }
    $ok
}

Test-Case 'on battery is no longer a readiness warning, since the stop says it' {
    $r = New-FineReading; $r.OnBattery = $true; $r.BatteryPercent = 42
    -not ((Rules $r) -contains 'battery')
}

Test-Case 'a given power source reads as the probe would: battery, AC and no battery' {
    $b = New-ReadinessPower -Source 'Battery' -Percent 42
    $a = New-ReadinessPower -Source 'AC' -Percent 18
    $d = New-ReadinessPower -Source 'NoBattery'
    ($b.OnBattery -eq $true) -and ($b.HasBattery -eq $true) -and ($b.Percent -eq 42) -and
    ($a.OnBattery -eq $false) -and ($a.HasBattery -eq $true) -and ($a.Percent -eq 18) -and
    ($d.OnBattery -eq $false) -and ($d.HasBattery -eq $false) -and ($null -eq $d.Percent)
}

Test-Case 'Invoke-LaptopPowerCheck stops on the power it is given and passes a desktop' {
    $stop = Invoke-LaptopPowerCheck -Power (New-ReadinessPower -Source 'Battery' -Percent 50)
    $pass = Invoke-LaptopPowerCheck -Power (New-ReadinessPower -Source 'NoBattery')
    ($null -ne $stop) -and $stop.Contains('on battery (50% left)') -and ($null -eq $pass)
}

Test-Case 'energy saver on is warned about' {
    $r = New-FineReading; $r.SaverOn = $true
    $report = Get-ReadinessReport -Reading $r
    ((Rules $r) -contains 'saver') -and $report.Warning.Contains('energy saver is on') -and $report.Warning.Contains('turn energy saver off')
}

Test-Case 'power mode Best power efficiency is warned about and told Best performance' {
    $r = New-FineReading; $r.PowerMode = 'Best power efficiency'
    $report = Get-ReadinessReport -Reading $r
    ((Rules $r) -contains 'saver') -and $report.Warning.Contains('set power mode to Best performance')
}

Test-Case 'a CPU held below 70% of its maximum clock is warned about' {
    $r = New-FineReading; $r.ClockLimitPercent = 43
    $report = Get-ReadinessReport -Reading $r
    ((Rules $r) -contains 'clock') -and $report.Warning.Contains('held to 43%') -and $report.Warning.Contains('Best performance')
}

Test-Case 'a CPU held to exactly 70% is not' {
    $r = New-FineReading; $r.ClockLimitPercent = 70
    -not ((Rules $r) -contains 'clock')
}

Test-Case 'an idle CPU clocked down is not warned about while its limit is known and full' {
    $r = New-FineReading; $r.ClockPercent = 20; $r.ClockLimitPercent = 100
    -not ((Rules $r) -contains 'clock')
}

Test-Case 'without the limit counter the current clock decides' {
    $low = New-FineReading; $low.ClockLimitPercent = $null; $low.ClockPercent = 55
    $high = New-FineReading; $high.ClockLimitPercent = $null; $high.ClockPercent = 95
    ((Rules $low) -contains 'clock') -and -not ((Rules $high) -contains 'clock')
}

Test-Case 'background CPU above 25% busy with Windows Update says wait for Windows Update' {
    $r = New-FineReading; $r.BusyPercent = 41
    $r.Processes = @($r.Processes) + @([pscustomobject]@{ Name = 'TiWorker'; Percent = 22 })
    $report = Get-ReadinessReport -Reading $r
    ((Rules $r) -contains 'busy') -and $report.Warning.Contains('41% of the CPU is busy') -and $report.Warning.Contains('wait for Windows Update to finish') -and (($report.Lines -join "`n").Contains('Windows Update busy (TiWorker 22%)'))
}

Test-Case 'background CPU above 25% busy with a Defender scan says wait for the scan' {
    $r = New-FineReading; $r.BusyPercent = 33
    $r.Processes = @([pscustomobject]@{ Name = 'msmpeng'; Percent = 28 })
    $report = Get-ReadinessReport -Reading $r
    $report.Warning.Contains('wait for the Defender scan to finish') -and (($report.Lines -join "`n").Contains('Defender scanning (msmpeng 28%)'))
}

Test-Case 'background CPU above 25% busy with something else names the busiest process' {
    $r = New-FineReading; $r.BusyPercent = 60
    $r.Processes = @([pscustomobject]@{ Name = 'chrome'; Percent = 51 }, [pscustomobject]@{ Name = 'svchost'; Percent = 3 })
    $report = Get-ReadinessReport -Reading $r
    $report.Warning.Contains('close chrome or wait for it to finish')
}

Test-Case 'exactly 25% busy is not warned about' {
    $r = New-FineReading; $r.BusyPercent = 25
    -not ((Rules $r) -contains 'busy')
}

Test-Case 'a quiet Windows Update process is not called busy' {
    $r = New-FineReading
    $r.Processes = @($r.Processes) + @([pscustomobject]@{ Name = 'usocoreworker'; Percent = 0.5 })
    (Get-ReadinessReport -Reading $r).Lines -contains '  Installers:   Windows Update quiet; Defender quiet'
}

Test-Case 'several problems make one warning with every fix once' {
    $r = New-FineReading; $r.SaverOn = $true; $r.PowerMode = 'Best power efficiency'; $r.ClockLimitPercent = 50; $r.BusyPercent = 30
    $report = Get-ReadinessReport -Reading $r
    $w = $report.Warning
    (@($report.Warnings).Count -eq 4) -and $w.Contains('turn energy saver off') -and $w.Contains('Best performance') -and $w.Contains('Warning only') -and -not $w.Contains("`n")
}

Test-Case 'the busiest five are listed, most first' {
    $line = @((Get-ReadinessReport -Reading (New-FineReading)).Lines | Where-Object { $_ -like '  Busiest:*' })[0]
    $line -eq '  Busiest:      svchost 2%, msmpeng 1%, explorer 1%, runner.listener 0%, dwm 0%'
}

Test-Case 'a reading where every probe failed prints unknown everywhere and warns about nothing' {
    $r = New-ReadinessReading
    $report = Get-ReadinessReport -Reading $r
    $text = $report.Lines -join "`n"
    ($null -eq $report.Warning) -and $text.Contains('Power source: unknown') -and $text.Contains('Power plan:   unknown; power mode unknown; energy saver unknown') -and $text.Contains('CPU clock:    unknown') -and $text.Contains('CPU busy:     unknown') -and $text.Contains('Busiest:      unknown') -and $text.Contains('Installers:   unknown')
}

Test-Case 'a desktop with no battery reads AC (no battery)' {
    $r = New-FineReading; $r.HasBattery = $false; $r.BatteryPercent = $null
    (Get-ReadinessReport -Reading $r).Lines -contains '  Power source: AC (no battery)'
}

Test-Case 'a probe that throws reads as unknown and does not throw' {
    $value = Invoke-ReadinessProbe { throw 'no such counter' }
    $null -eq $value
}

Test-Case 'the block is ASCII, so the 5.1 console and the summary show it as written' {
    $r = New-FineReading; $r.OnBattery = $true; $r.BusyPercent = 90
    $report = Get-ReadinessReport -Reading $r
    $all = ($report.Lines -join "`n") + $report.Warning
    -not ($all.ToCharArray() | Where-Object { [int]$_ -gt 127 })
}

$summary = Join-Path ([System.IO.Path]::GetTempPath()) ('readiness-summary-' + [guid]::NewGuid().ToString('N') + '.md')
Test-Case 'a slow laptop adds its block and warning to the job summary, after what was there' {
    [System.IO.File]::WriteAllText($summary, "earlier step`n")
    $r = New-FineReading; $r.OnBattery = $true; $r.SaverOn = $true
    $report = Get-ReadinessReport -Reading $r
    Add-ReadinessSummary -Report $report -Path $summary
    $text = [System.IO.File]::ReadAllText($summary)
    $text.StartsWith("earlier step`n### Laptop readiness") -and $text.Contains($report.Warning) -and $text.Contains('  Power source: battery (100% left)')
}
Remove-Item -Force $summary -ErrorAction SilentlyContinue

Test-Case 'Invoke-LaptopReadiness on a fine reading writes no summary' {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('readiness-none-' + [guid]::NewGuid().ToString('N') + '.md')
    Invoke-LaptopReadiness -Reading (New-FineReading) -SummaryPath $path 6>$null
    $written = Test-Path $path
    Remove-Item -Force $path -ErrorAction SilentlyContinue
    -not $written
}

Test-Case 'Invoke-LaptopReadiness on a slow reading warns and writes the summary' {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('readiness-slow-' + [guid]::NewGuid().ToString('N') + '.md')
    $r = New-FineReading; $r.SaverOn = $true
    $printed = (Invoke-LaptopReadiness -Reading $r -SummaryPath $path 6>&1 | Out-String)
    $written = Test-Path $path
    Remove-Item -Force $path -ErrorAction SilentlyContinue
    $written -and $printed.Contains('::warning::This laptop looks slow') -and $printed.Contains('Laptop readiness (warns only')
}

if ($bad -gt 0) {
    Write-Host "$bad of $n readiness cases gave the wrong answer."
    exit 1
}
Write-Host "All $n readiness cases gave the right answer."
exit 0
