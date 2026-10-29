<#
.SYNOPSIS
    Checks the run a promotion to stable names before anything is downloaded from it (ST-113, ADR-0008).

.DESCRIPTION
    promote-stable.yml downloads the pilot manifest from the ci run whose id the person promoting typed.
    This asks GitHub what that run is, with the job's own read-only token, and stops the job unless it is
    this repository's .github/workflows/ci.yml, started by a push, on main, and finished successfully: a
    run of a pull request, of another branch or of another workflow is never promoted. The manifest is
    then checked again by update-manifest.ps1 against the client's own keys.

    Reads PILOT_RUN (the run id), GH_TOKEN and GITHUB_REPOSITORY from the environment. Prints the run's
    id, workflow, event, branch and conclusion, never the token.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$id = [string]$env:PILOT_RUN
if ($id -notmatch '^\d+$') {
    throw 'PILOT_RUN must be the id of a ci run: digits only.'
}
if (-not $env:GH_TOKEN) {
    throw 'GH_TOKEN is not set; the run cannot be looked up.'
}

$run = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/$($env:GITHUB_REPOSITORY)/actions/runs/$id" -Headers @{
    Authorization = "Bearer $($env:GH_TOKEN)"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}

if ($run.path -cne '.github/workflows/ci.yml') {
    throw "Run $id is $($run.path), not .github/workflows/ci.yml. Nothing was promoted."
}
if ($run.event -cne 'push') {
    throw "Run $id was started by $($run.event), not a push to main. Nothing was promoted."
}
if ($run.head_branch -cne 'main') {
    throw "Run $id is of $($run.head_branch), not main. Nothing was promoted."
}
if ($run.conclusion -cne 'success') {
    throw "Run $id finished $($run.conclusion), not success. Nothing was promoted."
}
if ([string]$run.repository.full_name -cne [string]$env:GITHUB_REPOSITORY) {
    throw "Run $id belongs to another repository. Nothing was promoted."
}
Write-Host "  run $id is $($run.path), a push to main that succeeded"
