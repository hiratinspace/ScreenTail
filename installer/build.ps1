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

    By default it does all of that in one go, signing as it goes when signing is configured. With
    -Stage it does one part and never signs, so CI's signed job can build with no signing credential
    in reach and pass the credential only to the steps that run sign.ps1:

      Payload   step 1
      Package   step 3 from the payload already there, without signing
      Bundle    step 4 without signing, leaving the setup program's engine detached in work\engine.exe
                for signing on its own
      Reattach  puts work\engine.exe back into the setup program, and reports sizes

    Needs Windows, the .NET 10 SDK and network access (NuGet and Microsoft's download site). Writes
    only under -Output.

.PARAMETER Version
    major.minor.build, stamped into both executables and the package. Windows Installer compares the
    first three fields: major and minor up to 255, build up to 65535.

.PARAMETER Output
    Where everything is written. Default: artifacts\installer in the repository.

.PARAMETER Stage
    All (the default), or one of Payload, Package, Bundle, Reattach as above.

.PARAMETER MsiName
    The package's file name. Default ScreenTail.msi.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\installer\build.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Output,
    [ValidateSet('All', 'Payload', 'Package', 'Bundle', 'Reattach')][string]$Stage = 'All',
    [string]$MsiName = 'ScreenTail.msi'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# The pinned tools. Changing a version here is a reviewed change, like any other dependency.
$WixExtensions = @(
    @{ Id = 'WixToolset.Util.wixext'; Version = '5.0.2'; Dll = 'wixext5\WixToolset.Util.wixext.dll' },
    @{ Id = 'WixToolset.Bal.wixext'; Version = '5.0.2'; Dll = 'wixext5\WixToolset.BootstrapperApplications.wixext.dll' }
)
$VcRedistUrls = @('https://aka.ms/vc14/vc_redist.x64.exe', 'https://aka.ms/vs/17/release/vc_redist.x64.exe')
$SizeLimitBytes = 250MB

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

function Save-NuGetPackage([string]$Id, [string]$PackageVersion, [string]$Into) {
    # Downloaded from nuget.org and its repository signature verified before anything in it is used.
    $folder = Join-Path $Into "$Id.$PackageVersion"
    if (Test-Path (Join-Path $folder '.verified')) {
        return $folder
    }
    New-Item -ItemType Directory -Force -Path $Into | Out-Null
    $lower = $Id.ToLowerInvariant()
    $package = Join-Path $Into "$lower.$PackageVersion.nupkg"
    Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/$lower/$PackageVersion/$lower.$PackageVersion.nupkg" -OutFile $package
    Invoke-Checked "Verifying the signature of $Id $PackageVersion" { dotnet nuget verify $package --all | Out-Host }
    if (Test-Path $folder) {
        Remove-Item $folder -Recurse -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $folder)
    Set-Content -Path (Join-Path $folder '.verified') -Value $PackageVersion
    return $folder
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
if ($Stage -eq 'Package' -and -not (Test-Path (Join-Path $payload 'ScreenTail.Service.exe'))) {
    throw "There is no payload in $payload. Run -Stage Payload first."
}

Write-Step 'Getting WiX 5 and its extensions'
Push-Location $PSScriptRoot
try {
    Invoke-Checked 'Restoring the WiX tool' { dotnet tool restore | Out-Host }
} finally {
    Pop-Location
}
$ext = @{}
foreach ($extension in $WixExtensions) {
    $folder = Save-NuGetPackage $extension.Id $extension.Version (Join-Path $work 'nuget')
    $ext[$extension.Id] = Join-Path $folder $extension.Dll
}

function Invoke-Wix([string]$What, [string[]]$Arguments) {
    Push-Location $PSScriptRoot
    try {
        Invoke-Checked $What { dotnet tool run wix @Arguments | Out-Host }
    } finally {
        Pop-Location
    }
}

if ($Stage -in 'All', 'Package') {
    Write-Step "Building $MsiName"
    Invoke-Wix "Building $MsiName" @(
        'build', (Join-Path $PSScriptRoot 'Package.wxs'),
        '-arch', 'x64',
        '-ext', $ext['WixToolset.Util.wixext'],
        '-d', "Version=$Version",
        '-d', "Payload=$payload",
        '-o', $msi
    )
    Invoke-Sign @($msi)
}

if ($Stage -in 'All', 'Bundle') {
    if (-not (Test-Path $msi)) {
        throw "There is no $MsiName in $packages. Run -Stage Package first."
    }
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
        '-ext', $ext['WixToolset.Bal.wixext'],
        '-ext', $ext['WixToolset.Util.wixext'],
        '-d', "Version=$Version",
        '-d', "Msi=$msi",
        '-d', "VcRedist=$vcRedist",
        '-d', "VcRedistBuild=$($vcVersion.Build)",
        '-o', $bundle
    )

    # A setup program carries its engine inside it, and the engine is what runs first, so it is signed
    # on its own before the whole is signed: detach, sign, reattach, sign. Done every time, signed or
    # not, so the round trip is the one CI exercises.
    if (Test-Path $engine) {
        Remove-Item $engine -Force
    }
    Invoke-Wix 'Detaching the setup engine' @('burn', 'detach', $bundle, '-engine', $engine)
    Invoke-Sign @($engine)
}

if ($Stage -in 'All', 'Reattach') {
    if (-not (Test-Path $engine) -or -not (Test-Path $bundle)) {
        throw "There is no detached engine or setup program in $Output. Run -Stage Bundle first."
    }
    Invoke-Wix 'Reattaching the setup engine' @('burn', 'reattach', $bundle, '-engine', $engine, '-o', $bundle)
    Invoke-Sign @($bundle)
}

if ($Stage -eq 'Bundle') {
    return
}
$built = @(@($msi, $bundle) | Where-Object { Test-Path $_ })

Write-Step 'Sizes'
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
    $lines = @("### Installer $Version", '', "Signing: $signing", '', '| File | Size |', '|---|---|')
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
