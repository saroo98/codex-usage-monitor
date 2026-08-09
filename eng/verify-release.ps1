[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseRoot,
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('x64','arm64')][string[]]$Architectures = @('x64','arm64'),
    [switch]$Production,
    [string]$UpdateTrustAnchor
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
$root = [IO.Path]::GetFullPath($ReleaseRoot, $RepositoryRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Release root is missing: $root" }

function Resolve-ReleaseFile([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or [IO.Path]::IsPathRooted($Name) -or [IO.Path]::GetFileName($Name) -ne $Name) {
        throw "Unsafe release artifact name: $Name"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root $Name))
    if ([IO.Path]::GetDirectoryName($path) -ne $root.TrimEnd('\')) { throw "Release artifact escapes the release root: $Name" }
    return $path
}

function Find-WindowsSdkTool([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $candidate = Get-ChildItem $kitsRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64/$Name" } |
        Where-Object { Test-Path $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $candidate) { throw "$Name is required for production signature verification." }
    return $candidate
}

function Assert-PortableArchive([string]$ArchivePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        foreach ($requiredName in @(
            'CodexUsageMonitor/CodexUsageMonitor.exe',
            'CodexUsageMonitor/CodexUsageMonitor.UpdaterHost.exe',
            'CodexUsageMonitor/README.md',
            'CodexUsageMonitor/INSTALL.txt',
            'CodexUsageMonitor/UNINSTALL.txt',
            'CodexUsageMonitor/portable.mode')) {
            if ($names -notcontains $requiredName) { throw "Portable archive is missing required entry: $requiredName" }
        }
        if (@($names | Where-Object { $_ -match '(^|/)\.\.(/|$)|(^|/)[A-Za-z]:/' }).Count -gt 0) {
            throw "Portable archive contains an unsafe path: $ArchivePath"
        }
    }
    finally {
        $archive.Dispose()
    }
}

$required = @('SHA256SUMS.txt','BUILD-METADATA.json','THIRD-PARTY-NOTICES.md','LICENSE.txt','bom.json','update-manifest.json',"CodexUsageMonitor-$Version-source.zip")
foreach ($architecture in $Architectures) {
    $rid = "win-$architecture"
    $required += "CodexUsageMonitor-$Version-$rid-portable-framework-dependent.zip"
    $required += "CodexUsageMonitor-$Version-$rid-portable-self-contained.zip"
    $required += "CodexUsageMonitor-$Version-$rid-update.zip"
    $required += "CodexUsageMonitor-$Version-$architecture.msix"
}
if ($Architectures.Count -gt 1) { $required += "CodexUsageMonitor-$Version.msixbundle" }
if ($Production) { $required += "CodexUsageMonitor-$Version.appinstaller" }
foreach ($name in $required) {
    $path = Resolve-ReleaseFile $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) { throw "Required release artifact is missing or empty: $name" }
}

$inventory = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([0-9a-f]{64}) \*(.+)$') { throw "Invalid checksum inventory line: $line" }
    Resolve-ReleaseFile $Matches[2] | Out-Null
    if ($inventory.ContainsKey($Matches[2])) { throw "Duplicate checksum inventory entry: $($Matches[2])" }
    $inventory[$Matches[2]] = $Matches[1]
}
foreach ($entry in $inventory.GetEnumerator()) {
    $path = Resolve-ReleaseFile $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Checksum target is missing: $($entry.Key)" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw "Checksum mismatch: $($entry.Key)" }
}
foreach ($file in Get-ChildItem -LiteralPath $root -File | Where-Object Name -NotIn @('SHA256SUMS.txt','verification-report.json')) {
    if (-not $inventory.ContainsKey($file.Name)) { throw "Release artifact is missing from checksum inventory: $($file.Name)" }
}

foreach ($architecture in $Architectures) {
    $rid = "win-$architecture"
    & python tools/verify_update_archive.py --archive (Join-Path $root "CodexUsageMonitor-$Version-$rid-update.zip") --version $Version
    if ($LASTEXITCODE -ne 0) { throw "Update archive verification failed for $architecture." }
    foreach ($flavor in @('framework-dependent','self-contained')) {
        $archive = Join-Path $root "CodexUsageMonitor-$Version-$rid-portable-$flavor.zip"
        & python tools/deterministic_zip.py --verify $archive
        if ($LASTEXITCODE -ne 0) { throw "Portable archive verification failed: $archive" }
        Assert-PortableArchive $archive
    }
}
foreach ($archiveName in @("CodexUsageMonitor-$Version-source.zip") +
    @($Architectures | ForEach-Object { "CodexUsageMonitor-$Version-$_.msix" }) +
    $(if ($Architectures.Count -gt 1) { @("CodexUsageMonitor-$Version.msixbundle") } else { @() })) {
    & python tools/deterministic_zip.py --verify (Resolve-ReleaseFile $archiveName)
    if ($LASTEXITCODE -ne 0) { throw "Release container verification failed: $archiveName" }
}

$metadata = Get-Content -LiteralPath (Join-Path $root 'BUILD-METADATA.json') -Raw | ConvertFrom-Json
if ($metadata.version -ne $Version) { throw 'Build metadata version does not match the release version.' }
$sbom = Get-Content -LiteralPath (Join-Path $root 'bom.json') -Raw | ConvertFrom-Json
if ($sbom.bomFormat -ne 'CycloneDX' -or $sbom.metadata.component.version -ne $Version) { throw 'SBOM metadata is invalid or version-inconsistent.' }
$manifest = Get-Content -LiteralPath (Join-Path $root 'update-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.version -ne $Version -or @($manifest.assets).Count -ne $Architectures.Count) { throw 'Update manifest metadata is invalid.' }
$manifestArchitectures = @($manifest.assets | ForEach-Object { ([string]$_.architecture).ToLowerInvariant() } | Sort-Object -Unique)
$expectedArchitectures = @($Architectures | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)
if (Compare-Object $manifestArchitectures $expectedArchitectures) { throw 'Update manifest architecture coverage is invalid.' }
foreach ($asset in @($manifest.assets)) {
    $assetPath = Resolve-ReleaseFile $asset.fileName
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "Update manifest asset is missing: $($asset.fileName)" }
    if ((Get-Item $assetPath).Length -ne $asset.sizeBytes -or (Get-FileHash $assetPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
        throw "Update manifest asset metadata does not match: $($asset.fileName)"
    }
}
if ($Production) {
    if ([string]::IsNullOrWhiteSpace($UpdateTrustAnchor) -or [string]::IsNullOrWhiteSpace($manifest.signature)) { throw 'Production update signature or trust anchor is missing.' }
    & dotnet restore tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --locked-mode `
        -p:UpdatePublicKeyBase64=$UpdateTrustAnchor
    if ($LASTEXITCODE -ne 0) { throw 'Release manifest tool restore failed.' }
    & dotnet run --project tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --configuration Release `
        --no-restore -p:UpdatePublicKeyBase64=$UpdateTrustAnchor -- `
        verify --manifest (Join-Path $root 'update-manifest.json') --trust-anchor $UpdateTrustAnchor
    if ($LASTEXITCODE -ne 0) { throw 'Production update manifest signature verification failed.' }
    if (@($manifest.assets | Where-Object { @($_.publisherThumbprints).Count -eq 0 }).Count -gt 0) { throw 'Production update assets require publisher thumbprints.' }
    $signTool = Find-WindowsSdkTool 'signtool.exe'
    foreach ($packageName in @($Architectures | ForEach-Object { "CodexUsageMonitor-$Version-$_.msix" }) +
        $(if ($Architectures.Count -gt 1) { @("CodexUsageMonitor-$Version.msixbundle") } else { @() })) {
        & $signTool verify /pa /all (Resolve-ReleaseFile $packageName)
        if ($LASTEXITCODE -ne 0) { throw "Production package signature verification failed: $packageName" }
    }
    [xml](Get-Content -LiteralPath (Resolve-ReleaseFile "CodexUsageMonitor-$Version.appinstaller") -Raw) | Out-Null
}

$report = [ordered]@{
    version = $Version; commit = $metadata.commit; verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    production = [bool]$Production; artifactCount = (Get-ChildItem -LiteralPath $root -File).Count
    checksumCount = $inventory.Count; architectures = @($Architectures); status = 'passed'
}
$report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'verification-report.json') -Encoding utf8NoBOM
$report
