[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')]
    [string[]]$RuntimeIdentifiers = @('win-x64','win-arm64'),
    [string]$Version,
    # NON-PRODUCTION TEST KEY. Production release preflight must reject this value.
    [string]$UpdatePublicKeyBase64 = '11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=',
    [string]$GoogleOAuthClientId,
    [string]$MicrosoftOAuthClientId,
    [string]$MicrosoftOAuthTenant = 'common',
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
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-ProductVersion -RepositoryRoot $RepositoryRoot }

$releaseDirectory = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $RepositoryRoot 'artifacts/release'
} else {
    [IO.Path]::GetFullPath($OutputRoot, $RepositoryRoot)
}
New-Item $releaseDirectory -ItemType Directory -Force | Out-Null
$resolvedPublishRoot = if ([string]::IsNullOrWhiteSpace($PublishRoot)) { $null } else {
    [IO.Path]::GetFullPath($PublishRoot, $RepositoryRoot)
}
$workDirectory = Join-Path $RepositoryRoot ('artifacts/package-portable/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
$packages = [System.Collections.Generic.List[string]]::new()
try {
    foreach ($rid in $RuntimeIdentifiers) {
        foreach ($selfContained in @($true,$false)) {
            $flavor = if ($selfContained) { 'self-contained' } else { 'framework-dependent' }
            $stage = Join-Path $workDirectory "$rid/$flavor"
            New-Item -ItemType Directory -Path (Split-Path -Parent $stage) -Force | Out-Null
            if ($resolvedPublishRoot) {
                $PublishRoot = Join-Path $resolvedPublishRoot "$rid/$flavor"
                if (-not (Test-Path -LiteralPath $PublishRoot -PathType Container)) {
                    throw "Published source tree is missing: $PublishRoot"
                }
                Copy-Item -LiteralPath $PublishRoot -Destination $stage -Recurse
            } else {
                $publishArguments = @{
                    RuntimeIdentifier = $rid; SelfContained = $selfContained; Version = $Version
                    Configuration = $Configuration; NoRestore = [bool]$NoRestore
                    UpdatePublicKeyBase64 = $UpdatePublicKeyBase64
                    UpdateBuildFlavor = $UpdateBuildFlavor
                    OutputRoot = (Join-Path $workDirectory ".publish-$rid-$flavor")
                    GoogleOAuthClientId = $GoogleOAuthClientId; MicrosoftOAuthClientId = $MicrosoftOAuthClientId
                    MicrosoftOAuthTenant = $MicrosoftOAuthTenant
                }
                $portable = & "$PSScriptRoot/publish-portable.ps1" @publishArguments
                if ($LASTEXITCODE -ne 0) { throw "Portable publish failed for $rid/$flavor." }
                Copy-Item -LiteralPath $portable -Destination $stage -Recurse
            }

            & python tools/generate_update_file_manifest.py --source $stage --version $Version | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Update file manifest generation failed for $rid/$flavor." }
            $packageManifest = Join-Path $stage 'update-files.json'
            if (-not (Test-Path -LiteralPath $packageManifest -PathType Leaf) -or (Get-Item -LiteralPath $packageManifest).Length -le 0) {
                throw "The generated update file manifest is missing or empty for $rid/$flavor."
            }

            if ($selfContained) {
                $updateName = "CodexUsageMonitor-$Version-$rid-update.zip"
                $updateOutput = Join-Path $releaseDirectory $updateName
                & python tools/deterministic_zip.py --source $stage --output $updateOutput --prefix ''
                if ($LASTEXITCODE -ne 0) { throw "Update payload archive creation failed for $updateName." }
                & python tools/verify_update_archive.py --archive $updateOutput --version $Version
                if ($LASTEXITCODE -ne 0) { throw "Update payload archive verification failed for $updateName." }
                $packages.Add($updateOutput)
            }

            foreach ($instructionName in @('INSTALL.txt','UNINSTALL.txt')) {
                $instructionSource = Join-Path $RepositoryRoot "packaging/portable/$instructionName"
                Copy-Item -LiteralPath $instructionSource -Destination (Join-Path $stage $instructionName) -Force
            }
            # Portable packages keep their settings, history, logs, and update state
            # beside the executable. Update payloads must not carry it because the
            # updater copies the existing marker and data directory transactionally.
            New-Item -ItemType File -Path (Join-Path $stage 'portable.mode') -Force | Out-Null

            $name = "CodexUsageMonitor-$Version-$rid-portable-$flavor.zip"
            $output = Join-Path $releaseDirectory $name
            & python tools/deterministic_zip.py --source $stage --output $output --prefix CodexUsageMonitor
            if ($LASTEXITCODE -ne 0) { throw "Portable archive creation failed for $name." }
            $packages.Add($output)
        }
    }
}
finally {
    if (Test-Path -LiteralPath $workDirectory) { Remove-Item -LiteralPath $workDirectory -Recurse -Force }
}

$checksumLines = foreach ($package in $packages | Sort-Object) {
    $hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$([IO.Path]::GetFileName($package))"
}
$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

foreach ($line in Get-Content $checksumPath) {
    if ($line -notmatch '^([0-9a-f]{64}) \*(.+)$') { throw "Invalid checksum line: $line" }
    $file = Join-Path $releaseDirectory $Matches[2]
    if (-not (Test-Path $file -PathType Leaf)) { throw "Checksum target is missing: $($Matches[2])" }
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Matches[1]) { throw "Checksum mismatch for $($Matches[2])" }
}

$packages
