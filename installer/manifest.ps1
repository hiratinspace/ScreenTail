<#
.SYNOPSIS
    Binds the payload CI's signing job signs to the one the build job made (ST-112, ADR-0007).

.DESCRIPTION
    -Write, in the build job: the SHA-256 of every file in the payload, as one line of JSON (relative
    path to hash), written to the step's outputs as 'files' so the job can pass it on.

    -Check, in the signing job, before anything is signed:
      1. the artifact it downloaded (PAYLOAD_ARTIFACT_ID) is the one the build job uploaded: it belongs
         to this run, has the payload's name, has not expired, and its SHA-256 digest is the one the
         upload reported (PAYLOAD_ARTIFACT_DIGEST). Read from GitHub's API with the job's own token
         (GH_TOKEN), which can only read;
      2. the files in the folder are exactly the ones in the build's manifest (PAYLOAD_MANIFEST), each
         with the same SHA-256. Anything missing, extra or different stops the job.

    Signs nothing, builds nothing, prints file names and hashes only.

.PARAMETER Folder
    The payload folder.

.PARAMETER Write
    Write the manifest to the step's outputs.

.PARAMETER Check
    Check the downloaded artifact and the folder, as above.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [switch]$Write,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ArtifactName = 'screentail-installer-payload'

function Get-Manifest([string]$Root) {
    $root = (Resolve-Path $Root).Path
    $files = [ordered]@{}
    foreach ($file in Get-ChildItem $root -Recurse -File -Force | Sort-Object FullName) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $files[$relative] = (Get-FileHash -Algorithm SHA256 -Path $file.FullName).Hash.ToLowerInvariant()
    }
    return $files
}

if ($Write -eq $Check) {
    throw 'Give exactly one of -Write and -Check.'
}

if ($Write) {
    $manifest = Get-Manifest $Folder
    if ($manifest.Count -eq 0) {
        throw "$Folder holds no files."
    }
    $json = $manifest | ConvertTo-Json -Compress
    if (-not $env:GITHUB_OUTPUT) {
        throw 'GITHUB_OUTPUT is not set; -Write is for a CI step.'
    }
    # UTF-8 with no byte-order mark, which is what the runner reads.
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "files=$json`n")
    Write-Host "  manifest of $($manifest.Count) files written to the step outputs"
    return
}

# 1. The artifact is the build job's upload, unchanged.
$id = $env:PAYLOAD_ARTIFACT_ID
$expected = ([string]$env:PAYLOAD_ARTIFACT_DIGEST).ToLowerInvariant() -replace '^sha256:', ''
if ($id -notmatch '^\d+$') {
    throw 'The build job reported no payload artifact id.'
}
if ($expected -notmatch '^[0-9a-f]{64}$') {
    throw 'The build job reported no SHA-256 digest for the payload artifact.'
}
if (-not $env:GH_TOKEN) {
    throw 'GH_TOKEN is not set; the artifact cannot be looked up.'
}
$artifact = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/$($env:GITHUB_REPOSITORY)/actions/artifacts/$id" -Headers @{
    Authorization = "Bearer $($env:GH_TOKEN)"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$actual = ([string]$artifact.digest).ToLowerInvariant() -replace '^sha256:', ''
$problems = New-Object System.Collections.Generic.List[string]
if ($artifact.name -ne $ArtifactName) {
    $problems.Add("artifact $id is named '$($artifact.name)', not '$ArtifactName'")
}
if ([string]$artifact.workflow_run.id -ne [string]$env:GITHUB_RUN_ID) {
    $problems.Add("artifact $id belongs to run $($artifact.workflow_run.id), not this run ($($env:GITHUB_RUN_ID))")
}
if ($artifact.expired) {
    $problems.Add("artifact $id has expired")
}
if ($actual -ne $expected) {
    $problems.Add("artifact $id has digest '$actual', not the '$expected' its upload reported")
}

# 2. The files are exactly the build's, byte for byte.
$wanted = @{}
($env:PAYLOAD_MANIFEST | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $wanted[$_.Name] = ([string]$_.Value).ToLowerInvariant() }
if ($wanted.Count -eq 0) {
    $problems.Add('the build job reported an empty manifest')
}
$found = Get-Manifest $Folder
foreach ($name in $wanted.Keys | Sort-Object) {
    if (-not $found.Contains($name)) {
        $problems.Add("missing: $name")
    } elseif ($found[$name] -ne $wanted[$name]) {
        $problems.Add("different from the build's: $name")
    }
}
foreach ($name in $found.Keys) {
    if (-not $wanted.ContainsKey($name)) {
        $problems.Add("not in the build's manifest: $name")
    }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "::error::$_" }
    throw "The payload is not the one the build job made ($($problems.Count) problem(s) above). Nothing was signed."
}
Write-Host "  artifact $id is this run's $ArtifactName, digest $actual"
Write-Host "  all $($found.Count) files match the build's manifest"
