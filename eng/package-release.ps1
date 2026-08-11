[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateSet('x64','arm64')][string[]]$Architectures = @('x64','arm64'),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('Development','Production','PublicUnsigned')]
    [string]$UpdateBuildFlavor = $(if ($Configuration -eq 'Release') { 'Production' } else { 'Development' }),
    [ValidateSet('Validation','PublicUnsigned')]
    [string]$ReleaseMode = 'Validation',
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'saroo98/codex-usage-monitor',
    [string]$IdentityName = 'saroo98.CodexUsageMonitor',
    [string]$Publisher = 'CN=Codex Usage Monitor Development',
    [string]$PublisherDisplayName = 'Codex Usage Monitor Contributors',
    # NON-PRODUCTION TEST KEY. Production release preflight must reject this value.
    [string]$UpdateTrustAnchor = '11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=',
    [uri]$FeedBaseUri,
    [uri]$ReleaseNotesUri,
    [string]$GoogleOAuthClientId,
    [string]$MicrosoftOAuthClientId,
    [string]$MicrosoftOAuthTenant = 'common',
    [bool]$VerifyDeterminism = $true,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
. "$PSScriptRoot/ProductVersion.ps1"
Import-Module "$PSScriptRoot/ReleaseVerification.psm1" -Force

$centralVersion = Get-ProductVersion -RepositoryRoot $RepositoryRoot
if ($Version -ne $centralVersion) { throw "Requested version $Version does not match product version $centralVersion." }
$isPublicUnsigned = $ReleaseMode -eq 'PublicUnsigned'
if ($isPublicUnsigned) {
    $publicArchitectures = @($Architectures | Sort-Object -Unique)
    if ($publicArchitectures.Count -ne 2 -or $publicArchitectures -notcontains 'x64' -or $publicArchitectures -notcontains 'arm64') {
        throw 'Public unsigned packaging requires the exact x64 and arm64 architecture matrix.'
    }
    if ($FeedBaseUri -or $ReleaseNotesUri) {
        throw 'Public unsigned packaging derives immutable GitHub release URLs and does not accept feed overrides.'
    }
    if ($UpdateTrustAnchor -eq '11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=') {
        throw 'Public unsigned packaging rejects the repository validation trust anchor.'
    }
    try { $trustAnchorBytes = [Convert]::FromBase64String($UpdateTrustAnchor) }
    catch [FormatException] { throw 'Public unsigned packaging requires a valid base64 update trust anchor.' }
    try {
        if ($trustAnchorBytes.Length -ne 32) { throw 'Public unsigned packaging requires a 32-byte update trust anchor.' }
    }
    finally { if ($trustAnchorBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($trustAnchorBytes) } }
    if ([string]::IsNullOrWhiteSpace($env:UPDATE_PRIVATE_KEY_BASE64)) {
        throw 'Public unsigned packaging requires UPDATE_PRIVATE_KEY_BASE64 in the current process environment.'
    }
    $tagCommit = (& git rev-parse "refs/tags/v$Version^{}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagCommit) -or $tagCommit.Trim() -cne (& git rev-parse HEAD).Trim()) {
        throw "Public unsigned packaging requires tag v$Version to peel to HEAD."
    }
    $UpdateBuildFlavor = 'PublicUnsigned'
}
$releaseRoot = [IO.Path]::GetFullPath($OutputRoot, $RepositoryRoot)
$repositoryFullPath = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
if ($releaseRoot.TrimEnd('\') -eq $repositoryFullPath -or
    $repositoryFullPath.StartsWith($releaseRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be a dedicated directory below or outside the repository root.'
}
if ($FeedBaseUri) {
    $feedArchitectures = @($Architectures | Sort-Object -Unique)
    if ($feedArchitectures.Count -ne 2 -or $feedArchitectures -notcontains 'x64' -or $feedArchitectures -notcontains 'arm64') {
        throw 'App Installer generation requires the exact x64 and arm64 bundle architecture set.'
    }
}

if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$rids = $Architectures | ForEach-Object { "win-$_" }
$publishRoot = Join-Path (Split-Path -Parent $releaseRoot) ('.publish-' + [Guid]::NewGuid().ToString('N'))

try {
    $publishArguments = @{
        Version = $Version; OutputRoot = $publishRoot; Architectures = $Architectures
        Configuration = $Configuration; NoRestore = [bool]$NoRestore
        UpdatePublicKeyBase64 = $UpdateTrustAnchor
        UpdateBuildFlavor = $UpdateBuildFlavor
        GoogleOAuthClientId = $GoogleOAuthClientId; MicrosoftOAuthClientId = $MicrosoftOAuthClientId
        MicrosoftOAuthTenant = $MicrosoftOAuthTenant
    }
    & "$PSScriptRoot/publish-release-trees.ps1" @publishArguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Release-tree publishing failed.' }

    $portableArgs = @{
        RuntimeIdentifiers = $rids; Version = $Version; OutputRoot = $releaseRoot; PublishRoot = $publishRoot
        Configuration = $Configuration; NoRestore = [bool]$NoRestore; UpdatePublicKeyBase64 = $UpdateTrustAnchor
        UpdateBuildFlavor = $UpdateBuildFlavor
    }
    & "$PSScriptRoot/package-portable.ps1" @portableArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Portable release packaging failed.' }

    if (-not $isPublicUnsigned) {
        $msixArgs = @{
            Architectures = $Architectures; Version = $Version; OutputRoot = $releaseRoot; PublishRoot = $publishRoot
            Configuration = $Configuration; IdentityName = $IdentityName; Publisher = $Publisher
            PublisherDisplayName = $PublisherDisplayName; UpdatePublicKeyBase64 = $UpdateTrustAnchor
            UpdateBuildFlavor = $UpdateBuildFlavor
        }
        & "$PSScriptRoot/package-msix.ps1" @msixArgs | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'MSIX release packaging failed.' }
    }

    if ($VerifyDeterminism) {
        $determinismRoot = Join-Path (Split-Path -Parent $releaseRoot) ('.determinism-' + [Guid]::NewGuid().ToString('N'))
        try {
            $secondArgs = @{
                RuntimeIdentifiers = $rids; Version = $Version; OutputRoot = $determinismRoot
                PublishRoot = $publishRoot; Configuration = $Configuration
                UpdatePublicKeyBase64 = $UpdateTrustAnchor
                UpdateBuildFlavor = $UpdateBuildFlavor
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
        finally {
            if (Test-Path -LiteralPath $determinismRoot) { Remove-Item -LiteralPath $determinismRoot -Recurse -Force }
        }
    }
}
finally {
    if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
}

if ($FeedBaseUri -and -not $isPublicUnsigned) {
    $bundleFileName = "CodexUsageMonitor-$Version.msixbundle"
    $bundlePath = Join-Path $releaseRoot $bundleFileName
    if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf) -or (Get-Item -LiteralPath $bundlePath).Length -le 0) {
        throw "App Installer generation requires the existing nonempty bundle: $bundleFileName"
    }
    $appInstallerUri = [uri]($FeedBaseUri.AbsoluteUri.TrimEnd('/') + '/CodexUsageMonitor.appinstaller')
    $bundleUri = [uri]($FeedBaseUri.AbsoluteUri.TrimEnd('/') + "/$bundleFileName")
    & "$PSScriptRoot/generate-appinstaller.ps1" -AppInstallerUri $appInstallerUri -BundleUri $bundleUri `
        -Version $Version -IdentityName $IdentityName -Publisher $Publisher -OutputRoot $releaseRoot | Out-Null
}

$publishedAt = [DateTimeOffset]::FromUnixTimeSeconds([long](git show -s --format=%ct HEAD)).ToUniversalTime()
$assets = foreach ($architecture in $Architectures | Sort-Object) {
    $fileName = "CodexUsageMonitor-$Version-win-$architecture-update.zip"
    $path = Join-Path $releaseRoot $fileName
    [ordered]@{
        architecture = $architecture
        url = if ($isPublicUnsigned) { "https://github.com/$Repository/releases/download/v$Version/$fileName" } elseif ($FeedBaseUri) { $FeedBaseUri.AbsoluteUri.TrimEnd('/') + '/' + $fileName } else { "https://invalid.example/$fileName" }
        fileName = $fileName
        sizeBytes = (Get-Item -LiteralPath $path).Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        publisherThumbprints = @()
    }
}
$manifest = [ordered]@{
    schemaVersion = 1
    channel = 'stable'
    version = $Version
    publishedAtUtc = $publishedAt
    minimumOsBuild = 19041
    releaseNotesUrl = if ($isPublicUnsigned) { "https://github.com/$Repository/releases/tag/v$Version" } elseif ($ReleaseNotesUri) { $ReleaseNotesUri.AbsoluteUri } else { 'https://invalid.example/release-notes' }
    assets = @($assets)
    signature = ''
}
[IO.File]::WriteAllText(
    (Join-Path $releaseRoot 'update-manifest.json'),
    ($manifest | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

if ($isPublicUnsigned) {
    & dotnet run --project tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --configuration Release `
        -p:UpdatePublicKeyBase64=$UpdateTrustAnchor -- `
        validate-keypair --trust-anchor $UpdateTrustAnchor --private-key-env UPDATE_PRIVATE_KEY_BASE64
    if ($LASTEXITCODE -ne 0) { throw 'Public update signing key does not match the configured trust anchor.' }
    & dotnet run --project tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --configuration Release `
        -p:UpdatePublicKeyBase64=$UpdateTrustAnchor -- `
        sign --manifest (Join-Path $releaseRoot 'update-manifest.json') --trust-anchor $UpdateTrustAnchor --private-key-env UPDATE_PRIVATE_KEY_BASE64
    if ($LASTEXITCODE -ne 0) { throw 'Public update manifest signing failed.' }
    & dotnet run --project tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --configuration Release `
        -p:UpdatePublicKeyBase64=$UpdateTrustAnchor -- `
        verify --manifest (Join-Path $releaseRoot 'update-manifest.json') --trust-anchor $UpdateTrustAnchor
    if ($LASTEXITCODE -ne 0) { throw 'Public update manifest verification failed.' }
}

dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Local tool restore failed.' }
& dotnet CycloneDX CodexUsageMonitor.slnx --output $releaseRoot --filename bom.json --output-format Json --exclude-test-projects --set-name CodexUsageMonitor --set-version $Version --no-serial-number
if ($LASTEXITCODE -ne 0) { throw 'SBOM generation failed.' }
if ($isPublicUnsigned) {
    $sbomPath = Join-Path $releaseRoot 'bom.json'
    $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
    if ($sbom.bomFormat -cne 'CycloneDX' -or [string]::IsNullOrWhiteSpace([string]$sbom.specVersion)) {
        throw 'Generated SBOM does not match the required CycloneDX schema identity.'
    }
    if ($sbom.PSObject.Properties.Name -contains 'serialNumber') {
        throw 'CycloneDX generated an unexpected nondeterministic serial number.'
    }
    $commit = (& git rev-parse HEAD).Trim()
    $sbom | Add-Member -NotePropertyName serialNumber -NotePropertyValue (
        Get-ReleaseSbomSerialNumber -Repository $Repository -Version $Version -Commit $commit)
    $sbomJson = (($sbom | ConvertTo-Json -Depth 100) -replace "`r`n", "`n") + "`n"
    [IO.File]::WriteAllText($sbomPath, $sbomJson, [Text.UTF8Encoding]::new($false))
}

Copy-Item -LiteralPath LICENSE -Destination (Join-Path $releaseRoot 'LICENSE.txt')
Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination (Join-Path $releaseRoot 'THIRD-PARTY-NOTICES.md')
$sourceArchive = Join-Path $releaseRoot "CodexUsageMonitor-$Version-source.zip"
& git archive --format=zip --output=$sourceArchive --prefix="CodexUsageMonitor-$Version/" HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source archive generation failed.' }
$metadata = if ($isPublicUnsigned) {
    [ordered]@{
        product = 'Codex Usage Monitor for Windows'; version = $Version; commit = (git rev-parse HEAD).Trim()
        sdk = (& dotnet --version).Trim(); configuration = $Configuration; architectures = @($Architectures | Sort-Object)
        releaseMode = 'public-unsigned'; windowsAuthenticode = $false; attestationProvider = 'GitHub Actions'
        generatedAtUtc = $publishedAt.ToString('O')
    }
} else {
    [ordered]@{
        product = 'Codex Usage Monitor for Windows'; version = $Version; commit = (git rev-parse HEAD).Trim()
        sdk = (& dotnet --version).Trim(); configuration = $Configuration; architectures = @($Architectures)
        production = $false; generatedAtUtc = $publishedAt.ToString('O')
    }
}
$metadata | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $releaseRoot 'BUILD-METADATA.json') -Encoding utf8NoBOM

$unsignedMarkerPath = Join-Path $releaseRoot $(if ($isPublicUnsigned) { 'UNSIGNED-WINDOWS-RELEASE.txt' } else { 'UNSIGNED-RELEASE-CANDIDATE.txt' })
$unsignedMarkerText = if ($isPublicUnsigned) {
    "UNSIGNED WINDOWS RELEASE`nThese Windows executables are not Authenticode-signed and Windows can show an unverified or unknown publisher.`nVerify downloads against SHA256SUMS.txt and the GitHub artifact attestations from saroo98/codex-usage-monitor.`nDo not disable Windows security controls.`n"
} else {
    "UNSIGNED VALIDATION ARTIFACTS`nThese files are not production-signed. Do not publish or distribute them as a release.`n"
}
$unsignedMarkerBytes = [Text.UTF8Encoding]::new($false).GetBytes($unsignedMarkerText)
[IO.File]::WriteAllBytes($unsignedMarkerPath, $unsignedMarkerBytes)

$inventoryTargets = Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object Name -NotIn @('SHA256SUMS.txt','verification-report.json') | Sort-Object Name
$lines = foreach ($file in $inventoryTargets) { "$(($file | Get-FileHash -Algorithm SHA256).Hash.ToLowerInvariant()) *$($file.Name)" }
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))

& "$PSScriptRoot/verify-release.ps1" -ReleaseRoot $releaseRoot -Version $Version -Architectures $Architectures `
    -ReleaseMode $ReleaseMode -ExpectedRepository $Repository -UpdateTrustAnchor $UpdateTrustAnchor | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Independent release verification failed.' }
Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name | Select-Object -ExpandProperty FullName
