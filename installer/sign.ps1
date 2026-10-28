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
        and a credential the Azure SDK finds:
          in CI, GitHub's own OIDC token for the job (workload identity federation): AZURE_TENANT_ID
          and AZURE_CLIENT_ID of an app registration that trusts this repository's 'signing'
          environment, and the job's id-token permission. No client secret exists anywhere. The token
          is asked for here, in the signing step, and handed to signtool in a file that is deleted
          afterwards;
          on a developer's machine, an 'az login' session (or AZURE_CLIENT_SECRET for a service
          principal, which CI does not use).

      A certificate in the Windows certificate store (a hardware token, or a cloud HSM's key provider):
        SCREENTAIL_SIGNING_THUMBPRINT the certificate's SHA-1 thumbprint, current user's store
        SCREENTAIL_TIMESTAMP_URL      optional; default http://timestamp.digicert.com

    With neither, it signs nothing, lists what it would have signed, and exits successfully: an
    unsigned build is a development build, and the peer checks treat it as one (ADR-0003). With
    SCREENTAIL_REQUIRE_SIGNING=1 (CI's signing job sets it) an unconfigured run fails instead, so a job
    that is meant to sign can never produce an unsigned build by accident.

    With -Verify it signs nothing and checks the finished build instead: every file's signature is one
    Windows accepts; both executables have one and the same signing certificate; everything else in the
    payload is signed by that same publisher or arrived signed by Microsoft; the MSI, the setup program
    and its engine are signed by that publisher; and every signature of ours carries a timestamp. An
    unsigned build passes with a notice, unless SCREENTAIL_REQUIRE_SIGNING=1.

    It prints file names and the signing mode, never a credential. The signing tools run by absolute
    path: signtool from the Windows SDK, and Microsoft's Artifact Signing client at the version and
    SHA-256 pinned in installer/tools.json.

.PARAMETER Path
    The files to sign.

.PARAMETER Folder
    Sign every .exe and .dll under this folder (the published payload).

.PARAMETER Query
    Print the signing mode ('artifact-signing', 'certificate' or 'none') and exit.

.PARAMETER Verify
    Check the signatures of the files given, as above, instead of signing them.
#>
[CmdletBinding()]
param(
    [string[]]$Path = @(),
    [string]$Folder,
    [switch]$Query,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# GitHub's OIDC request settings, taken out of the environment before anything else, so no program this
# script starts (dotnet, signtool) inherits them. Only the token request below uses them; signtool gets
# the token it needs as AZURE_FEDERATED_TOKEN_FILE.
$TokenRequestUrl = $env:ACTIONS_ID_TOKEN_REQUEST_URL
$TokenRequestToken = $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN
Remove-Item Env:ACTIONS_ID_TOKEN_REQUEST_URL, Env:ACTIONS_ID_TOKEN_REQUEST_TOKEN -ErrorAction SilentlyContinue

. (Join-Path $PSScriptRoot 'tools.ps1')

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
    # Microsoft's client library, at the version and SHA-256 pinned in tools.json, its NuGet signatures
    # verified, and the library itself checked to be Microsoft-signed before signtool loads it.
    $dlib = Join-Path (Get-PinnedPackage 'Microsoft.ArtifactSigning.Client' $Into) 'bin\x64\Azure.CodeSigning.Dlib.dll'
    if (-not (Test-Path $dlib)) {
        throw 'The pinned Artifact Signing client package has no bin\x64\Azure.CodeSigning.Dlib.dll.'
    }
    $signature = Get-AuthenticodeSignature $dlib
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "The Artifact Signing client library is not validly signed by Microsoft ($($signature.Status))."
    }
    return $dlib
}

function Test-Build([string[]]$Files) {
    # The finished build, checked as a whole: what the peer checks and Windows will see.
    $mine = @('ScreenTail.Service.exe', 'ScreenTail.UI.exe')
    $signatures = @{}
    $unsigned = New-Object System.Collections.Generic.List[string]
    foreach ($file in $Files) {
        $signature = Get-AuthenticodeSignature $file
        $name = Split-Path $file -Leaf
        if ($signature.Status -eq 'NotSigned') {
            $unsigned.Add($name)
        } elseif ($signature.Status -ne 'Valid') {
            throw "$name carries a signature Windows does not accept ($($signature.Status))."
        }
        $signatures[$file] = $signature
    }
    if ($unsigned.Count -gt 0) {
        if ($env:SCREENTAIL_REQUIRE_SIGNING -eq '1') {
            throw "SCREENTAIL_REQUIRE_SIGNING is set and $($unsigned.Count) file(s) are not signed: $($unsigned -join ', ')"
        }
        Write-Host "  unsigned development build, $($unsigned.Count) file(s) not signed; nothing else to check"
        return
    }

    $exes = @($signatures.Keys | Where-Object { (Split-Path $_ -Leaf) -in $mine })
    if ($exes.Count -ne 2) {
        throw 'Verifying a build needs both ScreenTail.Service.exe and ScreenTail.UI.exe among the files.'
    }
    $thumbprints = @($exes | ForEach-Object { $signatures[$_].SignerCertificate.Thumbprint } | Sort-Object -Unique)
    if ($thumbprints.Count -ne 1) {
        throw 'The two executables were signed with different certificates. The peer checks accept only the same one.'
    }
    $publisher = $signatures[$exes[0]].SignerCertificate.Subject
    $ours = 0
    foreach ($file in $signatures.Keys) {
        $signature = $signatures[$file]
        $name = Split-Path $file -Leaf
        $subject = $signature.SignerCertificate.Subject
        $inPayload = $Folder -and $file.StartsWith((Resolve-Path $Folder).Path + '\', [StringComparison]::OrdinalIgnoreCase)
        if ($subject -eq $publisher) {
            if (-not $signature.TimeStamperCertificate) {
                throw "$name was signed without a timestamp. An untimestamped signature stops verifying when its certificate expires."
            }
            $ours++
        } elseif (-not ($inPayload -and $subject -match 'O=Microsoft Corporation')) {
            throw "$name is signed by $subject, which is neither this build's publisher nor, for a library that came with the payload, Microsoft."
        }
    }
    Write-Host "  $($signatures.Count) file(s) verified; $ours signed and timestamped by: $publisher"
}

$mode = Get-SigningMode
if ($Query) {
    return $mode
}

if ($Folder) {
    if (-not (Test-Path $Folder)) {
        throw "$Folder does not exist."
    }
    $Path += @(Get-ChildItem $Folder -Recurse -Include '*.exe', '*.dll' | ForEach-Object { $_.FullName })
}
$Path = @($Path | Where-Object { $_ } | ForEach-Object { (Resolve-Path $_).Path })
if ($Path.Count -eq 0) {
    if ($Verify) {
        throw 'There is nothing to verify.'
    }
    return
}
if ($Verify) {
    Test-Build $Path
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
        $others = @('ManagedIdentityCredential', 'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential',
            'AzureCliCredential', 'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
        if (-not $env:AZURE_CLIENT_SECRET -and $TokenRequestUrl -and $TokenRequestToken) {
            # GitHub Actions with the id-token permission: a short-lived token for this job, exchanged by
            # Azure for access to the signing account. Masked before anything could print it, written to
            # a file in the scratch folder for the Azure SDK, and gone when this step ends.
            if (-not ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID)) {
                throw 'OIDC signing needs AZURE_TENANT_ID and AZURE_CLIENT_ID (the app registration that trusts this repository).'
            }
            $response = Invoke-RestMethod -UseBasicParsing -Uri "$($TokenRequestUrl)&audience=api://AzureADTokenExchange" -Headers @{ Authorization = "Bearer $($TokenRequestToken)" }
            Write-Host "::add-mask::$($response.value)"
            $tokenFile = Join-Path $scratch 'oidc.jwt'
            Set-Content -Path $tokenFile -Value $response.value -NoNewline -Encoding ASCII
            $env:AZURE_FEDERATED_TOKEN_FILE = $tokenFile
            $metadata.ExcludeCredentials = @('EnvironmentCredential') + $others
            Write-Host '  credential: this job''s GitHub OIDC token (workload identity federation)'
        } elseif ($env:AZURE_CLIENT_SECRET) {
            # A service principal in the environment: ask for that credential and nothing else, so the
            # client does not spend a minute probing for a managed identity the machine does not have.
            $metadata.ExcludeCredentials = @('WorkloadIdentityCredential') + $others
            Write-Host '  credential: a service principal from the environment'
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
    Remove-Item Env:AZURE_FEDERATED_TOKEN_FILE -ErrorAction SilentlyContinue
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
