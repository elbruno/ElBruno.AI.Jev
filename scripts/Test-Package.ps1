[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [string]$Version,
    [switch]$RequireSourceLink,
    [string]$ExpectedRepositoryUrl,
    [switch]$UsePublicFeed,
    [ValidateRange(1, 20)]
    [int]$IndexAttempts = 10,
    [ValidateRange(1, 60)]
    [int]$IndexDelaySeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$packageId = 'ElBruno.AI.Jev'
$consumerRoot = Join-Path (Join-Path $repositoryRoot 'tests') "$packageId.PackageTests"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $props = [xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)
    $versionElement = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionElement) { throw 'No Version property found in Directory.Build.props; pass an explicit -Version.' }
    $Version = $versionElement.InnerText
}
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z') {
    throw 'Version must be SemVer (major.minor.patch with optional prerelease); build metadata is not a NuGet version identity.'
}
$prerelease = $Version.Split('-', 2)
if ($prerelease.Count -eq 2) {
    foreach ($identifier in $prerelease[1].Split('.')) {
        if ($identifier -match '^0[0-9]+$') { throw 'Numeric prerelease identifiers cannot have leading zeroes.' }
    }
}
if ($RequireSourceLink -and $ExpectedRepositoryUrl -cnotmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z') {
    throw 'Source Link verification requires the actual https://github.com/OWNER/REPOSITORY URL.'
}
if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'README.md') -PathType Leaf)) {
    throw 'Root README.md is not ready. Ask the metadata owner to finish it; this script will not create it.'
}
$nugetReadmePath = Join-Path (Join-Path $repositoryRoot 'docs') 'nuget-readme.md'
if (-not (Test-Path -LiteralPath $nugetReadmePath -PathType Leaf)) {
    throw 'docs/nuget-readme.md is not ready. The package requires its dedicated NuGet README.'
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path (Join-Path $repositoryRoot 'artifacts') 'packages'
}
$feed = (Resolve-Path -LiteralPath $PackageDirectory).ProviderPath
$packagePath = Join-Path $feed "$packageId.$Version.nupkg"
$symbolsPath = Join-Path $feed "$packageId.$Version.snupkg"
foreach ($path in @($packagePath, $symbolsPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package artifact: $path" }
}

Add-Type -AssemblyName System.IO.Compression
function Read-ZipBytes {
    param([System.IO.Compression.ZipArchive]$Archive, [string]$Name)
    $entries = @($Archive.Entries | Where-Object { $_.FullName -ceq $Name })
    if ($entries.Count -ne 1 -or $entries[0].Length -le 0) { throw "Expected one nonempty package entry: $Name" }
    $stream = $entries[0].Open()
    $buffer = [System.IO.MemoryStream]::new()
    try {
        $stream.CopyTo($buffer)
        return ,$buffer.ToArray()
    }
    finally {
        $buffer.Dispose()
        $stream.Dispose()
    }
}

function Read-Nuspec {
    param([System.IO.Compression.ZipArchive]$Archive)
    $entries = @($Archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
    if ($entries.Count -ne 1) { throw 'Expected exactly one package manifest.' }
    return ConvertFrom-PackageXml (Read-ZipBytes $Archive $entries[0].FullName)
}

function ConvertFrom-PackageXml {
    param([byte[]]$Bytes)
    $xml = [System.Xml.XmlDocument]::new()
    $xml.XmlResolver = $null
    $stream = [System.IO.MemoryStream]::new($Bytes)
    try {
        $xml.Load($stream)
        return $xml
    }
    finally {
        $stream.Dispose()
    }
}

function Get-Metadata {
    param([System.Xml.XmlDocument]$Manifest, [string]$Name)
    return $Manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='$Name']")
}

$package = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
$symbols = [System.IO.Compression.ZipFile]::OpenRead($symbolsPath)
try {
    $manifest = Read-Nuspec $package
    $symbolManifest = Read-Nuspec $symbols
    foreach ($spec in @($manifest, $symbolManifest)) {
        if ((Get-Metadata $spec 'id').InnerText -cne $packageId -or
            (Get-Metadata $spec 'version').InnerText -cne $Version) {
            throw 'Package/symbol identity or version does not match the requested release.'
        }
    }
    foreach ($name in @('authors', 'description', 'tags')) {
        $element = Get-Metadata $manifest $name
        if ($null -eq $element -or [string]::IsNullOrWhiteSpace($element.InnerText)) {
            throw "Missing package metadata: $name"
        }
    }
    if ((Get-Metadata $manifest 'readme').InnerText -cne 'README.md' -or
        (Get-Metadata $manifest 'icon').InnerText -cne 'nuget-icon.png') {
        throw 'Package README/icon metadata must identify the packaged files.'
    }
    $license = Get-Metadata $manifest 'license'
    if ($license.GetAttribute('type') -ne 'expression' -or $license.InnerText -ne 'MIT') {
        throw 'Expected the approved MIT license expression.'
    }
    $repository = Get-Metadata $manifest 'repository'
    if ($RequireSourceLink -and
        ($null -eq $repository -or $repository.GetAttribute('url') -cne $ExpectedRepositoryUrl -or
         $repository.GetAttribute('type') -ne 'git' -or $repository.GetAttribute('commit') -notmatch '^[0-9a-fA-F]{40}$')) {
        throw 'Release package must contain the actual repository URL, git type, and immutable commit.'
    }
    if ($RequireSourceLink) {
        foreach ($name in @('projectUrl', 'releaseNotes')) {
            $element = Get-Metadata $manifest $name
            if ($null -eq $element -or [string]::IsNullOrWhiteSpace($element.InnerText)) {
                throw "Missing release package metadata: $name"
            }
        }
    }
    $dependencies = @(Get-Metadata $manifest 'dependencies' |
        ForEach-Object { $_.SelectNodes("*[local-name()='group']/*[local-name()='dependency']") })
    foreach ($dependency in @('Microsoft.Extensions.AI', 'Microsoft.Extensions.Http')) {
        if (-not ($dependencies | Where-Object { $_.GetAttribute('id') -ceq $dependency -and $_.GetAttribute('version') })) {
            throw "Missing versioned package dependency: $dependency"
        }
    }

    $assemblyBytes = Read-ZipBytes $package "lib/net10.0/$packageId.dll"
    $xmlBytes = Read-ZipBytes $package "lib/net10.0/$packageId.xml"
    $readmeBytes = Read-ZipBytes $package 'README.md'
    $licenseBytes = Read-ZipBytes $package 'LICENSE'
    $iconBytes = Read-ZipBytes $package 'nuget-icon.png'
    $pdbBytes = Read-ZipBytes $symbols "lib/net10.0/$packageId.pdb"
    if ($assemblyBytes[0] -ne 0x4d -or $assemblyBytes[1] -ne 0x5a) { throw 'Invalid DLL signature.' }
    if ([System.Text.Encoding]::ASCII.GetString($pdbBytes, 0, 4) -ne 'BSJB') { throw 'Symbols are not a portable PDB.' }
    $xmlDocumentation = ConvertFrom-PackageXml $xmlBytes
    if ($xmlDocumentation.doc.assembly.name -ne $packageId -or $xmlDocumentation.doc.members.member.Count -lt 1) {
        throw 'Missing assembly XML documentation members.'
    }
    if ([System.Text.Encoding]::UTF8.GetString($readmeBytes) -notmatch 'ElBruno\.AI\.Jev' -or
        [System.Text.Encoding]::UTF8.GetString($licenseBytes) -notmatch 'MIT License') {
        throw 'Invalid packaged README or license content.'
    }
    $packagedReadme = [System.Text.Encoding]::UTF8.GetString($readmeBytes).TrimStart([char]0xFEFF).Replace("`r`n", "`n")
    $sourceReadme = [System.IO.File]::ReadAllText($nugetReadmePath).Replace("`r`n", "`n")
    if ($packagedReadme -cne $sourceReadme) {
        throw 'Packaged README.md must match docs/nuget-readme.md, not the root README. Use the matching release checkout for downloaded artifacts.'
    }
    if ($Version -ceq '0.5.0') {
        foreach ($text in @((Get-Metadata $manifest 'description').InnerText, (Get-Metadata $manifest 'releaseNotes').InnerText, $packagedReadme)) {
            if ($text -notmatch '\btentative\b' -or $text -notmatch '\bunverified\b') {
                throw 'Tentative 0.5.0 must disclose tentative status and unverified live compatibility in its description, release notes, and README.'
            }
        }
    }
    if ($iconBytes.Length -ge 1000000 -or [BitConverter]::ToString($iconBytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') {
        throw 'Package icon must be a PNG smaller than 1 MB.'
    }
    $widthBytes = $iconBytes[16..19]
    $heightBytes = $iconBytes[20..23]
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($widthBytes); [Array]::Reverse($heightBytes) }
    if ([BitConverter]::ToInt32($widthBytes, 0) -ne 128 -or [BitConverter]::ToInt32($heightBytes, 0) -ne 128) {
        throw 'Package icon must be 128 x 128.'
    }
}
finally {
    $symbols.Dispose()
    $package.Dispose()
}

$workRoot = Join-Path $consumerRoot '.work'
if ((Get-Item -LiteralPath $consumerRoot -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    throw 'Refusing a redirected consumer directory.'
}
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$workDirectory = Get-Item -LiteralPath $workRoot -Force
if ($workDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    throw 'Refusing a redirected scratch directory.'
}
$workRoot = $workDirectory.FullName
$runDirectory = Join-Path $workRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$oldEnvironment = @{}
foreach ($name in @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH')) {
    $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

try {
    Copy-Item -LiteralPath (Join-Path $consumerRoot "$packageId.PackageTests.csproj") -Destination $runDirectory
    Copy-Item -LiteralPath (Join-Path $consumerRoot 'Program.cs') -Destination $runDirectory
    $project = Join-Path $runDirectory "$packageId.PackageTests.csproj"
    $pdbPath = Join-Path $runDirectory "$packageId.pdb"
    [System.IO.File]::WriteAllBytes($pdbPath, $pdbBytes)
    $escapedFeed = [System.Security.SecurityElement]::Escape($feed)
    $localSource = if ($UsePublicFeed) { '' } else { "<add key=`"packed`" value=`"$escapedFeed`" />" }
    $localMapping = if ($UsePublicFeed) { '' } else {
        "<packageSource key=`"packed`"><package pattern=`"$packageId`" /></packageSource>"
    }
    $configuration = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear />$localSource<add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <packageSourceMapping>
    $localMapping
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
    $configPath = Join-Path $runDirectory 'NuGet.Config'
    [System.IO.File]::WriteAllText($configPath, $configuration)
    $attemptLimit = if ($UsePublicFeed) { $IndexAttempts } else { 1 }
    $restored = $false
    for ($attempt = 1; $attempt -le $attemptLimit; $attempt++) {
        $env:NUGET_PACKAGES = Join-Path $runDirectory "packages-$attempt"
        $env:NUGET_HTTP_CACHE_PATH = Join-Path $runDirectory "http-$attempt"
        $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $runDirectory "plugins-$attempt"
        & dotnet restore $project --configfile $configPath --packages $env:NUGET_PACKAGES --no-http-cache --force `
            "-p:JevPackageVersion=$Version" '-p:NuGetAudit=false' '-p:RestoreIgnoreFailedSources=false' `
            '-p:RestoreFallbackFolders=' '-p:RestoreAdditionalProjectFallbackFolders='
        if ($LASTEXITCODE -eq 0) {
            $restored = $true
            break
        }
        if ($attempt -lt $attemptLimit) {
            Write-Host "Exact public restore attempt $attempt/$attemptLimit failed; retrying in $IndexDelaySeconds seconds with a fresh isolated cache."
            Start-Sleep -Seconds $IndexDelaySeconds
        }
    }
    if (-not $restored) { throw "Exact package restore failed after $attemptLimit attempt(s); publication/indexing is NOT verified." }

    Invoke-Dotnet -Arguments @('build', $project, '--configuration', 'Release', '--no-restore', "-p:JevPackageVersion=$Version")
    $assemblyHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($assemblyBytes))
    $expectedRepository = if ($RequireSourceLink) { $ExpectedRepositoryUrl } else { '-' }
    $expectedCommit = if ($RequireSourceLink) { $repository.GetAttribute('commit') } else { '-' }
    Invoke-Dotnet -Arguments @('run', '--project', $project, '--configuration', 'Release', '--no-build', '--no-restore',
        "-p:JevPackageVersion=$Version", '--', $Version, $assemblyHash, $pdbPath, $expectedRepository, $expectedCommit)
    Write-Host "PASS: package ZIP, metadata, symbols, and isolated $(if ($UsePublicFeed) { 'public NuGet' } else { 'local packed' }) consumer."
}
finally {
    foreach ($name in $oldEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process')
    }
    $resolvedRun = (Get-Item -LiteralPath $runDirectory -Force).FullName
    if ((Split-Path $resolvedRun -Parent) -ne $workRoot -or (Split-Path $resolvedRun -Leaf) -notmatch '^[0-9a-f]{32}$') {
        throw 'Refusing cleanup outside this specific package-test run.'
    }
    Remove-Item -LiteralPath $resolvedRun -Recurse -Force
}
