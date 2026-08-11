[CmdletBinding()]
param(
    [string]$Version,
    # NON-PRODUCTION TEST KEY. Production release preflight must reject this value.
    [string]$UpdatePublicKeyBase64 = '11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=',
    [string]$GoogleOAuthClientId,
    [string]$MicrosoftOAuthClientId,
    [string]$MicrosoftOAuthTenant = 'common',
    [ValidateSet('x64','arm64')]
    [string[]]$Architectures = @('x64','arm64'),
    [string]$IdentityName = 'saroo98.CodexUsageMonitor',
    # Unsigned local-validation default only. Any trusted external package flow must supply its reviewed publisher.
    [string]$Publisher = 'CN=Codex Usage Monitor Development',
    [string]$PublisherDisplayName = 'Codex Usage Monitor Contributors',
    [string]$OutputRoot,
    [string]$PublishRoot,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('Development','Production','PublicUnsigned')]
    [string]$UpdateBuildFlavor = $(if ($Configuration -eq 'Release') { 'Production' } else { 'Development' }),
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
. "$PSScriptRoot/ProductVersion.ps1"
Import-Module "$PSScriptRoot/ReleaseAuthenticode.psm1" -Force
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-ProductVersion -RepositoryRoot $RepositoryRoot }
$makeAppx = Find-WindowsSdkTool 'makeappx.exe'

function Convert-PackageVersion([string]$SemanticVersion) {
    if ($SemanticVersion -notmatch '^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$') {
        throw "Version must be semantic major.minor.patch: $SemanticVersion"
    }
    foreach ($value in @($Matches[1],$Matches[2],$Matches[3])) {
        if ([int]$value -gt 65535) { throw 'MSIX version components must be <= 65535.' }
    }
    return "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
}

function Escape-Xml([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

$packageVersion = Convert-PackageVersion $Version
$releaseDirectory = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $RepositoryRoot 'artifacts/release'
} else {
    [IO.Path]::GetFullPath($OutputRoot, $RepositoryRoot)
}
$workDirectory = Join-Path $RepositoryRoot 'artifacts/msix'
$bundleInput = Join-Path $workDirectory 'bundle-input'
Remove-Item $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item $releaseDirectory,$workDirectory,$bundleInput -ItemType Directory -Force | Out-Null
$resolvedPublishRoot = if ([string]::IsNullOrWhiteSpace($PublishRoot)) { $null } else {
    [IO.Path]::GetFullPath($PublishRoot, $RepositoryRoot)
}
$builtPackages = [System.Collections.Generic.List[string]]::new()

foreach ($architecture in $Architectures) {
    $rid = "win-$architecture"
    $stage = Join-Path $workDirectory $architecture
    if ($resolvedPublishRoot) {
        $portable = Join-Path $resolvedPublishRoot "$rid/self-contained"
        if (-not (Test-Path -LiteralPath $portable -PathType Container)) {
            throw "Published MSIX source tree is missing: $portable"
        }
    } else {
        $arguments = @{
            RuntimeIdentifier = $rid; SelfContained = $true; Version = $Version
            Configuration = $Configuration; NoRestore = [bool]$NoRestore
            UpdatePublicKeyBase64 = $UpdatePublicKeyBase64
            UpdateBuildFlavor = $UpdateBuildFlavor
            GoogleOAuthClientId = $GoogleOAuthClientId; MicrosoftOAuthClientId = $MicrosoftOAuthClientId
            MicrosoftOAuthTenant = $MicrosoftOAuthTenant
        }
        $portable = & "$PSScriptRoot/publish-portable.ps1" @arguments
        if ($LASTEXITCODE -ne 0) { throw "MSIX publish failed for $architecture." }
    }

    Copy-Item -LiteralPath $portable -Destination $stage -Recurse
    New-Item (Join-Path $stage 'Assets') -ItemType Directory -Force | Out-Null
    Copy-Item src/CodexUsageMonitor.App/Resources/App-44.png (Join-Path $stage 'Assets/App-44.png')
    Copy-Item src/CodexUsageMonitor.App/Resources/App-50.png (Join-Path $stage 'Assets/App-50.png')
    Copy-Item src/CodexUsageMonitor.App/Resources/App-150.png (Join-Path $stage 'Assets/App-150.png')

    $manifest = Get-Content packaging/templates/msix/AppxManifest.xml -Raw
    $manifest = $manifest.Replace('@@IDENTITY_NAME@@', (Escape-Xml $IdentityName))
    $manifest = $manifest.Replace('@@PUBLISHER@@', (Escape-Xml $Publisher))
    $manifest = $manifest.Replace('@@PUBLISHER_DISPLAY_NAME@@', (Escape-Xml $PublisherDisplayName))
    $manifest = $manifest.Replace('@@PACKAGE_VERSION@@', $packageVersion)
    $manifest = $manifest.Replace('@@ARCHITECTURE@@', $architecture)
    if ($manifest.Contains('@@')) { throw 'MSIX manifest contains unresolved tokens.' }
    [IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

    $package = Join-Path $releaseDirectory "CodexUsageMonitor-$Version-$architecture.msix"
    & $makeAppx.FullName pack /d $stage /p $package /o /nv
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $architecture." }
    if ((Get-Item $package).Length -le 0) { throw "MSIX is empty: $package" }
    Copy-Item $package (Join-Path $bundleInput ([IO.Path]::GetFileName($package)))
    $builtPackages.Add($package)
}

if ($builtPackages.Count -gt 1) {
    $bundle = Join-Path $releaseDirectory "CodexUsageMonitor-$Version.msixbundle"
    & $makeAppx.FullName bundle /d $bundleInput /p $bundle /o
    if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed.' }
    $builtPackages.Add($bundle)
}

$builtPackages
