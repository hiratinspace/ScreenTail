<#
.SYNOPSIS
    Helpers shared by the demo scripts (setup.ps1, start.ps1, stop.ps1, report.ps1) and the e2e runner
    (scripts\windows\e2e\run-e2e.ps1). Dot-sourced, never run alone.

.DESCRIPTION
    Everything here is Windows PowerShell 5.1 compatible and ASCII only, because that is what the laptop
    runs and what CI's "Windows scripts parse" job checks.

    Three rules hold for every function in this file:
      - No secret is printed. Output captured from a tool is passed through Protect-Text before any of it
        is shown, and the drafting key, the database password and the device token are never echoed.
      - The only file a secret is written to is the API project's user-secrets file, the same file
        'dotnet user-secrets set' writes. It lives in the user profile, outside the repository. (When
        setup creates the Docker database, Docker also keeps that database's password in the container's
        own configuration, as it does for any container; 'docker inspect' shows it to this user.)
      - No check is weakened. TLS is validated by Windows exactly as the client validates it; nothing here
        installs a certificate callback or turns a guard off.
#>

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 can default to TLS 1.0, which Kestrel, Google and Anthropic all refuse. Adding TLS 1.2 to
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
$script:DemoBackendExited = Join-Path $script:DemoStateDir 'backend.exited'
$script:DemoServiceExited = Join-Path $script:DemoStateDir 'service.exited'

# What start's two windows print, copied to a file each so report.ps1 can include it. Only the lines the
# processes' scrubbing log sink wrote (INV-10) are kept; anything else is withheld as it is written (see
# DemoLogWriterSource below). Each file is capped: past DemoLogCapBytes it becomes <name>.1.log
# (replacing the one before) and a new one starts, and each start moves the last run's file aside the
# same way. At most two files of 2 MB per process.
$script:DemoLogDir = Join-Path $script:DemoStateDir 'logs'
$script:DemoBackendLog = Join-Path $script:DemoLogDir 'backend.log'
$script:DemoServiceLog = Join-Path $script:DemoLogDir 'service.log'
$script:DemoLogCapBytes = 2097152
$script:DemoLogWriterFile = Join-Path $script:DemoLogDir 'log-writer.txt'

# Where scripts\windows\e2e\run-e2e.ps1 writes its logs when it is not on a CI runner (a runner's job uses
# RUNNER_TEMP): e2e.log and <role>.out.log and <role>.err.log for each process it starts. Nothing filters
# them as they are written, so report.ps1 reads them through the same filter as the demo logs.
$script:DemoE2eDir = Join-Path $env:LOCALAPPDATA 'ScreenTail.Dev\e2e'

# The words run-e2e.ps1 makes each run's fake secret from: a first word, four digits and a second word.
# The secret itself is never saved, so report.ps1 removes anything made of these words by its shape.
$script:E2eSecretFirstWords = @('Tangerine', 'Marigold', 'Juniper', 'Lavender', 'Saffron')
$script:E2eSecretSecondWords = @('Wobble', 'Quiver', 'Ramble', 'Tumble', 'Glimmer')

# Where the backend's log was before it moved into logs\. Only report.ps1 reads it, for a laptop that
# last ran an older start.
$script:DemoOldBackendLog = Join-Path $script:DemoStateDir 'backend.log'

# Which device token the last start gave the capture service, and until when: "development <time>" or
# "activated". The time only, never the token.
$script:DemoTokenFile = Join-Path $script:DemoStateDir 'device-token.txt'

# Who the last start told the backend to draft with: one word, 'claude', 'gemini-flash' or 'scripted'.
# The saved choice is in user secrets; this says what the running backend was given, which differs for
# an offline rehearsal (Test-ScriptedRehearsal). Never a key.
$script:DemoDraftingFile = Join-Path $script:DemoStateDir 'drafting.txt'

# Where report.ps1 writes when the Desktop is synced to OneDrive, so the report does not leave the laptop
# before its owner has read it.
$script:DemoReportDir = Join-Path $script:DemoStateDir 'reports'

# Where the capture service keeps an invite-activated device's credential (DpapiDeviceCredentials).
$script:DemoDeviceCredential = Join-Path $env:LOCALAPPDATA 'ScreenTail\device.cred'

# The Docker container setup.ps1 creates when Docker is the database.
$script:DemoDbContainer = 'screentail-db'
$script:DemoDbVolume = 'screentail-pgdata'

# The settings the backend needs, by their configuration names. Summarization:ApiKey holds the key of
# whichever company drafts: Google's for Gemini, Anthropic's for Claude. Summarization:Provider says
# which, and the two are saved together because one without the other sends a key to the wrong company.
$script:KeyDrafting = 'Summarization:ApiKey'
$script:KeyProvider = 'Summarization:Provider'
$script:KeyModel = 'Summarization:Model'
$script:KeySigning = 'Jwt:SigningKey'
$script:KeyDatabase = 'ConnectionStrings:Postgres'

# Which company drafts, from the saved settings: 'claude' or 'gemini-flash'. A key saved before the
# provider setting existed is a Gemini key, because Gemini was the only choice then.
function Get-DraftingProvider([hashtable]$Secrets) {
    $saved = [string]$Secrets[$script:KeyProvider]
    if ($saved -and $saved.Trim().ToLowerInvariant() -eq 'claude') { return 'claude' }
    return 'gemini-flash'
}

# The provider's name as a person says it.
function Get-ProviderLabel([string]$Provider) {
    if ($Provider -eq 'claude') { return 'Claude (Anthropic)' }
    if ($Provider -eq 'scripted') { return 'the scripted test provider (no model, rehearsal only)' }
    return 'Gemini (Google)'
}

# Whether this start is an offline rehearsal (docs/dev/e2e.md): Summarization__Provider=scripted set in
# the window start runs from. The backend runs in Development, the only place it accepts the setting, and
# the note it writes says no model wrote it. Never saved, so the next plain start drafts for real again.
function Test-ScriptedRehearsal {
    $requested = [string]$env:Summarization__Provider
    return [bool]($requested -and $requested.Trim().ToLowerInvariant() -eq 'scripted')
}

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
    $clean = [regex]::Replace($clean, '(?i)(password|pwd)\s*=\s*("[^"]*"|''[^'']*''|[^;"\s]*)', '$1=***')
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
        # Delete sharing as well, so a log writer can still move a full file aside while this reads it.
        $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
        $reader = New-Object IO.StreamReader($stream)
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return @($text -split "`r?`n")
    }
    catch {
        return @()
    }
}

# The older file a capped log is moved to: backend.log becomes backend.1.log.
function Get-OlderLogPath([string]$Path) {
    return [IO.Path]::ChangeExtension($Path, '.1.log')
}

# Moves the last run's log aside, so a new run starts with an empty file and the one before is kept.
function Move-DemoLogAside([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $older = Get-OlderLogPath $Path
    try {
        if (Test-Path -LiteralPath $older) { Remove-Item -LiteralPath $older -Force }
        Move-Item -LiteralPath $Path -Destination $older
    }
    catch {
        # A file something still holds open is emptied instead, so the new run is not mixed into it.
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    }
}

# The code start.ps1 puts into each of its windows to write that window's log, and that report.ps1 runs
# again over what it reads. Kept as text because a window is a separate PowerShell, given its script as
# an encoded command. Windows PowerShell 5.1.
#
# A line is kept only if the scrubbing log sink wrote it. Both sinks (client/ScreenTail.Shared/Logging/
# ScrubbingLogger.cs and backend/src/ScreenTail.Api/Logging/ScrubbingLoggerProvider.cs) write
# "HH:mm:ss.fff LEVEL category: message", LEVEL being TRACE, DEBUG, "INFO ", "WARN ", ERROR or FATAL, and
# then an exception's lines indented by four spaces: "<Type>: <message>", "at <frame>" and "--- End of
# ...". Anything else a process prints went round the sink: the .NET runtime's "Unhandled exception."
# with the exception's message, or a native library's output. Such a line is withheld and only its length
# kept; of an unhandled exception only the type is kept.
#
# An exception's message is withheld even from the sink's own lines. The sink scrubs it for paths and
# addresses, but it is still where a window title or a customer's name ends up, which is why
# CrashReport.cs leaves it out. The type and the frames are kept.
#
# Every comparison is ordinal (-cmatch, [0-9], String.Equals and StartsWith with Ordinal): a culture
# comparison skips characters such as a soft hyphen, a zero-width space or a NUL, and a line that starts
# with one must not pass for one of the sink's.
$script:DemoLogWriterSource = @'
function ConvertTo-DemoLogLine([string]$Line, [hashtable]$State) {
    if ($null -eq $Line) { $Line = '' }
    if ($Line -cmatch '^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3} (TRACE|DEBUG|INFO |WARN |ERROR|FATAL) \S') {
        $State.InLogger = $true
        return $Line
    }
    if ($State.InLogger) {
        if ($Line.StartsWith('    at ', [StringComparison]::Ordinal)) { return $Line }
        if ($Line.StartsWith('    --- End of ', [StringComparison]::Ordinal)) { return $Line }
        if ($Line -cmatch '^    ([A-Za-z_][A-Za-z0-9_.`+]*): ') { return '    ' + $Matches[1] + ': [message withheld]' }
    }
    $State.InLogger = $false
    if ($Line.Length -eq 0) { return '' }

    # Three fixed sentences the capture service prints itself before its logger exists (Program.cs).
    if ([string]::Equals($Line, 'Another ScreenTail capture service is already running for this user, or something is holding its name. Exiting with code 3.', [StringComparison]::Ordinal)) { return $Line }
    if ([string]::Equals($Line, 'The capture service runs on Windows only.', [StringComparison]::Ordinal)) { return $Line }
    if ($Line -cmatch '^Warning: [A-Z0-9_]+ is set, so this process may be running code that is not ScreenTail''s\.$') { return $Line }

    # What this code itself writes. Read back from a file only: a process printing the same is withheld.
    # start.ps1's own lines say why a window's program stopped, and when it could not run at all that is
    # an exception's message: such a line keeps only that there was a reason. Any other [start.ps1] line
    # is withheld like a process's.
    if ($State.Reading) {
        if ($Line -cmatch '^\[line not from ScreenTail''s logger withheld, [0-9]+ chars\]$') { return $Line }
        if ($Line -cmatch '^\[[0-9]+ log line\(s\) could not be written here\]$') { return $Line }
        if ($Line -cmatch '^\[The log filter could not be checked, so nothing from this window is logged\.\]$') { return $Line }
        if ($Line -cmatch '^\[start\.ps1\] The (backend|capture service) has stopped \((exit code -?[0-9]{1,11}|unknown|still running; close this window to end it)\)\.$') { return $Line }
        if ($Line -cmatch '^\[start\.ps1\] The (backend|capture service) (could not run|has stopped)') {
            return '[start.ps1] The ' + $Matches[1] + ' ' + $Matches[2] + ' [reason withheld]'
        }
    }

    if ($Line -cmatch '^Unhandled exception\. ([A-Za-z_][A-Za-z0-9_.`+]*)') {
        return 'Unhandled exception. ' + $Matches[1] + ' [message and stack withheld]'
    }
    return "[line not from ScreenTail's logger withheld, $($Line.Length) chars]"
}

# One writer per window, kept open: FileShare ReadWrite and Delete, so report.ps1 can read it and it can
# be moved aside while read. Bytes are counted as written, so the cap costs no extra call per line.
$script:DemoLog = @{ Path = $null; Writer = $null; Bytes = 0; Cap = __CAP__; Dropped = 0; InLogger = $false; Reading = $false }

function Close-DemoLog {
    if ($script:DemoLog.Writer) {
        try { $script:DemoLog.Writer.Dispose() } catch { }
        $script:DemoLog.Writer = $null
    }
}

# Writes one line as it is. For start.ps1's own lines; a process's output goes through Write-DemoLog.
function Write-DemoLogText([string]$Text) {
    $ErrorActionPreference = 'Stop'
    $log = $script:DemoLog
    try {
        if (-not $log.Writer) {
            $stream = New-Object IO.FileStream($log.Path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]'ReadWrite, Delete')
            $log.Bytes = $stream.Length
            $log.Writer = New-Object IO.StreamWriter($stream, (New-Object Text.UTF8Encoding $false))
        }
        # A line that could not be written is said, not lost without a word.
        if ($log.Dropped -gt 0) {
            $note = "[$($log.Dropped) log line(s) could not be written here]"
            $log.Writer.WriteLine($note)
            $log.Bytes += $note.Length + 2
            $log.Dropped = 0
        }
        $log.Writer.WriteLine($Text)
        $log.Writer.Flush()
        $log.Bytes += [Text.Encoding]::UTF8.GetByteCount($Text) + 2
    }
    catch {
        $log.Dropped++
        Close-DemoLog
        return
    }
    if ($log.Bytes -gt $log.Cap) {
        # This file becomes <name>.1.log and the next line starts a new one. If a reader without delete
        # sharing holds it, the move fails, writing carries on in this file, and the next line tries again.
        Close-DemoLog
        try {
            $older = [IO.Path]::ChangeExtension($log.Path, '.1.log')
            if ([IO.File]::Exists($older)) { [IO.File]::Delete($older) }
            [IO.File]::Move($log.Path, $older)
        }
        catch { }
    }
}

function Write-DemoLog([string]$Line) {
    Write-DemoLogText (ConvertTo-DemoLogLine $Line $script:DemoLog)
}
'@
$script:DemoLogWriterSource = $script:DemoLogWriterSource.Replace('__CAP__', [string]$script:DemoLogCapBytes)

# The SHA-256 of the writer source as UTF-8, in lower-case hex. start.ps1 puts it into each window
# script, and the window loads the saved file only if the file still has it.
function Get-DemoLogWriterHash([string]$Source) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Source))
    }
    finally {
        $sha.Dispose()
    }
    return (-join ($bytes | ForEach-Object { $_.ToString('x2') }))
}

# The hash of the filter as this file built it, which report.ps1's end-to-end section is given with the
# source. Both come from this file, so in the report they always match: its guard is a probe of what the
# filter withholds, not this hash.
$script:DemoLogWriterHash = Get-DemoLogWriterHash $script:DemoLogWriterSource

# --- Running other programs ---------------------------------------------------------------------------

# Runs a console program and returns its exit code and every line it printed, stdout and stderr merged
# (Lines), and its stdout lines alone (Out). Out is for anything that may reach report.ps1's report: a
# stderr line is an error record, and an error's text is never put into the report.
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
    $out = New-Object System.Collections.Generic.List[string]
    try {
        $lines = @(& $FilePath @ArgumentList 2>&1 | ForEach-Object {
                if (-not ($_ -is [System.Management.Automation.ErrorRecord])) { $out.Add("$_") }
                "$_"
            })
        $code = $LASTEXITCODE
    }
    finally {
        if ($WorkingDirectory) { Pop-Location }
        $ErrorActionPreference = $previous
    }

    return [pscustomobject]@{ ExitCode = $code; Lines = $lines; Out = $out.ToArray() }
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
            'Open it in Notepad and fix it, or rename it and run setup again (you will be asked for the drafting key and the database password again).')
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
    # get_/set_ rather than .ConnectionString: the builder is a dictionary, and PowerShell turns
    # property syntax on a dictionary into a key named "ConnectionString".
    return $builder.get_ConnectionString()
}

# Host, port and password from a connection string, for probing and for scrubbing output.
function Get-ConnectionParts([string]$ConnectionString) {
    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder.set_ConnectionString($ConnectionString)
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

# The demo container's status line from 'docker ps -a', or $null when it does not exist. From docker's
# stdout only, so a warning docker writes to stderr is never taken for the status (report.ps1 shows it).
function Get-DbContainerStatus {
    $result = Invoke-Native -FilePath 'docker' -ArgumentList @('ps', '-a', '--filter', "name=^$script:DemoDbContainer$", '--format', '{{.Status}}')
    $line = @($result.Out | Where-Object { $_.Trim() }) | Select-Object -First 1
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

    # An old version's service, stopped or disabled beside a running new one, is not the problem, so a
    # stopped service is blamed only when none is running.
    $services = @(Get-PostgresServices)
    $running = @($services | Where-Object { $_.Status -eq 'Running' })
    if ($services.Count -gt 0 -and $running.Count -eq 0) {
        $service = $services[0]
        Stop-Demo -Problem "PostgreSQL is installed but its Windows service '$($service.Name)' is $($service.Status)." -Fix @(
            'Start it. Either open the Services app, find the PostgreSQL entry and press Start, or in a PowerShell',
            "window opened with 'Run as administrator' type:  Start-Service $($service.Name)",
            'Then run this script again.')
    }

    $docker = Get-DockerState
    $containerStatus = $null
    if ($docker -eq 'running') { $containerStatus = Get-DbContainerStatus }
    if ($containerStatus -and $containerStatus -notmatch '^Up') {
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

    if ($containerStatus) {
        Stop-Demo -Problem "The $script:DemoDbContainer container is running but nothing answers on $($HostName):$Port." -Fix @(
            "See why:  docker logs $script:DemoDbContainer",
            'If the saved connection points somewhere else, run setup with -NewDatabasePassword to enter it again.')
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
