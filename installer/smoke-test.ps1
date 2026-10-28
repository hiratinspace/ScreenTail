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

function Invoke-Msi([string]$Action, [string]$Package, [string]$LogName, [string]$Properties = '') {
    $log = Join-Path $Logs $LogName
    $code = Invoke-Process 'msiexec.exe' "$Action `"$Package`" /qn /norestart /l*v `"$log`" $Properties"
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

function Start-InstalledService {
    # The token is rewritten at every start, so one left by an earlier run would prove nothing.
    $token = Join-Path $dataDir 'ipc.token'
    Remove-Item $token -Force -ErrorAction SilentlyContinue
    $null = Start-Process -FilePath $serviceExe -WorkingDirectory $installDir -PassThru
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-Path $token) {
            return $true
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Get-UninstallReport([string]$Folder, [datetime]$Since) {
    # What the uninstall step said about itself, which WixQuietExec copied into the installer's log.
    @(Get-ChildItem $Folder -Filter '*.log' | Where-Object { $_.LastWriteTime -ge $Since } |
        ForEach-Object { Get-Content $_.FullName } | Where-Object { $_ -match 'ScreenTail uninstall:' } |
        ForEach-Object { $_.Substring($_.IndexOf('ScreenTail uninstall:')) } | Select-Object -Unique)
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
    $code = Invoke-Msi '/i' $Msi 'upgrade.log'
    Check 'msiexec /i new /qn over the older one exits 0, with no restart needed' ($code -eq 0) "exit $code"
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

# --- Uninstall -----------------------------------------------------------------------------------------
Write-Step 'Silent uninstall while the service is running'
$since = Get-Date
$code = Invoke-Msi '/x' $Msi 'uninstall.log'
Check 'msiexec /x /qn exits 0, with no restart needed' ($code -eq 0) "exit $code"
Test-Removed
$report = Get-UninstallReport $Logs $since
$report | ForEach-Object { Write-Host "  $_" }
Check 'the uninstall step reported what it did in the log' ($report.Count -ge 3)
Check "the UI's own Run value is gone" ($null -eq (Get-RunValue $userRun 'ScreenTail'))
Check "another program's Run value is untouched" ((Get-RunValue $userRun $decoyName) -eq $decoyCommand)
Remove-ItemProperty -Path $userRun -Name $decoyName -ErrorAction SilentlyContinue

if ($signedIn) {
    Check 'the data folder is gone (deleted as its signed-in owner)' (-not (Test-Path $dataDir))
} else {
    # The rule, not a gap in the test: nobody signed in means no token to delete with, and the system
    # account does not delete inside a profile on its own. Left, and said so in the log.
    Check 'the data folder is left in place because its owner has no session' (Test-Path $dataDir)
    Check 'and the uninstall log says it left one for that reason' (@($report | Where-Object { $_ -match '\b1 data folders whose owner is not signed in' }).Count -eq 1)
}
if (Test-Path $dataDir) {
    Remove-Item $dataDir -Recurse -Force
}

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
    Check 'the uninstall log says it left a folder reached through a link' (@($report | Where-Object { $_ -match '\b1 reached through a link' }).Count -eq 1)

    if (Test-Path $dataDir) {
        # Removes the junction, not what it points at.
        [IO.Directory]::Delete($dataDir)
    }
    Remove-Item $sentinel -Recurse -Force -ErrorAction SilentlyContinue
}

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
