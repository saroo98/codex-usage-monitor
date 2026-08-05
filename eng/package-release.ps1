[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateSet('x64','arm64')][string[]]$Architectures = @('x64','arm64'),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$IdentityName = 'saroo98.CodexUsageMonitor',
    [string]$Publisher = 'CN=Codex Usage Monitor Development',
    [string]$PublisherDisplayName = 'Codex Usage Monitor Contributors',
    [string]$SigningCertificatePath,
    [Security.SecureString]$SigningCertificatePassword,
    [string]$TimestampUrl,
    [string]$UpdatePrivateKeyPath,
    [string]$UpdateTrustAnchor,
    [uri]$FeedBaseUri,
    [uri]$ReleaseNotesUri,
    [string[]]$PublisherThumbprints = @(),
    [switch]$Production,
    [bool]$VerifyDeterminism = $true,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
. "$PSScriptRoot/ProductVersion.ps1"

$centralVersion = Get-ProductVersion -RepositoryRoot $RepositoryRoot
if ($Version -ne $centralVersion) { throw "Requested version $Version does not match product version $centralVersion." }
$releaseRoot = [IO.Path]::GetFullPath($OutputRoot, $RepositoryRoot)
$buildTrustAnchor = if ([string]::IsNullOrWhiteSpace($UpdateTrustAnchor)) {
    'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='
} else { $UpdateTrustAnchor }
$repositoryFullPath = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
if ($releaseRoot.TrimEnd('\') -eq $repositoryFullPath -or $repositoryFullPath.StartsWith($releaseRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be a dedicated directory below or outside the repository root.'
}
if ($Production) {
    $missing = @()
    foreach ($pair in @{
        Publisher = $Publisher; SigningCertificatePath = $SigningCertificatePath; TimestampUrl = $TimestampUrl;
        UpdatePrivateKeyPath = $UpdatePrivateKeyPath; UpdateTrustAnchor = $UpdateTrustAnchor;
        FeedBaseUri = $FeedBaseUri; ReleaseNotesUri = $ReleaseNotesUri
    }.GetEnumerator()) { if ([string]::IsNullOrWhiteSpace([string]$pair.Value)) { $missing += $pair.Key } }
    if ($PublisherThumbprints.Count -eq 0) { $missing += 'PublisherThumbprints' }
    if ($missing.Count) { throw "Production packaging is missing required values: $($missing -join ', ')." }
    if ($FeedBaseUri.Scheme -ne 'https' -or $ReleaseNotesUri.Scheme -ne 'https' -or $TimestampUrl -notmatch '^https://') {
        throw 'Production feed, release notes, and timestamp endpoints must use HTTPS.'
    }
    if ($Publisher -match 'Development') { throw 'Production packaging cannot use the development publisher identity.' }
    if (@(git status --porcelain).Count -gt 0) { throw 'Production packaging requires a clean working tree.' }
}
if ($UpdatePrivateKeyPath -and [string]::IsNullOrWhiteSpace($UpdateTrustAnchor)) {
    throw 'UpdateTrustAnchor is required whenever UpdatePrivateKeyPath is supplied.'
}

if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$rids = $Architectures | ForEach-Object { "win-$_" }
$portableArgs = @{
    RuntimeIdentifiers = $rids; Version = $Version; OutputRoot = $releaseRoot; Configuration = $Configuration
    NoRestore = [bool]$NoRestore; UpdatePublicKeyBase64 = $buildTrustAnchor
}
& "$PSScriptRoot/package-portable.ps1" @portableArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Portable release packaging failed.' }

$msixArgs = @{
    Architectures = $Architectures; Version = $Version; OutputRoot = $releaseRoot; Configuration = $Configuration
    IdentityName = $IdentityName; Publisher = $Publisher; PublisherDisplayName = $PublisherDisplayName
    NoRestore = [bool]$NoRestore; UpdatePublicKeyBase64 = $buildTrustAnchor
}
if ($SigningCertificatePath) { $msixArgs.CertificatePath = $SigningCertificatePath }
if ($SigningCertificatePassword) { $msixArgs.CertificatePassword = $SigningCertificatePassword }
if ($TimestampUrl) { $msixArgs.TimestampUrl = $TimestampUrl }
& "$PSScriptRoot/package-msix.ps1" @msixArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MSIX release packaging failed.' }

if ($FeedBaseUri) {
    & "$PSScriptRoot/generate-appinstaller.ps1" -BaseUri $FeedBaseUri -Version $Version -IdentityName $IdentityName -Publisher $Publisher -OutputRoot $releaseRoot | Out-Null
}

$publishedAt = [DateTimeOffset]::FromUnixTimeSeconds([long](git show -s --format=%ct HEAD)).ToUniversalTime()
$assets = foreach ($architecture in $Architectures | Sort-Object) {
    $fileName = "CodexUsageMonitor-$Version-win-$architecture-update.zip"
    $path = Join-Path $releaseRoot $fileName
    [ordered]@{
        architecture = $architecture
        url = if ($FeedBaseUri) { $FeedBaseUri.AbsoluteUri.TrimEnd('/') + '/' + $fileName } else { "https://invalid.example/$fileName" }
        fileName = $fileName
        sizeBytes = (Get-Item -LiteralPath $path).Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        publisherThumbprints = @($PublisherThumbprints)
    }
}
$manifest = [ordered]@{
    schemaVersion = 1
    channel = 'stable'
    version = $Version
    publishedAtUtc = $publishedAt
    minimumOsBuild = 19041
    releaseNotesUrl = if ($ReleaseNotesUri) { $ReleaseNotesUri.AbsoluteUri } else { 'https://invalid.example/release-notes' }
    assets = @($assets)
}
$manifest.signature = ''
$manifestPath = Join-Path $releaseRoot 'update-manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
if ($UpdatePrivateKeyPath) {
    if (-not $NoRestore) {
        & dotnet restore tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --locked-mode `
            -p:UpdatePublicKeyBase64=$UpdateTrustAnchor
        if ($LASTEXITCODE -ne 0) { throw 'Release manifest tool restore failed.' }
    }
    & dotnet run --project tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --configuration Release `
        --no-restore -p:UpdatePublicKeyBase64=$UpdateTrustAnchor -- `
        sign --manifest $manifestPath --private-key $UpdatePrivateKeyPath --trust-anchor $UpdateTrustAnchor
    if ($LASTEXITCODE -ne 0) { throw 'Update manifest signing failed.' }
}

dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Local tool restore failed.' }
& dotnet CycloneDX CodexUsageMonitor.slnx --output $releaseRoot --filename bom.json --output-format Json --exclude-test-projects --disable-package-restore --set-name CodexUsageMonitor --set-version $Version --no-serial-number
if ($LASTEXITCODE -ne 0) { throw 'SBOM generation failed.' }

Copy-Item -LiteralPath LICENSE -Destination (Join-Path $releaseRoot 'LICENSE.txt')
Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination (Join-Path $releaseRoot 'THIRD-PARTY-NOTICES.md')
$sourceArchive = Join-Path $releaseRoot "CodexUsageMonitor-$Version-source.zip"
& git archive --format=zip --output=$sourceArchive --prefix="CodexUsageMonitor-$Version/" HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source archive generation failed.' }
$metadata = [ordered]@{
    product = 'Codex Usage Monitor for Windows'; version = $Version; commit = (git rev-parse HEAD).Trim()
    sdk = (& dotnet --version).Trim(); configuration = $Configuration; architectures = @($Architectures)
    production = [bool]$Production; generatedAtUtc = $publishedAt.ToString('O')
}
$metadata | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $releaseRoot 'BUILD-METADATA.json') -Encoding utf8NoBOM

if ($VerifyDeterminism) {
    $determinismRoot = Join-Path (Split-Path -Parent $releaseRoot) ('.determinism-' + [Guid]::NewGuid().ToString('N'))
    try {
        $secondArgs = @{
            RuntimeIdentifiers = $rids; Version = $Version; OutputRoot = $determinismRoot
            Configuration = $Configuration; NoRestore = [bool]$NoRestore; UpdatePublicKeyBase64 = $buildTrustAnchor
        }
        & "$PSScriptRoot/package-portable.ps1" @secondArgs | Out-Null
        foreach ($rid in $rids) {
            foreach ($flavor in @('framework-dependent','self-contained')) {
                $name = "CodexUsageMonitor-$Version-$rid-portable-$flavor.zip"
                $firstHash = (Get-FileHash (Join-Path $releaseRoot $name) -Algorithm SHA256).Hash
                $secondHash = (Get-FileHash (Join-Path $determinismRoot $name) -Algorithm SHA256).Hash
                if ($firstHash -ne $secondHash) {
                    & python tools/deterministic_zip.py --compare (Join-Path $releaseRoot $name) (Join-Path $determinismRoot $name) | Out-Host
                    throw "Deterministic portable output check failed: $name"
                }
            }
        }
    }
    finally { if (Test-Path $determinismRoot) { Remove-Item -LiteralPath $determinismRoot -Recurse -Force } }
}

$inventoryTargets = Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object Name -NotIn @('SHA256SUMS.txt','verification-report.json') | Sort-Object Name
$lines = foreach ($file in $inventoryTargets) { "$(($file | Get-FileHash -Algorithm SHA256).Hash.ToLowerInvariant()) *$($file.Name)" }
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))

$verifyArgs = @{ ReleaseRoot = $releaseRoot; Version = $Version; Architectures = $Architectures; Production = [bool]$Production }
if ($Production) { $verifyArgs.UpdateTrustAnchor = $UpdateTrustAnchor }
& "$PSScriptRoot/verify-release.ps1" @verifyArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Independent release verification failed.' }
Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name | Select-Object -ExpandProperty FullName
