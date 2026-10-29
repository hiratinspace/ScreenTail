<#
.SYNOPSIS
    Writes and signs the update manifests for a signed ScreenTail.msi (ST-113, ADR-0008).

.DESCRIPTION
    For each channel, pilot and stable, a manifest the client believes only when its signature verifies
    with a key compiled into it (shared/release/update-trust.json):

      { "format": "screentail-update-manifest-v1", "key": "<key id>",
        "payload": "<base64 of the signed bytes>", "signature": "<base64 of r then s, 64 bytes>" }

    The signed bytes are UTF-8 JSON: the format, the channel, the architecture, the version, the oldest
    version that may update straight to it, the package's URL, SHA-256 and size, and when it was signed.
    The signature is ECDSA P-256 with SHA-256 (ES256), made by Azure Key Vault: the private key never
    leaves the vault and is never in this repository or on the runner. This step only sends the vault a
    SHA-256 digest and gets the signature back.

    Every signature is verified here, against the public keys in the trust file, before anything is
    written, so a manifest the client would refuse is never uploaded. A package URL on a host the client
    does not download from stops the step too.

    Configured from the environment of CI's signing job, never from a file:
      SCREENTAIL_UPDATE_KEY    the Key Vault key's identifier, with its version:
                               https://<vault>.vault.azure.net/keys/<name>/<version>
      SCREENTAIL_UPDATE_BASE   the HTTPS folder the packages are served from, ending in a slash; the
                               package's URL is <base><version>/ScreenTail.msi
      AZURE_TENANT_ID, AZURE_CLIENT_ID and the job's GitHub OIDC token (workload identity federation,
      the same app registration as Artifact Signing; no client secret exists).

    With neither SCREENTAIL_UPDATE_KEY nor SCREENTAIL_UPDATE_BASE it signs nothing, writes nothing, says
    so, and exits successfully: the same rule as sign.ps1 before the certificate exists. With one and not
    the other it fails, because that is a configuration half done.

    It prints the channel, the version, the hash and the size, never a token.

.PARAMETER Msi
    The signed ScreenTail.msi.

.PARAMETER Version
    The release's version, three whole numbers.

.PARAMETER Out
    Where to write <channel>/win-x64.json.

.PARAMETER MinimumVersion
    The oldest installed version that may update straight to this one. Default 0.1.0.

.PARAMETER Verify
    Signs nothing: checks that this manifest's signature verifies with a key in the trust file, as the
    client will, and exits non-zero when it does not. For checking a manifest before it is uploaded.

.PARAMETER Trust
    With -Verify, another trust file to check against. The default is the one compiled into the client.
#>
[CmdletBinding()]
param(
    [string]$Msi,
    [string]$Version,
    [string]$Out,
    [string]$MinimumVersion = '0.1.0',
    [string]$Verify,
    [string]$Trust
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# GitHub's OIDC request settings, taken out of the environment before anything else, as sign.ps1 does.
$TokenRequestUrl = $env:ACTIONS_ID_TOKEN_REQUEST_URL
$TokenRequestToken = $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN
Remove-Item Env:ACTIONS_ID_TOKEN_REQUEST_URL, Env:ACTIONS_ID_TOKEN_REQUEST_TOKEN -ErrorAction SilentlyContinue

$TrustFile = Join-Path $PSScriptRoot '..\shared\release\update-trust.json'
$EnvelopeFormat = 'screentail-update-manifest-v1'
$SignedFormat = 'screentail-update-v1'
$Channels = @('pilot', 'stable')
$KeyPattern = '^https://[a-z0-9-]+\.vault\.azure\.net/keys/[A-Za-z0-9-]+/[0-9a-f]{32}$'
$VersionPattern = '^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$'

function ConvertFrom-Base64Url([string]$Text) {
    $padded = $Text.Replace('-', '+').Replace('_', '/')
    switch ($padded.Length % 4) {
        2 { $padded += '==' }
        3 { $padded += '=' }
    }
    return [Convert]::FromBase64String($padded)
}

function ConvertTo-Base64Url([byte[]]$Bytes) {
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Test-Signature([byte[]]$SignedBytes, [byte[]]$Signature, [string]$Spki) {
    # The same check the client makes, here with Windows' own CNG so it runs on Windows PowerShell 5.1:
    # a P-256 SubjectPublicKeyInfo is a fixed 26-byte prefix and the 65-byte uncompressed point.
    $der = [Convert]::FromBase64String($Spki)
    $prefix = '3059301306072a8648ce3d020106082a8648ce3d030107034200'
    $head = -join ($der[0..25] | ForEach-Object { $_.ToString('x2') })
    if ($der.Length -ne 91 -or $head -ne $prefix -or $der[26] -ne 4) {
        throw 'A key in the trust file is not a P-256 public key.'
    }
    # BCRYPT_ECCKEY_BLOB: 'ECS1' (a P-256 public key), the coordinate length 32, then X and Y.
    $blob = New-Object byte[] 72
    $header = [byte[]](0x45, 0x43, 0x53, 0x31, 0x20, 0x00, 0x00, 0x00)
    [Array]::Copy($header, 0, $blob, 0, 8)
    [Array]::Copy($der, 27, $blob, 8, 64)
    $key = [System.Security.Cryptography.CngKey]::Import($blob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    try {
        $ecdsa = New-Object System.Security.Cryptography.ECDsaCng($key)
        $ecdsa.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
        return $ecdsa.VerifyData($SignedBytes, $Signature)
    } finally {
        $key.Dispose()
    }
}

if ($Verify) {
    $file = if ($Trust) { $Trust } else { $TrustFile }
    $known = @((Get-Content -Raw -Path $file | ConvertFrom-Json).keys)
    $manifest = Get-Content -Raw -Path $Verify | ConvertFrom-Json
    $key = $known | Where-Object { $_.id -ceq $manifest.key } | Select-Object -First 1
    if ($manifest.format -cne $EnvelopeFormat -or -not $key) {
        throw 'The manifest is not in the expected format, or names a key the trust file does not hold.'
    }
    if (-not (Test-Signature ([Convert]::FromBase64String($manifest.payload)) ([Convert]::FromBase64String($manifest.signature)) $key.spki)) {
        throw 'The manifest''s signature does not verify.'
    }
    Write-Host "  the manifest's signature verifies with key $($key.id)"
    return
}

if (-not ($Msi -and $Version -and $Out)) {
    throw 'Give -Msi, -Version and -Out, or -Verify.'
}

# What is being released.
if ($Version -notmatch $VersionPattern -or $MinimumVersion -notmatch $VersionPattern) {
    throw 'The version and the minimum version are three whole numbers.'
}
if (-not (Test-Path $Msi)) {
    throw "$Msi does not exist."
}
$size = (Get-Item $Msi).Length
$sha256 = (Get-FileHash -Algorithm SHA256 -Path $Msi).Hash.ToLowerInvariant()
$published = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)

$vaultKey = $env:SCREENTAIL_UPDATE_KEY
$base = $env:SCREENTAIL_UPDATE_BASE
if (-not $vaultKey -and -not $base) {
    Write-Host "  NOT SIGNED (update signing is not configured; docs/dev/installer.md says how): ScreenTail $Version, $size bytes, SHA-256 $sha256"
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host '::notice title=No update manifest::Update signing is not configured, so no update manifest was written.'
    }
    return
}
if (-not ($vaultKey -and $base)) {
    throw 'Update signing is partly configured: SCREENTAIL_UPDATE_KEY and SCREENTAIL_UPDATE_BASE are needed together.'
}
if ($vaultKey -notmatch $KeyPattern) {
    throw 'SCREENTAIL_UPDATE_KEY must be a Key Vault key identifier with its version: https://<vault>.vault.azure.net/keys/<name>/<version>.'
}
if ($base -notmatch '^https://[A-Za-z0-9.-]+/([A-Za-z0-9._~/-]*/)?$') {
    throw 'SCREENTAIL_UPDATE_BASE must be an https folder URL ending in a slash, with no query.'
}

# What the client trusts, from the file compiled into it.
$trust = Get-Content -Raw -Path $TrustFile | ConvertFrom-Json
$keys = @($trust.keys)
if ($keys.Count -eq 0) {
    throw 'shared/release/update-trust.json trusts no key yet, so the client would refuse any manifest. Add the public key first.'
}
$packageUrl = "$base$Version/ScreenTail.msi"
$packageHost = ([Uri]$packageUrl).Host
if (@($trust.packageHosts) -notcontains $packageHost) {
    throw "$packageHost is not one of the trust file's package hosts, so the client would refuse the package's URL."
}

# A token for the vault, from this job's OIDC token. Masked before anything could print it.
if (-not ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $TokenRequestUrl -and $TokenRequestToken)) {
    throw 'Signing a manifest needs AZURE_TENANT_ID, AZURE_CLIENT_ID and the job''s id-token permission.'
}
$oidc = Invoke-RestMethod -UseBasicParsing -Uri "$($TokenRequestUrl)&audience=api://AzureADTokenExchange" -Headers @{ Authorization = "Bearer $($TokenRequestToken)" }
Write-Host "::add-mask::$($oidc.value)"
$aad = Invoke-RestMethod -UseBasicParsing -Method Post -Uri "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token" -Body @{
    client_id = $env:AZURE_CLIENT_ID
    scope = 'https://vault.azure.net/.default'
    grant_type = 'client_credentials'
    client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
    client_assertion = $oidc.value
}
Write-Host "::add-mask::$($aad.access_token)"

foreach ($channel in $Channels) {
    # Written by hand, in a fixed order, from values checked above to need no escaping.
    $signedText = '{"format":"' + $SignedFormat + '","channel":"' + $channel + '","architecture":"x64","version":"' + $Version +
        '","minimumVersion":"' + $MinimumVersion + '","package":{"url":"' + $packageUrl + '","sha256":"' + $sha256 +
        '","size":' + $size + '},"published":"' + $published + '"}'
    $signedBytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($signedText)
    $digest = [System.Security.Cryptography.SHA256]::Create().ComputeHash($signedBytes)

    $answer = Invoke-RestMethod -UseBasicParsing -Method Post -Uri "$($vaultKey)/sign?api-version=7.4" -ContentType 'application/json' `
        -Headers @{ Authorization = "Bearer $($aad.access_token)" } `
        -Body (@{ alg = 'ES256'; value = (ConvertTo-Base64Url $digest) } | ConvertTo-Json -Compress)
    $signature = ConvertFrom-Base64Url $answer.value
    if ($signature.Length -ne 64) {
        throw "Key Vault answered with a $($signature.Length)-byte signature; ES256 is 64 bytes."
    }

    $keyId = $null
    foreach ($key in $keys) {
        if (Test-Signature $signedBytes $signature $key.spki) {
            $keyId = $key.id
            break
        }
    }
    if (-not $keyId) {
        throw 'The vault key''s signature does not verify with any key in shared/release/update-trust.json. Nothing was written.'
    }

    $envelope = '{"format":"' + $EnvelopeFormat + '","key":"' + $keyId + '","payload":"' + [Convert]::ToBase64String($signedBytes) +
        '","signature":"' + [Convert]::ToBase64String($signature) + '"}'
    $folder = Join-Path $Out $channel
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    [IO.File]::WriteAllText((Join-Path $folder 'win-x64.json'), $envelope, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  signed $channel manifest: ScreenTail $Version, $size bytes, SHA-256 $sha256, key $keyId"
}
