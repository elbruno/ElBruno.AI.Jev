[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$policy = Join-Path $PSScriptRoot 'Get-ReleaseMetadata.ps1'
$passed = 0

function Assert-ReleasePolicy {
    param([hashtable]$Inputs, [string]$ExpectedVersion, [bool]$ExpectedPublish = $false, [string]$ExpectedError, [string]$ExpectedVerificationRun = '')
    $failure = $null
    $result = $null
    try {
        $result = & $policy -Repository 'elbruno/ElBruno.AI.Jev' @Inputs
    }
    catch {
        $failure = $_.Exception.Message
    }
    if ($ExpectedError) {
        if (-not $failure -or $failure -notmatch $ExpectedError) {
            throw "Expected '$ExpectedError', received '$failure'."
        }
    }
    elseif ($failure -or $result.Version -cne $ExpectedVersion -or $result.PublishAllowed -ne $ExpectedPublish -or
        $result.RepositoryUrl -cne 'https://github.com/elbruno/ElBruno.AI.Jev' -or $result.VerificationRun -cne $ExpectedVerificationRun) {
        throw "Unexpected release policy result: $failure"
    }
    $script:passed++
}

Assert-ReleasePolicy @{ EventName = 'release'; ReleaseTag = 'v0.5.0' } -ExpectedVersion '0.5.0'
Assert-ReleasePolicy @{ EventName = 'release'; ReleaseTag = '1.0.0' } -ExpectedVersion '1.0.0'
Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.5.0'; Ref = 'v0.5.0' } -ExpectedVersion '0.5.0'
Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.5.0'; Ref = 'v0.5.0'; ApproveUnverifiedPublication = $true } -ExpectedVersion '0.5.0' -ExpectedPublish $true
Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.5.0'; Ref = ('a' * 40); ApproveUnverifiedPublication = $true } -ExpectedVersion '0.5.0' -ExpectedPublish $true
Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.6.0-preview.1'; Ref = '0.6.0-preview.1'; ApproveUnverifiedPublication = $true } -ExpectedVersion '0.6.0-preview.1' -ExpectedPublish $true
Assert-ReleasePolicy @{ EventName = 'release'; ReleaseTag = 'v0.5.0'; ApproveUnverifiedPublication = $true } -ExpectedError 'explicitly approved manual'
foreach ($version in @('0.5.1', '1.0.0', '0.5.0-beta.1')) {
    Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = $version; Ref = "v$version"; ApproveUnverifiedPublication = $true } -ExpectedError 'authorized tentative 0.5.0'
}
foreach ($version in @('01.5.0', '0.5.0+build', '0.5.0;echo unsafe')) {
    Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = $version; Ref = ('a' * 40) } -ExpectedError 'Expected SemVer'
}
Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.5.0-preview.01'; Ref = ('a' * 40) } -ExpectedError 'cannot start with zero'
Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.5.0'; Ref = 'main'; ApproveUnverifiedPublication = $true } -ExpectedError 'Manual ref must'
Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.5.0'; Ref = 'v0.5.1'; ApproveUnverifiedPublication = $true } -ExpectedError 'Manual ref must'
Assert-ReleasePolicy @{ EventName = 'release'; ReleaseTag = 'V0.5.0' } -ExpectedError 'Expected SemVer'
Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.5.0'; Ref = ('a' * 40); VerifyPublishedRun = '35729776067' } -ExpectedVersion '0.5.0' -ExpectedVerificationRun '35729776067'
Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.5.0'; Ref = ('a' * 40); VerifyPublishedRun = '35729776067'; ApproveUnverifiedPublication = $true } -ExpectedError 'publication approval disabled'
Assert-ReleasePolicy @{ EventName = 'release'; ReleaseTag = 'v0.5.0'; VerifyPublishedRun = '35729776067' } -ExpectedError 'manual dispatch'
foreach ($run in @('0', '001', '-1', 'bad/run')) {
    Assert-ReleasePolicy @{ EventName = 'workflow_dispatch'; Version = '0.5.0'; Ref = ('a' * 40); VerifyPublishedRun = $run } -ExpectedError 'numeric original publication run ID'
}
Write-Host "PASS: $passed release-policy regression checks."
