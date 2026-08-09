[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64','win-arm64')]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory)]
    [bool]$SelfContained,

    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',

    [string]$Version,

    [string]$UpdatePublicKeyBase64 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',

    [string]$GoogleOAuthClientId,

    [string]$MicrosoftOAuthClientId,

    [string]$MicrosoftOAuthTenant = 'common',

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
. "$PSScriptRoot/ProductVersion.ps1"
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-ProductVersion -RepositoryRoot $RepositoryRoot }

$flavor = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
$outputRoot = Join-Path $RepositoryRoot "artifacts/publish/$RuntimeIdentifier/$flavor"
$appOutput = Join-Path $outputRoot 'app'
$updaterOutput = Join-Path $outputRoot 'updater'
$mergedOutput = Join-Path $outputRoot 'portable'
$buildArtifactsRoot = Join-Path $outputRoot 'build'
Remove-Item $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item $appOutput,$updaterOutput,$mergedOutput -ItemType Directory -Force | Out-Null

$common = @(
    '--configuration', $Configuration,
    '--runtime', $RuntimeIdentifier,
    '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
    '--artifacts-path', $buildArtifactsRoot,
    ('-p:Version=' + $Version),
    ('-p:VersionPrefix=' + $Version),
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:PublishReadyToRun=false',
    '-p:PublishTrimmed=false',
    '-p:ReleasePackagingRestore=true',
    ('-p:UpdatePublicKeyBase64=' + $UpdatePublicKeyBase64)
)
if ($NoRestore) { $common += '--no-restore' }
if (-not [string]::IsNullOrWhiteSpace($GoogleOAuthClientId)) { $common += ('-p:GoogleOAuthClientId=' + $GoogleOAuthClientId.Trim()) }
if (-not [string]::IsNullOrWhiteSpace($MicrosoftOAuthClientId)) { $common += ('-p:MicrosoftOAuthClientId=' + $MicrosoftOAuthClientId.Trim()) }
if (-not [string]::IsNullOrWhiteSpace($MicrosoftOAuthTenant)) { $common += ('-p:MicrosoftOAuthTenant=' + $MicrosoftOAuthTenant.Trim()) }

& dotnet publish src/CodexUsageMonitor.App/CodexUsageMonitor.App.csproj @common '--output' $appOutput '-p:PublishSingleFile=false' | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Application publish failed for $RuntimeIdentifier/$flavor." }

& dotnet publish src/CodexUsageMonitor.UpdaterHost/CodexUsageMonitor.UpdaterHost.csproj @common '--output' $updaterOutput '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Updater publish failed for $RuntimeIdentifier/$flavor." }

function Copy-VerifiedTree {
    param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Destination)
    Get-ChildItem $Source -File -Recurse | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Source, $_.FullName)
        $target = Join-Path $Destination $relative
        New-Item (Split-Path -Parent $target) -ItemType Directory -Force | Out-Null
        if (Test-Path $target -PathType Leaf) {
            $existing = (Get-FileHash $target -Algorithm SHA256).Hash
            $incoming = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            if ($existing -ne $incoming) { throw "Publish output collision has different bytes: $relative" }
            return
        }
        Copy-Item $_.FullName $target
    }
}

Copy-VerifiedTree $appOutput $mergedOutput
Copy-VerifiedTree $updaterOutput $mergedOutput
Copy-Item LICENSE (Join-Path $mergedOutput 'LICENSE.txt')
if (Test-Path THIRD-PARTY-NOTICES.md) { Copy-Item THIRD-PARTY-NOTICES.md (Join-Path $mergedOutput 'THIRD-PARTY-NOTICES.md') }
if (Test-Path README.md) { Copy-Item README.md (Join-Path $mergedOutput 'README.md') }

$buildInfo = [ordered]@{
    product = 'Codex Usage Monitor for Windows'
    version = $Version
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $SelfContained
    commit = (git rev-parse HEAD).Trim()
    sourceDateEpoch = if ($env:SOURCE_DATE_EPOCH) { $env:SOURCE_DATE_EPOCH } else { (git show -s --format=%ct HEAD).Trim() }
}
$buildInfo | ConvertTo-Json | Set-Content (Join-Path $mergedOutput 'BUILD-INFO.json') -Encoding utf8NoBOM

$primary = Join-Path $mergedOutput 'CodexUsageMonitor.exe'
$updater = Join-Path $mergedOutput 'CodexUsageMonitor.UpdaterHost.exe'
if (-not (Test-Path $primary -PathType Leaf)) { throw 'Primary executable was not published.' }
if (-not (Test-Path $updater -PathType Leaf)) { throw 'Updater executable was not published.' }
if ((Get-Item $primary).Length -le 0 -or (Get-Item $updater).Length -le 0) { throw 'Published executable is empty.' }

& python tools/generate_update_file_manifest.py --source $mergedOutput --version $Version | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Update file manifest generation failed for $RuntimeIdentifier/$flavor." }
$packageManifest = Join-Path $mergedOutput 'update-files.json'
if (-not (Test-Path $packageManifest -PathType Leaf) -or (Get-Item $packageManifest).Length -le 0) {
    throw 'The generated update file manifest is missing or empty.'
}

Write-Output $mergedOutput
