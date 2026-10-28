<#
.SYNOPSIS
    The pinned tools the installer build and CI's signing job use (ST-112, ADR-0007). Dot-sourced by
    package.ps1 and sign.ps1; does nothing on its own.

.DESCRIPTION
    installer/tools.json names every package with its exact version and the SHA-256 of its .nupkg.
    Get-PinnedPackage downloads one from nuget.org (or reuses a copy already downloaded), refuses it
    unless its SHA-256 is the pinned one and its NuGet signatures verify, and extracts it into a fresh
    folder. Nothing already on disk is ever run: only bytes that match the pin. Changing a version or
    a hash in tools.json is a reviewed change, like any other dependency.
#>

$PinnedToolsFile = Join-Path $PSScriptRoot 'tools.json'

function Get-Dotnet {
    # By absolute path, so a dotnet earlier on PATH is never the one that runs.
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) {
        throw "The .NET SDK was not found at $dotnet."
    }
    return $dotnet
}

function Get-PinnedPackage([string]$Id, [string]$Into) {
    $pins = Get-Content -Raw -Path $PinnedToolsFile | ConvertFrom-Json
    $pin = @($pins.packages | Where-Object { $_.id -eq $Id })
    if ($pin.Count -ne 1) {
        throw "installer/tools.json pins $($pin.Count) packages named $Id. It must pin exactly one."
    }
    $pin = $pin[0]
    if ($pin.version -notmatch '^\d+\.\d+\.\d+$' -or $pin.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "installer/tools.json must pin $Id to one exact version and a lowercase SHA-256."
    }

    $lower = $Id.ToLowerInvariant()
    $version = $pin.version
    New-Item -ItemType Directory -Force -Path $Into | Out-Null
    $package = Join-Path $Into "$lower.$version.nupkg"
    if (-not (Test-Path $package) -or (Get-FileHash -Algorithm SHA256 -Path $package).Hash -ne $pin.sha256) {
        Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/$lower/$version/$lower.$version.nupkg" -OutFile $package
    }
    $hash = (Get-FileHash -Algorithm SHA256 -Path $package).Hash.ToLowerInvariant()
    if ($hash -ne $pin.sha256) {
        Remove-Item $package -Force
        throw "$Id $version from nuget.org has SHA-256 $hash, not the pinned $($pin.sha256). Refusing to use it."
    }

    $dotnet = Get-Dotnet
    Push-Location $Into
    try {
        & $dotnet nuget verify $package --all | Out-Host
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($code -ne 0) {
        throw "The NuGet signatures of $Id $version did not verify."
    }

    $folder = Join-Path $Into "$lower.$version"
    if (Test-Path $folder) {
        Remove-Item $folder -Recurse -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $folder)
    Write-Host "  $Id $version, SHA-256 and signatures verified"
    return $folder
}
