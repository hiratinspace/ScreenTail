<#
.SYNOPSIS
    Wraps a payload into ScreenTail.msi and ScreenTail-Setup.exe with WiX, one step at a time (ST-112,
    ADR-0007).

.DESCRIPTION
    The packaging half of the installer build, and the only packaging CI's signing job runs. It runs
    nothing compiled from this repository: only WiX 5 and its two extensions, at the versions and
    SHA-256 hashes pinned in installer/tools.json, verified and extracted fresh on every call, reading
    the authoring in Package.wxs and Bundle.wxs. It signs nothing. build.ps1 calls it for a whole
    build, and CI calls it one step at a time with a sign.ps1 step between each, because every layer has
    to be signed before the next one wraps it:

      Msi       ScreenTail.msi (or -MsiName) from the payload folder
      Bundle    ScreenTail-Setup.exe from that package and Microsoft's Visual C++ runtime installer,
                downloaded here and checked to be Microsoft-signed
      Detach    takes the setup program's engine out into work\engine.exe, to be signed on its own
      Reattach  puts the engine back into the setup program, and reports sizes against the ticket's
                250 MB limit

    Before anything else it removes the job's OIDC request variables from its own environment, so the
    tools it starts never see them. Needs Windows, the .NET SDK (to verify packages and host WiX) and
    network access. Writes only under -Output.

.PARAMETER Step
    Msi, Bundle, Detach or Reattach, as above.

.PARAMETER Version
    major.minor.build, the same version build.ps1 stamped into the payload.

.PARAMETER Output
    Where the payload is and everything is written. Default: artifacts\installer in the repository.

.PARAMETER MsiName
    The package's file name. Default ScreenTail.msi.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Msi', 'Bundle', 'Detach', 'Reattach')][string]$Step,
    [string]$Version = '0.1.0',
    [string]$Output,
    [string]$MsiName = 'ScreenTail.msi'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Remove-Item Env:ACTIONS_ID_TOKEN_REQUEST_URL, Env:ACTIONS_ID_TOKEN_REQUEST_TOKEN -ErrorAction SilentlyContinue

. (Join-Path $PSScriptRoot 'tools.ps1')

$VcRedistUrls = @('https://aka.ms/vc14/vc_redist.x64.exe', 'https://aka.ms/vs/17/release/vc_redist.x64.exe')
$SizeLimitBytes = 250MB

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$' -or [int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65535) {
    throw "Version '$Version' is not major.minor.build within Windows Installer's limits (255.255.65535)."
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Output) {
    $Output = Join-Path $repo 'artifacts\installer'
}
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$work = Join-Path $Output 'work'
$payload = Join-Path $Output 'payload'
$packages = Join-Path $Output 'packages'
$tools = Join-Path $work 'tools'
$msi = Join-Path $packages $MsiName
$bundle = Join-Path $packages 'ScreenTail-Setup.exe'
$engine = Join-Path $work 'engine.exe'
New-Item -ItemType Directory -Force -Path $work, $packages | Out-Null

function Write-Step([string]$Title) {
    Write-Host ''
    Write-Host "== $Title"
}

function Format-Size([long]$Bytes) {
    return ('{0:N1} MB' -f ($Bytes / 1MB))
}

Write-Step "WiX 5 for step $Step ($Version)"
$dotnet = Get-Dotnet
$wix = Join-Path (Get-PinnedPackage 'wix' $tools) 'tools\net6.0\any\wix.dll'
if (-not (Test-Path $wix)) {
    throw "The pinned WiX package has no tools\net6.0\any\wix.dll."
}

function Get-Extension([string]$Id, [string]$Dll) {
    $path = Join-Path (Get-PinnedPackage $Id $tools) $Dll
    if (-not (Test-Path $path)) {
        throw "The pinned $Id package has no $Dll."
    }
    return $path
}

function Invoke-Wix([string]$What, [string[]]$Arguments) {
    & $dotnet $wix @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
}

if ($Step -eq 'Msi') {
    if (-not (Test-Path (Join-Path $payload 'ScreenTail.Service.exe'))) {
        throw "There is no payload in $payload. Run build.ps1 -Stage Payload first."
    }
    $util = Get-Extension 'WixToolset.Util.wixext' 'wixext5\WixToolset.Util.wixext.dll'
    Write-Step "Building $MsiName"
    Invoke-Wix "Building $MsiName" @(
        'build', (Join-Path $PSScriptRoot 'Package.wxs'),
        '-arch', 'x64',
        '-ext', $util,
        '-d', "Version=$Version",
        '-d', "Payload=$payload",
        '-o', $msi
    )
    Write-Host "  $msi"
    return
}

if ($Step -eq 'Bundle') {
    if (-not (Test-Path $msi)) {
        throw "There is no $MsiName in $packages. Run -Step Msi first."
    }
    $util = Get-Extension 'WixToolset.Util.wixext' 'wixext5\WixToolset.Util.wixext.dll'
    $bal = Get-Extension 'WixToolset.Bal.wixext' 'wixext5\WixToolset.BootstrapperApplications.wixext.dll'

    Write-Step 'Getting the Visual C++ runtime installer'
    $vcRedist = Join-Path $work 'vc_redist.x64.exe'
    if (Test-Path $vcRedist) {
        Remove-Item $vcRedist -Force
    }
    foreach ($url in $VcRedistUrls) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $vcRedist
            break
        } catch {
            Write-Host "  $url did not answer: $($_.Exception.Message)"
        }
    }
    if (-not (Test-Path $vcRedist)) {
        throw 'Could not download the Visual C++ runtime installer.'
    }
    $signature = Get-AuthenticodeSignature $vcRedist
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "The downloaded Visual C++ runtime installer is not validly signed by Microsoft ($($signature.Status), $($signature.SignerCertificate.Subject))."
    }
    $vcVersion = [version](Get-Item $vcRedist).VersionInfo.FileVersion
    Write-Host "  vc_redist.x64.exe $vcVersion, signed by Microsoft"

    Write-Step 'Building ScreenTail-Setup.exe'
    Invoke-Wix 'Building ScreenTail-Setup.exe' @(
        'build', (Join-Path $PSScriptRoot 'Bundle.wxs'),
        '-arch', 'x64',
        '-ext', $bal,
        '-ext', $util,
        '-d', "Version=$Version",
        '-d', "Msi=$msi",
        '-d', "VcRedist=$vcRedist",
        '-d', "VcRedistBuild=$($vcVersion.Build)",
        '-o', $bundle
    )
    Write-Host "  $bundle"
    return
}

# A setup program carries its engine inside it, and the engine is what runs first, so the engine is
# signed on its own before the whole is signed: detach, sign, reattach, sign. Done every time, signed
# or not, so the round trip is the one CI exercises.
if ($Step -eq 'Detach') {
    if (-not (Test-Path $bundle)) {
        throw "There is no setup program in $packages. Run -Step Bundle first."
    }
    if (Test-Path $engine) {
        Remove-Item $engine -Force
    }
    Write-Step 'Detaching the setup engine'
    Invoke-Wix 'Detaching the setup engine' @('burn', 'detach', $bundle, '-engine', $engine)
    Write-Host "  $engine"
    return
}

if (-not (Test-Path $engine) -or -not (Test-Path $bundle)) {
    throw "There is no detached engine or setup program in $Output. Run -Step Bundle and -Step Detach first."
}
Write-Step 'Reattaching the setup engine'
Invoke-Wix 'Reattaching the setup engine' @('burn', 'reattach', $bundle, '-engine', $engine, '-o', $bundle)

Write-Step 'Sizes'
$built = @(@($msi, $bundle) | Where-Object { Test-Path $_ })
$tooBig = $false
foreach ($file in $built) {
    $length = (Get-Item $file).Length
    $verdict = 'within the 250 MB limit'
    if ($length -gt $SizeLimitBytes) {
        $verdict = 'OVER the 250 MB limit'
        $tooBig = $true
    }
    Write-Host ('  {0}: {1}, {2}' -f (Split-Path $file -Leaf), (Format-Size $length), $verdict)
}
if ($env:GITHUB_STEP_SUMMARY) {
    $lines = @("### Installer $Version", '', '| File | Size |', '|---|---|')
    foreach ($file in $built) {
        $lines += ('| {0} | {1} |' -f (Split-Path $file -Leaf), (Format-Size (Get-Item $file).Length))
    }
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}
if ($tooBig) {
    throw 'An installer is over the 250 MB the ticket allows (models excluded; the models are downloaded, not shipped).'
}
Write-Host ''
Write-Host "Built: $($built -join ', ')"
