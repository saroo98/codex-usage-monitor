[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$UpdatePublicKeyBase64,
    [ValidateSet('x64','arm64')][string[]]$Architectures = @('x64','arm64'),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('Development','Production','PublicUnsigned')]
    [string]$UpdateBuildFlavor = $(if ($Configuration -eq 'Release') { 'Production' } else { 'Development' }),
    [string]$GoogleOAuthClientId,
    [string]$MicrosoftOAuthClientId,
    [string]$MicrosoftOAuthTenant = 'common',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot

$publishRoot = [IO.Path]::GetFullPath($OutputRoot, $RepositoryRoot)
$repositoryFullPath = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
if ($publishRoot.TrimEnd('\') -eq $repositoryFullPath -or
    $repositoryFullPath.StartsWith($publishRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be a dedicated directory below or outside the repository root.'
}

if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$treeNames = [System.Collections.Generic.List[string]]::new()
$versionRecords = [System.Collections.Generic.List[object]]::new()
foreach ($architecture in $Architectures) {
    $rid = "win-$architecture"
    foreach ($selfContained in @($true,$false)) {
        $flavor = if ($selfContained) { 'self-contained' } else { 'framework-dependent' }
        $treeName = "$rid/$flavor"
        $workingRoot = Join-Path $publishRoot ".$architecture-$flavor-work"
        $destination = Join-Path $publishRoot $treeName
        $arguments = @{
            RuntimeIdentifier = $rid; SelfContained = $selfContained; Version = $Version
            Configuration = $Configuration; NoRestore = [bool]$NoRestore
            UpdatePublicKeyBase64 = $UpdatePublicKeyBase64; OutputRoot = $workingRoot
            UpdateBuildFlavor = $UpdateBuildFlavor
            GoogleOAuthClientId = $GoogleOAuthClientId; MicrosoftOAuthClientId = $MicrosoftOAuthClientId
            MicrosoftOAuthTenant = $MicrosoftOAuthTenant
        }
        $portable = & "$PSScriptRoot/publish-portable.ps1" @arguments
        if ($LASTEXITCODE -ne 0) { throw "Release-tree publish failed for $treeName." }
        if (-not (Test-Path -LiteralPath $portable -PathType Container)) { throw "Published tree is missing: $treeName" }

        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Move-Item -LiteralPath $portable -Destination $destination
        Remove-Item -LiteralPath $workingRoot -Recurse -Force
        $treeNames.Add($treeName)

        $firstPartyFiles = @(Get-ChildItem -LiteralPath $destination -File -Recurse | Where-Object {
            $_.Extension -in @('.exe','.dll') -and $_.BaseName.StartsWith('CodexUsageMonitor', [StringComparison]::Ordinal)
        })
        if ($firstPartyFiles.Count -eq 0) { throw "Published tree contains no first-party binaries: $treeName" }
        foreach ($file in $firstPartyFiles) {
            $versionInfo = $file.VersionInfo
            $record = [pscustomobject]@{
                Tree = $treeName
                File = [IO.Path]::GetRelativePath($destination, $file.FullName)
                ProductName = $versionInfo.ProductName
                CompanyName = $versionInfo.CompanyName
                ProductVersion = $versionInfo.ProductVersion
                FileVersion = $versionInfo.FileVersion
            }
            foreach ($propertyName in @('ProductName','CompanyName','ProductVersion','FileVersion')) {
                if ([string]::IsNullOrWhiteSpace([string]$record.$propertyName)) {
                    throw "First-party binary metadata is missing $propertyName in $treeName/$($record.File)."
                }
            }
            $versionRecords.Add($record)
        }
    }
}

$productNames = @($versionRecords.ProductName | Sort-Object -Unique)
$companyNames = @($versionRecords.CompanyName | Sort-Object -Unique)
$productVersions = @($versionRecords.ProductVersion | Sort-Object -Unique)
$fileVersions = @($versionRecords.FileVersion | Sort-Object -Unique)
if ($productNames.Count -ne 1 -or $companyNames.Count -ne 1 -or $productVersions.Count -ne 1 -or $fileVersions.Count -ne 1) {
    $details = $versionRecords | Sort-Object Tree,File | Format-Table -AutoSize | Out-String
    throw "First-party binary metadata differs across release trees.`n$details"
}

$metadata = [ordered]@{
    version = $Version
    commit = (git rev-parse HEAD).Trim()
    productName = $productNames[0]
    companyName = $companyNames[0]
    productVersion = $productVersions[0]
    fileVersion = $fileVersions[0]
    sdkVersion = (& dotnet --version).Trim()
    trees = @($treeNames | Sort-Object)
}
[IO.File]::WriteAllText(
    (Join-Path $publishRoot 'PUBLISH-METADATA.json'),
    ($metadata | ConvertTo-Json -Depth 4),
    [Text.UTF8Encoding]::new($false))

Write-Output $publishRoot
