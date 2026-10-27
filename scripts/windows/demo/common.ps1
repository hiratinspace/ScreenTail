<#
.SYNOPSIS
    Helpers shared by the demo scripts (setup.ps1, start.ps1, stop.ps1). Dot-sourced, never run alone.

.DESCRIPTION
    Everything here is Windows PowerShell 5.1 compatible and ASCII only, because that is what the laptop
    runs and what CI's "Windows scripts parse" job checks.

    Three rules hold for every function in this file:
      - No secret is printed. Output captured from a tool is passed through Protect-Text before any of it
        is shown, and the Gemini key, the database password and the device token are never echoed.
      - The only file a secret is written to is the API project's user-secrets file, the same file
        'dotnet user-secrets set' writes. It lives in the user profile, outside the repository.
      - No check is weakened. TLS is validated by Windows exactly as the client validates it; nothing here
        installs a certificate callback or turns a guard off.
#>

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 can default to TLS 1.0, which Kestrel and Google both refuse. Adding TLS 1.2 to
# whatever is already allowed changes nothing about how certificates are checked.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# --- Where things are -------------------------------------------------------------------------------

$script:DemoRepo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$script:DemoBackendDir = Join-Path $script:DemoRepo 'backend'
$script:DemoApiDir = Join-Path $script:DemoRepo 'backend\src\ScreenTail.Api'
$script:DemoApiProject = Join-Path $script:DemoApiDir 'ScreenTail.Api.csproj'
$script:DemoRunLocal = Join-Path $script:DemoRepo 'scripts\windows\run-local.ps1'

# The backend address. It has to be https and it has to be localhost: the development certificate is
# issued for localhost only, and the client's egress guard refuses plain http (INV-8).
$script:DemoBackendPort = 5001
$script:DemoBackendUrl = 'https://localhost:5001'

# The demo's own working files. Not inside %LOCALAPPDATA%\ScreenTail, which is the product's data
# directory and is emptied by "Delete everything".
$script:DemoStateDir = Join-Path $env:LOCALAPPDATA 'ScreenTail.Dev\demo'
$script:DemoAppDir = Join-Path $script:DemoStateDir 'app'
$script:DemoStateFile = Join-Path $script:DemoStateDir 'state.json'
$script:DemoBuildStamp = Join-Path $script:DemoStateDir 'client-build.txt'
$script:DemoBackendLog = Join-Path $script:DemoStateDir 'backend.log'
$script:DemoBackendExited = Join-Path $script:DemoStateDir 'backend.exited'
$script:DemoServiceLog = Join-Path $script:DemoStateDir 'service.log'
$script:DemoServiceExited = Join-Path $script:DemoStateDir 'service.exited'

# Where the capture service keeps an invite-activated device's credential (DpapiDeviceCredentials).
$script:DemoDeviceCredential = Join-Path $env:LOCALAPPDATA 'ScreenTail\device.cred'

# The Docker container setup.ps1 creates when Docker is the database.
$script:DemoDbContainer = 'screentail-db'
$script:DemoDbVolume = 'screentail-pgdata'

# The three settings the backend needs, by their configuration names.
$script:KeyGemini = 'Summarization:ApiKey'
$script:KeySigning = 'Jwt:SigningKey'
$script:KeyDatabase = 'ConnectionStrings:Postgres'

# --- Output -------------------------------------------------------------------------------------------

function Write-Step([string]$Title) {
    Write-Host ''
    Write-Host "== $Title" -ForegroundColor Cyan
}

function Write-Ok([string]$Text) {
    Write-Host "  ok: $Text" -ForegroundColor Green
}

function Write-Note([string]$Text) {
    Write-Host "  $Text"
}

function Write-Caution([string]$Text) {
    Write-Host "  warning: $Text" -ForegroundColor Yellow
}

# Says what is wrong and what to do about it, then ends the script with exit code 1. Every failure in
# the demo scripts goes through here, so the owner always sees the same two parts in the same order.
function Stop-Demo {
    param(
        [Parameter(Mandatory)][string]$Problem,
        [string[]]$Fix = @(),
        [string[]]$Detail = @()
    )

    Write-Host ''
    Write-Host "PROBLEM: $Problem" -ForegroundColor Red
    if ($Detail.Count -gt 0) {
        Write-Host ''
        Write-Host '  What the tool said (secrets removed):' -ForegroundColor DarkGray
        foreach ($line in $Detail) {
            Write-Host "    $line" -ForegroundColor DarkGray
        }
    }
    if ($Fix.Count -gt 0) {
        Write-Host ''
        Write-Host 'What to do:' -ForegroundColor Yellow
        foreach ($line in $Fix) {
            Write-Host "  $line"
        }
    }
    Write-Host ''
    exit 1
}

# Removes anything that looks like a secret from text a tool printed, before it is shown. The patterns
# are deliberately broad; a mangled log line costs nothing, a printed key costs a key.
function Protect-Text {
    param([string]$Text, [string[]]$Secrets = @())

    if ($null -eq $Text) { return '' }
    $clean = $Text
    foreach ($secret in $Secrets) {
        if ($secret -and $secret.Length -ge 4) {
            $clean = $clean.Replace($secret, '***')
        }
    }
    $clean = [regex]::Replace($clean, '(?i)(password|pwd)\s*=\s*[^;"\s]*', '$1=***')
    $clean = [regex]::Replace($clean, 'SCREENTAIL_DEVICE_TOKEN=\S+', 'SCREENTAIL_DEVICE_TOKEN=***')
    $clean = [regex]::Replace($clean, 'eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+', '<token>')
    $clean = [regex]::Replace($clean, 'AIza[0-9A-Za-z_\-]{20,}', '<key>')
    return $clean
}

# The last few lines of a tool's output, cleaned, for Stop-Demo's -Detail.
function Get-Tail {
    param([string[]]$Lines, [int]$Count = 12, [string[]]$Secrets = @())

    $kept = @($Lines | Where-Object { $_ -and $_.Trim() })
    if ($kept.Count -gt $Count) {
        $kept = $kept[($kept.Count - $Count)..($kept.Count - 1)]
    }
    return @($kept | ForEach-Object { Protect-Text -Text $_ -Secrets $Secrets })
}

# The lines of a log another process may still be writing, or none. Opened for shared reading, because
# Get-Content can fail on a file whose writer has it open.
function Read-SharedLog([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }
    try {
        $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $reader = New-Object IO.StreamReader($stream)
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return @($text -split "`r?`n")
    }
    catch {
        return @()
    }
}

# --- Running other programs ---------------------------------------------------------------------------

# Runs a console program and returns its exit code and every line it printed, stdout and stderr merged.
#
# Windows PowerShell 5.1 turns each stderr line into an error record when it is redirected, and with
# ErrorActionPreference 'Stop' the first one throws: a tool that logs to stderr (the backend does, by
# design) would look like a crash. So the preference is relaxed for exactly this call.
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ($WorkingDirectory) { Push-Location -LiteralPath $WorkingDirectory }
    try {
        $lines = @(& $FilePath @ArgumentList 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    }
    finally {
        if ($WorkingDirectory) { Pop-Location }
        $ErrorActionPreference = $previous
    }

    return [pscustomobject]@{ ExitCode = $code; Lines = $lines }
}

# Runs a script block with some environment variables set for this process only, then puts back what was
# there before. A value of $null removes the variable for the duration. Child processes started inside
# the block inherit exactly this environment; nothing is written to the user or machine environment.
function Invoke-WithEnvironment {
    param([Parameter(Mandatory)][hashtable]$Variables, [Parameter(Mandatory)][scriptblock]$Body)

    # Unusual local names: the body runs in this function's scope, so a local called $name here would
    # hide a $name the caller's body meant.
    $demoSavedEnvironment = @{}
    foreach ($demoVariableName in $Variables.Keys) {
        $demoSavedEnvironment[$demoVariableName] = [Environment]::GetEnvironmentVariable($demoVariableName, 'Process')
        [Environment]::SetEnvironmentVariable($demoVariableName, $Variables[$demoVariableName], 'Process')
    }
    try {
        & $Body
    }
    finally {
        foreach ($demoVariableName in $demoSavedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($demoVariableName, $demoSavedEnvironment[$demoVariableName], 'Process')
        }
    }
}

# Starts a visible PowerShell window that runs a script. The script is passed as -EncodedCommand, so no
# path or quote in it can be mangled on the way. Returns the window's process.
function Start-DemoWindow {
    param([Parameter(Mandatory)][string]$Script)

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    return Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -PassThru
}

# A path, made safe to put inside single quotes in a generated script.
function ConvertTo-Quoted([string]$Text) {
    return "'" + $Text.Replace("'", "''") + "'"
}

# --- Checks -------------------------------------------------------------------------------------------

function Assert-Windows {
    if ($env:OS -ne 'Windows_NT') {
        Stop-Demo -Problem 'This is not Windows. The capture service and the UI only run on Windows.' -Fix @('Run this on the Windows laptop.')
    }
    if ($PSVersionTable.PSVersion.Major -lt 5) {
        Stop-Demo -Problem "This PowerShell is version $($PSVersionTable.PSVersion); the demo scripts need 5.1 or later." -Fix @('Use the Windows PowerShell that comes with Windows 10 or 11.')
    }
    if (-not (Test-Path -LiteralPath $script:DemoApiProject)) {
        Stop-Demo -Problem "Cannot find the backend project under $script:DemoRepo." -Fix @('Run the script from inside a ScreenTail clone, for example C:\src\ScreenTail.')
    }
}

# The .NET 10 SDK, found and accepted by the repository's global.json. Returns the version.
function Assert-DotNetSdk {
    $install = 'winget install --id Microsoft.DotNet.SDK.10 -e --source winget'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Stop-Demo -Problem 'dotnet is not installed, or not on PATH in this window.' -Fix @(
            "Install the .NET 10 SDK:  $install",
            'Then open a NEW PowerShell window (so it sees the new PATH) and run this again.')
    }

    # From the repository root, so global.json decides. It names 10.0.100 and rolls forward within 10.0.
    $result = Invoke-Native -FilePath 'dotnet' -ArgumentList @('--version') -WorkingDirectory $script:DemoRepo
    $version = ($result.Lines | Select-Object -Last 1)
    if ($result.ExitCode -ne 0 -or -not ($version -match '^10\.')) {
        Stop-Demo -Problem 'No .NET 10 SDK that this repository accepts is installed.' -Fix @(
            "Install it:  $install",
            'Then open a NEW PowerShell window and run this again.') -Detail (Get-Tail -Lines $result.Lines -Count 6)
    }
    return $version
}

# Whether the ASP.NET Core development certificate exists, is valid and is trusted by this user.
function Test-DevCertificateTrusted {
    $result = Invoke-Native -FilePath 'dotnet' -ArgumentList @('dev-certs', 'https', '--check', '--trust')
    return ($result.ExitCode -eq 0)
}

# --- User secrets ---------------------------------------------------------------------------------------

# The API project's user-secrets file: %APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json.
# This is the file 'dotnet user-secrets set' writes, and the API reads it only in Development.
# Reading and writing it directly means a secret never appears on a command line.
function Get-UserSecretsPath {
    $project = [IO.File]::ReadAllText($script:DemoApiProject)
    $match = [regex]::Match($project, '<UserSecretsId>\s*([^<\s]+)\s*</UserSecretsId>')
    if (-not $match.Success) {
        Stop-Demo -Problem 'The API project has no UserSecretsId, so there is nowhere safe to keep its secrets.' -Fix @('This is a repository problem, not a laptop one. Report it.')
    }
    return Join-Path $env:APPDATA ('Microsoft\UserSecrets\' + $match.Groups[1].Value + '\secrets.json')
}

function Add-FlatEntries {
    param($Node, [string]$Prefix, [hashtable]$Into)

    foreach ($property in $Node.PSObject.Properties) {
        if ($Prefix) { $key = $Prefix + ':' + $property.Name } else { $key = $property.Name }
        if ($property.Value -is [System.Management.Automation.PSCustomObject]) {
            Add-FlatEntries -Node $property.Value -Prefix $key -Into $Into
        }
        else {
            $Into[$key] = [string]$property.Value
        }
    }
}

# Every user secret, as a flat table keyed like 'Section:Key'. Values are held in memory only.
function Read-UserSecrets {
    $path = Get-UserSecretsPath
    $table = @{}
    if (-not (Test-Path -LiteralPath $path)) {
        return $table
    }
    $raw = [IO.File]::ReadAllText($path)
    if (-not $raw.Trim()) {
        return $table
    }
    try {
        $parsed = $raw | ConvertFrom-Json
    }
    catch {
        Stop-Demo -Problem "The user-secrets file is not valid JSON: $path" -Fix @(
            'Open it in Notepad and fix it, or rename it and run setup again (you will be asked for the Gemini key and the database password again).')
    }
    Add-FlatEntries -Node $parsed -Prefix '' -Into $table
    return $table
}

# Sets one user secret, keeping every other one. The value is never printed.
function Set-UserSecret {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Value)

    $table = Read-UserSecrets
    $table[$Name] = $Value

    $ordered = [ordered]@{}
    foreach ($key in ($table.Keys | Sort-Object)) {
        $ordered[$key] = $table[$key]
    }

    $path = Get-UserSecretsPath
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $json = ConvertTo-Json -InputObject $ordered -Depth 2
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding $false))
}

function Test-SigningKey([string]$Key) {
    return ($Key -and $Key.Trim() -and [Text.Encoding]::UTF8.GetByteCount($Key) -ge 32)
}

# 48 random bytes from the system's cryptographic generator, base64: 64 characters. Written for .NET
# Framework, whose RandomNumberGenerator has no static GetBytes(int).
function New-SigningKey {
    $bytes = New-Object byte[] 48
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

# Reads a line without showing it, and returns it as plain text for this process only.
function Read-Masked([string]$Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

# --- The database ---------------------------------------------------------------------------------------

function New-ConnectionString {
    param([string]$HostName, [int]$Port, [string]$User, [string]$Password)

    # The builder quotes whatever needs quoting, so a password with ; or = in it still works.
    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder['Host'] = $HostName
    $builder['Port'] = [string]$Port
    $builder['Database'] = 'screentail'
    $builder['Username'] = $User
    $builder['Password'] = $Password
    return $builder.ConnectionString
}

# Host, port and password from a connection string, for probing and for scrubbing output.
function Get-ConnectionParts([string]$ConnectionString) {
    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder.ConnectionString = $ConnectionString
    $hostName = 'localhost'
    $port = 5432
    $password = ''
    foreach ($name in @('Host', 'Server')) {
        if ($builder.ContainsKey($name)) { $hostName = [string]$builder[$name] }
    }
    if ($builder.ContainsKey('Port')) { $port = [int]$builder['Port'] }
    if ($builder.ContainsKey('Password')) { $password = [string]$builder['Password'] }
    return [pscustomobject]@{ Host = $hostName; Port = $port; Password = $password }
}

function Test-TcpPort {
    param([string]$HostName, [int]$Port, [int]$TimeoutMs = 1500)

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $attempt = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $attempt.AsyncWaitHandle.WaitOne($TimeoutMs)) {
            return $false
        }
        $client.EndConnect($attempt)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function Get-PostgresServices {
    return @(Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue)
}

# 'none' when there is no docker command, 'stopped' when Docker Desktop is not running, 'running'.
function Get-DockerState {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return 'none'
    }
    $info = Invoke-Native -FilePath 'docker' -ArgumentList @('info', '--format', '{{.ServerVersion}}')
    if ($info.ExitCode -ne 0) {
        return 'stopped'
    }
    return 'running'
}

# The demo container's status line from 'docker ps -a', or $null when it does not exist.
function Get-DbContainerStatus {
    $result = Invoke-Native -FilePath 'docker' -ArgumentList @('ps', '-a', '--filter', "name=^$script:DemoDbContainer$", '--format', '{{.Status}}')
    $line = @($result.Lines | Where-Object { $_.Trim() }) | Select-Object -First 1
    if ($result.ExitCode -ne 0 -or -not $line) {
        return $null
    }
    return $line
}

function Wait-DbContainerReady {
    param([int]$Seconds = 60)

    for ($i = 0; $i -lt $Seconds; $i++) {
        $ready = Invoke-Native -FilePath 'docker' -ArgumentList @('exec', $script:DemoDbContainer, 'pg_isready', '-U', 'postgres')
        if ($ready.ExitCode -eq 0) {
            return $true
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

# Explains why PostgreSQL is not answering and stops, or starts the demo's own Docker container and
# returns 'docker-started' when that is all it took.
function Resolve-PostgresDown {
    param([string]$HostName, [int]$Port)

    $services = Get-PostgresServices
    foreach ($service in $services) {
        if ($service.Status -ne 'Running') {
            Stop-Demo -Problem "PostgreSQL is installed but its Windows service '$($service.Name)' is $($service.Status)." -Fix @(
                'Start it. Either open the Services app, find the PostgreSQL entry and press Start, or in a PowerShell',
                "window opened with 'Run as administrator' type:  Start-Service $($service.Name)",
                'Then run this script again.')
        }
    }

    $docker = Get-DockerState
    if ($docker -eq 'running' -and (Get-DbContainerStatus)) {
        Write-Note "Starting the demo database container ($script:DemoDbContainer)..."
        $started = Invoke-Native -FilePath 'docker' -ArgumentList @('start', $script:DemoDbContainer)
        if ($started.ExitCode -ne 0) {
            Stop-Demo -Problem "Docker would not start the $script:DemoDbContainer container." -Detail (Get-Tail -Lines $started.Lines) -Fix @(
                "Look at it in Docker Desktop, or run:  docker logs $script:DemoDbContainer")
        }
        if (-not (Wait-DbContainerReady)) {
            Stop-Demo -Problem "The $script:DemoDbContainer container started but PostgreSQL inside it did not become ready in a minute." -Fix @(
                "See why:  docker logs $script:DemoDbContainer")
        }
        return 'docker-started'
    }
    if ($docker -eq 'stopped' -and $services.Count -eq 0) {
        Stop-Demo -Problem "Nothing answers on $($HostName):$Port, and Docker is installed but not running." -Fix @(
            'Start Docker Desktop and wait until it says it is running, then run this script again.')
    }

    if ($services.Count -gt 0) {
        Stop-Demo -Problem "The PostgreSQL service is running but nothing answers on $($HostName):$Port." -Fix @(
            'PostgreSQL may be listening on another port. The port is part of the connection string that setup saved;',
            'run setup with -NewDatabasePassword to enter the connection details again.')
    }

    Stop-Demo -Problem "PostgreSQL is not running: nothing answers on $($HostName):$Port." -Fix @(
        'Run setup again; it finds or explains the database.')
}

# The dotnet-ef version the repository pins, read from Directory.Packages.props so it cannot drift from
# the EF Core version the migrations were made with.
function Get-EfToolVersion {
    $props = [IO.File]::ReadAllText((Join-Path $script:DemoBackendDir 'Directory.Packages.props'))
    $match = [regex]::Match($props, 'Include="Microsoft\.EntityFrameworkCore\.Design"\s+Version="([^"]+)"')
    if ($match.Success) { return $match.Groups[1].Value }
    return '10.0.12'
}

# A fresh window does not have the global tools folder on PATH until the next sign-in.
function Add-DotNetToolsToPath {
    $tools = Join-Path $env:USERPROFILE '.dotnet\tools'
    if (($env:Path -split ';') -notcontains $tools) {
        $env:Path = $env:Path + ';' + $tools
    }
}

function Get-EfToolInstalledVersion {
    $list = Invoke-Native -FilePath 'dotnet' -ArgumentList @('tool', 'list', '--global')
    foreach ($line in $list.Lines) {
        $match = [regex]::Match($line, '^\s*dotnet-ef\s+(\S+)')
        if ($match.Success) { return $match.Groups[1].Value }
    }
    return $null
}

# Creates the database if it is missing and applies every migration. Applying twice is a no-op.
# Returns Outcome 'ok', 'password' (PostgreSQL refused the password), 'unreachable' or 'failed'.
function Invoke-DatabaseUpdate {
    param([Parameter(Mandatory)][string]$ConnectionString, [switch]$NoBuild)

    Add-DotNetToolsToPath
    $arguments = @('ef', 'database', 'update', '--project', 'src\ScreenTail.Api', '--startup-project', 'src\ScreenTail.Api')
    if ($NoBuild) {
        # --configuration in full: 'dotnet ef' reads -c as --context.
        $arguments += @('--no-build', '--configuration', 'Debug')
    }

    # DesignTimeContextFactory reads the connection string from this environment variable and nowhere
    # else. Set for this process only, for this one command.
    $result = Invoke-WithEnvironment -Variables @{ 'ConnectionStrings__Postgres' = $ConnectionString } -Body {
        Invoke-Native -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $script:DemoBackendDir
    }

    $text = ($result.Lines -join "`n")
    $outcome = 'failed'
    if ($result.ExitCode -eq 0) {
        $outcome = 'ok'
    }
    elseif ($text -match '28P01|password authentication failed|no password (was|has been) provided') {
        $outcome = 'password'
    }
    elseif ($text -match 'Failed to connect|actively refused|No connection could be made|timeout during connect|Connection refused') {
        $outcome = 'unreachable'
    }

    $applied = @($result.Lines | Where-Object { $_ -match 'Applying migration' }).Count
    $secret = (Get-ConnectionParts $ConnectionString).Password
    return [pscustomobject]@{
        Outcome = $outcome
        Applied = $applied
        Detail = (Get-Tail -Lines $result.Lines -Secrets @($secret))
    }
}

# --- HTTP -----------------------------------------------------------------------------------------------

# One GET, validated by Windows the way the client validates it. Returns the status code (0 when there
# was no HTTP answer), the WebExceptionStatus name when the request failed below HTTP (ConnectFailure,
# TrustFailure, SecureChannelFailure, Timeout, ...), and the body.
function Invoke-Probe {
    param([Parameter(Mandatory)][string]$Url, [hashtable]$Headers = @{}, [int]$TimeoutSeconds = 5)

    $result = [pscustomobject]@{ Status = 0; Failure = $null; Body = '' }
    $response = $null
    try {
        $request = [Net.HttpWebRequest]::Create($Url)
        $request.Method = 'GET'
        $request.Timeout = $TimeoutSeconds * 1000
        $request.ReadWriteTimeout = $TimeoutSeconds * 1000
        foreach ($name in $Headers.Keys) {
            $request.Headers.Add($name, $Headers[$name])
        }
        $response = $request.GetResponse()
    }
    catch {
        $exception = $_.Exception
        while ($exception -and -not ($exception -is [Net.WebException])) {
            $exception = $exception.InnerException
        }
        if (-not $exception) {
            $result.Failure = 'Unknown'
            return $result
        }
        if ($exception.Response) {
            $response = $exception.Response
        }
        else {
            $result.Failure = [string]$exception.Status
            return $result
        }
    }

    try {
        $result.Status = [int]$response.StatusCode
        $reader = New-Object IO.StreamReader($response.GetResponseStream())
        try { $result.Body = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally {
        $response.Close()
    }
    return $result
}

# When a JWT stops working, from its own exp claim, or $null if it is not a JWT. Reading the claim needs
# no key; whether the backend accepts the token is asked separately, of the backend.
function Get-TokenExpiry([string]$Token) {
    if (-not $Token) { return $null }
    $parts = $Token.Split('.')
    if ($parts.Count -ne 3) { return $null }
    $payload = $parts[1].Replace('-', '+').Replace('_', '/')
    switch ($payload.Length % 4) {
        2 { $payload += '==' }
        3 { $payload += '=' }
    }
    try {
        $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
    }
    catch {
        return $null
    }
    if (-not $claims.exp) { return $null }
    return [DateTimeOffset]::FromUnixTimeSeconds([long]$claims.exp)
}

# The process listening on a local TCP port, or $null.
function Get-PortOwner([int]$Port) {
    if (-not (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
        return $null
    }
    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $listener) {
        return $null
    }
    $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    if ($process) {
        return [pscustomobject]@{ Id = $process.Id; Name = $process.ProcessName }
    }
    return [pscustomobject]@{ Id = $listener.OwningProcess; Name = '(unknown)' }
}

# --- What start.ps1 started -------------------------------------------------------------------------------

# The state file records each process start.ps1 launched by id, name and start time, so stop.ps1 can be
# sure a process is the one it started and not a later one that happens to have the same id.
function Get-DemoState {
    if (-not (Test-Path -LiteralPath $script:DemoStateFile)) {
        return @()
    }
    try {
        $parsed = [IO.File]::ReadAllText($script:DemoStateFile) | ConvertFrom-Json
        # Through Where-Object, because @($null) is an array of one.
        return @($parsed.Entries | Where-Object { $_ })
    }
    catch {
        return @()
    }
}

function Save-DemoState([object[]]$Entries) {
    if (-not (Test-Path -LiteralPath $script:DemoStateDir)) {
        New-Item -ItemType Directory -Path $script:DemoStateDir -Force | Out-Null
    }
    $document = [ordered]@{ Written = (Get-Date).ToString('o'); Entries = @($Entries) }
    [IO.File]::WriteAllText($script:DemoStateFile, (ConvertTo-Json -InputObject $document -Depth 4), (New-Object Text.UTF8Encoding $false))
}

function New-ProcessEntry {
    param([Parameter(Mandatory)][string]$Role, [Parameter(Mandatory)][Diagnostics.Process]$Process)

    $ticks = 0
    try { $ticks = $Process.StartTime.ToUniversalTime().Ticks } catch { $ticks = 0 }
    return [pscustomobject]@{ Role = $Role; Kind = 'process'; Id = $Process.Id; Name = $Process.ProcessName; StartTicks = $ticks }
}

# The live process an entry names, or $null when it has exited or the id now belongs to something else.
function Get-EntryProcess($Entry) {
    if ($Entry.Kind -ne 'process') { return $null }
    $process = Get-Process -Id ([int]$Entry.Id) -ErrorAction SilentlyContinue
    if (-not $process) { return $null }
    if ($process.ProcessName -ne $Entry.Name) { return $null }
    try {
        $ticks = $process.StartTime.ToUniversalTime().Ticks
    }
    catch {
        return $null
    }
    if ([Math]::Abs($ticks - [long]$Entry.StartTicks) -gt 10000000) {
        return $null
    }
    return $process
}

# The self-hosted GitHub Actions runner lives on this laptop. A hardware job takes over the keyboard,
# the mouse and the screen, so one starting during the demo would be seen by the audience.
function Get-RunnerState {
    if (Get-Process -Name 'Runner.Worker' -ErrorAction SilentlyContinue) { return 'job' }
    if (Get-Process -Name 'Runner.Listener' -ErrorAction SilentlyContinue) { return 'idle' }
    return 'none'
}
