[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReportDirectory,
    [ValidateRange(0, 100)]
    [decimal]$MinimumLineCoverage = 90,
    [ValidateRange(0, 100)]
    [decimal]$MinimumBranchCoverage = 85
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -LiteralPath $ReportDirectory -Filter 'coverage.cobertura.xml' -File -Recurse |
    Group-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } |
    ForEach-Object { $_.Group[0] })
if ($reports.Count -ne 1) {
    throw "Expected exactly one distinct unit-test coverage report, found $($reports.Count). Use a clean, dedicated results directory per test run."
}

$document = [System.Xml.XmlDocument]::new()
$document.XmlResolver = $null
$document.Load($reports[0].FullName)
$coverage = $document.DocumentElement
if ($coverage.Name -ne 'coverage') { throw 'Expected a Cobertura coverage document.' }
$packages = @($coverage.SelectNodes('packages/package'))
if ($packages.Count -ne 1 -or $packages[0].GetAttribute('name') -ne 'ElBruno.AI.Jev') {
    throw 'Coverage must measure only the ElBruno.AI.Jev assembly; unrelated modules must not inflate the totals.'
}

$culture = [System.Globalization.CultureInfo]::InvariantCulture
foreach ($kind in @('lines', 'branches')) {
    $valid = [long]::Parse($coverage.GetAttribute("$kind-valid"), $culture)
    $covered = [long]::Parse($coverage.GetAttribute("$kind-covered"), $culture)
    if ($valid -le 0 -or $covered -lt 0 -or $covered -gt $valid) {
        throw "Invalid or empty $kind coverage counts: $covered/$valid."
    }
    $percent = [decimal]100 * $covered / $valid
    $minimum = if ($kind -eq 'lines') { $MinimumLineCoverage } else { $MinimumBranchCoverage }
    Write-Host ("{0}: {1:F2}% ({2}/{3}); required {4}%" -f $kind, $percent, $covered, $valid, $minimum)
    if ($percent -lt $minimum) { throw "$kind coverage is below the required $minimum%." }
}
