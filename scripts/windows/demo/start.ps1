<#
.SYNOPSIS
    Starts the ScreenTail demo: the backend, then the capture service and the UI pointed at it.

.DESCRIPTION
    Run setup.ps1 once first. Then, every time:
      1. checks what setup saved, the certificate, port 5001 and PostgreSQL, and says exactly what is
         wrong if any of them is not right;
      2. builds the backend if it changed, applies any new database migration, and starts the backend in
         its own window as https://localhost:5001 in the Development environment (which is what makes it
         read the user secrets). Waits until /health answers over a trusted certificate;
      3. gets a device token: an invite-activated device renews its own; otherwise a Development device
         is enrolled and its one-hour token is checked against the backend;
      4. publishes the capture service and the UI into one folder if the client changed (the pipe
         handshake needs both in one folder), then starts the service in its own window with
         SCREENTAIL_BACKEND and SCREENTAIL_DEVICE_TOKEN set for that process only, and then the UI.

    Nothing is set machine-wide or user-wide, and nothing is installed. stop.ps1 stops exactly what this
    started. Running start again while the demo is running changes nothing.

    The lines the backend's and the capture service's scrubbing loggers write are also kept in
    %LOCALAPPDATA%\ScreenTail.Dev\demo\logs (backend.log, service.log; 2 MB each, the one before kept as
    .1.log), so report.cmd can include them. Anything else they print (a .NET crash message, native
    library output) shows in their window only; the log has it withheld (see common.ps1).

.PARAMETER Rebuild
    Publish the capture service and the UI again even if nothing seems to have changed.

.EXAMPLE
    scripts\windows\demo\start.cmd
#>
[CmdletBinding()]
param(
    [switch]$Rebuild
)

. (Join-Path $PSScriptRoot 'common.ps1')

$entries = @()
$stopHint = 'Then run scripts\windows\demo\stop.cmd to close what this start opened, and start again.'

# Stop-Demo, plus a reminder to run stop.cmd once something has been started.
function Fail {
    param([string]$Problem, [string[]]$Fix = @(), [string[]]$Detail = @())
    if ($entries.Count -gt 0) { $Fix = @($Fix) + @($stopHint) }
    Stop-Demo -Problem $Problem -Fix $Fix -Detail $Detail
}

function Add-Entry($Entry) {
    $script:entries = @($script:entries) + @($Entry)
    Save-DemoState $script:entries
}

# Turns the end of the backend's log into a sentence.
function Get-BackendFailure([string[]]$Lines) {
    $text = $Lines -join "`n"
    if ($text -match 'address already in use|Failed to bind') {
        return @('Port 5001 was taken by something else as the backend started.', 'Close whatever uses it (start says which, if you run it again), then start again.')
    }
    if ($text -match 'developer certificate|Unable to configure HTTPS endpoint') {
        return @('The backend could not find a valid development certificate.', 'Run setup.cmd again; it re-creates and trusts it.')
    }
    if ($text -match 'Jwt:SigningKey') {
        return @('The backend did not get its signing key: user secrets were not read.', 'Run setup.cmd again. If it says the key is saved, tell the developer: the Development environment is not being applied.')
    }
    if ($text -match 'ConnectionStrings:Postgres') {
        return @('The backend did not get its database connection: user secrets were not read.', 'Run setup.cmd again. If it says the connection is saved, tell the developer: the Development environment is not being applied.')
    }
    return @('The backend stopped while starting.', 'Read its window (titled "ScreenTail demo: backend"); the lines above are its last words.')
}

Write-Host 'Starting the ScreenTail demo.'

# --- 1. Checks ----------------------------------------------------------------------------------------------

Write-Step 'Checks'
Assert-Windows
$sdk = Assert-DotNetSdk
Write-Ok ".NET SDK $sdk"

# An earlier start that is still running, or one that was stopped some other way.
# @() because a single entry comes back as one object, and on PowerShell 5.1 that has no Count.
$previous = @(Get-DemoState)
if ($previous.Count -gt 0) {
    $alive = @($previous | Where-Object { Get-EntryProcess $_ })
    if ($alive.Count -gt 0) {
        $roles = @($alive | ForEach-Object { $_.Role })
        if ($roles -contains 'backend-window' -and $roles -contains 'service' -and $roles -contains 'ui') {
            Write-Ok 'The demo is already running. Nothing to do.'
            Write-Note 'To restart it: scripts\windows\demo\stop.cmd, then start.cmd.'
            exit 0
        }
        Stop-Demo -Problem "Part of an earlier start is still running ($($roles -join ', ')), and part of it is not." -Fix @(
            'Run scripts\windows\demo\stop.cmd, then start.cmd again.')
    }
    # Nothing of the last start is running. A database container it started is carried over, so that
    # stop still stops it.
    $entries = @($previous | Where-Object { $_.Kind -eq 'container' })
    Save-DemoState $entries
}

switch (Get-RunnerState) {
    'job' {
        Stop-Demo -Problem 'A GitHub Actions job is running on this laptop right now. Hardware jobs drive the keyboard, mouse and screen, and start ScreenTail themselves.' -Fix @(
            'Wait for it to finish (github.com/hiratinspace/ScreenTail/actions shows it), then start again.')
    }
    'idle' {
        Write-Caution 'The GitHub Actions runner is on. A hardware job that starts during the demo takes over the screen.'
        Write-Note 'Before the audience arrives, close the runner''s minimized "run.cmd" window. It comes back at next sign-in.'
    }
}

$others = @(Get-Process -Name 'ScreenTail.Service', 'ScreenTail.UI' -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) {
    $lines = @($others | ForEach-Object {
        if ($_.Path) { $where = $_.Path } else { $where = '(path unreadable)' }
        "pid $($_.Id)  $($_.ProcessName)  $where"
    })
    Stop-Demo -Problem 'ScreenTail is already running, started some other way (not by this script). There can be only one capture service per user.' -Detail $lines -Fix @(
        'Quit it from its tray icon (right-click > Quit) and press Ctrl+C in the capture service window,',
        "or close it from PowerShell:  Stop-Process -Id $(@($others | ForEach-Object { $_.Id }) -join ',')",
        'Then start again.')
}

$secrets = Read-UserSecrets
$missing = @()
if (-not $secrets[$script:KeyGemini]) { $missing += "the Gemini key ($script:KeyGemini)" }
if (-not (Test-SigningKey $secrets[$script:KeySigning])) { $missing += "the signing key ($script:KeySigning)" }
if (-not $secrets[$script:KeyDatabase]) { $missing += "the database connection ($script:KeyDatabase)" }
if ($missing.Count -gt 0) {
    Stop-Demo -Problem ('Setup has not been run, or did not finish. Missing: ' + ($missing -join '; ') + '.') -Fix @(
        'Run scripts\windows\demo\setup.cmd, then start again.')
}
Write-Ok 'The Gemini key, the signing key and the database connection are saved (values not shown).'

if (-not (Test-DevCertificateTrusted)) {
    Stop-Demo -Problem 'The development certificate is missing, expired or not trusted, so the capture service would refuse the backend.' -Fix @(
        'Run scripts\windows\demo\setup.cmd again and answer Yes when Windows asks about the certificate.')
}
Write-Ok 'The development certificate is trusted.'

$owner = Get-PortOwner $script:DemoBackendPort
if ($owner) {
    Stop-Demo -Problem "Port $script:DemoBackendPort is already in use by $($owner.Name) (pid $($owner.Id)). The backend needs it." -Fix @(
        'If that is an earlier ScreenTail backend (usually "dotnet"), close its window, or:',
        "  Stop-Process -Id $($owner.Id)",
        'Otherwise close the program that uses it. Then start again.')
}
if (Test-TcpPort -HostName 'localhost' -Port $script:DemoBackendPort) {
    Stop-Demo -Problem "Something already answers on port $script:DemoBackendPort. The backend needs it." -Fix @(
        'Close the program using it (resmon.exe > Network > Listening Ports shows which), then start again.')
}
Write-Ok "Port $script:DemoBackendPort is free."

foreach ($name in @('SCREENTAIL_BACKEND', 'SCREENTAIL_DEVICE_TOKEN')) {
    foreach ($scope in @('User', 'Machine')) {
        if ([Environment]::GetEnvironmentVariable($name, $scope)) {
            Write-Caution "$name is set in your Windows $scope environment. The demo overrides it for what it starts, but other programs see it."
            Write-Note "  To remove it:  [Environment]::SetEnvironmentVariable('$name', `$null, '$scope')"
        }
    }
}

# --- 2. Database and backend --------------------------------------------------------------------------------

Write-Step 'PostgreSQL'
$database = Get-ConnectionParts $secrets[$script:KeyDatabase]
if (-not (Test-TcpPort -HostName $database.Host -Port $database.Port)) {
    if ((Resolve-PostgresDown -HostName $database.Host -Port $database.Port) -eq 'docker-started' -and
        @($entries | Where-Object { $_.Kind -eq 'container' }).Count -eq 0) {
        Add-Entry ([pscustomobject]@{ Role = 'database'; Kind = 'container'; Id = 0; Name = $script:DemoDbContainer; StartTicks = 0 })
        Write-Ok "Started the $script:DemoDbContainer container (stop.cmd stops it again)."
    }
}
Write-Ok "PostgreSQL answers on $($database.Host):$($database.Port)."

Write-Step 'Building the backend'
$build = Invoke-Native -FilePath 'dotnet' -ArgumentList @('build', $script:DemoApiProject, '-c', 'Debug', '--nologo', '-v', 'quiet')
if ($build.ExitCode -ne 0) {
    Fail -Problem 'The backend does not build.' -Detail (Get-Tail -Lines $build.Lines -Count 20) -Fix @(
        'Make sure the clone is on main and up to date (git pull). If it still fails, send these lines to the developer.')
}
$targetPath = Invoke-Native -FilePath 'dotnet' -ArgumentList @('msbuild', $script:DemoApiProject, '-getProperty:TargetPath', '-p:Configuration=Debug', '-nologo')
$apiDll = (@($targetPath.Lines | Where-Object { $_.Trim() }) | Select-Object -Last 1)
if ($apiDll) { $apiDll = $apiDll.Trim() }
if ($targetPath.ExitCode -ne 0 -or -not $apiDll -or -not (Test-Path -LiteralPath $apiDll)) {
    Fail -Problem 'The backend built, but its output could not be found.' -Detail (Get-Tail -Lines $targetPath.Lines -Count 6) -Fix @('Send these lines to the developer.')
}
Write-Ok 'The backend is built.'

Write-Step 'Database migrations'
Add-DotNetToolsToPath
if (-not (Get-EfToolInstalledVersion)) {
    Fail -Problem 'dotnet-ef is not installed, so the database cannot be brought up to date.' -Fix @('Run scripts\windows\demo\setup.cmd.')
}
$update = Invoke-DatabaseUpdate -ConnectionString $secrets[$script:KeyDatabase] -NoBuild
switch ($update.Outcome) {
    'ok' {
        if ($update.Applied -gt 0) { Write-Ok "Applied $($update.Applied) new migration(s)." } else { Write-Ok 'The database is up to date.' }
    }
    'password' {
        Fail -Problem 'PostgreSQL refused the saved password.' -Fix @('Run:  scripts\windows\demo\setup.cmd -NewDatabasePassword')
    }
    'unreachable' {
        Fail -Problem 'PostgreSQL stopped answering while the database was being checked.' -Detail $update.Detail -Fix @('Check the PostgreSQL service or Docker Desktop, then start again.')
    }
    default {
        Fail -Problem 'Bringing the database up to date failed.' -Detail $update.Detail -Fix @('Send these lines to the developer.')
    }
}

Write-Step "Starting the backend on $script:DemoBackendUrl"
if (-not (Test-Path -LiteralPath $script:DemoLogDir)) {
    New-Item -ItemType Directory -Path $script:DemoLogDir -Force | Out-Null
}
# This run's logs start empty; the last run's are kept as <name>.1.log for report.cmd.
Move-DemoLogAside $script:DemoBackendLog
Move-DemoLogAside $script:DemoServiceLog
foreach ($file in @($script:DemoBackendExited, $script:DemoTokenFile, $script:DemoOldBackendLog)) {
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
}

# Both windows write their log through common.ps1's DemoLogWriterSource, saved here as a file each window
# reads: only the lines the process's scrubbing logger wrote are kept, anything else it prints is
# withheld, and the file is capped. The window itself still shows everything.
[IO.File]::WriteAllText($script:DemoLogWriterFile, $script:DemoLogWriterSource, (New-Object Text.UTF8Encoding $false))

# The window's script. Development is what makes the host read user secrets; the variables are set
# inside the window, for the backend only. Its output is shown and copied to backend.log, which holds no
# content (the backend's logger scrubs it, INV-10) and lets start explain a failure.
$backendScript = @'
. ([scriptblock]::Create([IO.File]::ReadAllText(__LOGWRITER__)))
$ErrorActionPreference = 'Continue'
$Host.UI.RawUI.WindowTitle = 'ScreenTail demo: backend'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = __URL__
Set-Location -LiteralPath __DIR__
Write-Host 'ScreenTail backend (Development). Leave this window open; stop.cmd closes it.'
$script:DemoLog.Path = __LOG__
$code = 'unknown'
try {
    & dotnet __DLL__ 2>&1 | ForEach-Object { $line = "$_"; Write-Host $line; Write-DemoLog $line }
    $code = "exit code $LASTEXITCODE"
}
catch {
    $code = $_.Exception.Message
    Write-Host $code -ForegroundColor Red
    Write-DemoLogText "[start.ps1] The backend could not run: $code"
}
Write-Host ''
Write-Host "The backend has stopped ($code). This window stays open so you can read why." -ForegroundColor Yellow
Write-DemoLogText "[start.ps1] The backend has stopped ($code)."
Close-DemoLog
Set-Content -LiteralPath __EXITED__ -Value $code
'@
$backendScript = $backendScript.Replace('__URL__', (ConvertTo-Quoted $script:DemoBackendUrl))
$backendScript = $backendScript.Replace('__DIR__', (ConvertTo-Quoted $script:DemoApiDir))
$backendScript = $backendScript.Replace('__DLL__', (ConvertTo-Quoted $apiDll))
$backendScript = $backendScript.Replace('__LOG__', (ConvertTo-Quoted $script:DemoBackendLog))
$backendScript = $backendScript.Replace('__EXITED__', (ConvertTo-Quoted $script:DemoBackendExited))
$backendScript = $backendScript.Replace('__LOGWRITER__', (ConvertTo-Quoted $script:DemoLogWriterFile))

$backendWindow = Invoke-WithEnvironment -Variables @{ 'SCREENTAIL_DEVICE_TOKEN' = $null } -Body {
    Start-DemoWindow -Script $backendScript
}
$backendWindowEntry = New-ProcessEntry -Role 'backend-window' -Process $backendWindow
Add-Entry $backendWindowEntry

Write-Note 'Waiting for it to answer (up to 90 seconds)...'
$healthy = $false
$clock = [Diagnostics.Stopwatch]::StartNew()
while ($clock.Elapsed.TotalSeconds -lt 90) {
    if (Test-Path -LiteralPath $script:DemoBackendExited) {
        $log = Read-SharedLog $script:DemoBackendLog
        $failure = Get-BackendFailure $log
        Fail -Problem $failure[0] -Detail (Get-Tail -Lines $log) -Fix @($failure[1])
    }
    if (-not (Get-EntryProcess $backendWindowEntry)) {
        Fail -Problem 'The backend window was closed before the backend answered.' -Fix @('Start again, and leave the backend window open.')
    }
    $probe = Invoke-Probe -Url "$script:DemoBackendUrl/health" -TimeoutSeconds 3
    if ($probe.Status -eq 200) {
        $healthy = $true
        break
    }
    if ($probe.Failure -eq 'TrustFailure') {
        Fail -Problem 'The backend answered, but Windows does not trust its certificate. The capture service would refuse it the same way.' -Fix @(
            'Run scripts\windows\demo\setup.cmd again and answer Yes when Windows asks about the certificate.')
    }
    if ($probe.Failure -eq 'SecureChannelFailure') {
        Fail -Problem 'The backend answered, but the HTTPS handshake failed (no protocol both sides accept).' -Fix @(
            'Run setup.cmd again to re-create the certificate. If it repeats, run scripts\windows\demo\report.cmd',
            'and send the file it puts on your Desktop to the developer.')
    }
    if ($probe.Status -gt 0) {
        Fail -Problem "The backend answered /health with HTTP $($probe.Status) instead of 200." -Fix @(
            'Run scripts\windows\demo\report.cmd and send the file it puts on your Desktop to the developer.')
    }
    Start-Sleep -Seconds 1
}
if (-not $healthy) {
    $log = Read-SharedLog $script:DemoBackendLog
    Fail -Problem 'The backend did not answer within 90 seconds.' -Detail (Get-Tail -Lines $log) -Fix @(
        'Read the backend window. The first start after a build can be slow; try once more before digging.')
}
Write-Ok "The backend answers at $script:DemoBackendUrl/health over a trusted certificate."

# The backend's own process, so stop can be sure it is gone even if the window is closed by hand.
$listener = Get-PortOwner $script:DemoBackendPort
if ($listener) {
    $hostProcess = Get-Process -Id $listener.Id -ErrorAction SilentlyContinue
    if ($hostProcess) { Add-Entry (New-ProcessEntry -Role 'backend' -Process $hostProcess) }
}

# --- 3. The device token --------------------------------------------------------------------------------------

Write-Step 'Device token'
$token = $null
$expires = $null
if (Test-Path -LiteralPath $script:DemoDeviceCredential) {
    [IO.File]::WriteAllText($script:DemoTokenFile, 'activated')
    Write-Ok 'This device was activated with an invite, so it renews its own token. No development token is needed.'
    Write-Note 'If a draft later says the device is not enrolled or was revoked, see "Activated device" in docs\dev\demo.md.'
}
else {
    # A token already in this window's environment is used if it is a JWT with at least 20 minutes left
    # and the backend accepts it. Otherwise a Development device is enrolled for a fresh one.
    $candidate = $env:SCREENTAIL_DEVICE_TOKEN
    $candidateExpires = Get-TokenExpiry $candidate
    if ($candidate -and $candidateExpires -and $candidateExpires -gt [DateTimeOffset]::UtcNow.AddMinutes(20)) {
        $check = Invoke-Probe -Url "$script:DemoBackendUrl/v1/me" -Headers @{ 'Authorization' = "Bearer $candidate" }
        if ($check.Status -eq 200) {
            $token = $candidate
            $expires = $candidateExpires
            Write-Ok 'Using the device token already in this window''s environment.'
        }
    }
    elseif ($candidate) {
        Write-Note 'The device token in this window''s environment has expired or is about to; getting a new one.'
    }

    if (-not $token) {
        $enrol = Invoke-WithEnvironment -Variables @{ 'ASPNETCORE_ENVIRONMENT' = 'Development'; 'ASPNETCORE_URLS' = $null } -Body {
            Invoke-Native -FilePath 'dotnet' -ArgumentList @($apiDll, '--enrol-dev-device') -WorkingDirectory $script:DemoApiDir
        }
        foreach ($line in $enrol.Lines) {
            $match = [regex]::Match($line, '^SCREENTAIL_DEVICE_TOKEN=(\S+)\s*$')
            if ($match.Success) { $token = $match.Groups[1].Value }
        }
        if ($enrol.ExitCode -ne 0 -or -not $token) {
            Fail -Problem 'Could not enrol a development device.' -Detail (Get-Tail -Lines $enrol.Lines) -Fix @(
                'If the lines mention the database, check PostgreSQL. If they mention Development, send them to the developer.')
        }
        $expires = Get-TokenExpiry $token
    }

    $me = Invoke-Probe -Url "$script:DemoBackendUrl/v1/me" -Headers @{ 'Authorization' = "Bearer $token" }
    if ($me.Status -eq 401) {
        Fail -Problem 'The backend refused the device token (401). It has expired, or it was signed with a different key.' -Fix @(
            'Start again; it enrols a fresh one. If this repeats straight after enrolling, run setup.cmd again.')
    }
    if ($me.Status -ne 200) {
        Fail -Problem "The backend answered the token check with HTTP $($me.Status) instead of 200." -Fix @(
            'Run scripts\windows\demo\report.cmd and send the file it puts on your Desktop to the developer.')
    }
    # When it runs out, for report.cmd. The time only; the token itself goes to the service and nowhere
    # else.
    if ($expires) { $until = $expires.ToUniversalTime().ToString('o') } else { $until = 'unknown' }
    [IO.File]::WriteAllText($script:DemoTokenFile, "development $until")
    if ($expires) {
        Write-Ok ('The backend accepts the device token. It is good until ' + $expires.ToLocalTime().ToString('HH:mm') + ' (one hour).')
    }
    else {
        Write-Ok 'The backend accepts the device token.'
    }
}

# --- 4. The client ----------------------------------------------------------------------------------------------

Write-Step 'The capture service and the UI'
$serviceExe = Join-Path $script:DemoAppDir 'ScreenTail.Service.exe'
$uiExe = Join-Path $script:DemoAppDir 'ScreenTail.UI.exe'

# Publishing takes a minute or two, so it is done only when something under client\ or shared\ is newer
# than the last publish. bin and obj are build output and do not count.
function Get-NewestSourceTicks {
    $newest = 0
    $pending = New-Object System.Collections.Stack
    foreach ($root in @('client', 'shared')) {
        $path = Join-Path $script:DemoRepo $root
        if (Test-Path -LiteralPath $path) { $pending.Push($path) }
    }
    foreach ($file in @('Directory.Build.props', 'global.json')) {
        $path = Join-Path $script:DemoRepo $file
        if (Test-Path -LiteralPath $path) {
            $ticks = [IO.File]::GetLastWriteTimeUtc($path).Ticks
            if ($ticks -gt $newest) { $newest = $ticks }
        }
    }
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($file in [IO.Directory]::GetFiles($directory)) {
            $ticks = [IO.File]::GetLastWriteTimeUtc($file).Ticks
            if ($ticks -gt $newest) { $newest = $ticks }
        }
        foreach ($child in [IO.Directory]::GetDirectories($directory)) {
            $leaf = [IO.Path]::GetFileName($child)
            if ($leaf -notin @('bin', 'obj', '.vs', 'TestResults', 'node_modules')) { $pending.Push($child) }
        }
    }
    return $newest
}

$sourceTicks = Get-NewestSourceTicks
[long]$stampTicks = 0
if (Test-Path -LiteralPath $script:DemoBuildStamp) {
    [long]::TryParse(([IO.File]::ReadAllText($script:DemoBuildStamp)).Trim(), [ref]$stampTicks) | Out-Null
}
$publish = $Rebuild -or -not (Test-Path -LiteralPath $serviceExe) -or -not (Test-Path -LiteralPath $uiExe) -or $sourceTicks -gt $stampTicks

if ($publish) {
    Write-Note 'Publishing the capture service and the UI into one folder (a few minutes the first time)...'
    try {
        & $script:DemoRunLocal -Output $script:DemoAppDir -NoLaunch
    }
    catch {
        Fail -Problem 'Publishing the capture service and the UI failed.' -Detail @(Protect-Text $_.Exception.Message) -Fix @(
            'Read the lines above it. Make sure the clone is up to date (git pull); if it still fails, send them to the developer.')
    }
    if (-not (Test-Path -LiteralPath $serviceExe) -or -not (Test-Path -LiteralPath $uiExe)) {
        Fail -Problem "Publishing finished, but the two programs are not in $script:DemoAppDir." -Fix @('Read the publishing output above, and send it to the developer.')
    }
    [IO.File]::WriteAllText($script:DemoBuildStamp, [string]$sourceTicks)
    Write-Ok "Published into $script:DemoAppDir."
}
else {
    Write-Ok 'The published client is up to date (start with -Rebuild to publish it again anyway).'
}

if (Test-Path -LiteralPath $script:DemoServiceExited) { Remove-Item -LiteralPath $script:DemoServiceExited -Force }

# The service writes its own log to stderr. The window shows it and writes the logger's lines to
# logs\service.log through the log writer (anything else the service prints is withheld there). If the
# service cannot start at all (an Application Control policy, say), the window says so and the reason is
# written where start can read it.
#
# The output is read on a second thread rather than piped through PowerShell, because PowerShell ends a
# piped program the moment Ctrl+C is pressed, and Ctrl+C is how the service is asked to finish cleanly.
# The service shares this window's console, so it still gets the Ctrl+C itself, and the second thread
# keeps reading while it finishes. That thread is the log's only writer until the output ends.
$serviceScript = @'
$writer = [IO.File]::ReadAllText(__LOGWRITER__)
. ([scriptblock]::Create($writer))
$ErrorActionPreference = 'Continue'
$Host.UI.RawUI.WindowTitle = 'ScreenTail demo: capture service'
Set-Location -LiteralPath __DIR__
Write-Host 'ScreenTail capture service. Ctrl+C here stops capture cleanly; stop.cmd closes this window.'
$script:DemoLog.Path = __LOG__
$code = 'unknown'
$service = $null
$copying = $null
try {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = __EXE__
    $start.WorkingDirectory = __DIR__
    $start.UseShellExecute = $false
    $start.RedirectStandardError = $true
    $service = [Diagnostics.Process]::Start($start)
    $copy = {
        param($Reader, $Path, $Writer)
        . ([scriptblock]::Create($Writer))
        $script:DemoLog.Path = $Path
        while ($true) {
            $line = $Reader.ReadLine()
            if ($null -eq $line) { break }
            [Console]::WriteLine($line)
            Write-DemoLog $line
        }
        Close-DemoLog
    }
    $copier = [powershell]::Create()
    $null = $copier.AddScript($copy.ToString()).AddArgument($service.StandardError).AddArgument(__LOG__).AddArgument($writer)
    $copying = $copier.BeginInvoke()
    while (-not $service.WaitForExit(500)) { }
    $code = "exit code $($service.ExitCode)"
}
catch {
    $code = $_.Exception.Message
    Write-Host $code -ForegroundColor Red
    if (-not $copying) { Write-DemoLogText "[start.ps1] The capture service could not run: $code" }
}
finally {
    if ($service) {
        # After Ctrl+C this window stops waiting at once, and the service takes a few seconds to finish.
        if ($service.WaitForExit(35000)) { $code = "exit code $($service.ExitCode)" } else { $code = 'still running; close this window to end it' }
    }
    $copied = $true
    if ($copying) { $copied = $copying.AsyncWaitHandle.WaitOne(3000) }
    Write-Host ''
    Write-Host "The capture service has stopped ($code). This window stays open so you can read why." -ForegroundColor Yellow
    if ($copied) { Write-DemoLogText "[start.ps1] The capture service has stopped ($code)." }
    Close-DemoLog
    Set-Content -LiteralPath __EXITED__ -Value $code
}
'@
$serviceScript = $serviceScript.Replace('__LOGWRITER__', (ConvertTo-Quoted $script:DemoLogWriterFile))
$serviceScript = $serviceScript.Replace('__LOG__', (ConvertTo-Quoted $script:DemoServiceLog))
$serviceScript = $serviceScript.Replace('__DIR__', (ConvertTo-Quoted $script:DemoAppDir))
$serviceScript = $serviceScript.Replace('__EXE__', (ConvertTo-Quoted $serviceExe))
$serviceScript = $serviceScript.Replace('__EXITED__', (ConvertTo-Quoted $script:DemoServiceExited))

# The two variables the service reads, in its environment only. The token travels in the environment
# block, never on a command line. With an activated device the token is removed instead, so nothing stale
# from the user's environment is inherited.
$serviceWindow = Invoke-WithEnvironment -Variables @{ 'SCREENTAIL_BACKEND' = $script:DemoBackendUrl; 'SCREENTAIL_DEVICE_TOKEN' = $token } -Body {
    Start-DemoWindow -Script $serviceScript
}
Add-Entry (New-ProcessEntry -Role 'service-window' -Process $serviceWindow)

Write-Note 'Waiting for the capture service to open its pipe (up to 30 seconds)...'
$service = $null
$listening = $false
$pipesUnknown = $false
for ($second = 0; $second -lt 30; $second++) {
    if (Test-Path -LiteralPath $script:DemoServiceExited) {
        # The window may still be writing it.
        Start-Sleep -Milliseconds 300
        $reason = ((Read-SharedLog $script:DemoServiceExited) -join ' ').Trim()
        if (-not $reason) { $reason = '(no reason recorded)' }
        if ($reason -match 'Application Control|blocked by|group policy') {
            Fail -Problem 'Windows blocked the capture service from running (an Application Control policy). Fresh unsigned builds trip it on this laptop.' -Detail @($reason) -Fix @(
                'Allow it in Windows Security > App & browser control, or ask the developer (ticket ST-112 is the signing fix).')
        }
        Fail -Problem 'The capture service stopped straight after starting.' -Detail @($reason) -Fix @(
            'Read its window (titled "ScreenTail demo: capture service"). If it says another service is running, run stop.cmd and start again.')
    }
    if (-not $service) {
        $service = Get-Process -Name 'ScreenTail.Service' -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $serviceExe } | Select-Object -First 1
        if ($service) { Add-Entry (New-ProcessEntry -Role 'service' -Process $service) }
    }
    try {
        $pipes = @([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*\ScreenTail.*' })
    }
    catch {
        # .NET Framework refuses to list the pipes when any pipe on the machine has a name that is not a
        # valid path. Then all that can be said is that the service is still running after a few seconds.
        $pipes = @()
        $pipesUnknown = $true
    }
    if ($service -and $pipes.Count -gt 0) {
        $listening = $true
        break
    }
    if ($service -and $pipesUnknown -and $second -ge 5) {
        break
    }
    Start-Sleep -Seconds 1
}
if (-not $service) {
    Fail -Problem 'The capture service did not start.' -Fix @('Read its window (titled "ScreenTail demo: capture service").')
}
if ($listening) {
    Write-Ok 'The capture service is running and listening for the UI.'
}
elseif ($pipesUnknown) {
    Write-Ok 'The capture service is running. (Windows would not list its pipe, so whether the UI can reach it shows on the tray icon.)'
}
else {
    Write-Caution 'The capture service is running, but its pipe did not appear in 30 seconds. The UI may show "service unavailable" for a moment.'
}

try {
    $ui = Invoke-WithEnvironment -Variables @{ 'SCREENTAIL_DEVICE_TOKEN' = $null; 'SCREENTAIL_BACKEND' = $null } -Body {
        Start-Process -FilePath $uiExe -WorkingDirectory $script:DemoAppDir -PassThru
    }
}
catch {
    $reason = Protect-Text $_.Exception.Message
    if ($reason -match 'Application Control|blocked by|group policy') {
        Fail -Problem 'Windows blocked the UI from running (an Application Control policy).' -Detail @($reason) -Fix @(
            'Allow it in Windows Security > App & browser control, or ask the developer (ST-112).')
    }
    Fail -Problem 'The UI could not be started.' -Detail @($reason)
}
Add-Entry (New-ProcessEntry -Role 'ui' -Process $ui)
Start-Sleep -Seconds 4
if ($ui.HasExited) {
    Fail -Problem "The UI closed straight after starting (exit code $($ui.ExitCode))." -Fix @(
        'Start again. If it repeats, set SCREENTAIL_CRASH_REPORTS=1 in this window, start again, and send the crash report to the developer.')
}
Write-Ok 'The UI is running: the ScreenTail icon is in the notification area (bottom right; it may be under the ^ arrow).'

# --- Ready ------------------------------------------------------------------------------------------------------

Write-Host ''
Write-Host 'READY.' -ForegroundColor Green
Write-Note "Backend: $script:DemoBackendUrl (window 'ScreenTail demo: backend')"
Write-Note "Capture service: window 'ScreenTail demo: capture service'"
Write-Note 'UI: the tray icon. The first time, the setup wizard opens: on Activate, press Skip for now.'
if ($expires) {
    Write-Note ('Device token good until ' + $expires.ToLocalTime().ToString('HH:mm') + '. If the demo runs later than that, run stop.cmd then start.cmd.')
}
Write-Host ''
Write-Host 'Do this next: open Remote Desktop to the demo PC (the recording pill appears by itself), or put a window' -ForegroundColor Green
Write-Host 'in front and press Ctrl+Alt+R. Ctrl+Alt+S stops and drafts. To end: scripts\windows\demo\stop.cmd' -ForegroundColor Green
