<#
.SYNOPSIS
Sets the official TypeSafe Jev API key for every sample and live integration test.
.DESCRIPTION
Prompts for a masked key and passes JSON through standard input to Secret Manager.
All projects share ElBruno.AI.Jev.Development. Other secrets are preserved.
User-secrets are stored outside the repository but are not encrypted.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$storeId = 'ElBruno.AI.Jev.Development'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK before configuring user-secrets.'
}

if (-not $PSCmdlet.ShouldProcess($storeId, 'Set Jev:ApiKey for all samples and live integration tests')) {
    return
}

$secureKey = Read-Host 'Enter your official TypeSafe Jev API key (input is hidden)' -AsSecureString
$buffer = [IntPtr]::Zero
$plainKey = $null
$payload = $null
$json = $null

try {
    $buffer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($buffer)
    if ([string]::IsNullOrWhiteSpace($plainKey) -or $plainKey -match '\s') {
        throw 'The API key must be nonempty and contain no whitespace. No secret was changed.'
    }

    $payload = @{ 'Jev:ApiKey' = $plainKey }
    $json = $payload | ConvertTo-Json -Compress
    $json | dotnet user-secrets set --id $storeId
    if ($LASTEXITCODE -ne 0) {
        throw "Secret Manager failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Configured Jev:ApiKey for all 10 samples and the live integration-test project.'
    Write-Host 'The key was not included in command arguments. No inference request was made.'
}
finally {
    if ($buffer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($buffer)
    }
    $plainKey = $null
    $payload = $null
    $json = $null
    $secureKey.Dispose()
}
