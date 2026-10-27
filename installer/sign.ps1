<#
.SYNOPSIS
    The one signing step for ScreenTail's installer (ST-112, ADR-0007).

.DESCRIPTION
    Signs the files it is given with SHA-256 and an RFC 3161 timestamp, so a signature stays valid after
    its certificate expires (the peer checks rely on that: see Authenticode.cs). A file that already
    carries a valid signature, such as Microsoft's WPF libraries, is left as it is. After signing, every
    file must verify, carry a timestamp, and have been signed by one and the same certificate: the peer
    checks accept the other process only when its signer is this process's signer.

    Configured from the environment, never from a file in the repository:

      Azure Artifact Signing (formerly Trusted Signing), the recommended way:
        SCREENTAIL_SIGNING_ENDPOINT   for example https://eus.codesigning.azure.net
        SCREENTAIL_SIGNING_ACCOUNT    the Artifact Signing account name
        SCREENTAIL_SIGNING_PROFILE    the certificate profile name
        and a credential the Azure SDK finds: AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_CLIENT_SECRET
        for a service principal (CI), or an 'az login' session on a developer's machine.

      A certificate in the Windows certificate store (a hardware token, or a cloud HSM's key provider):
        SCREENTAIL_SIGNING_THUMBPRINT the certificate's SHA-1 thumbprint, current user's store
        SCREENTAIL_TIMESTAMP_URL      optional; default http://timestamp.digicert.com

    With neither, it signs nothing, lists what it would have signed, and exits successfully: an
    unsigned build is a development build, and the peer checks treat it as one (ADR-0003). With
    SCREENTAIL_REQUIRE_SIGNING=1 (CI's signed job sets it) an unconfigured run fails instead, so a job
    that is meant to sign can never produce an unsigned build by accident.

    It prints file names and the signing mode, never a credential.

.PARAMETER Path
    The files to sign.

.PARAMETER Query
    Print the signing mode ('artifact-signing', 'certificate' or 'none') and exit.
#>
[CmdletBinding()]
param(
    [string[]]$Path = @(),
    [switch]$Query
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ArtifactSigningClient = @{ Id = 'Microsoft.ArtifactSigning.Client'; Version = '1.0.146' }
$ArtifactSigningTimestamp = 'http://timestamp.acs.microsoft.com'

function Get-SigningMode {
    $endpoint = $env:SCREENTAIL_SIGNING_ENDPOINT
    $account = $env:SCREENTAIL_SIGNING_ACCOUNT
    $profileName = $env:SCREENTAIL_SIGNING_PROFILE
    if ($endpoint -or $account -or $profileName) {
        if (-not ($endpoint -and $account -and $profileName)) {
            throw 'Artifact Signing is partly configured: SCREENTAIL_SIGNING_ENDPOINT, SCREENTAIL_SIGNING_ACCOUNT and SCREENTAIL_SIGNING_PROFILE are needed together.'
        }
        return 'artifact-signing'
    }
    if ($env:SCREENTAIL_SIGNING_THUMBPRINT) {
        return 'certificate'
    }
    return 'none'
}

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName 'x64\signtool.exe')) } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if (-not $found) {
        throw "signtool.exe was not found under $kits. Install the Windows SDK's signing tools."
    }
    return Join-Path $found.FullName 'x64\signtool.exe'
}

function Get-ArtifactSigningDlib([string]$Into) {
    # Microsoft's client library, from nuget.org, its repository signature verified, and the library
    # itself checked to be Microsoft-signed before signtool loads it.
    $id = $ArtifactSigningClient.Id
    $version = $ArtifactSigningClient.Version
    $folder = Join-Path $Into "$id.$version"
    $dlib = Join-Path $folder 'bin\x64\Azure.CodeSigning.Dlib.dll'
    if (-not (Test-Path $dlib)) {
        New-Item -ItemType Directory -Force -Path $Into | Out-Null
        $lower = $id.ToLowerInvariant()
        $package = Join-Path $Into "$lower.$version.nupkg"
        Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/$lower/$version/$lower.$version.nupkg" -OutFile $package
        dotnet nuget verify $package --all | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "The signature of $id $version did not verify."
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($package, $folder)
    }
    $signature = Get-AuthenticodeSignature $dlib
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "The Artifact Signing client library is not validly signed by Microsoft ($($signature.Status))."
    }
    return $dlib
}

$mode = Get-SigningMode
if ($Query) {
    return $mode
}

$Path = @($Path | Where-Object { $_ })
if ($Path.Count -eq 0) {
    return
}

# What needs a signature, and what already has a good one. Anything else (a signature that does not
# match its file, a chain Windows will not accept) is a payload in trouble and stops the build.
$toSign = New-Object System.Collections.Generic.List[string]
foreach ($file in $Path) {
    $status = (Get-AuthenticodeSignature $file).Status
    $name = Split-Path $file -Leaf
    if ($status -eq 'NotSigned') {
        $toSign.Add($file)
    } elseif ($status -eq 'Valid') {
        Write-Host "  already signed, left as it is: $name"
    } else {
        throw "$name carries a signature Windows does not accept ($status)."
    }
}

if ($toSign.Count -eq 0) {
    return
}

if ($mode -eq 'none') {
    if ($env:SCREENTAIL_REQUIRE_SIGNING -eq '1') {
        throw 'SCREENTAIL_REQUIRE_SIGNING is set and signing is not configured. Refusing to produce an unsigned build here.'
    }
    $names = ($toSign | ForEach-Object { Split-Path $_ -Leaf }) -join ', '
    Write-Host "  NOT SIGNED (signing is not configured; docs/dev/installer.md says how to turn it on): $names"
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host "::notice title=Unsigned build::Signing is not configured for this run, so these are development builds: $names"
    }
    return
}

$signtool = Find-SignTool
$arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('screentail-sign-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    if ($mode -eq 'artifact-signing') {
        $dlib = Get-ArtifactSigningDlib (Join-Path $scratch 'client')
        $metadata = [ordered]@{
            Endpoint = $env:SCREENTAIL_SIGNING_ENDPOINT
            CodeSigningAccountName = $env:SCREENTAIL_SIGNING_ACCOUNT
            CertificateProfileName = $env:SCREENTAIL_SIGNING_PROFILE
        }
        if ($env:AZURE_CLIENT_SECRET) {
            # A service principal in the environment (CI): ask for that credential and nothing else, so
            # the client does not spend a minute probing for a managed identity the runner does not have.
            $metadata.ExcludeCredentials = @('ManagedIdentityCredential', 'WorkloadIdentityCredential', 'SharedTokenCacheCredential',
                'VisualStudioCredential', 'VisualStudioCodeCredential', 'AzureCliCredential', 'AzurePowerShellCredential',
                'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
        }
        $metadataFile = Join-Path $scratch 'metadata.json'
        $metadata | ConvertTo-Json | Set-Content -Path $metadataFile -Encoding ASCII
        $arguments += @('/tr', $ArtifactSigningTimestamp, '/dlib', $dlib, '/dmdf', $metadataFile)
    } else {
        $timestamp = $env:SCREENTAIL_TIMESTAMP_URL
        if (-not $timestamp) {
            $timestamp = 'http://timestamp.digicert.com'
        }
        $arguments += @('/tr', $timestamp, '/sha1', $env:SCREENTAIL_SIGNING_THUMBPRINT)
    }

    Write-Host "  signing $($toSign.Count) file(s) ($mode): $(($toSign | ForEach-Object { Split-Path $_ -Leaf }) -join ', ')"
    & $signtool @arguments @toSign | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE."
    }
} finally {
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

# Every file verifies, carries a timestamp, and has the same signer as the others: one publisher is
# what both peer checks compare.
$signers = @{}
foreach ($file in $toSign) {
    $signature = Get-AuthenticodeSignature $file
    $name = Split-Path $file -Leaf
    if ($signature.Status -ne 'Valid') {
        throw "$name did not verify after signing ($($signature.Status))."
    }
    if (-not $signature.TimeStamperCertificate) {
        throw "$name was signed without a timestamp. An untimestamped signature stops verifying when its certificate expires."
    }
    $signers[$signature.SignerCertificate.Thumbprint] = $signature.SignerCertificate.Subject
}
if ($signers.Count -ne 1) {
    throw "The files were signed by $($signers.Count) different certificates. Both processes must share one signer."
}
Write-Host "  signed and timestamped by: $(@($signers.Values)[0])"
