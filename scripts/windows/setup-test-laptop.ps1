<#
.SYNOPSIS
    Turns a spare Windows laptop into ScreenTail's real-hardware test machine: a self-hosted GitHub
    Actions runner that runs inside the signed-in desktop session, so hooks, UI Automation and screen
    capture all work.

.DESCRIPTION
    Run once, in PowerShell, as the account that will stay signed in on the laptop. A standard
    (non-admin) account is fine; winget may show UAC prompts. The script:
      1. installs Git, the .NET 8 and .NET 10 SDKs and the VC++ x64 runtime with winget;
      2. downloads the latest GitHub Actions runner into -RunnerDir and registers it for the repo with
         the label 'screentail-win'. It does NOT install it as a Windows service: services run in
         session 0, where there is no desktop to hook or capture;
      3. adds a Startup-folder shortcut so the runner starts at sign-in, and starts it now;
      4. stops the laptop sleeping or turning off the screen while plugged in.
    Steps it cannot do for you are printed at the end.

.PARAMETER Token
    Runner registration token, valid for one hour. Ask the build agent for one, or create it at
    GitHub > repo Settings > Actions > Runners > New self-hosted runner.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\setup-test-laptop.ps1 -Token <TOKEN>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Token,
    [string]$RepoUrl = 'https://github.com/hiratinspace/ScreenTail',
    [string]$RunnerDir = 'C:\actions-runner',
    [string]$Labels = 'screentail-win'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-Step([string]$Title) {
    Write-Host ''
    Write-Host "== $Title" -ForegroundColor Cyan
}

function Install-WingetPackage([string]$Id) {
    & winget list --id $Id --exact --accept-source-agreements | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "$Id is already installed."
        return
    }
    & winget install --id $Id --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "winget install $Id failed (exit code $LASTEXITCODE)." }
}

# --- 1. Prerequisites --------------------------------------------------------------------------

Write-Step 'Prerequisites'
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'winget not found. Install "App Installer" from the Microsoft Store, then re-run.'
}
foreach ($id in 'Git.Git', 'Microsoft.DotNet.SDK.8', 'Microsoft.DotNet.SDK.10', 'Microsoft.VCRedist.2015+.x64') {
    Install-WingetPackage $id
}
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

# --- 2. Runner ---------------------------------------------------------------------------------

Write-Step 'GitHub Actions runner'
if (Test-Path (Join-Path $RunnerDir '.runner')) {
    Write-Host "A runner is already configured in $RunnerDir; skipping download and registration."
}
else {
    $architecture = (Get-CimInstance Win32_Processor | Select-Object -First 1).Architecture
    if ($architecture -eq 12) { $runnerArch = 'arm64' } else { $runnerArch = 'x64' }

    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/actions/runner/releases/latest' -Headers @{ 'User-Agent' = 'screentail-setup' }
    $version = $release.tag_name.TrimStart('v')
    $asset = $release.assets | Where-Object { $_.name -eq "actions-runner-win-$runnerArch-$version.zip" } | Select-Object -First 1
    if (-not $asset) { throw "No runner package for win-$runnerArch in release $($release.tag_name)." }

    New-Item -ItemType Directory -Force -Path $RunnerDir | Out-Null
    $zip = Join-Path $env:TEMP $asset.name
    Write-Host "Downloading $($asset.name)..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $RunnerDir -Force
    Remove-Item $zip

    Push-Location $RunnerDir
    try {
        & .\config.cmd --unattended --url $RepoUrl --token $Token --name $env:COMPUTERNAME --labels $Labels --work _work --replace
        if ($LASTEXITCODE -ne 0) {
            throw "Runner registration failed (exit code $LASTEXITCODE). Tokens expire after an hour; get a fresh one and re-run."
        }
    }
    finally {
        Pop-Location
    }
}

# --- 3. Start at sign-in, in the desktop session ---------------------------------------------

Write-Step 'Start the runner at sign-in'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'ScreenTail Actions Runner.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $RunnerDir 'run.cmd'
$shortcut.WorkingDirectory = $RunnerDir
$shortcut.WindowStyle = 7 # minimized
$shortcut.Save()
Write-Host "Created $shortcutPath"

if (Get-Process -Name 'Runner.Listener' -ErrorAction SilentlyContinue) {
    Write-Host 'The runner is already running.'
}
else {
    Start-Process -FilePath (Join-Path $RunnerDir 'run.cmd') -WorkingDirectory $RunnerDir -WindowStyle Minimized
    Write-Host 'Started the runner (minimized console window).'
}

# --- 4. Power ----------------------------------------------------------------------------------

Write-Step 'Power settings while plugged in'
foreach ($setting in 'standby-timeout-ac', 'monitor-timeout-ac', 'hibernate-timeout-ac') {
    & powercfg /change $setting 0
    if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set $setting; set it in Settings > System > Power." }
}

# --- Manual steps ------------------------------------------------------------------------------

Write-Step 'Done. Manual steps left'
@"
 1. Automatic sign-in, so the runner comes back after reboots and Windows updates:
      winget install Microsoft.Sysinternals.Autologon
    then run Autologon and enter this account's password. It stores the password encrypted as an
    LSA secret. Only do this on a machine used just for testing.
 2. Settings > Accounts > Sign-in options: "If you've been away, when should Windows require you to
    sign in again?" = Never, and turn off Dynamic lock. No screen saver.
    A locked screen is the secure desktop: nothing can be hooked or captured there.
 3. Keep it plugged in with the lid open (or attach a monitor or HDMI dummy plug). With the lid closed
    and no display, Windows may stop drawing the desktop and captures come back black.
 4. Use this laptop only for testing. CI takes screenshots of its desktop.
 5. Tell the build agent the runner is online. It confirms through the GitHub API and switches on
    the laptop job (repository variable HW_RUNNER=true).
"@ | Write-Host
