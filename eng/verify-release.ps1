[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseRoot,
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('x64','arm64')][string[]]$Architectures = @('x64','arm64'),
    [ValidateSet('Validation','PublicUnsigned')][string]$ReleaseMode = 'Validation',
    [string]$UpdateTrustAnchor,
    [string]$ExpectedRepository = 'saroo98/codex-usage-monitor',
    [string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
Import-Module "$PSScriptRoot/ReleaseVerification.psm1" -Force
$root = [IO.Path]::GetFullPath($ReleaseRoot, $RepositoryRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Release root is missing: $root" }
$isPublicUnsigned = $ReleaseMode -eq 'PublicUnsigned'
if ($isPublicUnsigned) {
    $publicArchitectures = @($Architectures | Sort-Object -Unique)
    if ($publicArchitectures.Count -ne 2 -or $publicArchitectures -notcontains 'x64' -or $publicArchitectures -notcontains 'arm64') {
        throw 'Public unsigned verification requires the exact x64 and arm64 architecture matrix.'
    }
    if ([string]::IsNullOrWhiteSpace($UpdateTrustAnchor)) { throw 'Public unsigned verification requires the update trust anchor.' }
    if ($UpdateTrustAnchor -eq '11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=') {
        throw 'Public unsigned verification rejects the repository validation trust anchor.'
    }
}
$unsignedMarkerPath = Join-Path $root 'UNSIGNED-RELEASE-CANDIDATE.txt'
$expectedUnsignedMarkerBytes = [Text.UTF8Encoding]::new($false).GetBytes(
    "UNSIGNED VALIDATION ARTIFACTS`nThese files are not production-signed. Do not publish or distribute them as a release.`n")
if ($isPublicUnsigned) {
    $unsignedMarkerPath = Join-Path $root 'UNSIGNED-WINDOWS-RELEASE.txt'
    $expectedPublicMarkerBytes = [Text.UTF8Encoding]::new($false).GetBytes(
        "UNSIGNED WINDOWS RELEASE`nThese Windows executables are not Authenticode-signed and Windows can show an unverified or unknown publisher.`nVerify downloads against SHA256SUMS.txt and the GitHub artifact attestations from saroo98/codex-usage-monitor.`nDo not disable Windows security controls.`n")
    if (-not (Test-Path -LiteralPath $unsignedMarkerPath -PathType Leaf)) {
        throw 'Required release artifact is missing or empty: UNSIGNED-WINDOWS-RELEASE.txt'
    }
    $actualPublicMarkerBytes = [IO.File]::ReadAllBytes($unsignedMarkerPath)
    if ($actualPublicMarkerBytes.Length -ne $expectedPublicMarkerBytes.Length -or
        [Convert]::ToHexString($actualPublicMarkerBytes) -ne [Convert]::ToHexString($expectedPublicMarkerBytes)) {
        throw 'UNSIGNED-WINDOWS-RELEASE.txt does not contain the required exact UTF-8, no-BOM LF bytes.'
    }
} else {
    if (-not (Test-Path -LiteralPath $unsignedMarkerPath -PathType Leaf)) {
        throw 'Required release artifact is missing or empty: UNSIGNED-RELEASE-CANDIDATE.txt'
    }
    $actualUnsignedMarkerBytes = [IO.File]::ReadAllBytes($unsignedMarkerPath)
    if ($actualUnsignedMarkerBytes.Length -ne $expectedUnsignedMarkerBytes.Length -or
        [Convert]::ToHexString($actualUnsignedMarkerBytes) -ne [Convert]::ToHexString($expectedUnsignedMarkerBytes)) {
        throw 'UNSIGNED-RELEASE-CANDIDATE.txt does not contain the required exact UTF-8, no-BOM LF bytes.'
    }
}

function Resolve-ReleaseFile([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or [IO.Path]::IsPathRooted($Name) -or [IO.Path]::GetFileName($Name) -ne $Name) {
        throw "Unsafe release artifact name: $Name"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root $Name))
    if ([IO.Path]::GetDirectoryName($path) -ne $root.TrimEnd('\')) { throw "Release artifact escapes the release root: $Name" }
    return $path
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

function Assert-PublicUnsignedArchiveBoundary([string]$ArchivePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ($name -match '(?i)(^|/)(?:\.signpath|AppxSignature\.p7x|signing-request[^/]*)(?:/|$)' -or
                $name -match '(?i)\.(?:pfx|p12|cer)$') {
                throw 'Public unsigned archive contains forbidden signing-provider or certificate material.'
            }
        }
    }
    finally { $archive.Dispose() }
}

function Assert-SourceArchiveMatchesHead([string]$ArchivePath,[string]$Version,[string]$ExpectedCommit) {
    $head = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedCommit) { throw 'Public source verification checkout does not match build metadata.' }
    $expected = @(& git ls-tree -r --name-only HEAD | ForEach-Object { "CodexUsageMonitor-$Version/$($_.Replace('\','/'))" } | Sort-Object)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate the tagged source tree.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try { $actual = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) } | ForEach-Object FullName | Sort-Object) }
    finally { $archive.Dispose() }
    if (@(Compare-Object $expected $actual -CaseSensitive).Count -ne 0) {
        throw 'Public source archive does not exactly match the tagged source tree.'
    }
}

$required = @('SHA256SUMS.txt','BUILD-METADATA.json','THIRD-PARTY-NOTICES.md','LICENSE.txt','bom.json','update-manifest.json',"CodexUsageMonitor-$Version-source.zip")
foreach ($architecture in $Architectures) {
    $rid = "win-$architecture"
    $required += "CodexUsageMonitor-$Version-$rid-portable-framework-dependent.zip"
    $required += "CodexUsageMonitor-$Version-$rid-portable-self-contained.zip"
    $required += "CodexUsageMonitor-$Version-$rid-update.zip"
    if (-not $isPublicUnsigned) { $required += "CodexUsageMonitor-$Version-$architecture.msix" }
}
if (-not $isPublicUnsigned -and $Architectures.Count -gt 1) { $required += "CodexUsageMonitor-$Version.msixbundle" }
if ($isPublicUnsigned) { $required += 'UNSIGNED-WINDOWS-RELEASE.txt' }
foreach ($name in $required) {
    $path = Resolve-ReleaseFile $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) { throw "Required release artifact is missing or empty: $name" }
}
if ($isPublicUnsigned) {
    if (@($Architectures | Sort-Object -Unique).Count -ne 2 -or $Architectures -notcontains 'x64' -or $Architectures -notcontains 'arm64') {
        throw 'Release verification requires the exact x64 and arm64 architecture matrix.'
    }
    $actualReleaseNames = @((Get-ChildItem -LiteralPath $root -File | ForEach-Object Name) |
        Where-Object { $_ -ne 'verification-report.json' } | Sort-Object)
    $expectedReleaseNames = @($required | Sort-Object -Unique)
    if (@(Compare-Object $expectedReleaseNames $actualReleaseNames -CaseSensitive).Count -ne 0) {
        throw 'Release root does not contain the exact reviewed artifact matrix.'
    }
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

if ($isPublicUnsigned) {
    $expectedInventoryNames = @($required | Where-Object { $_ -ne 'SHA256SUMS.txt' } | Sort-Object)
    $actualInventoryNames = @($inventory.Keys | Sort-Object)
    if (@(Compare-Object $expectedInventoryNames $actualInventoryNames -CaseSensitive).Count -ne 0) {
        throw 'Checksum inventory does not exactly match the reviewed artifact matrix.'
    }
}

$metadata = Read-ReleaseBuildMetadata -Path (Join-Path $root 'BUILD-METADATA.json') -PublicUnsigned:$isPublicUnsigned
if ([string]$metadata.version -cne $Version) { throw 'Build metadata version does not match the release version.' }
$metadataArchitectures = @($metadata.architectures | Sort-Object)
$requestedArchitectures = @($Architectures | Sort-Object)
if (@(Compare-Object $requestedArchitectures $metadataArchitectures -CaseSensitive).Count -ne 0) {
    throw 'Build metadata architecture coverage does not match the requested verification matrix.'
}
if ($isPublicUnsigned) {
    Assert-SourceArchiveMatchesHead -ArchivePath (Join-Path $root "CodexUsageMonitor-$Version-source.zip") `
        -Version $Version -ExpectedCommit ([string]$metadata.commit)
}

foreach ($architecture in $Architectures) {
    $rid = "win-$architecture"
    & python tools/verify_update_archive.py --archive (Join-Path $root "CodexUsageMonitor-$Version-$rid-update.zip") --version $Version
    if ($LASTEXITCODE -ne 0) { throw "Update archive verification failed for $architecture." }
    Test-ReleaseArchive -ArchivePath (Join-Path $root "CodexUsageMonitor-$Version-$rid-update.zip") `
        -TemporaryBase ([IO.Path]::GetTempPath()) -ArchiveKind Update -Version $Version | Out-Null
    if ($isPublicUnsigned) { Assert-PublicUnsignedArchiveBoundary (Join-Path $root "CodexUsageMonitor-$Version-$rid-update.zip") }
    foreach ($flavor in @('framework-dependent','self-contained')) {
        $archive = Join-Path $root "CodexUsageMonitor-$Version-$rid-portable-$flavor.zip"
        & python tools/deterministic_zip.py --verify $archive
        if ($LASTEXITCODE -ne 0) { throw "Portable archive verification failed: $archive" }
        Assert-PortableArchive $archive
        Test-ReleaseArchive -ArchivePath $archive -TemporaryBase ([IO.Path]::GetTempPath()) -ArchiveKind Portable -Version $Version | Out-Null
        if ($isPublicUnsigned) { Assert-PublicUnsignedArchiveBoundary $archive }
    }
}
$containerNames = @("CodexUsageMonitor-$Version-source.zip")
if (-not $isPublicUnsigned) {
    $containerNames += @($Architectures | ForEach-Object { "CodexUsageMonitor-$Version-$_.msix" })
    if ($Architectures.Count -gt 1) { $containerNames += "CodexUsageMonitor-$Version.msixbundle" }
}
foreach ($archiveName in $containerNames) {
    & python tools/deterministic_zip.py --verify (Resolve-ReleaseFile $archiveName)
    if ($LASTEXITCODE -ne 0) { throw "Release container verification failed: $archiveName" }
}

$sbom = Get-Content -LiteralPath (Join-Path $root 'bom.json') -Raw | ConvertFrom-Json
if ($sbom.bomFormat -ne 'CycloneDX' -or $sbom.metadata.component.version -ne $Version) { throw 'SBOM metadata is invalid or version-inconsistent.' }
if ($isPublicUnsigned) {
    $requiredPackageNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($lockFile in Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src') -Recurse -Filter packages.lock.json -File) {
        $lock = Get-Content -LiteralPath $lockFile.FullName -Raw | ConvertFrom-Json
        foreach ($framework in $lock.dependencies.PSObject.Properties.Value) {
            foreach ($dependency in $framework.PSObject.Properties) {
                if ([string]$dependency.Value.type -ceq 'Direct') {
                    $null = $requiredPackageNames.Add($dependency.Name)
                }
            }
        }
    }
    if ($requiredPackageNames.Count -eq 0) { throw 'No direct source dependencies were found in packages.lock.json files.' }
    $sbomPackageNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($component in @($sbom.components)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$component.name)) {
            $null = $sbomPackageNames.Add([string]$component.name)
        }
    }
    $missingPackageNames = @($requiredPackageNames | Where-Object { -not $sbomPackageNames.Contains($_) } | Sort-Object)
    if ($missingPackageNames.Count -ne 0) {
        throw "SBOM dependency coverage is incomplete. Missing direct source packages: $($missingPackageNames -join ', ')"
    }
}
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
    if ($isPublicUnsigned) {
        $expectedName = "CodexUsageMonitor-$Version-win-$(([string]$asset.architecture).ToLowerInvariant())-update.zip"
        $expectedUrl = "https://github.com/$ExpectedRepository/releases/download/v$Version/$expectedName"
        if ([string]$asset.fileName -cne $expectedName -or [string]$asset.url -cne $expectedUrl -or @($asset.publisherThumbprints).Count -ne 0) {
            throw 'Public unsigned update manifest asset contract is invalid.'
        }
    }
}
if ($isPublicUnsigned) {
    if ([string]$manifest.releaseNotesUrl -cne "https://github.com/$ExpectedRepository/releases/tag/v$Version" -or
        [string]::IsNullOrWhiteSpace([string]$manifest.signature)) {
        throw 'Public unsigned update manifest release metadata is invalid.'
    }
    & dotnet run --project tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --configuration Release `
        -p:UpdatePublicKeyBase64=$UpdateTrustAnchor -- `
        verify --manifest (Join-Path $root 'update-manifest.json') --trust-anchor $UpdateTrustAnchor
    if ($LASTEXITCODE -ne 0) { throw 'Public unsigned update manifest signature verification failed.' }
}
$report = New-ReleaseVerificationReport -Version $Version -MetadataCommit ([string]$metadata.commit) `
    -ArtifactCount (Get-ChildItem -LiteralPath $root -File).Count -ChecksumCount $inventory.Count -Architectures $Architectures
$report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'verification-report.json') -Encoding utf8NoBOM
$report
