<#
.SYNOPSIS
    Runs ScreenTail on this machine: publishes the capture service and the UI into one directory and
    starts both.

.DESCRIPTION
    The two processes refuse to talk to each other unless they are installed the way a release installs
    them, and 'dotnet run' on each project separately is not that.

    ADR-0003's handshake asks three questions of whoever is on the other end of the pipe, and the third
    is who they are. In a release both executables are Authenticode-signed by the same publisher and the
    check is a thumbprint comparison. A development build is not signed, so there is no thumbprint to
    compare and the rule falls back to the only other thing that is hard to fake without already owning
    the machine: the peer's executable must sit in the same directory as ours.

    'dotnet run --project client\ScreenTail.Service' and the same for the UI put the two executables in
    their own bin folders, which fails that check in both directions. The symptom is a tray icon stuck on
    "service unavailable", retrying forever, with the reason recorded nowhere a person would look. This
    script is the one-line answer: publish both into one directory, then run them from there.

    It is for developing and for checking the product on a real screen. It is not an installer, and the
    directory it writes is disposable.

.PARAMETER Output
    Where both processes are published. Anything outside the repository is fine; it is rewritten on every
    run.

.PARAMETER Configuration
    Debug by default, because this exists to be looked at with a debugger attached.

.PARAMETER NoLaunch
    Publish, check, and stop. Use it when you want to start the two processes yourself, or under a
    debugger.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\windows\run-local.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\windows\run-local.ps1 -NoLaunch
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $env:LOCALAPPDATA 'ScreenTail\dev-run'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Title) {
    Write-Host ''
    Write-Host "== $Title" -ForegroundColor Cyan
}

# PowerShell's current location and the process's working directory are not the same thing, and
# [IO.Path]::GetFullPath uses the second. Resolving -Output through the session state means a relative
# path means what whoever typed it meant, and that the comparison below is against the same string the
# publish writes to.
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$service = Join-Path $repo 'client\ScreenTail.Service'
$ui = Join-Path $repo 'client\ScreenTail.UI'

if (-not (Test-Path $service) -or -not (Test-Path $ui)) {
    throw "Cannot find the client projects under $repo. Run this from inside a ScreenTail clone."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'No dotnet on PATH. Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10'
}

# One capture service per user (ADR-0003). This has to be settled before anything is published, because
# a running service holds its own executable open and the clean below would fail on the lock -- which
# reads as a broken script rather than as the one-line problem it is.
#
# Where it is running from decides what to say. One in this directory is the ordinary case, usually the
# last run with its UI quit and its service left up. One somewhere else is the dangerous case: it holds
# the pipe, it is a build nobody here chose, and the handshake will refuse it either way.
$target = $Output.TrimEnd('\')
$running = @(Get-Process -Name 'ScreenTail.Service' -ErrorAction SilentlyContinue)

if ($running.Count -gt 0) {
    Write-Step 'A capture service is already running'
    foreach ($process in $running) {
        $where = if ($process.Path) { $process.Path } else { '(path unreadable)' }
        $here = $process.Path -and (Split-Path $process.Path -Parent).TrimEnd('\') -ieq $target
        $note = if ($here) { 'this directory' } else { 'somewhere else, and the UI here would refuse it' }
        Write-Host "  pid $($process.Id): $where"
        Write-Host "    $note"
    }

    Write-Host ''
    Write-Host '  Stop it first: Ctrl+C in its window, or Stop-Process -Name ScreenTail.Service.'
    Write-Host '  Then run this again.'
    return
}

# A stale copy of either executable would still satisfy the same-directory rule while running code from
# a branch nobody remembers checking out. Cheaper to delete it than to debug it.
Write-Step "Publishing both processes into $Output"
if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
}

foreach ($project in @($service, $ui)) {
    $name = Split-Path $project -Leaf
    Write-Host "  $name"
    dotnet publish $project -c $Configuration -o $Output --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $name failed."
    }
}

$serviceExe = Join-Path $Output 'ScreenTail.Service.exe'
$uiExe = Join-Path $Output 'ScreenTail.UI.exe'
foreach ($exe in @($serviceExe, $uiExe)) {
    if (-not (Test-Path $exe)) {
        throw "Published, but $exe is not there. The handshake needs both executables in one directory."
    }
}

Write-Host ''
Write-Host 'Both executables are in one directory, which is what the pipe handshake requires of an' -ForegroundColor Green
Write-Host 'unsigned build. The service logs "dev-same-directory" on startup when it agrees.' -ForegroundColor Green

if ($NoLaunch) {
    Write-Step 'Not launching (-NoLaunch)'
    Write-Host "  $serviceExe"
    Write-Host "  $uiExe"
    return
}

Write-Step 'Starting the capture service'
Write-Host '  Its window carries the log. Leave it open; Ctrl+C there stops capture.'
Start-Process -FilePath $serviceExe -WorkingDirectory $Output

# The UI retries on its own, so this is politeness rather than a requirement: starting them in this order
# means the first thing on screen is a connected tray icon instead of a disconnected one.
Start-Sleep -Seconds 2

Write-Step 'Starting the UI'
Start-Process -FilePath $uiExe -WorkingDirectory $Output

Write-Host ''
Write-Host 'What to expect:' -ForegroundColor Cyan
Write-Host '  - No window. The UI starts in the notification area, which is the whole interface until'
Write-Host '    there is a draft to review. Right-click the tray icon for diagnostics and the session'
Write-Host '    controls.'
Write-Host '  - Nothing is recording yet. Press Ctrl+Alt+R to start a session from whatever window is in'
Write-Host '    front, or bring a remote-support tool to the foreground and detection starts one for you.'
Write-Host '  - Ctrl+Alt+P pauses and resumes, Ctrl+Alt+S stops and drafts, Ctrl+Alt+M marks a moment.'
Write-Host '  - The recording pill appears whenever capture is anything but idle. That is INV-4, and it'
Write-Host '    is the thing worth watching on a real screen.'
Write-Host '  - First run downloads a speech model in the background. It cannot block capture or stop a'
Write-Host '    session; without it you get clicks and screenshots and no narration.'
Write-Host ''
Write-Host 'To stop: Quit from the tray menu, then Ctrl+C in the service window.'
