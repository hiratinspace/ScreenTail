<#
.SYNOPSIS
    One-time setup for the ScreenTail demo on this laptop. Safe to run again: whatever is already done is
    checked and kept.

.DESCRIPTION
    Gets the laptop to the point where start.ps1 can run the whole loop (record, redact, draft with
    Claude or Gemini, review):
      1. checks Windows and the .NET 10 SDK the repository asks for;
      2. trusts the ASP.NET Core development certificate, so the client's HTTPS call to the backend on
         https://localhost:5001 is validated like any other (Windows asks you to confirm once);
      3. asks which company drafts the notes (Claude from Anthropic, or Gemini from Google), asks for that
         company's API key without showing it, checks it with that company, and saves the key as the API
         project's user secret Summarization:ApiKey and the choice as Summarization:Provider;
      4. generates the JWT signing key (Jwt:SigningKey) if there is none;
      5. finds PostgreSQL (a Windows service, or Docker), asks for its password if it needs one, saves the
         connection string as the user secret ConnectionStrings:Postgres, and creates or migrates the
         'screentail' database.

    Secrets go to one place only: the API project's user-secrets file in your profile
    (%APPDATA%\Microsoft\UserSecrets\screentail-api\secrets.json), the same file 'dotnet user-secrets'
    writes. Nothing is printed, logged or put on a command line.

.PARAMETER Provider
    Who drafts the notes: 'claude' (Anthropic) or 'gemini' (Google). Left out, setup keeps what is saved;
    on a laptop with nothing saved it asks, and Enter means Claude. Changing it asks for the other
    company's key, because the saved key belongs to the company it was checked with.

.PARAMETER NewKey
    Ask for the drafting key again even though one is saved. -NewGeminiKey still works and means the same.

.PARAMETER NewDatabasePassword
    Ask for the database password again even though a connection string is saved.

.PARAMETER DatabasePort
    PostgreSQL's port when it is a Windows service or something setup did not create. 5432 by default.

.PARAMETER SkipKeyCheck
    Do not ask Anthropic or Google whether the key works. For a laptop that is offline while you set it up.

.EXAMPLE
    scripts\windows\demo\setup.cmd

.EXAMPLE
    scripts\windows\demo\setup.cmd -Provider claude

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\windows\demo\setup.ps1 -NewKey
#>
[CmdletBinding()]
param(
    [ValidateSet('claude', 'gemini')]
    [string]$Provider = '',
    [Alias('NewGeminiKey')]
    [switch]$NewKey,
    [switch]$NewDatabasePassword,
    [int]$DatabasePort = 5432,
    [switch]$SkipKeyCheck
)

. (Join-Path $PSScriptRoot 'common.ps1')

function Get-SummarizationModel([hashtable]$Secrets, [string]$Provider) {
    if ($Secrets[$script:KeyModel]) {
        return $Secrets[$script:KeyModel]
    }
    # The default lives in the code, so it is read from the code rather than copied here to go stale.
    if ($Provider -eq 'claude') { $constant = 'DefaultClaudeModel' } else { $constant = 'DefaultGeminiModel' }
    $source = Join-Path $script:DemoApiDir 'Summarize\SummarizationOptions.cs'
    if (Test-Path -LiteralPath $source) {
        $match = [regex]::Match([IO.File]::ReadAllText($source), 'public const string ' + $constant + ' = "([^"]+)"')
        if ($match.Success) { return $match.Groups[1].Value }
    }
    return $null
}

# Asks Anthropic which models the key may use, with the key in a header (never in the URL). This lists
# models; it drafts nothing and costs nothing. The version header is the one the backend's SDK sends.
function Test-ClaudeKey([string]$Key, [string]$Model) {
    $probe = Invoke-Probe -Url 'https://api.anthropic.com/v1/models?limit=1000' -Headers @{ 'x-api-key' = $Key; 'anthropic-version' = '2026-10-21' } -TimeoutSeconds 15
    if ($probe.Status -eq 200) {
        try {
            $listed = @(($probe.Body | ConvertFrom-Json).data | ForEach-Object { $_.id })
        }
        catch {
            return 'ok'
        }
        if ($listed -contains $Model) { return 'ok' }
        return 'model'
    }
    if ($probe.Status -eq 0) { return 'offline' }
    if ($probe.Status -eq 401) { return 'invalid' }
    if ($probe.Status -eq 402) { return 'billing' }
    if ($probe.Status -eq 403) { return 'forbidden' }
    if ($probe.Status -eq 429) { return 'quota' }
    return "http $($probe.Status)"
}

# What Test-ClaudeKey's answer means. Returns $true when the key should be saved.
function Show-ClaudeKeyVerdict([string]$Verdict, [string]$Model) {
    switch -Regex ($Verdict) {
        '^ok$' {
            Write-Ok "Anthropic accepts the key, and the model '$Model' is available to it."
            return $true
        }
        '^offline$' {
            Write-Caution 'Could not reach Anthropic to check the key (no internet, or a proxy). Keeping it unchecked.'
            Write-Note 'Drafting needs the internet on the day either way.'
            return $true
        }
        '^quota$' {
            Write-Caution 'Anthropic knows the key but is rate limiting it right now.'
            Write-Note 'The key is fine. Drafting works again in a minute.'
            return $true
        }
        '^billing$' {
            Write-Caution 'Anthropic knows the key but says the account has a billing problem (out of credit?).'
            Write-Note 'The key is kept. Add credit in the Claude Console (Settings > Billing) before the demo.'
            return $true
        }
        '^model$' {
            Write-Caution "The key works, but Anthropic does not list the model '$Model' for it."
            return $true
        }
        '^forbidden$' {
            Write-Caution 'Anthropic recognises the key but refuses it for this (workspace or organization permissions).'
            return $false
        }
        '^invalid$' {
            Write-Caution 'Anthropic says this is not a valid API key. It may be mistyped, cut short, or revoked.'
            return $false
        }
        default {
            Write-Caution "Anthropic answered $Verdict, which this script does not recognise. Keeping the key unchecked."
            return $true
        }
    }
}

# Asks Google about the model the backend will use, with the key in a header (never in the URL, which
# proxies log). This reads the model's description; it drafts nothing and costs nothing.
function Test-GeminiKey([string]$Key, [string]$Model) {
    $probe = Invoke-Probe -Url "https://generativelanguage.googleapis.com/v1beta/models/$Model" -Headers @{ 'x-goog-api-key' = $Key } -TimeoutSeconds 15
    if ($probe.Status -eq 200) { return 'ok' }
    if ($probe.Status -eq 0) { return 'offline' }
    if ($probe.Status -eq 400 -and $probe.Body -match 'location is not supported') { return 'location' }
    if ($probe.Status -eq 400 -or $probe.Status -eq 401) { return 'invalid' }
    if ($probe.Status -eq 403) { return 'forbidden' }
    if ($probe.Status -eq 404) { return 'model' }
    if ($probe.Status -eq 429) { return 'quota' }
    return "http $($probe.Status)"
}

# What Test-GeminiKey's answer means. Returns $true when the key should be saved.
function Show-KeyVerdict([string]$Verdict, [string]$Model) {
    switch -Regex ($Verdict) {
        '^ok$' {
            Write-Ok "Google accepts the key, and the model '$Model' is available to it."
            return $true
        }
        '^offline$' {
            Write-Caution 'Could not reach Google to check the key (no internet, or a proxy). Keeping it unchecked.'
            Write-Note 'Drafting needs the internet on the day either way.'
            return $true
        }
        '^quota$' {
            Write-Caution "Google knows the key but says its quota is used up for now (free tier: 20 requests a day, 5 a minute)."
            Write-Note 'The key is fine. Drafting works again when the quota resets.'
            return $true
        }
        '^model$' {
            Write-Caution "The key works, but Google does not offer the model '$Model' to it. The name may have been retired."
            return $true
        }
        '^location$' {
            Write-Caution 'Google says the Gemini API is not offered where this laptop is (by its internet address).'
            Write-Note 'A VPN that changes the location, or a key with billing enabled, may be needed. The key itself is kept.'
            return $true
        }
        '^forbidden$' {
            Write-Caution 'Google recognises the key but refuses it: the Generative Language API is off for its project, or the key is restricted to other APIs or addresses.'
            return $false
        }
        '^invalid$' {
            Write-Caution 'Google says this is not a valid API key. It may be mistyped, cut short, or deleted.'
            return $false
        }
        default {
            Write-Caution "Google answered $Verdict, which this script does not recognise. Keeping the key unchecked."
            return $true
        }
    }
}

function New-RandomPassword {
    $alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $bytes = New-Object byte[] 24
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    $characters = foreach ($b in $bytes) { $alphabet[$b % $alphabet.Length] }
    return (-join $characters)
}

Write-Host 'ScreenTail demo setup. Safe to run again: it keeps whatever is already done.'

# --- 1. This machine ---------------------------------------------------------------------------------

Write-Step 'This machine'
Assert-Windows
$sdk = Assert-DotNetSdk
Write-Ok ".NET SDK $sdk"
Write-Ok "Repository at $script:DemoRepo"

# A running backend holds its build output open, so building under it fails with an error that reads
# like a broken clone.
$running = @(Get-DemoState | Where-Object { Get-EntryProcess $_ })
if ($running.Count -gt 0) {
    Stop-Demo -Problem 'The demo is running. Setup rebuilds the backend, which cannot be done while it runs.' -Fix @(
        'Run scripts\windows\demo\stop.cmd, then setup again.')
}

switch (Get-RunnerState) {
    'job' { Write-Caution 'A GitHub Actions job is running on this laptop right now. Setup carries on; do not start the demo until it finishes.' }
    'idle' { Write-Note 'The GitHub Actions runner is on this laptop. Before the audience arrives, close its window so no CI job can take over the screen (docs\dev\demo.md says how).' }
}

# --- 2. The development certificate -------------------------------------------------------------------

Write-Step 'The development certificate (HTTPS on localhost)'
if (Test-DevCertificateTrusted) {
    Write-Ok 'The ASP.NET Core development certificate is installed and trusted.'
}
else {
    Write-Note 'Installing and trusting it. Windows will show a Security Warning asking whether to install a'
    Write-Note 'certificate for "localhost". Answer Yes.'
    $trust = Invoke-Native -FilePath 'dotnet' -ArgumentList @('dev-certs', 'https', '--trust')
    if (-not (Test-DevCertificateTrusted)) {
        Stop-Demo -Problem 'The development certificate is still not trusted.' -Detail (Get-Tail -Lines $trust.Lines -Count 6) -Fix @(
            'If you answered No to the Windows prompt, run setup again and answer Yes.',
            'If there was no prompt, run:  dotnet dev-certs https --clean   then run setup again.',
            'Without it the capture service refuses the backend''s certificate, which is the check working, not a bug.')
    }
    Write-Ok 'The development certificate is installed and trusted.'
}

# --- 3. Who drafts, and their key ---------------------------------------------------------------------

Write-Step 'Who drafts the notes, and their API key'
$secrets = Read-UserSecrets
$savedProvider = Get-DraftingProvider $secrets
$saved = $secrets[$script:KeyDrafting]

if ($Provider -eq 'claude') { $chosen = 'claude' }
elseif ($Provider -eq 'gemini') { $chosen = 'gemini-flash' }
elseif ($saved) { $chosen = $savedProvider }
else {
    Write-Note 'The notes can be drafted by Claude (Anthropic) or by Gemini (Google).'
    $answer = Read-Host -Prompt 'Type C for Claude or G for Gemini, then Enter (just Enter means Claude)'
    if ($answer -and $answer.Trim().ToUpperInvariant().StartsWith('G')) { $chosen = 'gemini-flash' } else { $chosen = 'claude' }
}

$label = Get-ProviderLabel $chosen
$switching = $saved -and ($chosen -ne $savedProvider)
if ($switching) {
    Write-Note ("Switching from " + (Get-ProviderLabel $savedProvider) + " to $label. The saved key is the other company's, so a new one is needed.")
}
Write-Ok "Drafting: $label."

# A model saved for the other company would be asked for by name and refused on every draft.
$savedModel = $secrets[$script:KeyModel]
if ($savedModel) {
    $family = 'gemini-'
    if ($chosen -eq 'claude') { $family = 'claude-' }
    if (-not $savedModel.StartsWith($family)) {
        Stop-Demo -Problem "The user secret $script:KeyModel is '$savedModel', which is not a $label model. Every draft would be refused." -Fix @(
            'Remove it so the backend uses its default model:',
            "  dotnet user-secrets remove $script:KeyModel --project backend\src\ScreenTail.Api",
            'Then run setup again.')
    }
}

$model = Get-SummarizationModel $secrets $chosen
$verdict = 'unchecked'
if ($chosen -eq 'claude') { $keyName = 'Claude API key' } else { $keyName = 'Gemini API key' }

function Test-DraftingKey([string]$Key) {
    if ($chosen -eq 'claude') { return Test-ClaudeKey -Key $Key -Model $model }
    return Test-GeminiKey -Key $Key -Model $model
}

function Show-DraftingKeyVerdict([string]$Verdict) {
    if ($chosen -eq 'claude') { return Show-ClaudeKeyVerdict -Verdict $Verdict -Model $model }
    return Show-KeyVerdict -Verdict $Verdict -Model $model
}

if ($saved -and -not $NewKey -and -not $switching) {
    Write-Ok "A $keyName is already saved. Keeping it (run setup with -NewKey to replace it)."
    if (-not $SkipKeyCheck -and $model) {
        $verdict = Test-DraftingKey $saved.Trim()
        if (-not (Show-DraftingKeyVerdict $verdict)) {
            Stop-Demo -Problem "The saved $keyName does not work." -Fix @(
                'Get the key again and run:',
                '  scripts\windows\demo\setup.cmd -NewKey')
        }
    }
}
else {
    if ($chosen -eq 'claude') {
        Write-Note 'The key is in the Claude Console (platform.claude.com > API keys). It starts with sk-ant-.'
    }
    Write-Note 'Paste the key and press Enter. Nothing appears while you paste or type; that is on purpose.'
    Write-Note '(Right-click pastes in a PowerShell window.)'
    $accepted = $false
    for ($attempt = 1; $attempt -le 3 -and -not $accepted; $attempt++) {
        $key = (Read-Masked $keyName).Trim()
        if (-not $key) {
            Write-Caution 'Nothing was entered.'
            continue
        }
        if ($key -match '\s') {
            Write-Caution 'That has a space or line break in the middle. Paste only the key.'
            continue
        }
        Write-Note "Received $($key.Length) characters."
        if ($SkipKeyCheck -or -not $model) {
            if (-not $model) { Write-Caution 'Could not read the model name from the code, so the key is not checked.' }
            $accepted = $true
        }
        else {
            $verdict = Test-DraftingKey $key
            $accepted = Show-DraftingKeyVerdict $verdict
        }
        if ($accepted) {
            Set-UserSecret -Name $script:KeyDrafting -Value $key
            Write-Ok "Saved as the user secret $script:KeyDrafting."
        }
    }
    $key = $null
    if (-not $accepted) {
        Stop-Demo -Problem "No working $keyName was entered." -Fix @(
            'Copy the key again (Claude Console > API keys, or Google AI Studio > Get API key).',
            'Then run setup again.')
    }
}

# Saved after the key, so a provider is never saved beside the other company's key.
if ((Read-UserSecrets)[$script:KeyProvider] -ne $chosen) {
    Set-UserSecret -Name $script:KeyProvider -Value $chosen
    Write-Ok "Saved the choice as the user secret $script:KeyProvider ($chosen)."
}

if ($verdict -eq 'model') {
    Stop-Demo -Problem "The key is saved, but the backend asks for the model '$model', which is not offered to it. Every draft would fail." -Fix @(
        'Ask the developer for a model name the account can use, then run (with that name in place of NAME):',
        "  dotnet user-secrets set $script:KeyModel NAME --project backend\src\ScreenTail.Api",
        'and run setup again.')
}

# --- 4. The signing key -----------------------------------------------------------------------------------

Write-Step 'The token signing key'
$secrets = Read-UserSecrets
if (Test-SigningKey $secrets[$script:KeySigning]) {
    Write-Ok "A signing key is already saved ($script:KeySigning). Keeping it."
}
else {
    Set-UserSecret -Name $script:KeySigning -Value (New-SigningKey)
    Write-Ok "Generated a new random signing key and saved it as the user secret $script:KeySigning."
}

# --- 5. PostgreSQL ------------------------------------------------------------------------------------------

Write-Step 'PostgreSQL'
$secrets = Read-UserSecrets
$connection = $secrets[$script:KeyDatabase]
$mode = 'saved'
$ask = $false

if ($connection -and -not $NewDatabasePassword) {
    Write-Ok "A connection string is already saved ($script:KeyDatabase). Using it."
    $parts = Get-ConnectionParts $connection
    if (-not (Test-TcpPort -HostName $parts.Host -Port $parts.Port)) {
        $null = Resolve-PostgresDown -HostName $parts.Host -Port $parts.Port
    }
}
else {
    $connection = $null
    $services = @(Get-PostgresServices)
    $docker = Get-DockerState
    $winget = 'winget install --id PostgreSQL.PostgreSQL.17 -e --source winget'

    if ($services.Count -gt 0) {
        # A running service first: after an upgrade the old version's service often sits stopped beside it.
        $service = @($services | Where-Object { $_.Status -eq 'Running' }) | Select-Object -First 1
        if (-not $service) { $service = $services[0] }
        Write-Ok "Found the Windows service $($service.Name) ($($service.Status))."
        if ($service.Status -ne 'Running') {
            try {
                Start-Service -Name $service.Name
                Write-Ok "Started $($service.Name)."
            }
            catch {
                Stop-Demo -Problem "PostgreSQL's service $($service.Name) is $($service.Status) and starting it needs an administrator." -Fix @(
                    'Open the Services app, find the PostgreSQL entry and press Start. Or, in a PowerShell window opened',
                    "with 'Run as administrator':  Start-Service $($service.Name)",
                    'Then run setup again.')
            }
        }
        $mode = 'service'
        $ask = $true
    }
    elseif ($docker -eq 'running' -and (Get-DbContainerStatus)) {
        $status = Get-DbContainerStatus
        Write-Ok "Found the Docker container $script:DemoDbContainer ($status)."
        if ($status -notmatch '^Up') {
            $null = Resolve-PostgresDown -HostName '127.0.0.1' -Port 5432
        }
        $mode = 'docker-existing'
        $ask = $true
    }
    elseif (Test-TcpPort -HostName 'localhost' -Port $DatabasePort) {
        Write-Ok "Something answers on localhost:$DatabasePort; assuming it is PostgreSQL."
        $mode = 'listener'
        $ask = $true
    }
    elseif ($docker -eq 'running') {
        Write-Note "No PostgreSQL installed, but Docker is running. Creating the container $script:DemoDbContainer"
        Write-Note '(PostgreSQL 17, reachable from this laptop only, data kept in a Docker volume across restarts).'
        Write-Note 'The first time, Docker downloads the image, which can take a few minutes.'
        $password = New-RandomPassword
        $created = Invoke-WithEnvironment -Variables @{ 'POSTGRES_PASSWORD' = $password } -Body {
            # '-e POSTGRES_PASSWORD' with no value hands Docker this process's variable, so the password
            # is not on a command line.
            Invoke-Native -FilePath 'docker' -ArgumentList @(
                'run', '-d', '--name', $script:DemoDbContainer, '--restart', 'unless-stopped',
                '-p', '127.0.0.1:5432:5432', '-e', 'POSTGRES_PASSWORD', '-e', 'POSTGRES_DB=screentail',
                '-v', ($script:DemoDbVolume + ':/var/lib/postgresql/data'), 'postgres:17-alpine')
        }
        if ($created.ExitCode -ne 0) {
            Stop-Demo -Problem 'Docker could not create the database container.' -Detail (Get-Tail -Lines $created.Lines -Secrets @($password)) -Fix @(
                'If the message says port 5432 is already allocated, something else is using it: stop that, or install',
                "PostgreSQL instead:  $winget")
        }
        if (-not (Wait-DbContainerReady -Seconds 90)) {
            Stop-Demo -Problem 'The container was created but PostgreSQL inside it did not become ready.' -Fix @(
                "See why:  docker logs $script:DemoDbContainer")
        }
        Write-Ok "Created $script:DemoDbContainer. Its password was generated and goes straight into user secrets."
        $connection = New-ConnectionString -HostName '127.0.0.1' -Port 5432 -User 'postgres' -Password $password
        $password = $null
        $mode = 'docker-new'
    }
    elseif ($docker -eq 'stopped') {
        Stop-Demo -Problem 'There is no PostgreSQL on this laptop, and Docker is installed but not running.' -Fix @(
            'Either start Docker Desktop, wait until it says it is running, and run setup again (setup then creates the database container),',
            "or install PostgreSQL:  $winget",
            '  The installer asks for a password for the "postgres" user. Choose one and keep it; setup asks for it next.')
    }
    else {
        Stop-Demo -Problem 'PostgreSQL is not installed (no PostgreSQL service, nothing on port 5432, no Docker).' -Fix @(
            "Install it:  $winget",
            '  The installer asks for a password for the "postgres" user. Choose one and keep it; setup asks for it next.',
            '  Leave everything else at its default (port 5432).',
            'Then open a NEW PowerShell window and run setup again.')
    }
}

Write-Step 'The database tools'
$efVersion = Get-EfToolVersion
$installed = Get-EfToolInstalledVersion
if ($installed -eq $efVersion) {
    Write-Ok "dotnet-ef $efVersion is installed."
}
else {
    if ($installed) { $verb = 'update' } else { $verb = 'install' }
    Write-Note "Running: dotnet tool $verb --global dotnet-ef --version $efVersion"
    $toolArguments = @('tool', $verb, '--global', 'dotnet-ef', '--version', $efVersion)
    # A newer dotnet-ef (installed by hand for the runbook, say) is replaced with the pinned one, which
    # 'update' refuses to do without being told.
    if ($verb -eq 'update') { $toolArguments += '--allow-downgrade' }
    $tool = Invoke-Native -FilePath 'dotnet' -ArgumentList $toolArguments
    if ($tool.ExitCode -ne 0) {
        Stop-Demo -Problem "Could not $verb dotnet-ef $efVersion." -Detail (Get-Tail -Lines $tool.Lines) -Fix @(
            'Read the lines above. Most often it is the internet connection (it downloads from nuget.org); then run setup again.')
    }
    Write-Ok "dotnet-ef $efVersion is installed."
}

Write-Step 'Building the backend'
$build = Invoke-Native -FilePath 'dotnet' -ArgumentList @('build', $script:DemoApiProject, '-c', 'Debug', '--nologo', '-v', 'quiet')
if ($build.ExitCode -ne 0) {
    Stop-Demo -Problem 'The backend does not build.' -Detail (Get-Tail -Lines $build.Lines -Count 20) -Fix @(
        'Make sure the clone is on main and up to date (git pull), then run setup again.',
        'If it still fails, send the lines above to the developer.')
}
Write-Ok 'The backend builds.'

Write-Step 'Creating or updating the database'
for ($attempt = 1; $attempt -le 3; $attempt++) {
    if (-not $connection) {
        if ($mode -eq 'docker-existing') {
            Write-Note "Enter the password of the 'postgres' user in the $script:DemoDbContainer container."
            Write-Note '(A container made by the old runbook used the password: dev)'
            $hostName = '127.0.0.1'
            $port = 5432
        }
        else {
            Write-Note "Enter the password you chose for the 'postgres' user when you installed PostgreSQL."
            $hostName = 'localhost'
            $port = $DatabasePort
        }
        $typed = Read-Masked 'PostgreSQL password'
        $connection = New-ConnectionString -HostName $hostName -Port $port -User 'postgres' -Password $typed
        $typed = $null
    }

    $update = Invoke-DatabaseUpdate -ConnectionString $connection -NoBuild
    if ($update.Outcome -eq 'ok') {
        break
    }
    if ($update.Outcome -eq 'password') {
        if ($mode -eq 'docker-new') {
            Stop-Demo -Problem "PostgreSQL refused the password setup just generated. The Docker volume $script:DemoDbVolume already holds an older database with a different password." -Fix @(
                'Either run setup with -NewDatabasePassword and enter that older password, or remove the old data and start fresh:',
                "  docker rm -f $script:DemoDbContainer",
                "  docker volume rm $script:DemoDbVolume",
                '  then run setup again.')
        }
        if (-not $ask) {
            Stop-Demo -Problem 'PostgreSQL refused the saved password.' -Fix @(
                'Run setup with -NewDatabasePassword and enter the right one.')
        }
        Write-Caution 'PostgreSQL refused that password. Try again.'
        $connection = $null
        continue
    }
    if ($update.Outcome -eq 'unreachable') {
        $parts = Get-ConnectionParts $connection
        $null = Resolve-PostgresDown -HostName $parts.Host -Port $parts.Port
        continue
    }
    Stop-Demo -Problem 'Creating or updating the database failed.' -Detail $update.Detail -Fix @(
        'Read the lines above. If they mention a migration, the clone may be out of date: git pull, then run setup again.')
}
if ($update.Outcome -eq 'password') {
    Stop-Demo -Problem 'PostgreSQL refused the password three times.' -Fix @(
        'Run setup again and type it carefully. If it is forgotten, ask the developer how to reset it, or use Docker instead.')
}
if ($update.Outcome -ne 'ok') {
    Stop-Demo -Problem 'The database could not be reached after three tries.' -Detail $update.Detail -Fix @(
        'Check that PostgreSQL is running (Services app, or Docker Desktop), then run setup again.')
}

if ($update.Applied -gt 0) {
    Write-Ok "Database 'screentail' updated: $($update.Applied) migration(s) applied."
}
else {
    Write-Ok "Database 'screentail' is up to date."
}

$current = (Read-UserSecrets)[$script:KeyDatabase]
if ($current -ne $connection) {
    Set-UserSecret -Name $script:KeyDatabase -Value $connection
    Write-Ok "Saved the connection string as the user secret $script:KeyDatabase."
}
$connection = $null

# --- Done ---------------------------------------------------------------------------------------------------

Write-Step 'Setup is complete'
Write-Note 'Saved in user secrets (values not shown):'
Write-Note "  $script:KeyDrafting, $script:KeyProvider, $script:KeySigning, $script:KeyDatabase"
Write-Host ''
Write-Host 'Next: scripts\windows\demo\start.cmd' -ForegroundColor Green
