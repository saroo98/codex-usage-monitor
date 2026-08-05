[CmdletBinding()]
param(
    [string]$Version,
    [string]$UpdatePublicKeyBase64 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
    [ValidateSet('x64','arm64')]
    [string[]]$Architectures = @('x64','arm64'),
    [string]$IdentityName = 'saroo98.CodexUsageMonitor',
    [string]$Publisher = 'CN=Codex Usage Monitor Development',
    [string]$PublisherDisplayName = 'Codex Usage Monitor Contributors',
    [string]$CertificatePath,
    [Security.SecureString]$CertificatePassword,
    [string]$TimestampUrl,
    [string]$OutputRoot,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
. "$PSScriptRoot/ProductVersion.ps1"
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-ProductVersion -RepositoryRoot $RepositoryRoot }

function Find-WindowsSdkTool([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $candidate = Get-ChildItem $kitsRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64/$Name" } |
        Where-Object { Test-Path $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $candidate) { throw "$Name is required for MSIX packaging." }
    return $candidate
}
$makeAppx = Find-WindowsSdkTool 'makeappx.exe'
$signTool = if ($CertificatePath) { Find-WindowsSdkTool 'signtool.exe' } else { $null }

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

function Sign-Package([string]$Path) {
    if (-not $CertificatePath) { return }
    if (-not (Test-Path $CertificatePath -PathType Leaf)) { throw "Signing certificate not found: $CertificatePath" }
    $plainPassword = if ($CertificatePassword) {
        [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword))
    } else { $null }
    try {
        $arguments = @('sign','/fd','SHA256','/f',$CertificatePath)
        if ($plainPassword) { $arguments += @('/p',$plainPassword) }
        if ($TimestampUrl) { $arguments += @('/tr',$TimestampUrl,'/td','SHA256') }
        $arguments += $Path
        & $signTool @arguments
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $Path" }
        & $signTool verify /pa /all $Path
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $Path" }
    }
    finally {
        if ($plainPassword) { $plainPassword = $null }
    }
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
$builtPackages = [System.Collections.Generic.List[string]]::new()

foreach ($architecture in $Architectures) {
    $rid = "win-$architecture"
    $arguments = @{
        RuntimeIdentifier = $rid; SelfContained = $true; Version = $Version
        Configuration = $Configuration; NoRestore = [bool]$NoRestore; UpdatePublicKeyBase64 = $UpdatePublicKeyBase64
    }
    $portable = & "$PSScriptRoot/publish-portable.ps1" @arguments
    if ($LASTEXITCODE -ne 0) { throw "MSIX publish failed for $architecture." }

    $stage = Join-Path $workDirectory $architecture
    Copy-Item $portable $stage -Recurse
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
    & $makeAppx pack /d $stage /p $package /o /nv
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $architecture." }
    if ((Get-Item $package).Length -le 0) { throw "MSIX is empty: $package" }
    Sign-Package $package
    Copy-Item $package (Join-Path $bundleInput ([IO.Path]::GetFileName($package)))
    $builtPackages.Add($package)
}

if ($builtPackages.Count -gt 1) {
    $bundle = Join-Path $releaseDirectory "CodexUsageMonitor-$Version.msixbundle"
    & $makeAppx bundle /d $bundleInput /p $bundle /o
    if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed.' }
    Sign-Package $bundle
    $builtPackages.Add($bundle)
}

$builtPackages
