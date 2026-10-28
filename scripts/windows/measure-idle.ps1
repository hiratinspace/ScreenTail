<#
.SYNOPSIS
    Measures the running ScreenTail service and UI: processor time and memory, whole process (ST-031).

.DESCRIPTION
    The laptop's CI tests (client/ScreenTail.Tests.Windows/Performance) measure what ScreenTail's own
    code adds to a process, inside the test process, because starting the real service on the laptop
    would open the demo's store, key and pipe. What they cannot see is the whole of each process: the
    .NET runtime, the loaded assemblies, SQLCipher, the OCR engine, and the speech model once a session
    has loaded it. This reads those from the two real processes while they run, for example during a
    demo rehearsal (docs/dev/demo.md).

    Run it twice: once with no session (the pill says "Not recording") after ScreenTail has been up for
    a minute, and once while a session records. Say which in the -Label.

    Read-only. It reads processor time and memory counters through Get-Process and nothing else: no
    window titles, no command lines, nothing a technician typed or saw. The output is a Markdown table
    for docs/dev/performance.md.

    Kept to ASCII on purpose: Windows PowerShell 5.1 reads a .ps1 with no byte-order mark as ANSI, and
    ci.yml parse-checks every script here against 5.1 and refuses non-ASCII.

.PARAMETER Label
    What was happening, for the table: "idle", "recording", "recording, speaking".

.PARAMETER Seconds
    How long to measure for. Default 60.

.PARAMETER Slices
    How many slices to split that into, so one busy moment shows as one slice rather than hiding in an
    average. Default 6.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\windows\measure-idle.ps1 -Label idle
#>
param(
    [string]$Label = 'idle',
    [int]$Seconds = 60,
    [int]$Slices = 6
)

$ErrorActionPreference = 'Stop'

$names = @('ScreenTail.Service', 'ScreenTail.UI')
$cores = [Environment]::ProcessorCount
$sliceSeconds = [Math]::Max(1, [int][Math]::Round($Seconds / $Slices))
$frequency = [double][Diagnostics.Stopwatch]::Frequency

function Read-Sample {
    param([string]$Name)

    $process = Get-Process -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $process) {
        return $null
    }

    [pscustomobject]@{
        Id         = $process.Id
        At         = [Diagnostics.Stopwatch]::GetTimestamp()
        CpuMs      = $process.TotalProcessorTime.TotalMilliseconds
        Private    = $process.PrivateMemorySize64
        WorkingSet = $process.WorkingSet64
        Threads    = $process.Threads.Count
        Handles    = $process.HandleCount
    }
}

function Format-Spread {
    param([double[]]$Values)

    if ($Values.Count -eq 0) {
        return 'n/a'
    }

    $sorted = @($Values | Sort-Object)
    $median = $sorted[[int][Math]::Floor($sorted.Count / 2)]
    return ('{0:F2} ({1:F2} to {2:F2})' -f $median, $sorted[0], $sorted[-1])
}

$samples = @{}
foreach ($name in $names) {
    $first = Read-Sample -Name $name
    if (-not $first) {
        Write-Host "$name is not running. Start ScreenTail first (docs/dev/demo.md)."
        exit 1
    }

    $samples[$name] = New-Object System.Collections.ArrayList
    [void]$samples[$name].Add($first)
}

Write-Host "Measuring $($names -join ' and ') for $($sliceSeconds * $Slices) s in $Slices slices of $sliceSeconds s ($Label)..."
for ($i = 0; $i -lt $Slices; $i++) {
    Start-Sleep -Seconds $sliceSeconds
    foreach ($name in $names) {
        $next = Read-Sample -Name $name
        if (-not $next -or $next.Id -ne $samples[$name][0].Id) {
            Write-Host "$name stopped or restarted during the measurement. Run it again."
            exit 1
        }

        [void]$samples[$name].Add($next)
    }
}

$rows = @()
$privateTotal = 0
foreach ($name in $names) {
    $list = $samples[$name]
    $rates = @()
    for ($i = 1; $i -lt $list.Count; $i++) {
        $wallMs = ($list[$i].At - $list[$i - 1].At) * 1000.0 / $frequency
        $rates += (($list[$i].CpuMs - $list[$i - 1].CpuMs) / $wallMs) * 100.0
    }

    $wholeMs = ($list[-1].At - $list[0].At) * 1000.0 / $frequency
    $whole = (($list[-1].CpuMs - $list[0].CpuMs) / $wholeMs) * 100.0
    $last = $list[-1]
    $privateTotal += $last.Private
    $row = '| {0} | {1} | {2:F2} | {3:F3} | {4:F0} MB | {5:F0} MB | {6} | {7} |' -f $name, $Label, $whole, ($whole / $cores), ($last.Private / 1MB), ($last.WorkingSet / 1MB), $last.Threads, $last.Handles
    $rows += $row
    Write-Host ("{0}: by slice, % of one core: {1}" -f $name, (Format-Spread -Values $rates))
}

$lines = @(
    '',
    ("{0}, {1} logical processors, {2:yyyy-MM-dd HH:mm}" -f $env:COMPUTERNAME, $cores, (Get-Date)),
    '',
    '| Process | While | CPU, % of one core | CPU, % of the machine | Private memory | Working set | Threads | Handles |',
    '|---|---|---|---|---|---|---|---|'
) + $rows + @(
    '',
    ('Both processes together: {0:F0} MB private (ST-031 budget 600 MB with speech; idle CPU budget 1%).' -f ($privateTotal / 1MB))
)
$lines | ForEach-Object { Write-Host $_ }
