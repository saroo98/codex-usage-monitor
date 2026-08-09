[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')]
    [string[]]$RuntimeIdentifiers = @('win-x64','win-arm64'),
    [string]$Version,
    [string]$UpdatePublicKeyBase64 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
    [string]$GoogleOAuthClientId,
    [string]$MicrosoftOAuthClientId,
    [string]$MicrosoftOAuthTenant = 'common',
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

$releaseDirectory = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $RepositoryRoot 'artifacts/release'
} else {
    [IO.Path]::GetFullPath($OutputRoot, $RepositoryRoot)
}
New-Item $releaseDirectory -ItemType Directory -Force | Out-Null
$packages = [System.Collections.Generic.List[string]]::new()
foreach ($rid in $RuntimeIdentifiers) {
    foreach ($selfContained in @($true,$false)) {
        $flavor = if ($selfContained) { 'self-contained' } else { 'framework-dependent' }
        $publishArguments = @{
            RuntimeIdentifier = $rid; SelfContained = $selfContained; Version = $Version
            Configuration = $Configuration; NoRestore = [bool]$NoRestore; UpdatePublicKeyBase64 = $UpdatePublicKeyBase64
            GoogleOAuthClientId = $GoogleOAuthClientId; MicrosoftOAuthClientId = $MicrosoftOAuthClientId
            MicrosoftOAuthTenant = $MicrosoftOAuthTenant
        }
        $portable = & "$PSScriptRoot/publish-portable.ps1" @publishArguments
        if ($LASTEXITCODE -ne 0) { throw "Portable publish failed for $rid/$flavor." }

        foreach ($instructionName in @('INSTALL.txt','UNINSTALL.txt')) {
            $instructionSource = Join-Path $RepositoryRoot "packaging/portable/$instructionName"
            Copy-Item -LiteralPath $instructionSource -Destination (Join-Path $portable $instructionName) -Force
        }

        # Portable packages keep their settings, history, logs, and update state
        # beside the executable. Add the marker only to the user-facing ZIP.
        # Update payloads must not carry it because the updater copies the
        # existing marker and data directory transactionally.
        $portableMarker = Join-Path $portable 'portable.mode'
        if (Test-Path -LiteralPath $portableMarker) { Remove-Item -LiteralPath $portableMarker -Force }
        New-Item -ItemType File -Path $portableMarker -Force | Out-Null

        if ($selfContained) {
            $updateName = "CodexUsageMonitor-$Version-$rid-update.zip"
            $updateOutput = Join-Path $releaseDirectory $updateName
            Remove-Item -LiteralPath $portableMarker -Force
            foreach ($instructionName in @('INSTALL.txt','UNINSTALL.txt')) {
                $instructionPath = Join-Path $portable $instructionName
                if (Test-Path -LiteralPath $instructionPath) { Remove-Item -LiteralPath $instructionPath -Force }
            }
            & python tools/deterministic_zip.py --source $portable --output $updateOutput --prefix ''
            if ($LASTEXITCODE -ne 0) { throw "Update payload archive creation failed for $updateName." }
            & python tools/verify_update_archive.py --archive $updateOutput --version $Version
            if ($LASTEXITCODE -ne 0) { throw "Update payload archive verification failed for $updateName." }
            $packages.Add($updateOutput)
            foreach ($instructionName in @('INSTALL.txt','UNINSTALL.txt')) {
                $instructionSource = Join-Path $RepositoryRoot "packaging/portable/$instructionName"
                Copy-Item -LiteralPath $instructionSource -Destination (Join-Path $portable $instructionName) -Force
            }
            New-Item -ItemType File -Path $portableMarker -Force | Out-Null
        }

        $name = "CodexUsageMonitor-$Version-$rid-portable-$flavor.zip"
        $output = Join-Path $releaseDirectory $name
        & python tools/deterministic_zip.py --source $portable --output $output --prefix CodexUsageMonitor
        if ($LASTEXITCODE -ne 0) { throw "Portable archive creation failed for $name." }
        $packages.Add($output)
    }
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
