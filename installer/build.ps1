<#
.SYNOPSIS
    Builds ScreenTail's Windows installer: ScreenTail.msi and ScreenTail-Setup.exe (ST-112, ADR-0007).

.DESCRIPTION
    One repeatable build, the same on a developer's Windows machine and on CI's hosted runner:

      1. Publishes the capture service and the UI self-contained and single-file for win-x64
         (client/Packaging.props), into one folder, and checks the result: both executables side by
         side, no loose managed assembly, both built as Windows programs, nothing but the x64 natives.
      2. Signs every executable and library in it that is not already signed (installer/sign.ps1).
         Without signing configured it signs nothing and says so.
      3. Builds ScreenTail.msi from that folder with WiX 5, and signs it.
      4. Downloads Microsoft's Visual C++ runtime installer, checks it is Microsoft-signed, and builds
         ScreenTail-Setup.exe, which installs that runtime and then the package. Signs it.
      5. Reports sizes against the ticket's 250 MB limit.

    By default it does all of that in one go, signing as it goes when signing is configured. Steps 3
    to 5 are installer/package.ps1, which needs nothing from this repository but the authoring and
    runs only WiX at the versions and hashes pinned in installer/tools.json. With -Stage Payload it
    does step 1 alone and signs nothing: CI builds the payload that way, with no signing credential
    anywhere in the job, and its signing job signs it and runs package.ps1 and sign.ps1 one step at a
    time, each layer signed before the next one wraps it (ADR-0007).

    Needs Windows, the .NET 10 SDK and network access (NuGet and Microsoft's download site). Writes
    only under -Output.

.PARAMETER Version
    major.minor.build, stamped into both executables and the package. Windows Installer compares the
    first three fields: major and minor up to 255, build up to 65535.

.PARAMETER Output
    Where everything is written. Default: artifacts\installer in the repository.

.PARAMETER Stage
    All (the default), or Payload for step 1 alone.

.PARAMETER MsiName
    The package's file name. Default ScreenTail.msi.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\installer\build.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Output,
    [ValidateSet('All', 'Payload')][string]$Stage = 'All',
    [string]$MsiName = 'ScreenTail.msi'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$Title) {
    Write-Host ''
    Write-Host "== $Title"
}

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
}

function Get-Subsystem([string]$Path) {
    # The PE optional header's Subsystem field: 2 is a Windows program, 3 a console program.
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

function Test-Managed([string]$Path) {
    try {
        [void][Reflection.AssemblyName]::GetAssemblyName($Path)
        return $true
    } catch [BadImageFormatException] {
        return $false
    }
}

function Format-Size([long]$Bytes) {
    return ('{0:N1} MB' -f ($Bytes / 1MB))
}

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
$sign = Join-Path $PSScriptRoot 'sign.ps1'
New-Item -ItemType Directory -Force -Path $work, $packages | Out-Null

$signing = & $sign -Query
$whole = $Stage -eq 'All'
if ($whole) {
    Write-Host "ScreenTail installer $Version. Signing: $signing."
} else {
    Write-Host "ScreenTail installer $Version, stage $Stage. This stage signs nothing; sign.ps1 runs as its own step."
}

# Signing inside this script happens only for the whole build. A stage leaves it to its own step.
function Invoke-Sign([string[]]$Files) {
    if ($whole) {
        & $sign -Path $Files
    }
}

$msi = Join-Path $packages $MsiName
$bundle = Join-Path $packages 'ScreenTail-Setup.exe'
$engine = Join-Path $work 'engine.exe'

if ($Stage -in 'All', 'Payload') {
    Write-Step 'Publishing the capture service and the UI (self-contained, single file, win-x64)'
    $publish = Join-Path $work 'publish'
    foreach ($folder in @($payload, $publish)) {
        if (Test-Path $folder) {
            Remove-Item $folder -Recurse -Force
        }
    }

    foreach ($project in @('ScreenTail.Service', 'ScreenTail.UI')) {
        Write-Host "  $project"
        $csproj = Join-Path $repo "client\$project\$project.csproj"
        Invoke-Checked "Publishing $project" {
            dotnet publish $csproj -c Release -p:ScreenTailInstaller=true -p:Version=$Version -o (Join-Path $publish $project) --nologo -v quiet
        }
    }

    # One folder, because the peer checks' development rule wants both executables side by side. A file
    # both publishes produce (the SQLCipher library, the remote-tool registry) must be the same file;
    # two different files under one name would mean one process runs with the other's copy.
    Write-Step 'Merging both into one payload'
    New-Item -ItemType Directory -Force -Path $payload | Out-Null
    foreach ($project in @('ScreenTail.Service', 'ScreenTail.UI')) {
        $root = Join-Path $publish $project
        foreach ($file in Get-ChildItem $root -Recurse -File) {
            $relative = $file.FullName.Substring($root.Length + 1)
            $target = Join-Path $payload $relative
            if (Test-Path $target) {
                if ((Get-FileHash $target).Hash -ne (Get-FileHash $file.FullName).Hash) {
                    throw "Both publishes produced $relative and they differ. The two processes would run against different copies."
                }
                continue
            }
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item $file.FullName $target
        }
    }

    # Whisper.net's package copies its natives for every Windows architecture; this package is x64.
    # Symbol files are not shipped (crash reports are stack-only, ST-011).
    foreach ($other in @('runtimes\win-arm64', 'runtimes\win-x86')) {
        $path = Join-Path $payload $other
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
            Write-Host "  dropped $other (this package is x64 only)"
        }
    }
    Get-ChildItem $payload -Recurse -Include '*.pdb', '*.xml' | ForEach-Object {
        Write-Host "  dropped $($_.Name) (symbols and docs are not shipped)"
        Remove-Item $_.FullName -Force
    }

    Write-Step 'Checking the payload'
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($exe in @('ScreenTail.Service.exe', 'ScreenTail.UI.exe')) {
        $path = Join-Path $payload $exe
        if (-not (Test-Path $path)) {
            $problems.Add("$exe is not at the top of the payload. The installer puts both executables in one folder and the peer checks need them there.")
            continue
        }
        $subsystem = Get-Subsystem $path
        if ($subsystem -ne 2) {
            $problems.Add("$exe is built as subsystem $subsystem, not as a Windows program (2). Started at sign-in it would open a console window.")
        }
    }
    foreach ($loose in Get-ChildItem $payload -Recurse -Include '*.runtimeconfig.json', '*.deps.json') {
        $problems.Add("$($loose.Name) is beside the executable. In a single-file build it is inside it, where the signature covers it.")
    }

    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($file in Get-ChildItem $payload -Recurse -File | Sort-Object FullName) {
        $relative = $file.FullName.Substring($payload.Length + 1)
        $kind = 'data'
        if ($file.Extension -in '.dll', '.exe') {
            if ($file.Extension -eq '.exe') {
                $kind = 'executable (single file)'
            } elseif (Test-Managed $file.FullName) {
                $kind = 'MANAGED ASSEMBLY'
                $problems.Add("$relative is a managed assembly outside the single-file executables. Its code would not be covered by their signatures.")
            } else {
                $kind = 'native library'
            }
        }
        $rows.Add([pscustomobject]@{ File = $relative; Size = (Format-Size $file.Length); Kind = $kind })
    }
    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

    # What the native libraries need from the machine. Windows' own libraries are there; the Visual C++
    # runtime is not on a fresh Windows, which is why the setup program installs it and the package
    # refuses to install without it.
    $dumpbin = $null
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $dumpbin = & $vswhere -latest -products * -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe' | Select-Object -First 1
    }
    if ($dumpbin) {
        $vc = New-Object System.Collections.Generic.List[string]
        foreach ($native in Get-ChildItem $payload -Recurse -Filter '*.dll' | Where-Object { -not (Test-Managed $_.FullName) }) {
            $imports = & $dumpbin /nologo /dependents $native.FullName | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[\w.-]+\.dll$' }
            foreach ($import in $imports) {
                if ($import -match '^(msvcp140|vcruntime140|vcruntime140_1|vcomp140|concrt140)\.dll$') {
                    $vc.Add("$($native.Name) -> $import")
                } elseif (-not (Test-Path (Join-Path $native.DirectoryName $import)) -and -not (Test-Path (Join-Path $payload $import)) -and -not (Test-Path (Join-Path "$env:SystemRoot\System32" $import)) -and $import -notmatch '^api-ms-win-') {
                    $problems.Add("$($native.Name) needs $import, which is neither in the payload nor part of Windows.")
                }
            }
        }
        if ($vc.Count -gt 0) {
            Write-Host 'Needs the Visual C++ 2015-2022 x64 runtime (installed by ScreenTail-Setup.exe, required by ScreenTail.msi):'
            $vc | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
        }
    } else {
        Write-Host 'dumpbin not found (Visual Studio C++ tools); not listing what the native libraries import.'
    }

    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "::error::$_" }
        throw "The payload is not what the installer expects ($($problems.Count) problem(s) above)."
    }
    Write-Host ('Payload: {0} files, {1}.' -f $rows.Count, (Format-Size ((Get-ChildItem $payload -Recurse -File | Measure-Object Length -Sum).Sum)))

    if ($whole) {
        Write-Step 'Signing the payload'
        Invoke-Sign @(Get-ChildItem $payload -Recurse -Include '*.exe', '*.dll' | ForEach-Object { $_.FullName })
    }
}

if ($Stage -eq 'Payload') {
    return
}

# Steps 3 to 5, each layer signed before the next one wraps it: the MSI holds the signed payload, the
# setup program holds the signed MSI, and the setup program's engine is signed on its own before the
# whole is signed. The same sequence CI's signing job runs, one step at a time.
$package = Join-Path $PSScriptRoot 'package.ps1'
& $package -Step Msi -Version $Version -Output $Output -MsiName $MsiName
Invoke-Sign @($msi)
& $package -Step Bundle -Version $Version -Output $Output -MsiName $MsiName
& $package -Step Detach -Version $Version -Output $Output -MsiName $MsiName
Invoke-Sign @($engine)
& $package -Step Reattach -Version $Version -Output $Output -MsiName $MsiName
Invoke-Sign @($bundle)

Write-Step 'Checking every signature'
& $sign -Verify -Folder $payload -Path $msi, $engine, $bundle
Write-Host "Signing: $signing."
