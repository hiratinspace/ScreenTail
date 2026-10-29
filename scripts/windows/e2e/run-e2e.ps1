<#
.SYNOPSIS
    ST-013: runs ScreenTail's whole loop on this Windows machine, unattended, and says which step broke.

.DESCRIPTION
    Starts PostgreSQL, creates a database of its own, starts the backend in Development with the demo PSA
    and the scripted drafting provider (no model key, no cost), enrols a development device, publishes and
    starts the capture service and the UI the way the demo scripts do, then runs the driver
    (client\ScreenTail.E2E), which takes the session through the pipe commands the UI uses: a test window
    with known text, All windows, start, captures, stop, the draft, publish to demo ticket #48213, and
    checks of what was stored and what the backend received. Last, it stops everything and checks that
    each process stopped cleanly and that the run's fake secret appears in no log.

    Every step prints one line, PASS or FAIL, with what it waited for. setup-NN lines are this script's,
    loop-NN lines the driver's, and teardown-NN lines the shutdown's.

    Nothing is installed for good and nothing is set machine-wide, with one exception on a CI runner:
    the ASP.NET Core development certificate is created and trusted for the machine, because the hosted
    VM has none and is thrown away after the job. On the laptop, run scripts\windows\demo\setup.cmd once
    first; this script then uses the database server and the certificate setup made, in a database of
    its own (screentail_e2e), and on a port of its own, so it does not touch the demo's.

    No image leaves the machine: the outputs are text logs, and the driver checks the stored frames
    through the service's own answers, never by reading pixels.

    docs/dev/e2e.md says what each step proves.

.PARAMETER Port
    The backend's port. Not the demo's 5001, so a demo backend left running is not in the way.

.PARAMETER OutDir
    Where the logs go. $env:RUNNER_TEMP\e2e on a runner, %LOCALAPPDATA%\ScreenTail.Dev\e2e otherwise.

.PARAMETER InjectClicks
    Real left clicks on the test window instead of mark-moment captures. Takes over the mouse, so it also
    needs SCREENTAIL_ALLOW_INPUT_INJECTION=1, as the laptop's input job sets.

.EXAMPLE
    scripts\windows\e2e\run-e2e.cmd
#>
[CmdletBinding()]
param(
    [int]$Port = 5071,
    [string]$OutDir,
    [switch]$InjectClicks
)

. (Join-Path $PSScriptRoot '..\demo\common.ps1')

$script:Failures = 0
$script:Passes = 0
$script:StepNumber = @{}
$script:Started = @()

if (-not $OutDir) {
    if ($env:RUNNER_TEMP) { $OutDir = Join-Path $env:RUNNER_TEMP 'e2e' } else { $OutDir = Join-Path $env:LOCALAPPDATA 'ScreenTail.Dev\e2e' }
}
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$script:Log = Join-Path $OutDir 'e2e.log'
[IO.File]::WriteAllText($script:Log, '', (New-Object Text.UTF8Encoding $false))

$AppDir = Join-Path $env:LOCALAPPDATA 'ScreenTail.Dev\e2e-app'
$BackendUrl = "https://localhost:$Port"
$OnHostedRunner = ($env:RUNNER_ENVIRONMENT -eq 'github-hosted')
$DataDir = Join-Path $env:LOCALAPPDATA 'ScreenTail'

# --- Output ------------------------------------------------------------------------------------------

function Write-Line([string]$Text) {
    Write-Host $Text
    [IO.File]::AppendAllText($script:Log, $Text + "`r`n", (New-Object Text.UTF8Encoding $false))
}

function Step {
    param([Parameter(Mandatory)][string]$Phase, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$WaitingFor, [Parameter(Mandatory)][scriptblock]$Body)

    $number = 1 + [int]$script:StepNumber[$Phase]
    $script:StepNumber[$Phase] = $number
    $label = '{0}-{1:00} {2}' -f $Phase, $number, $Name
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $detail = & $Body
        $script:Passes++
        Write-Line ('PASS {0} ({1:0.0} s): waited for {2}. {3}' -f $label, $clock.Elapsed.TotalSeconds, $WaitingFor, (@($detail) -join ' '))
        return $true
    }
    catch {
        $script:Failures++
        Write-Line ('FAIL {0} ({1:0.0} s): waited for {2}. {3}' -f $label, $clock.Elapsed.TotalSeconds, $WaitingFor, (Protect-Text $_.Exception.Message))
        return $false
    }
}

function Finish {
    Write-Line ''
    if ($script:Failures -eq 0) {
        Write-Line "PASS e2e: all $script:Passes steps passed. Logs: $OutDir"
        exit 0
    }
    Write-Line "FAIL e2e: $script:Failures step(s) failed, $script:Passes passed. Logs: $OutDir"
    exit 1
}

# A fake secret for this run only, so a leak cannot be a coincidence and nothing secret-shaped sits in the
# repository. Two words and four digits: the shape a password typed into a support session has.
function New-FakeSecret {
    $bytes = New-Object byte[] 4
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    $first = @('Tangerine', 'Marigold', 'Juniper', 'Lavender', 'Saffron')[$bytes[0] % 5]
    $second = @('Wobble', 'Quiver', 'Ramble', 'Tumble', 'Glimmer')[$bytes[1] % 5]
    $digits = '{0:0000}' -f ((([int]$bytes[2]) * 256 + [int]$bytes[3]) % 10000)
    return $first + $digits + $second
}

function Start-Logged {
    param([string]$Role, [string]$FilePath, [string[]]$Arguments = @(), [string]$WorkingDirectory, [hashtable]$Environment)

    $out = Join-Path $OutDir "$Role.out.log"
    $err = Join-Path $OutDir "$Role.err.log"
    $process = Invoke-WithEnvironment -Variables $Environment -Body {
        $startArgs = @{
            FilePath = $FilePath
            WorkingDirectory = $WorkingDirectory
            WindowStyle = 'Hidden'
            RedirectStandardOutput = $out
            RedirectStandardError = $err
            PassThru = $true
        }
        if ($Arguments.Count -gt 0) { $startArgs['ArgumentList'] = $Arguments }
        Start-Process @startArgs
    }
    # Windows PowerShell 5.1 forgets a started process's exit code unless its handle was opened while it
    # ran; touching it here is what makes ExitCode readable after the process stops.
    $null = $process.Handle
    $script:Started = @($script:Started) + @([pscustomobject]@{ Role = $Role; Process = $process })
    return $process
}

function Read-Logs([string]$Role) {
    return @(Read-SharedLog (Join-Path $OutDir "$Role.out.log")) + @(Read-SharedLog (Join-Path $OutDir "$Role.err.log"))
}

# The letters of the secret in runs of five or more, so a log that holds it with one digit misread still counts.
function Get-SecretParts([string]$Secret) {
    return @([regex]::Matches($Secret, '[A-Za-z]{5,}') | ForEach-Object { $_.Value }) + @($Secret)
}

Write-Line "ScreenTail end-to-end run (ST-013), $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') on $env:COMPUTERNAME"
Write-Line "Backend $BackendUrl, logs in $OutDir, clicks: $(if ($InjectClicks) { 'real (SendInput)' } else { 'mark moment over the pipe' })"
Write-Line ''

# --- Setup -------------------------------------------------------------------------------------------

$secret = New-FakeSecret
$signingKey = New-SigningKey

if (-not (Step 'setup' 'windows' 'Windows PowerShell 5.1 or later inside a ScreenTail clone' {
    if ($env:OS -ne 'Windows_NT') { throw 'This is not Windows.' }
    if ($PSVersionTable.PSVersion.Major -lt 5) { throw "PowerShell $($PSVersionTable.PSVersion) is too old." }
    if (-not (Test-Path -LiteralPath $script:DemoApiProject)) { throw "No backend project under $script:DemoRepo." }
    "PowerShell $($PSVersionTable.PSVersion), repository $script:DemoRepo."
})) { Finish }

if (-not (Step 'setup' 'dotnet' 'a .NET 10 SDK that global.json accepts' {
    $result = Invoke-Native -FilePath 'dotnet' -ArgumentList @('--version') -WorkingDirectory $script:DemoRepo
    $version = ($result.Lines | Select-Object -Last 1)
    if ($result.ExitCode -ne 0 -or -not ($version -match '^10\.')) { throw "dotnet --version said: $version" }
    "SDK $version."
})) { Finish }

if (-not (Step 'setup' 'nothing-running' 'no ScreenTail service or UI already running for this user' {
    $others = @(Get-Process -Name 'ScreenTail.Service', 'ScreenTail.UI', 'ScreenTail.E2E' -ErrorAction SilentlyContinue)
    if ($others.Count -gt 0) {
        throw ('Already running: ' + (@($others | ForEach-Object { "$($_.ProcessName) pid $($_.Id)" }) -join ', ') + '. Stop the demo (scripts\windows\demo\stop.cmd) or quit ScreenTail first.')
    }
    if (Test-Path -LiteralPath $script:DemoDeviceCredential) {
        throw "This machine has an invite-activated device ($script:DemoDeviceCredential), which the service prefers to a development token, and the e2e database does not know it. See 'Activated device' in docs\dev\demo.md."
    }
    if (Get-PortOwner $Port) { throw "Port $Port is in use by $((Get-PortOwner $Port).Name)." }
    'Nothing in the way.'
})) { Finish }

$connection = $null
if (-not (Step 'setup' 'postgres' 'PostgreSQL to answer' {
    if ($OnHostedRunner -and @(Get-PostgresServices).Count -gt 0) {
        # The hosted image ships PostgreSQL as a disabled service, with its credentials in PGUSER and
        # PGPASSWORD (actions/runner-images, Windows databases).
        $service = @(Get-PostgresServices)[0]
        if ($service.Status -ne 'Running') {
            Set-Service -Name $service.Name -StartupType Manual
            Start-Service -Name $service.Name
        }
        $user = if ($env:PGUSER) { $env:PGUSER } else { 'postgres' }
        $password = if ($env:PGPASSWORD) { $env:PGPASSWORD } else { 'root' }
        $script:connection = New-ConnectionString -HostName 'localhost' -Port 5432 -User $user -Password $password
        $where = "the runner's $($service.Name) service"
    }
    else {
        $saved = (Read-UserSecrets)[$script:KeyDatabase]
        if (-not $saved) { throw 'No database connection is saved. Run scripts\windows\demo\setup.cmd once first.' }
        $script:connection = $saved
        $where = 'the server demo setup saved'
    }

    # A database of the test's own, so a run never touches the demo's sessions or tenants.
    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder.set_ConnectionString($script:connection)
    $builder['Database'] = 'screentail_e2e'
    $script:connection = $builder.get_ConnectionString()

    $parts = Get-ConnectionParts $script:connection
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-TcpPort -HostName $parts.Host -Port $parts.Port)) {
        if ((Get-Date) -gt $deadline) { throw "Nothing answers on $($parts.Host):$($parts.Port) after 60 s ($where)." }
        Start-Sleep -Seconds 1
    }
    "$where answers on $($parts.Host):$($parts.Port); database screentail_e2e."
})) { Finish }
$connection = $script:connection

if (-not (Step 'setup' 'certificate' 'the development certificate to be trusted, as the client checks it' {
    if (Test-DevCertificateTrusted) { return 'Already trusted for this user.' }
    if (-not $OnHostedRunner) { throw 'The development certificate is missing or not trusted. Run scripts\windows\demo\setup.cmd (answer Yes to Windows).' }

    # A hosted VM: create the certificate, then trust it for the machine. The per-user trust store asks
    # with a dialog nobody can answer; the machine store, as the runner's administrator, does not.
    $created = Invoke-Native -FilePath 'dotnet' -ArgumentList @('dev-certs', 'https')
    if ($created.ExitCode -ne 0) { throw ('dotnet dev-certs https failed: ' + ((Get-Tail -Lines $created.Lines -Count 4) -join ' ')) }
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Extensions | Where-Object { $_.Oid.Value -eq '1.3.6.1.4.1.311.84.1.1' } } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $certificate) { throw 'dotnet dev-certs made no certificate in CurrentUser\My.' }
    $file = Join-Path $OutDir 'devcert.cer'
    Export-Certificate -Cert $certificate -FilePath $file -Type CERT | Out-Null
    Import-Certificate -FilePath $file -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Remove-Item -LiteralPath $file -Force
    'Created and trusted for this machine (a CI runner, thrown away after the job).'
})) { Finish }

$apiDll = $null
if (-not (Step 'setup' 'backend-build' 'the backend to build' {
    $build = Invoke-Native -FilePath 'dotnet' -ArgumentList @('build', $script:DemoApiProject, '-c', 'Debug', '--nologo', '-v', 'quiet')
    if ($build.ExitCode -ne 0) { throw ('The backend does not build: ' + ((Get-Tail -Lines $build.Lines -Count 8) -join ' | ')) }
    $target = Invoke-Native -FilePath 'dotnet' -ArgumentList @('msbuild', $script:DemoApiProject, '-getProperty:TargetPath', '-p:Configuration=Debug', '-nologo')
    $dll = (@($target.Lines | Where-Object { $_.Trim() }) | Select-Object -Last 1)
    if (-not $dll -or -not (Test-Path -LiteralPath $dll.Trim())) { throw 'The backend built and its output could not be found.' }
    $script:apiDll = $dll.Trim()
    'Built (Debug).'
})) { Finish }
$apiDll = $script:apiDll

if (-not (Step 'setup' 'migrations' 'every migration to apply to screentail_e2e' {
    Add-DotNetToolsToPath
    if (-not (Get-EfToolInstalledVersion)) {
        $install = Invoke-Native -FilePath 'dotnet' -ArgumentList @('tool', 'install', '--global', 'dotnet-ef', '--version', (Get-EfToolVersion))
        if ($install.ExitCode -ne 0) { throw ('dotnet-ef would not install: ' + ((Get-Tail -Lines $install.Lines -Count 4) -join ' ')) }
    }
    $update = Invoke-DatabaseUpdate -ConnectionString $connection -NoBuild
    if ($update.Outcome -ne 'ok') { throw "Migrations: $($update.Outcome). $(($update.Detail | Select-Object -Last 4) -join ' | ')" }
    "$($update.Applied) applied (zero means it was up to date)."
})) { Finish }

$backendEnvironment = @{
    'ASPNETCORE_ENVIRONMENT' = 'Development'
    'ASPNETCORE_URLS' = $BackendUrl
    'ConnectionStrings__Postgres' = $connection
    'Jwt__SigningKey' = $signingKey
    'Providers__Demo' = 'true'
    'Summarization__Provider' = 'scripted'
    'SCREENTAIL_DEVICE_TOKEN' = $null
    'SCREENTAIL_E2E_SECRET' = $null
}

$backend = $null
if (-not (Step 'setup' 'backend' "the backend to answer /health over a trusted certificate on $BackendUrl" {
    $script:backend = Start-Logged -Role 'backend' -FilePath 'dotnet' -Arguments @("`"$apiDll`"") -WorkingDirectory $script:DemoApiDir -Environment $backendEnvironment
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if ($script:backend.HasExited) { throw ('The backend stopped while starting: ' + ((Get-Tail -Lines (Read-Logs 'backend') -Count 6 -Secrets @($signingKey)) -join ' | ')) }
        $probe = Invoke-Probe -Url "$BackendUrl/health" -TimeoutSeconds 3
        if ($probe.Status -eq 200) { return "pid $($script:backend.Id), Development, demo PSA and scripted drafting on." }
        if ($probe.Failure -eq 'TrustFailure') { throw 'The backend answered and Windows does not trust its certificate.' }
        Start-Sleep -Seconds 1
    }
    throw 'No answer from /health in 90 s.'
})) { Finish }
$backend = $script:backend

$token = $null
if (-not (Step 'setup' 'enrol' 'a development device token the backend accepts' {
    $enrolEnvironment = $backendEnvironment.Clone()
    $enrolEnvironment['ASPNETCORE_URLS'] = $null
    $enrol = Invoke-WithEnvironment -Variables $enrolEnvironment -Body {
        Invoke-Native -FilePath 'dotnet' -ArgumentList @($apiDll, '--enrol-dev-device') -WorkingDirectory $script:DemoApiDir
    }
    foreach ($line in $enrol.Lines) {
        $match = [regex]::Match($line, '^SCREENTAIL_DEVICE_TOKEN=(\S+)\s*$')
        if ($match.Success) { $script:token = $match.Groups[1].Value }
    }
    if ($enrol.ExitCode -ne 0 -or -not $script:token) { throw ('Enrolment failed: ' + ((Get-Tail -Lines $enrol.Lines -Count 4 -Secrets @($signingKey)) -join ' | ')) }
    $me = Invoke-Probe -Url "$BackendUrl/v1/me" -Headers @{ 'Authorization' = "Bearer $script:token" }
    if ($me.Status -ne 200) { throw "The backend answered /v1/me with $($me.Status)." }
    'Enrolled; the backend accepts the token (value not shown).'
})) { Finish }
$token = $script:token

if (-not (Step 'setup' 'client-publish' 'the capture service, the UI and the driver published into one folder' {
    & $script:DemoRunLocal -Output $AppDir -NoLaunch | Out-Null
    $driverProject = Join-Path $script:DemoRepo 'client\ScreenTail.E2E\ScreenTail.E2E.csproj'
    $published = Invoke-Native -FilePath 'dotnet' -ArgumentList @('publish', $driverProject, '-c', 'Debug', '-o', $AppDir, '--nologo', '-v', 'quiet')
    if ($published.ExitCode -ne 0) { throw ('Publishing the driver failed: ' + ((Get-Tail -Lines $published.Lines -Count 6) -join ' | ')) }
    foreach ($exe in @('ScreenTail.Service.exe', 'ScreenTail.UI.exe', 'ScreenTail.E2E.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $AppDir $exe))) { throw "$exe is not in $AppDir." }
    }
    "Service, UI and driver in $AppDir."
})) { Finish }

$serviceEnvironment = @{
    'SCREENTAIL_BACKEND' = $BackendUrl
    'SCREENTAIL_DEVICE_TOKEN' = $token
    'SCREENTAIL_LOG_LEVEL' = 'Debug'
    'SCREENTAIL_CRASH_REPORTS' = '1'
    'SCREENTAIL_E2E_SECRET' = $null
}
$crashDir = Join-Path $DataDir 'crashes'
$crashesBefore = @()
if (Test-Path -LiteralPath $crashDir) { $crashesBefore = @(Get-ChildItem -LiteralPath $crashDir -File | ForEach-Object { $_.Name }) }

$service = $null
if (-not (Step 'setup' 'service' 'the capture service to start and publish its pipe token' {
    $tokenFile = Join-Path $DataDir 'ipc.token'
    $before = if (Test-Path -LiteralPath $tokenFile) { (Get-Item -LiteralPath $tokenFile).LastWriteTimeUtc } else { [DateTime]::MinValue }
    $script:service = Start-Logged -Role 'service' -FilePath (Join-Path $AppDir 'ScreenTail.Service.exe') -WorkingDirectory $AppDir -Environment $serviceEnvironment
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        if ($script:service.HasExited) { throw ("The service exited with code $($script:service.ExitCode): " + ((Get-Tail -Lines (Read-Logs 'service') -Count 6) -join ' | ')) }
        if ((Test-Path -LiteralPath $tokenFile) -and (Get-Item -LiteralPath $tokenFile).LastWriteTimeUtc -gt $before) {
            $started = @(Read-Logs 'service' | Where-Object { $_ -match 'Capture service started' })
            if ($started.Count -gt 0) { return "pid $($script:service.Id). $(($started[-1] -replace '^.*?(Capture service started)', '$1'))" }
        }
        Start-Sleep -Milliseconds 500
    }
    throw ('The service did not say it started in 45 s: ' + ((Get-Tail -Lines (Read-Logs 'service') -Count 6) -join ' | '))
})) { Finish }
$service = $script:service

$ui = $null
if (-not (Step 'setup' 'ui' 'the UI to start and stay up' {
    $uiEnvironment = @{ 'SCREENTAIL_DEVICE_TOKEN' = $null; 'SCREENTAIL_BACKEND' = $null; 'SCREENTAIL_CRASH_REPORTS' = '1'; 'SCREENTAIL_E2E_SECRET' = $null }
    $script:ui = Invoke-WithEnvironment -Variables $uiEnvironment -Body {
        Start-Process -FilePath (Join-Path $AppDir 'ScreenTail.UI.exe') -WorkingDirectory $AppDir -PassThru
    }
    $null = $script:ui.Handle
    $script:Started = @($script:Started) + @([pscustomobject]@{ Role = 'ui'; Process = $script:ui })
    Start-Sleep -Seconds 5
    if ($script:ui.HasExited) { throw "The UI exited with code $($script:ui.ExitCode)." }
    "pid $($script:ui.Id)."
})) { Finish }
$ui = $script:ui

# --- The loop ----------------------------------------------------------------------------------------

Write-Line ''
$driverArgs = @('--backend', $BackendUrl)
if ($InjectClicks) { $driverArgs += '--inject-clicks' }
$driverCode = Invoke-WithEnvironment -Variables @{ 'SCREENTAIL_E2E_SECRET' = $secret; 'SCREENTAIL_DEVICE_TOKEN' = $null } -Body {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # The secret is not scrubbed from the driver's lines: the driver never prints it, and the log
        # check below is how a line that did would be caught.
        & (Join-Path $AppDir 'ScreenTail.E2E.exe') @driverArgs 2>&1 | ForEach-Object { Write-Line (Protect-Text -Text "$_" -Secrets @($token, $signingKey)) }
        $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
}
if ($driverCode -ne 0) { $script:Failures++ } else { $script:Passes++ }
Write-Line ''

# --- Teardown ----------------------------------------------------------------------------------------

$driverExe = Join-Path $AppDir 'ScreenTail.E2E.exe'

function Stop-Console {
    param($Process, [string]$Role, [string]$Said)

    if ($Process.HasExited) { throw "$Role had already exited, with code $($Process.ExitCode), before it was asked to stop." }
    $sent = Invoke-Native -FilePath $driverExe -ArgumentList @('--stop', [string]$Process.Id)
    if ($sent.ExitCode -ne 0) {
        $why = switch ($sent.ExitCode) {
            3 { 'its console could not be attached to' }
            4 { 'its console is shared with other processes, which the event would reach too' }
            5 { 'GenerateConsoleCtrlEvent failed' }
            default { "the helper exited $($sent.ExitCode)" }
        }
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        throw "Ctrl+Break could not be sent to $Role ($why); it was killed instead."
    }
    if (-not $Process.WaitForExit(30000)) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        throw "$Role did not stop within 30 s of Ctrl+Break, and was killed."
    }
    $Process.WaitForExit()
    $said = @(Read-Logs $Role | Where-Object { $_ -match $Said })
    if ($Process.ExitCode -ne 0) { throw "$Role exited with code $($Process.ExitCode)." }
    if ($said.Count -eq 0) { throw "$Role exited 0 without logging that it was stopping ('$Said')." }
    "Exit code 0, and it said it was stopping."
}

$null = Step 'teardown' 'ui-alive' 'the UI to still be running after the whole session' {
    if ($ui.HasExited) { throw "The UI exited during the run, with code $($ui.ExitCode)." }
    'Still running.'
}

$null = Step 'teardown' 'service-stop' 'the capture service to stop cleanly on Ctrl+Break' {
    Stop-Console -Process $service -Role 'service' -Said 'Capture service stopping'
}

$null = Step 'teardown' 'service-log-level' 'the service to log at the level SCREENTAIL_LOG_LEVEL asked for (Debug)' {
    $debug = @(Read-Logs 'service' | Where-Object { $_ -match '^\S+ DEBUG ' })
    if ($debug.Count -eq 0) { throw 'SCREENTAIL_LOG_LEVEL=Debug and the service logged no Debug line.' }
    "$($debug.Count) Debug line(s)."
}

$null = Step 'teardown' 'ui-outlives-service' 'the UI to survive the service going away' {
    Start-Sleep -Seconds 5
    if ($ui.HasExited) { throw "The UI exited with code $($ui.ExitCode) when the service stopped." }
    # The tray's Quit is the UI's only way out, and it is a click; there is nothing to send it. It is
    # closed here, and the check that matters is the one above: it did not fall over.
    Stop-Process -Id $ui.Id -Force -ErrorAction SilentlyContinue
    'Still running five seconds later; closed.'
}

$null = Step 'teardown' 'backend-stop' 'the backend to stop cleanly on Ctrl+Break' {
    Stop-Console -Process $backend -Role 'backend' -Said 'Application is shutting down'
}

$null = Step 'teardown' 'no-crash' 'no crash report from the service or the UI' {
    $now = @()
    if (Test-Path -LiteralPath $crashDir) { $now = @(Get-ChildItem -LiteralPath $crashDir -File | Where-Object { $crashesBefore -notcontains $_.Name }) }
    if ($now.Count -gt 0) {
        foreach ($report in $now) {
            Write-Line "  crash report $($report.Name):"
            foreach ($line in @(Read-SharedLog $report.FullName | Select-Object -First 25)) { Write-Line "    $line" }
        }
        throw "$($now.Count) new crash report(s)."
    }
    'None.'
}

$null = Step 'teardown' 'no-secret-in-logs' "the run's fake secret to appear in no log" {
    $parts = Get-SecretParts $secret
    $hits = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $OutDir -File -Filter '*.log')) {
        $text = (@(Read-SharedLog $file.FullName) -join "`n")
        foreach ($part in $parts) {
            if ($text.IndexOf($part, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hits += $file.Name; break }
        }
    }
    if ($hits.Count -gt 0) { throw "Found in: $(($hits | Sort-Object -Unique) -join ', ')." }
    "Checked $(@(Get-ChildItem -LiteralPath $OutDir -File -Filter '*.log').Count) log(s)."
}

# Anything still running that this script started, whatever happened above.
foreach ($entry in $script:Started) {
    try {
        if (-not $entry.Process.HasExited) {
            Write-Line "  stopping $($entry.Role) (pid $($entry.Process.Id)), still running"
            Stop-Process -Id $entry.Process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    catch { }
}

Finish
