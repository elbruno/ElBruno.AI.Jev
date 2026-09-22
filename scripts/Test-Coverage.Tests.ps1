[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts') ('coverage-script-tests-' + [Guid]::NewGuid().ToString('N'))
$checkScript = Join-Path $PSScriptRoot 'Test-Coverage.ps1'
$utf8 = [Text.UTF8Encoding]::new($false)
$passed = 0

function New-CoverageXml {
    param([long]$Lines = 90, [long]$Branches = 85, [long]$Valid = 100, [string]$Assembly = 'ElBruno.AI.Jev')
    return "<coverage lines-covered='$Lines' lines-valid='$Valid' branches-covered='$Branches' branches-valid='$Valid'><packages><package name='$Assembly'/></packages></coverage>"
}

function Assert-CoverageCheck {
    param([string]$Name, [string[]]$Reports = @(), [string]$ExpectedError)
    $directory = Join-Path $testRoot $Name
    [void][IO.Directory]::CreateDirectory($directory)
    for ($index = 0; $index -lt $Reports.Count; $index++) {
        $reportDirectory = Join-Path $directory "attachment-$index"
        [void][IO.Directory]::CreateDirectory($reportDirectory)
        [IO.File]::WriteAllText((Join-Path $reportDirectory 'coverage.cobertura.xml'), $Reports[$index], $utf8)
    }
    $failure = $null
    try {
        & $checkScript -ReportDirectory $directory
    }
    catch {
        $failure = $_.Exception.Message
    }
    if ($ExpectedError) {
        if (-not $failure -or $failure -notmatch $ExpectedError) {
            throw "$Name expected '$ExpectedError', received '$failure'."
        }
    }
    elseif ($failure) {
        throw "$Name unexpectedly failed: $failure"
    }
    $script:passed++
}

try {
    $boundary = New-CoverageXml
    Assert-CoverageCheck -Name 'exact-thresholds' -Reports @($boundary)
    Assert-CoverageCheck -Name 'trx-attachment-copy' -Reports @($boundary, $boundary)
    Assert-CoverageCheck -Name 'different-reports' -Reports @($boundary, (New-CoverageXml -Lines 99)) -ExpectedError 'exactly one distinct'
    Assert-CoverageCheck -Name 'missing-report' -ExpectedError 'exactly one distinct'
    Assert-CoverageCheck -Name 'line-threshold' -Reports @((New-CoverageXml -Lines 8999 -Branches 10000 -Valid 10000)) -ExpectedError 'lines coverage is below'
    Assert-CoverageCheck -Name 'branch-threshold' -Reports @((New-CoverageXml -Lines 10000 -Branches 8499 -Valid 10000)) -ExpectedError 'branches coverage is below'
    Assert-CoverageCheck -Name 'wrong-assembly' -Reports @((New-CoverageXml -Assembly 'Unrelated')) -ExpectedError 'only the ElBruno.AI.Jev assembly'
    Assert-CoverageCheck -Name 'impossible-counts' -Reports @((New-CoverageXml -Lines 101)) -ExpectedError 'Invalid or empty lines'
    Assert-CoverageCheck -Name 'empty-counts' -Reports @((New-CoverageXml -Lines 0 -Branches 0 -Valid 0)) -ExpectedError 'Invalid or empty lines'
    Write-Host "PASS: $passed coverage-gate regression checks."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
