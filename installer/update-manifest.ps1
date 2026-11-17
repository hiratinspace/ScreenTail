<#
.SYNOPSIS
    Writes and signs an update manifest for a signed ScreenTail.msi (ST-113, ADR-0008).

.DESCRIPTION
    A manifest the client believes only when its signature verifies with a key compiled into it
    (shared/release/update-trust.json):

      { "format": "screentail-update-manifest-v1", "key": "<key id>",
        "payload": "<base64 of the signed bytes>", "signature": "<base64 of r then s, 64 bytes>" }

    The signed bytes are UTF-8 JSON: the format, the channel, the architecture, the version, the oldest
    version that may update straight to it, the package's URL, SHA-256 and size, and when it was signed.
    The channel is inside the signed bytes, so a manifest signed for one channel is refused on the other.
    The signature is ECDSA P-256 with SHA-256 (ES256), made by Azure Key Vault: the private key never
    leaves the vault and is never in this repository or on the runner. This step only sends the vault a
    SHA-256 digest and gets the signature back.

    One channel per run, named with -Channel, which has no default and no "all":

      -Channel pilot, with -Msi, -Version and -Out: what CI's signing job runs for every signed build.

      -Channel stable, with -Promote and -Out: the promotion (promote-stable.yml), and the only way a
      stable manifest is ever signed. It signs, for stable, exactly the release an earlier build signed
      for pilot: the pilot manifest must verify with a trusted key, say pilot, and carry the version the
      person promoting typed (PROMOTE_VERSION). It refuses to run unless SCREENTAIL_STABLE_RELEASE is
      'true', a variable only the owner's protected release-stable environment holds, so it fails closed
      anywhere else. A build is refused -Channel stable.

    Each channel has its own key. Every key in the trust file is trusted for one channel, and the client
    believes a manifest only when the key that signed it is trusted for the channel the manifest names, so
    a pilot key cannot make a stable manifest the client accepts, and the other way round. Each key lives
    in Key Vault behind its own Azure identity: the pilot identity trusts only the 'signing' environment
    and may sign only with the pilot key; the stable identity trusts only 'release-stable' and may sign
    only with the stable key.

    Every signature is verified here, against the public keys the trust file holds for the channel being
    signed, before anything is written, so a manifest the client would refuse is never uploaded. A package
    URL on a host the client does not download from stops the step too.

    Configured from the environment of the job that runs it, never from a file. A run reads only its own
    channel's two settings and refuses to start while the other channel's are in its environment:
      SCREENTAIL_UPDATE_PILOT_KEY, SCREENTAIL_UPDATE_STABLE_KEY
                               that channel's Key Vault key identifier, with its version:
                               https://<vault>.vault.azure.net/keys/<name>/<version>
      SCREENTAIL_UPDATE_PILOT_CLIENT_ID, SCREENTAIL_UPDATE_STABLE_CLIENT_ID
                               that channel's Azure app registration (its client id)
      SCREENTAIL_UPDATE_BASE   (a build) the HTTPS folder the packages are served from, ending in a
                               slash; the package's URL is <base><version>/ScreenTail.msi
      SCREENTAIL_STABLE_RELEASE, PROMOTE_VERSION (a promotion), as above
      AZURE_TENANT_ID and the job's GitHub OIDC token (workload identity federation; no client secret
      exists).

    A build with neither SCREENTAIL_UPDATE_PILOT_KEY nor SCREENTAIL_UPDATE_BASE signs nothing, writes
    nothing, says so, and exits successfully: the same rule as sign.ps1 before the certificate exists.
    With one and not the other it fails, because that is a configuration half done.

    It prints the channel, the version, the hash and the size, never a token.

.PARAMETER Channel
    pilot (a build) or stable (a promotion). Required.

.PARAMETER Msi
    A build: the signed ScreenTail.msi.

.PARAMETER Version
    A build: the release's version, three whole numbers.

.PARAMETER Out
    Where to write <channel>/win-x64.json.

.PARAMETER MinimumVersion
    A build: the oldest installed version that may update straight to this one. Default 0.1.0.

.PARAMETER Promote
    A promotion: the pilot manifest of the release to sign for stable.

.PARAMETER Verify
    Signs nothing: checks that this manifest's signature verifies with a key in the trust file, and that
    the key is trusted for the channel the manifest names, as the client will, and exits non-zero when
    either does not hold. For checking a manifest before it is uploaded.

.PARAMETER Trust
    With -Verify, another trust file to check against. The default is the one compiled into the client.
#>
[CmdletBinding()]
param(
    [string]$Channel,
    [string]$Msi,
    [string]$Version,
    [string]$Out,
    [string]$MinimumVersion = '0.1.0',
    [string]$Promote,
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
$KeyPattern = '^https://[a-z0-9-]+\.vault\.azure\.net/keys/[A-Za-z0-9-]+/[0-9a-f]{32}$'
$VersionPattern = '^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$'
$UrlPattern = '^https://[A-Za-z0-9.-]+/[A-Za-z0-9._~/-]*$'

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

function Get-TrustedKeys($Trusted) {
    # The trust file's keys, each trusted for exactly one channel, and no key or id in it twice: a key
    # listed for both channels would make the two one.
    $keys = @($Trusted.keys)
    $seen = @{}
    foreach ($key in $keys) {
        if (@('pilot', 'stable') -cnotcontains [string]$key.channel) {
            throw 'Every key in the trust file names the one channel it signs for: pilot or stable.'
        }
        foreach ($mark in @(('spki:' + [string]$key.spki), ('id:' + [string]$key.id))) {
            if ($seen.ContainsKey($mark)) {
                throw 'A key or a key id is in the trust file twice. Each key signs for one channel only.'
            }
            $seen[$mark] = $true
        }
    }
    return ,$keys
}

function Find-Key($Keys, [byte[]]$SignedBytes, [byte[]]$Signature) {
    foreach ($key in $Keys) {
        if (Test-Signature $SignedBytes $Signature $key.spki) {
            return $key.id
        }
    }
    return $null
}

if ($Verify) {
    $file = if ($Trust) { $Trust } else { $TrustFile }
    $known = Get-TrustedKeys (Get-Content -Raw -Path $file | ConvertFrom-Json)
    $manifest = Get-Content -Raw -Path $Verify | ConvertFrom-Json
    $key = $known | Where-Object { $_.id -ceq $manifest.key } | Select-Object -First 1
    if ($manifest.format -cne $EnvelopeFormat -or -not $key) {
        throw 'The manifest is not in the expected format, or names a key the trust file does not hold.'
    }
    $verifyBytes = [Convert]::FromBase64String($manifest.payload)
    if (-not (Test-Signature $verifyBytes ([Convert]::FromBase64String($manifest.signature)) $key.spki)) {
        throw 'The manifest''s signature does not verify.'
    }
    # Only now is the signed part read: the channel it names must be the one its key is trusted for.
    $signedChannel = [string]([Text.Encoding]::UTF8.GetString($verifyBytes) | ConvertFrom-Json).channel
    if ($signedChannel -cne [string]$key.channel) {
        throw 'The manifest is for a channel its key is not trusted for, so the client would refuse it.'
    }
    Write-Host "  the manifest's signature verifies with key $($key.id), trusted for $($key.channel)"
    return
}

# One channel, named, every time.
if ($Channel -notin @('pilot', 'stable')) {
    throw 'Give -Channel pilot (a build) or -Channel stable (a promotion). There is no default and no "all".'
}
if ($Channel -ceq 'stable' -and -not $Promote) {
    throw 'Stable is signed only by promotion (promote-stable.yml), never by a build.'
}
if ($Promote -and $Channel -cne 'stable') {
    throw 'A promotion signs stable, from a pilot manifest.'
}
if (-not $Out) {
    throw 'Give -Out.'
}

# Each channel's own key and identity, and only those. The other channel's settings in this run's
# environment mean the two environments are not separate, so nothing is signed.
if ($Channel -ceq 'stable') {
    $vaultKey = $env:SCREENTAIL_UPDATE_STABLE_KEY
    $clientId = $env:SCREENTAIL_UPDATE_STABLE_CLIENT_ID
    $foreign = $env:SCREENTAIL_UPDATE_PILOT_KEY -or $env:SCREENTAIL_UPDATE_PILOT_CLIENT_ID
} else {
    $vaultKey = $env:SCREENTAIL_UPDATE_PILOT_KEY
    $clientId = $env:SCREENTAIL_UPDATE_PILOT_CLIENT_ID
    $foreign = $env:SCREENTAIL_UPDATE_STABLE_KEY -or $env:SCREENTAIL_UPDATE_STABLE_CLIENT_ID
}
if ($foreign -or $env:SCREENTAIL_UPDATE_KEY) {
    throw "This $Channel run can see another channel's update signing settings (or the old shared SCREENTAIL_UPDATE_KEY). Each channel signs only with its own key and identity, set on its own environment. Nothing was signed."
}

$trusted = Get-Content -Raw -Path $TrustFile | ConvertFrom-Json
$keys = Get-TrustedKeys $trusted
# The keys the client trusts for this channel: the only ones this run's signature may verify with.
$channelKeys = @($keys | Where-Object { $_.channel -ceq $Channel })
$pilotKeys = @($keys | Where-Object { $_.channel -ceq 'pilot' })

if ($Promote) {
    # The gate first: outside the owner's release-stable environment nothing is read and nothing signed.
    if ($env:SCREENTAIL_STABLE_RELEASE -cne 'true') {
        throw 'Promotion to stable runs only in the release-stable environment, whose SCREENTAIL_STABLE_RELEASE is true. Nothing was signed.'
    }
    if ($env:PROMOTE_VERSION -notmatch $VersionPattern) {
        throw 'PROMOTE_VERSION must be the version being promoted, three whole numbers.'
    }
    $envelopeIn = Get-Content -Raw -Path $Promote | ConvertFrom-Json
    if ($envelopeIn.format -cne $EnvelopeFormat) {
        throw 'That is not an update manifest.'
    }
    $pilotBytes = [Convert]::FromBase64String($envelopeIn.payload)
    if (-not (Find-Key $pilotKeys $pilotBytes ([Convert]::FromBase64String($envelopeIn.signature)))) {
        throw 'The pilot manifest does not verify with any key the client trusts for pilot, so it is not promoted.'
    }
    $pilot = [Text.Encoding]::UTF8.GetString($pilotBytes) | ConvertFrom-Json
    if ($pilot.channel -cne 'pilot' -or $pilot.version -cne $env:PROMOTE_VERSION) {
        throw 'The manifest is not the pilot release of the version given, so it is not promoted.'
    }
    $Version = [string]$pilot.version
    $MinimumVersion = [string]$pilot.minimumVersion
    $packageUrl = [string]$pilot.package.url
    $sha256 = [string]$pilot.package.sha256
    $size = [long]$pilot.package.size
} else {
    if (-not ($Msi -and $Version)) {
        throw 'A build gives -Msi and -Version.'
    }
    if (-not (Test-Path $Msi)) {
        throw "$Msi does not exist."
    }
    $size = (Get-Item $Msi).Length
    $sha256 = (Get-FileHash -Algorithm SHA256 -Path $Msi).Hash.ToLowerInvariant()
    $base = $env:SCREENTAIL_UPDATE_BASE
    if (-not $vaultKey -and -not $base) {
        Write-Host "  NOT SIGNED (update signing is not configured; docs/dev/installer.md says how): ScreenTail $Version, $size bytes, SHA-256 $sha256"
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-Host '::notice title=No update manifest::Update signing is not configured, so no update manifest was written.'
        }
        return
    }
    if (-not ($vaultKey -and $base)) {
        throw 'Update signing is partly configured: SCREENTAIL_UPDATE_PILOT_KEY and SCREENTAIL_UPDATE_BASE are needed together.'
    }
    if ($base -notmatch '^https://[A-Za-z0-9.-]+/([A-Za-z0-9._~/-]*/)?$') {
        throw 'SCREENTAIL_UPDATE_BASE must be an https folder URL ending in a slash, with no query.'
    }
    $packageUrl = "$base$Version/ScreenTail.msi"
}

# What is signed, held to what the client accepts before the vault is asked for anything.
if ($Version -notmatch $VersionPattern -or $MinimumVersion -notmatch $VersionPattern) {
    throw 'The version and the minimum version are three whole numbers.'
}
if ($sha256 -cnotmatch '^[0-9a-f]{64}$' -or $size -lt 1 -or $packageUrl -notmatch $UrlPattern) {
    throw 'The package''s hash, size or URL is not in the shape the client accepts.'
}
if ($channelKeys.Count -eq 0) {
    throw "shared/release/update-trust.json trusts no key for $Channel yet, so the client would refuse any $Channel manifest. Add that channel's public key first."
}
$packageHost = ([Uri]$packageUrl).Host
if (@($trusted.packageHosts) -notcontains $packageHost) {
    throw "$packageHost is not one of the trust file's package hosts, so the client would refuse the package's URL."
}
if ($vaultKey -notmatch $KeyPattern) {
    throw "SCREENTAIL_UPDATE_$($Channel.ToUpperInvariant())_KEY must be a Key Vault key identifier with its version: https://<vault>.vault.azure.net/keys/<name>/<version>."
}

# A token for the vault, from this job's OIDC token, for this channel's identity. Masked before anything
# could print it.
if (-not ($env:AZURE_TENANT_ID -and $clientId -and $TokenRequestUrl -and $TokenRequestToken)) {
    throw "Signing a $Channel manifest needs AZURE_TENANT_ID, SCREENTAIL_UPDATE_$($Channel.ToUpperInvariant())_CLIENT_ID and the job's id-token permission."
}
$oidc = Invoke-RestMethod -UseBasicParsing -Uri "$($TokenRequestUrl)&audience=api://AzureADTokenExchange" -Headers @{ Authorization = "Bearer $($TokenRequestToken)" }
Write-Host "::add-mask::$($oidc.value)"
$aad = Invoke-RestMethod -UseBasicParsing -Method Post -Uri "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token" -Body @{
    client_id = $clientId
    scope = 'https://vault.azure.net/.default'
    grant_type = 'client_credentials'
    client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
    client_assertion = $oidc.value
}
Write-Host "::add-mask::$($aad.access_token)"

# Written by hand, in a fixed order, from values checked above to need no escaping.
$published = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
$signedText = '{"format":"' + $SignedFormat + '","channel":"' + $Channel + '","architecture":"x64","version":"' + $Version +
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
foreach ($key in $channelKeys) {
    if (Test-Signature $signedBytes $signature $key.spki) {
        $keyId = $key.id
        break
    }
}
if (-not $keyId) {
    throw 'The vault key''s signature does not verify with any key shared/release/update-trust.json trusts for this channel. Nothing was written.'
}

$envelope = '{"format":"' + $EnvelopeFormat + '","key":"' + $keyId + '","payload":"' + [Convert]::ToBase64String($signedBytes) +
    '","signature":"' + [Convert]::ToBase64String($signature) + '"}'
$folder = Join-Path $Out $Channel
New-Item -ItemType Directory -Force -Path $folder | Out-Null
[IO.File]::WriteAllText((Join-Path $folder 'win-x64.json'), $envelope, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "  signed $Channel manifest: ScreenTail $Version, $size bytes, SHA-256 $sha256, key $keyId"
