<#
.SYNOPSIS
    Installs, upgrades and uninstalls ScreenTail silently, and checks what each step left (ST-112).

.DESCRIPTION
    The installer's acceptance check on a real Windows machine, the way an MSP tool runs it: msiexec
    with /qn, then the setup program with /quiet. For a throwaway machine only (CI's hosted runner, a
    VM): it installs into Program Files, starts the service, and deletes ScreenTail's data folder for
    the user running it. It refuses to start where ScreenTail is installed or has data, unless -Force.

    Every check prints PASS or FAIL with what it saw, the uninstall step's own report is copied from
    the installer's log, and a summary goes to the job summary on CI. Exits 1 if anything failed.

.PARAMETER Msi
    The package to test.

.PARAMETER Version
    The version the package was built with, as it should appear in Programs and Features.

.PARAMETER OlderMsi
    An older package built from the same payload. Installed first, so the test can check that an
    upgrade replaces it and keeps the user's data.

.PARAMETER Bundle
    ScreenTail-Setup.exe. Installed and uninstalled after the package, with a link planted where the
    data folder would be, to check the uninstall leaves what a link points at untouched.

.PARAMETER Logs
    Where the installer logs are written.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OlderMsi,
    [string]$Bundle,
    [string]$Logs = (Join-Path ([IO.Path]::GetTempPath()) 'screentail-installer-logs'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:ProgramFiles 'ScreenTail'
$serviceExe = Join-Path $installDir 'ScreenTail.Service.exe'
$uiExe = Join-Path $installDir 'ScreenTail.UI.exe'
$dataDir = Join-Path $env:LOCALAPPDATA 'ScreenTail'
$machineRun = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$userRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\ScreenTail.lnk'
$uninstallKeys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
$decoyName = 'ScreenTail.SmokeDecoy'
$decoyCommand = '"C:\Elsewhere\ScreenTail.Dev\ScreenTail.UI.exe"'

$results = New-Object System.Collections.Generic.List[object]

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Check = $Name; Result = $(if ($Ok) { 'PASS' } else { 'FAIL' }); Detail = $Detail })
    if ($Ok) {
        Write-Host "PASS  $Name $Detail"
    } else {
        Write-Host "FAIL  $Name $Detail"
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-Host "::error title=Installer smoke test::$Name $Detail"
        }
    }
}

function Write-Step([string]$Title) {
    Write-Host ''
    Write-Host "== $Title"
}

function Invoke-Process([string]$File, [string]$Arguments, [int]$TimeoutSeconds = 600, [string]$StdOut) {
    $start = @{ FilePath = $File; ArgumentList = $Arguments; PassThru = $true; WindowStyle = 'Hidden' }
    if ($StdOut) {
        $start.RedirectStandardOutput = $StdOut
        $start.Remove('WindowStyle')
        $start.NoNewWindow = $true
    }
    $process = Start-Process @start
    # Windows PowerShell only reports ExitCode for a process whose handle was taken while it ran.
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        return -1
    }
    return $process.ExitCode
}

function Invoke-Msi([string]$Action, [string]$Package, [string]$LogName, [string]$Properties = '', [string]$Ui = '/qn') {
    $log = Join-Path $Logs $LogName
    $code = Invoke-Process 'msiexec.exe' "$Action `"$Package`" $Ui /norestart /l*v `"$log`" $Properties"
    if ($code -ne 0 -and (Test-Path $log)) {
        Write-Host "--- last lines of $LogName"
        Get-Content $log -Tail 40 | ForEach-Object { Write-Host "    $_" }
    }
    return $code
}

function Get-ArpEntries {
    foreach ($key in $uninstallKeys) {
        if (Test-Path $key) {
            Get-ChildItem $key | ForEach-Object { Get-ItemProperty $_.PSPath } | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'ScreenTail' }
        }
    }
}

function Get-RunValue([string]$Key, [string]$Name) {
    $item = Get-ItemProperty -Path $Key -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties[$Name]) {
        return $item.$Name
    }
    return $null
}

function Get-InstalledProcesses {
    @(Get-Process -Name 'ScreenTail.Service', 'ScreenTail.UI' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir + '\', [StringComparison]::OrdinalIgnoreCase) })
}

function Get-Subsystem([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $header = $reader.ReadInt32()
        $stream.Position = $header + 24 + 68
        return [int]$reader.ReadUInt16()
    } finally {
        $stream.Dispose()
    }
}

function Start-InstalledService([string]$ErrorLog) {
    # The token is rewritten at every start, so one left by an earlier run would prove nothing.
    $token = Join-Path $dataDir 'ipc.token'
    Remove-Item $token -Force -ErrorAction SilentlyContinue
    $start = @{ FilePath = $serviceExe; WorkingDirectory = $installDir; PassThru = $true }
    if ($ErrorLog) {
        # The service logs to standard error, scrubbed (INV-10); a Windows program's goes nowhere unless asked.
        # Standard output to a file too: a redirected start hands the child every standard handle it does
        # not redirect, and a service holding the job's own output open keeps the step from ever ending.
        $start.RedirectStandardError = $ErrorLog
        $start.RedirectStandardOutput = "$ErrorLog.out"
    }
    $null = Start-Process @start
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-Path $token) {
            return $true
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Get-UninstallReport([string]$Folder, [datetime]$Since, [string]$Prefix = 'ScreenTail uninstall:') {
    # What the uninstall step (or the upgrade step, by its own prefix) said about itself, which
    # WixQuietExec copied into the installer's log.
    @(Get-ChildItem $Folder -Filter '*.log' | Where-Object { $_.LastWriteTime -ge $Since } |
        ForEach-Object { Get-Content $_.FullName } | Where-Object { $_.Contains($Prefix) } |
        ForEach-Object { $_.Substring($_.IndexOf($Prefix)) } | Select-Object -Unique)
}

# ST-112: another program reading the installed service's executable as an upgrade removes it, the way the
# hosted runner's monitor (provjobd) and virus scanners read a program's file while it runs and keep it open
# a few seconds past its exit, without sharing deletion. Windows Installer cannot delete a file held like
# that and leaves it for a restart (3010), so the old version's removal has to wait for it. The holder is a
# separate process: it waits for the running service to end (Restart Manager ends it about a second before
# the old version's files are removed), then opens the executable for reading, read sharing only, for
# $holdMs, and writes how long it held it. The paths and the process id reach it through its environment,
# and its command is encoded, so nothing in a path can change what it runs.
$holdMs = 4000
$holdScript = @'
$record = $env:ST_HOLD_RECORD
try {
    if ($env:ST_HOLD_PID) {
        $service = $null
        try { $service = [Diagnostics.Process]::GetProcessById([int]$env:ST_HOLD_PID) } catch { $service = $null }
        if ($service -and -not $service.WaitForExit(300000)) {
            Set-Content -Path $record -Value 'the service did not end'
            exit 2
        }
    }
    $file = [IO.File]::Open($env:ST_HOLD_PATH, 'Open', 'Read', 'Read')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds ([int]$env:ST_HOLD_MS)
    $file.Dispose()
    Set-Content -Path $record -Value ('held {0} ms' -f $clock.ElapsedMilliseconds)
    exit 0
} catch {
    Set-Content -Path $record -Value ('could not hold it: ' + $_.Exception.GetType().Name)
    exit 1
}
'@

function Start-ForeignHolder([string]$Name) {
    $service = @(Get-InstalledProcesses | Where-Object { $_.ProcessName -eq 'ScreenTail.Service' }) | Select-Object -First 1
    Check "${Name}: the installed service is running before the upgrade" ($null -ne $service)
    # A file name from the step's name: "msiexec /qn upgrade" has a slash in it.
    $record = Join-Path $Logs ('holder-' + ($Name -replace '[^A-Za-z0-9]+', '-') + '.txt')
    Remove-Item $record -Force -ErrorAction SilentlyContinue
    $env:ST_HOLD_PATH = $serviceExe
    $env:ST_HOLD_PID = $(if ($service) { [string]$service.Id } else { '' })
    $env:ST_HOLD_MS = [string]$holdMs
    $env:ST_HOLD_RECORD = $record
    try {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($holdScript))
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $process = Start-Process -FilePath $powershell -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded -WindowStyle Hidden -PassThru
        $null = $process.Handle
    } finally {
        Remove-Item Env:\ST_HOLD_PATH, Env:\ST_HOLD_PID, Env:\ST_HOLD_MS, Env:\ST_HOLD_RECORD -ErrorAction SilentlyContinue
    }
    return [pscustomobject]@{ Name = $Name; Process = $process; Record = $record }
}

function Get-PendingRestartFiles {
    # Windows' list of file operations left for the next restart, which is where a file Windows Installer
    # could not delete or replace goes. Only entries for the install folder count.
    $item = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction SilentlyContinue
    $entries = @()
    foreach ($name in @('PendingFileRenameOperations', 'PendingFileRenameOperations2')) {
        if ($item -and $item.PSObject.Properties[$name]) {
            $entries += @($item.$name)
        }
    }
    @($entries | Where-Object { $_ -and $_.IndexOf($installDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

function Test-UpgradeHeld($Holder, [string[]]$Report) {
    # The holder really held the file through the old version's removal, the removal waited for it, and
    # nothing was left for a restart.
    $name = $Holder.Name
    if (-not $Holder.Process.WaitForExit(60000)) {
        $Holder.Process.Kill()
    }
    $line = Get-Content $Holder.Record -ErrorAction SilentlyContinue | Select-Object -First 1
    $held = 0
    if ($line -match '^held (\d+) ms$') {
        $held = [int]$Matches[1]
    }
    Check "${name}: another program held ScreenTail.Service.exe open, read sharing only, from the old service's end for $holdMs ms" ($held -ge ($holdMs - 50)) "$line"
    $wait = @($Report | Where-Object { $_ -match 'waited (\d+) ms for other programs to close the installed files; (\d+) still open\.' }) | Select-Object -First 1
    $waited = -1
    $open = -1
    if ($wait -match 'waited (\d+) ms for other programs to close the installed files; (\d+) still open\.') {
        $waited = [long]$Matches[1]
        $open = [int]$Matches[2]
    }
    # Any momentary holder (the runner's own monitor) can make the wait non-zero, so it has to have lasted
    # at least half the hold: that is what proves the hold overlapped the removal and the removal outlasted
    # it. The holder opens the file only once the old service has ended, and the step looks twice, 250 ms
    # apart, after ending it; a runner slow enough to start the hold after both looks would lose that race
    # and fail here, never pass falsely. Opening earlier, while the service still runs, would put the
    # holder on Restart Manager's list of programs to close, which is not what a scanner meets.
    $stillOpen = @($Report | Where-Object { $_ -match 'still open when the wait ended' })
    Check "${name}: the old version's removal waited for the other program before its files were removed" ($waited -ge ($holdMs / 2) -and $open -eq 0) ((@($wait) + $stillOpen) -join ' ')
    $pending = @(Get-PendingRestartFiles)
    Check "${name}: no installed file is left for the next restart" ($pending.Count -eq 0) ($pending -join ', ')
}

function Test-ReleaseWait([string[]]$Report) {
    # The uninstall step's last act: Windows Installer deletes the files the moment it exits, so it waits
    # until no other program has one open. "0 still open" is what lets the delete succeed without a restart.
    $line = @($Report | Where-Object { $_ -match 'waited \d+ ms for other programs to close the installed files' }) | Select-Object -First 1
    # When the wait ran out, the step names the files still open below the install folder and the kind of
    # hold; printed with the failure, so the next one says which file it was.
    $stillOpen = @($Report | Where-Object { $_ -match 'still open when the wait ended' })
    $stillOpen | ForEach-Object { Write-Host "  $_" }
    Check 'the uninstall step waited until no other program had an installed file open' ($line -match '; 0 still open\.') ((@($line) + $stillOpen) -join ' ')
}

function Invoke-Close([string]$Exe, [string]$Mode, [string]$Name, [switch]$Restart) {
    # In a process of its own, ended after 90 s: a Restart Manager call has no timeout of its own.
    $out = Join-Path $Logs "close-$Name.txt"
    $restartSwitch = $(if ($Restart) { " -Restart -StoreFolder `"$dataDir`"" } else { '' })
    # Both outputs to files, so neither this process nor what Restart Manager restarts from it holds the
    # job's own output open.
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'close-running.ps1')`" -Exe `"$Exe`" -Mode $Mode$restartSwitch"
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    $null = $process.Handle
    if ($process.WaitForExit(90000)) {
        $code = $process.ExitCode
    } else {
        $process.Kill()
        $code = -1
    }
    $lines = @(Get-Content $out -ErrorAction SilentlyContinue)
    Get-Content "$out.err" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  stderr: $_" }
    $lines | ForEach-Object { Write-Host "  $_" }
    return [pscustomobject]@{ Code = $code; Lines = $lines }
}

function Test-ClosedByItself([string]$What, $Result) {
    # Windows waits about five seconds for a program it asked; ScreenTail takes a fraction of that.
    Check "${What}: the request was made" ($Result.Code -eq 0 -and @($Result.Lines | Where-Object { $_ -eq 'processes 1' }).Count -eq 1) "exit $($Result.Code)"
    $line = @($Result.Lines | Where-Object { $_ -match '^exited \d+ \d+ -?\d+$' }) | Select-Object -First 1
    $parts = "$line" -split ' '
    Check "${What}: it ended by itself within 5 s, with exit code 0" ($line -and [int]$parts[2] -le 5000 -and [int]$parts[3] -eq 0) "$line"
}

function Test-RestartManagerShutdown([string]$What, $Result) {
    # Matched as strings: a line that is not there comes back from Select-Object as an empty result, and
    # -match on that is an empty array, not $false.
    Check "${What}: Restart Manager was asked about the one running copy and nothing else" (@($Result.Lines | Where-Object { $_ -eq 'listed 1' }).Count -eq 1) (@($Result.Lines | Where-Object { $_ -match '^listed ' }) -join ' ')
    $shutdown = @($Result.Lines | Where-Object { $_ -match '^shutdown ' }) | Select-Object -First 1
    Check "${What}: Restart Manager's shutdown without force succeeded" ("$shutdown" -match '^shutdown 0 ') "$shutdown"
}

function Test-StoreOpen([string]$What) {
    # What makes Test-StoreClosed mean something: while the service runs, the files are there.
    $wal = Join-Path $dataDir 'store.db-wal'
    for ($i = 0; $i -lt 20 -and -not (Test-Path $wal); $i++) { Start-Sleep -Milliseconds 250 }
    Check "${What}: while the service runs, its store has a write-ahead log" (Test-Path $wal)
}

function Test-StoreClosed([string]$What, $Result) {
    # SQLite folds its write-ahead log in and removes it, with the shared-memory file, when the store is
    # closed; a process ended mid-run leaves both behind. After a restart, what was there between the end
    # and the restart, as close-running.ps1 saw it.
    $seen = @($Result.Lines | Where-Object { $_ -match '^store-left ' }) | Select-Object -First 1
    if ($seen) {
        $left = @(("$seen" -replace '^store-left ', '') -split ',' | Where-Object { $_ -and $_ -ne 'none' })
    } else {
        $left = @(Get-ChildItem $dataDir -Filter 'store.db-*' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    }
    Check "${What}: the store was closed, no write-ahead log or shared-memory file left" ($left.Count -eq 0) ($left -join ', ')
}

function Test-InstalledLayout([string]$ExpectedVersion) {
    foreach ($file in @('ScreenTail.Service.exe', 'ScreenTail.UI.exe', 'e_sqlcipher.dll', 'Registry\remote-tools.json', 'Registry\exclusions-default.json', 'runtimes\win-x64\whisper.dll')) {
        Check "installed: $file" (Test-Path (Join-Path $installDir $file))
    }
    $managed = @(Get-ChildItem $installDir -Recurse -Filter '*.dll' | Where-Object {
            try { [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); $true } catch [BadImageFormatException] { $false }
        })
    Check 'no managed assembly outside the single-file executables' ($managed.Count -eq 0) (($managed | ForEach-Object { $_.Name }) -join ', ')
    foreach ($exe in @($serviceExe, $uiExe)) {
        if (Test-Path $exe) {
            $subsystem = Get-Subsystem $exe
            Check "$(Split-Path $exe -Leaf) is a Windows program (no console window)" ($subsystem -eq 2) "subsystem $subsystem"
        }
    }
    $run = Get-RunValue $machineRun 'ScreenTail.Service'
    Check 'the service starts at sign-in from the machine Run key' ($run -eq "`"$serviceExe`"") "value: $run"
    Check 'nothing starts the UI from the machine Run key' ($null -eq (Get-RunValue $machineRun 'ScreenTail.UI'))
    Check 'Start menu shortcut' (Test-Path $shortcut)
    if (Test-Path $shortcut) {
        $target = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath
        Check 'the shortcut opens the installed UI' ($target -ieq $uiExe) "target: $target"
    }
    $msiEntries = @(Get-ArpEntries | Where-Object { $_.PSObject.Properties['WindowsInstaller'] -and $_.WindowsInstaller -eq 1 })
    Check 'one ScreenTail package is installed' ($msiEntries.Count -eq 1) "found $($msiEntries.Count)"
    if ($msiEntries.Count -eq 1 -and $ExpectedVersion) {
        Check "the installed version is $ExpectedVersion" ($msiEntries[0].DisplayVersion -eq $ExpectedVersion) "found $($msiEntries[0].DisplayVersion)"
    }
}

function Test-Removed {
    Check 'the install folder is gone' (-not (Test-Path $installDir))
    Check 'the machine Run value is gone' ($null -eq (Get-RunValue $machineRun 'ScreenTail.Service'))
    Check 'the Start menu shortcut is gone' (-not (Test-Path $shortcut))
    Check 'no ScreenTail entry is left in Programs and Features' (@(Get-ArpEntries).Count -eq 0)
    Check 'no ScreenTail process is running' (@(Get-Process -Name 'ScreenTail.Service', 'ScreenTail.UI' -ErrorAction SilentlyContinue).Count -eq 0)
    Check 'the machine key the shortcut used is gone' (-not (Test-Path 'HKLM:\Software\ScreenTail'))
}

# --- Preconditions -------------------------------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this as an administrator: the package installs for the whole machine.'
}
if (-not $Force -and ((Test-Path $installDir) -or (Test-Path $dataDir))) {
    throw "ScreenTail is installed here, or $dataDir exists. This test uninstalls it and deletes that folder; run it on a throwaway machine, or pass -Force."
}
foreach ($file in @($Msi, $OlderMsi, $Bundle) | Where-Object { $_ }) {
    if (-not (Test-Path $file)) {
        throw "$file does not exist."
    }
}
New-Item -ItemType Directory -Force -Path $Logs | Out-Null
$Msi = (Resolve-Path $Msi).Path
$version = $Version

# Whether this account has a session on the machine, which is when the uninstall deletes its data
# folder (with its own rights); without one, the folder is left and the uninstall says so.
$sessionLine = $null
try {
    $sessionLine = (& query.exe user 2>$null) | Where-Object { $_ -match "^\s*>?$([regex]::Escape($env:USERNAME))\s" } | Select-Object -First 1
} catch {
    $sessionLine = $null
}
$signedIn = [bool]$sessionLine
Write-Host "This account has a session on the machine: $signedIn"

# --- An install folder outside Program Files is refused -------------------------------------------------
Write-Step 'A silent install into another folder is refused'
$elsewhere = Join-Path $env:SystemDrive 'ScreenTailElsewhere'
$code = Invoke-Msi '/i' $Msi 'install-elsewhere.log' "INSTALLFOLDER=$elsewhere"
Check 'msiexec /i with INSTALLFOLDER outside Program Files fails' ($code -ne 0) "exit $code"
Check 'and installs nothing there' (-not (Test-Path (Join-Path $elsewhere 'ScreenTail.Service.exe')))
Check 'and registers nothing' (@(Get-ArpEntries).Count -eq 0)
Remove-Item $elsewhere -Recurse -Force -ErrorAction SilentlyContinue

# --- Install (and upgrade) -----------------------------------------------------------------------------
# A fresh profile need not have a Run key of its own yet (some hosted images do not), and the checks
# below plant values in it.
if (-not (Test-Path $userRun)) {
    New-Item -Path $userRun -Force | Out-Null
}

if ($OlderMsi) {
    Write-Step 'Silent install of the older package'
    $code = Invoke-Msi '/i' (Resolve-Path $OlderMsi).Path 'install-older.log'
    Check 'msiexec /i older /qn exits 0' ($code -eq 0) "exit $code"
    Test-InstalledLayout $null

    Write-Step 'The installed service starts and makes its data folder'
    Check 'the installed service starts and writes its pipe token' (Start-InstalledService)
    Check 'the running service is the installed one' ((Get-InstalledProcesses).Count -ge 1)

    New-ItemProperty -Path $userRun -Name 'ScreenTail' -Value "`"$uiExe`"" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $userRun -Name $decoyName -Value $decoyCommand -PropertyType String -Force | Out-Null

    Write-Step 'Silent upgrade to the new package while the service is running'
    # ST-113: the update installer holds the package open, sharing it for reading only, for as long as
    # Windows Installer runs. The upgrade runs the same way here, so a package Windows Installer could not
    # open alongside that handle fails this check rather than a technician's update.
    # And another program reads the service's executable as the old version is removed.
    $since = Get-Date
    $holder = Start-ForeignHolder 'msiexec /qn upgrade'
    $held = [System.IO.File]::Open((Resolve-Path $Msi).Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $code = Invoke-Msi '/i' $Msi 'upgrade.log'
    } finally {
        $held.Dispose()
    }
    Check 'msiexec /i new /qn over the older one exits 0, with no restart needed' ($code -eq 0) "exit $code"
    Check 'the upgrade ran while the package was held open for reading only, as the update installer holds it' ($code -eq 0)
    $upgradeReport = Get-UninstallReport $Logs $since 'ScreenTail upgrade:'
    $upgradeReport | ForEach-Object { Write-Host "  $_" }
    Test-UpgradeHeld $holder $upgradeReport
    Check 'the upgrade ran no uninstall step' (@(Get-UninstallReport $Logs $since).Count -eq 0)
    Check 'the upgrade closed the running copies' ((Get-InstalledProcesses).Count -eq 0)
    Check 'the upgrade kept the user data (store key still there)' (Test-Path (Join-Path $dataDir 'store.key'))
    Check "the upgrade left the UI's own Run value alone" ($null -ne (Get-RunValue $userRun 'ScreenTail'))
} else {
    Write-Step 'Silent install'
    $code = Invoke-Msi '/i' $Msi 'install.log'
    Check 'msiexec /i /qn exits 0' ($code -eq 0) "exit $code"
    New-ItemProperty -Path $userRun -Name 'ScreenTail' -Value "`"$uiExe`"" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $userRun -Name $decoyName -Value $decoyCommand -PropertyType String -Force | Out-Null
}

Test-InstalledLayout $version

Write-Step 'The installed executables run'
$capabilities = Join-Path $Logs 'capabilities.json'
$code = Invoke-Process $serviceExe '--capabilities' 120 $capabilities
$json = $null
try {
    $json = Get-Content $capabilities -Raw | ConvertFrom-Json
} catch {
    $json = $null
}
Check 'the installed service answers --capabilities with JSON' (($code -eq 0 -or $code -eq 1) -and $null -ne $json) "exit $code"

$gallery = Join-Path $Logs 'gallery'
$code = Invoke-Process $uiExe "--gallery --screenshot `"$gallery`"" 180
$pngs = @(Get-ChildItem $gallery -Filter '*.png' -ErrorAction SilentlyContinue)
Check 'the installed UI renders its gallery (WPF from the single file)' ($code -eq 0 -and $pngs.Count -ge 3) "exit $code, $($pngs.Count) images"

Check 'the installed service starts and writes its pipe token' (Start-InstalledService)

# --- Windows asks the running programs to close ------------------------------------------------------
# ST-112. The service has one hidden window so that Windows can ask it to close (sign-out, shutdown, an
# installer's Restart Manager, WM_CLOSE). Before it, Restart Manager listed the service as a program it
# could not ask (RmUnknownApp), refused to shut it down without force, and every installer had to end it.
# Each request must end the program by itself, promptly, with its store closed.
Write-Step 'WM_CLOSE to the running service, as taskkill without /f sends it'
Test-StoreOpen 'the running service'
$closed = Invoke-Close $serviceExe 'CloseMessage' 'service-close-message'
Check 'WM_CLOSE reached the service through its session-end window' (@($closed.Lines | Where-Object { $_ -match '^posted \d+ 1$' }).Count -eq 1)
Test-ClosedByItself 'WM_CLOSE to the service' $closed
Test-StoreClosed 'WM_CLOSE to the service'

Write-Step "Restart Manager's request to the running service, as Windows Installer makes it"
$serviceLog = Join-Path $Logs 'service-restart-manager.log'
Check 'the installed service starts again, logging to a file' (Start-InstalledService $serviceLog)
Test-StoreOpen 'the restarted service'
$closed = Invoke-Close $serviceExe 'RestartManager' 'service-restart-manager' -Restart
$type = @($closed.Lines | Where-Object { $_ -match '^app-type \d+ ' }) | Select-Object -First 1
Check 'Restart Manager sees the service as a program with a window it can ask, not RmUnknownApp' ("$type" -match '^app-type \d+ [12]$') "$type"
Test-RestartManagerShutdown 'the service' $closed
Test-ClosedByItself "Restart Manager's request to the service" $closed
Test-StoreClosed "Restart Manager's request to the service" $closed
Check 'the store was looked at between the end and the restart' (@($closed.Lines | Where-Object { $_ -match '^store-left ' }).Count -eq 1)
$said = @(Get-Content $serviceLog -ErrorAction SilentlyContinue | Where-Object { $_ -match 'Windows ended the session for the capture service \(restart-manager\): Stopped' })
Check "the service's log says Restart Manager asked, and that it had stopped before it answered" ($said.Count -eq 1)

# Registered for restart, so another program's installer that closes it (one replacing the Visual C++
# runtime its speech libraries load) does not leave it down until the next sign-in. The restart is the
# ordinary start: watching, not recording.
Check 'Restart Manager can restart the service: it registered for restart' (@($closed.Lines | Where-Object { $_ -match '^restartable \d+ True$' }).Count -eq 1)
Check "Restart Manager's restart succeeded" (@($closed.Lines | Where-Object { $_ -eq 'restart 0' }).Count -eq 1) (@($closed.Lines | Where-Object { $_ -match '^restart ' }) -join ' ')
$again = @($closed.Lines | Where-Object { $_ -match '^restarted \d+$' }) | Select-Object -First 1
Check 'Restart Manager started the installed service again' ($null -ne $again) "$again"
if ($again) {
    $againId = [int]("$again" -split ' ')[1]
    Start-Sleep -Seconds 3
    $alive = Get-Process -Id $againId -ErrorAction SilentlyContinue
    Check 'the restarted service is still running a few seconds later (it is the one copy, not a second refused by the name)' ($null -ne $alive)
    Test-StoreOpen 'the restarted service'

    # Asked to close again, so the rest of the test meets a service it started itself.
    $closed = Invoke-Close $serviceExe 'CloseMessage' 'restarted-service-close-message'
    Test-ClosedByItself 'WM_CLOSE to the restarted service' $closed
}

Write-Step "Restart Manager's request to the running UI"
$ui = Start-Process -FilePath $uiExe -WorkingDirectory $installDir -PassThru
# Until the UI has a window up (its tray and pill come with the shell), at most 30 s.
for ($i = 0; $i -lt 60; $i++) {
    $ui.Refresh()
    if ($ui.HasExited -or $ui.MainWindowHandle -ne [IntPtr]::Zero) {
        break
    }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 1
$closed = Invoke-Close $uiExe 'RestartManager' 'ui-restart-manager'
Test-RestartManagerShutdown 'the UI' $closed
Check 'Restart Manager can restart the UI: it registered for restart' (@($closed.Lines | Where-Object { $_ -match '^restartable \d+ True$' }).Count -eq 1)
Test-ClosedByItself "Restart Manager's request to the UI" $closed
Get-Process -Name 'ScreenTail.UI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Check 'the installed service starts again for the uninstall' (Start-InstalledService)

# --- A second technician, never signed in, with data and a planted link --------------------------------
# A real local account with a real profile that has never been used, which is what an MSP's uninstall
# meets for every technician who is not at their desk. Its data folder gets a key, a credential and a
# store, and in place of the credential's temporary file a symbolic link to a file outside the profile.
# The uninstall must remove the key and the credential, leave the store, and leave the link and what it
# points at untouched.
Write-Step 'A second technician who is not signed in, with data and a planted link'
Add-Type -Namespace ScreenTailSmoke -Name Profiles -MemberDefinition '[DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int CreateProfile(string sid, string name, System.Text.StringBuilder path, uint size);'
$otherName = 'stsmoke' + (Get-Random -Minimum 1000 -Maximum 9999)
$password = ConvertTo-SecureString ([guid]::NewGuid().ToString('N') + 'Aa1!') -AsPlainText -Force
New-LocalUser -Name $otherName -Password $password -Description 'ScreenTail installer smoke test' | Out-Null
$otherSid = (New-Object Security.Principal.NTAccount($otherName)).Translate([Security.Principal.SecurityIdentifier]).Value
$profilePath = New-Object System.Text.StringBuilder 260
$hr = [ScreenTailSmoke.Profiles]::CreateProfile($otherSid, $otherName, $profilePath, 260)
$otherProfile = $profilePath.ToString()
Check 'a profile exists for a second technician who has never signed in' ($hr -eq 0 -and $otherProfile -and (Test-Path $otherProfile)) ('result 0x{0:X8}' -f $hr)
$otherData = Join-Path $otherProfile 'AppData\Local\ScreenTail'
$outside = Join-Path $env:SystemDrive 'ScreenTailSmokeOutside'
New-Item -ItemType Directory -Force -Path $otherData, $outside | Out-Null
foreach ($name in @('store.key', 'device.cred', 'store.db')) {
    Set-Content -Path (Join-Path $otherData $name) -Value 'smoke'
}
Set-Content -Path (Join-Path $outside 'target.txt') -Value 'must survive the uninstall'
New-Item -ItemType SymbolicLink -Path (Join-Path $otherData 'device.cred.tmp') -Target (Join-Path $outside 'target.txt') | Out-Null

# --- Uninstall -----------------------------------------------------------------------------------------
Write-Step 'Silent uninstall while the service is running'
$runningBefore = @(Get-InstalledProcesses | Where-Object { $_.Name -eq 'ScreenTail.Service' }).Count
# ScreenTail's own processes, so the check below can tell another program's hold from theirs.
$screenTailIds = @(Get-Process -Name 'ScreenTail.Service', 'ScreenTail.UI' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
Check 'the installed service is running before the uninstall' ($runningBefore -ge 1) "running $runningBefore"
$since = Get-Date
$code = Invoke-Msi '/x' $Msi 'uninstall.log'
Check 'msiexec /x /qn exits 0, with no restart needed' ($code -eq 0) "exit $code"
Test-Removed
$report = Get-UninstallReport $Logs $since
$report | ForEach-Object { Write-Host "  $_" }
Check 'the uninstall step reported what it did in the log' ($report.Count -ge 5)
Test-ReleaseWait $report
# ST-112: Windows Installer's Restart Manager asks the running service to close in InstallValidate, and it
# does, so the uninstall step finds none left to end (before the service had a window this always said 1).
# The package asks only programs registered for restart and nobody when anything else holds an installed
# file (MSIRMSHUTDOWN=2), so 1 is right too, but only when the installer's log shows such a holder: one
# Restart Manager detected that is not a ScreenTail process. Counted, never named: a friendly name can be
# a window title.
$stoppedLine = @($report | Where-Object { $_ -match 'running copies stopped' }) | Select-Object -First 1
$stopped = if ("$stoppedLine" -match 'uninstall: (\d+) running copies stopped') { [int]$Matches[1] } else { -1 }
$detected = @(Get-Content (Join-Path $Logs 'uninstall.log') -ErrorAction SilentlyContinue |
    Where-Object { $_ -match 'RESTART MANAGER: Detected that application with id (\d+)' } |
    ForEach-Object { [int]([regex]::Match($_, 'with id (\d+)').Groups[1].Value) })
$otherHolders = @($detected | Where-Object { $screenTailIds -notcontains $_ } | Select-Object -Unique).Count
Check 'Restart Manager closed the running service before the uninstall step, or another program held a file so nobody was asked' ($stopped -eq 0 -or ($stopped -eq 1 -and $otherHolders -ge 1)) "stopped $stopped, other holders $otherHolders"
Check "the UI's own Run value is gone" ($null -eq (Get-RunValue $userRun 'ScreenTail'))
Check "another program's Run value is untouched" ((Get-RunValue $userRun $decoyName) -eq $decoyCommand)
Remove-ItemProperty -Path $userRun -Name $decoyName -ErrorAction SilentlyContinue

if ($signedIn) {
    Check 'the data folder is gone (deleted as its signed-in owner)' (-not (Test-Path $dataDir))
} else {
    # The rule, not a gap in the test: nobody signed in means no token to delete the folder with. Only
    # the key and the credentials go, and the store stays, unreadable.
    Check "this account's store key is gone" (-not (Test-Path (Join-Path $dataDir 'store.key')))
    Check "and its store stays, unreadable without the key" (Test-Path (Join-Path $dataDir 'store.db'))
}
if (Test-Path $dataDir) {
    Remove-Item $dataDir -Recurse -Force
}

Check "the signed-out technician's store key is gone" (-not (Test-Path (Join-Path $otherData 'store.key')))
Check "the signed-out technician's device credential is gone" (-not (Test-Path (Join-Path $otherData 'device.cred')))
Check "the signed-out technician's store stays, unreadable without the key" (Test-Path (Join-Path $otherData 'store.db'))
$link = Get-Item -LiteralPath (Join-Path $otherData 'device.cred.tmp') -Force -ErrorAction SilentlyContinue
Check 'the planted link is left as it was' ($null -ne $link -and ($link.Attributes -band [IO.FileAttributes]::ReparsePoint))
Check 'and what it points at, outside the profile, is untouched' (Test-Path (Join-Path $outside 'target.txt'))
Check 'the uninstall log counts the signed-out folder and the file it left in doubt' (@($report | Where-Object { $_ -match 'not signed in: [1-9]\d* data folders had their key and credentials removed' -and $_ -match '[1-9]\d* files left in doubt' }).Count -eq 1)

Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $otherSid } | Remove-CimInstance -ErrorAction SilentlyContinue
Remove-LocalUser -Name $otherName -ErrorAction SilentlyContinue
Remove-Item $outside -Recurse -Force -ErrorAction SilentlyContinue

# --- The setup program, and a link where the data folder would be --------------------------------------
if ($Bundle) {
    $Bundle = (Resolve-Path $Bundle).Path
    Write-Step 'Silent install with the setup program'
    $code = Invoke-Process $Bundle "/quiet /norestart /log `"$(Join-Path $Logs 'setup-install.log')`""
    Check 'ScreenTail-Setup.exe /quiet exits 0' ($code -eq 0) "exit $code"
    Test-InstalledLayout $version
    $bundles = @(Get-ArpEntries | Where-Object { $_.PSObject.Properties['BundleVersion'] })
    Check 'the setup program is listed in Programs and Features' ($bundles.Count -eq 1) "found $($bundles.Count)"

    # A folder outside the profile, and the data folder replaced by a junction to it. The uninstall must
    # leave the junction and everything it points at.
    $sentinel = Join-Path $env:SystemDrive 'ScreenTailSmokeSentinel'
    New-Item -ItemType Directory -Force -Path $sentinel | Out-Null
    Set-Content -Path (Join-Path $sentinel 'keep.txt') -Value 'must survive the uninstall'
    New-Item -ItemType Junction -Path $dataDir -Target $sentinel | Out-Null

    Write-Step 'Silent uninstall with the setup program, with a link where the data folder was'
    $since = Get-Date
    $code = Invoke-Process $Bundle "/uninstall /quiet /norestart /log `"$(Join-Path $Logs 'setup-uninstall.log')`""
    Check 'ScreenTail-Setup.exe /uninstall /quiet exits 0' ($code -eq 0) "exit $code"
    Test-Removed
    $report = Get-UninstallReport $Logs $since
    $report | ForEach-Object { Write-Host "  $_" }
    Check 'what the link points at is untouched' (Test-Path (Join-Path $sentinel 'keep.txt'))
    Check 'the uninstall log says it left a folder reached through a link' (@($report | Where-Object { $_ -match 'left alone: 1 reached through a link' }).Count -eq 1)
    Test-ReleaseWait $report

    if (Test-Path $dataDir) {
        # Removes the junction, not what it points at.
        [IO.Directory]::Delete($dataDir)
    }
    Remove-Item $sentinel -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Upgrades the way the update installer and the setup program run them, with the executable held ----
# ST-113's update installer starts msiexec /i <package> /passive /norestart; the setup program upgrades
# through its own engine. Each runs over the older package with its service running, while another program
# reads the service's executable from the moment that service ends, and must end 0 with the new version
# installed and nothing left for a restart.
if ($OlderMsi) {
    Write-Step 'Upgrade with the update installer''s command line while another program reads the service'
    $code = Invoke-Msi '/i' (Resolve-Path $OlderMsi).Path 'install-older-2.log'
    Check 'msiexec /i older /qn exits 0 (for the update installer''s upgrade)' ($code -eq 0) "exit $code"
    Check 'the installed service starts and writes its pipe token' (Start-InstalledService)
    $since = Get-Date
    $holder = Start-ForeignHolder 'msiexec /passive upgrade'
    $code = Invoke-Msi '/i' $Msi 'upgrade-passive.log' '' '/passive'
    Check 'msiexec /i new /passive /norestart over the older one exits 0, with no restart needed' ($code -eq 0) "exit $code"
    $upgradeReport = Get-UninstallReport $Logs $since 'ScreenTail upgrade:'
    $upgradeReport | ForEach-Object { Write-Host "  $_" }
    Test-UpgradeHeld $holder $upgradeReport
    Test-InstalledLayout $version
    Check 'the upgrade kept the user data (store key still there)' (Test-Path (Join-Path $dataDir 'store.key'))

    if ($Bundle) {
        Write-Step 'Upgrade with the setup program while another program reads the service'
        $since = Get-Date
        $code = Invoke-Msi '/x' $Msi 'uninstall-2.log'
        Check 'msiexec /x /qn exits 0, with no restart needed (before the setup program''s upgrade)' ($code -eq 0) "exit $code"
        Test-Removed
        Test-ReleaseWait (Get-UninstallReport $Logs $since)
        $code = Invoke-Msi '/i' (Resolve-Path $OlderMsi).Path 'install-older-3.log'
        Check 'msiexec /i older /qn exits 0 (for the setup program''s upgrade)' ($code -eq 0) "exit $code"
        Check 'the installed service starts and writes its pipe token' (Start-InstalledService)
        $since = Get-Date
        $holder = Start-ForeignHolder 'setup program upgrade'
        $code = Invoke-Process $Bundle "/quiet /norestart /log `"$(Join-Path $Logs 'setup-upgrade.log')`""
        Check 'ScreenTail-Setup.exe /quiet over the older package exits 0, with no restart needed' ($code -eq 0) "exit $code"
        $upgradeReport = Get-UninstallReport $Logs $since 'ScreenTail upgrade:'
        $upgradeReport | ForEach-Object { Write-Host "  $_" }
        Test-UpgradeHeld $holder $upgradeReport
        Test-InstalledLayout $version

        Write-Step 'Silent uninstall with the setup program, after its upgrade'
        $since = Get-Date
        $code = Invoke-Process $Bundle "/uninstall /quiet /norestart /log `"$(Join-Path $Logs 'setup-uninstall-2.log')`""
        Check 'ScreenTail-Setup.exe /uninstall /quiet exits 0, after its upgrade' ($code -eq 0) "exit $code"
        Test-Removed
        Test-ReleaseWait (Get-UninstallReport $Logs $since)
    } else {
        $since = Get-Date
        $code = Invoke-Msi '/x' $Msi 'uninstall-2.log'
        Check 'msiexec /x /qn exits 0, with no restart needed (after the update installer''s upgrade)' ($code -eq 0) "exit $code"
        Test-Removed
        Test-ReleaseWait (Get-UninstallReport $Logs $since)
    }
    $pending = @(Get-PendingRestartFiles)
    Check 'no installed file is left for the next restart at the end' ($pending.Count -eq 0) ($pending -join ', ')
    if (Test-Path $dataDir) {
        Remove-Item $dataDir -Recurse -Force
    }
}

# Anything of ScreenTail's still running here would be a failure above; ended now, so it cannot hold the
# job's output open past the end of the script.
$left = @(Get-Process -Name 'ScreenTail.Service', 'ScreenTail.UI' -ErrorAction SilentlyContinue)
Write-Host "ScreenTail processes left at the end: $($left.Count)"
$left | Stop-Process -Force -ErrorAction SilentlyContinue

# --- Summary -------------------------------------------------------------------------------------------
$failed = @($results | Where-Object { $_.Result -eq 'FAIL' })
Write-Host ''
Write-Host "$($results.Count - $failed.Count) of $($results.Count) checks passed."
if ($env:GITHUB_STEP_SUMMARY) {
    $lines = @('### Installer smoke test', '', "Session for this account: $signedIn", '', '| Check | Result | Detail |', '|---|---|---|')
    foreach ($result in $results) {
        $lines += ('| {0} | {1} | {2} |' -f $result.Check, $result.Result, ($result.Detail -replace '\|', '/'))
    }
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}
# Said explicitly either way. CI's PowerShell wrapper exits with the last native command's code when the
# script does not, and a tool asked a question above (query.exe, for one) may leave a non-zero one behind.
# The checks decide.
if ($failed.Count -gt 0) {
    exit 1
}
exit 0
