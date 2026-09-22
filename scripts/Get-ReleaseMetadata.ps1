[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('release', 'workflow_dispatch')]
    [string]$EventName,
    [string]$ReleaseTag,
    [string]$Version,
    [string]$Ref,
    [Parameter(Mandatory)]
    [string]$Repository,
    [switch]$ApproveUnverifiedPublication,
    [string]$VerifyPublishedRun = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($EventName -ceq 'release') { $Version = $ReleaseTag -creplace '^v', '' }
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z') {
    throw 'Expected SemVer without build metadata; published tags may have one lowercase v prefix.'
}
$parts = $Version.Split('-', 2)
if ($parts.Count -eq 2) {
    foreach ($identifier in $parts[1].Split('.')) {
        if ($identifier -match '^0[0-9]+$') { throw 'Numeric prerelease identifiers cannot start with zero.' }
    }
}
if ($EventName -ceq 'workflow_dispatch' -and $Ref -cne "v$Version" -and $Ref -cne $Version -and $Ref -notmatch '^[0-9a-fA-F]{40}\z') {
    throw 'Manual ref must be the matching version tag (optionally prefixed v) or a full commit SHA.'
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository identity.' }
$publishAllowed = $false
if ($VerifyPublishedRun) {
    if ($EventName -cne 'workflow_dispatch' -or $VerifyPublishedRun -notmatch '^[1-9][0-9]{0,19}\z' -or $ApproveUnverifiedPublication) {
        throw 'Public verification requires a numeric original publication run ID, manual dispatch, and publication approval disabled.'
    }
}
if ($ApproveUnverifiedPublication) {
    $isPreview = $Version -cmatch '^[0-9]+\.[0-9]+\.[0-9]+-preview(?:\.[0-9A-Za-z-]+)*\z'
    if ($EventName -cne 'workflow_dispatch' -or (-not $isPreview -and $Version -cne '0.5.0')) {
        throw 'Live compatibility is unverified. Only an explicitly approved manual preview or the authorized tentative 0.5.0 may be published.'
    }
    $publishAllowed = $true
}
[pscustomobject]@{
    Version = $Version
    RepositoryUrl = "https://github.com/$Repository"
    PublishAllowed = $publishAllowed
    VerificationRun = $VerifyPublishedRun
}
