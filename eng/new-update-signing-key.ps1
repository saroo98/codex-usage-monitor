[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'new-update-signing-key.ps1 is supported only on Windows.'
}

function Get-NormalizedDirectoryPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($pathRoot)) {
        throw "Directory path has no resolvable root: $Path"
    }
    if ($fullPath.Equals($pathRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $pathRoot
    }
    return $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Resolve-PhysicalDirectoryPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Purpose,
        [System.Collections.Generic.HashSet[string]]$VisitedLinks
    )

    if ($null -eq $VisitedLinks) {
        $VisitedLinks = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    }

    $fullPath = Get-NormalizedDirectoryPath -Path $Path
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    try { $rootItem = Get-Item -LiteralPath $pathRoot -Force -ErrorAction Stop }
    catch { throw "$Purpose root could not be inspected: $pathRoot" }
    if ($rootItem -isnot [IO.DirectoryInfo]) {
        throw "$Purpose root is not a directory: $pathRoot"
    }

    $current = Get-NormalizedDirectoryPath -Path $rootItem.FullName
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        if (-not $VisitedLinks.Add($current)) {
            throw "$Purpose traverses a cyclic reparse point: $current"
        }
        try { $rootTarget = $rootItem.ResolveLinkTarget($true) }
        catch { throw "$Purpose reparse-point target could not be resolved: $current" }
        if ($null -eq $rootTarget -or -not $rootTarget.Exists -or $rootTarget -isnot [IO.DirectoryInfo]) {
            throw "$Purpose reparse-point target could not be resolved to an existing directory: $current"
        }
        $current = Resolve-PhysicalDirectoryPath -Path $rootTarget.FullName -Purpose $Purpose -VisitedLinks $VisitedLinks
    }

    $relativePath = $fullPath.Substring($pathRoot.Length)
    $segments = @($relativePath -split '[\\/]' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    for ($index = 0; $index -lt $segments.Count; $index++) {
        $candidate = Join-Path $current $segments[$index]
        $item = $null
        try { $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop }
        catch [System.Management.Automation.ItemNotFoundException] {
            $current = $candidate
            for ($remaining = $index + 1; $remaining -lt $segments.Count; $remaining++) {
                $current = Join-Path $current $segments[$remaining]
            }
            return Get-NormalizedDirectoryPath -Path $current
        }
        catch { throw "$Purpose path component could not be inspected: $candidate" }

        if ($item -isnot [IO.DirectoryInfo]) {
            throw "$Purpose path component is not a directory: $candidate"
        }
        $current = Get-NormalizedDirectoryPath -Path $item.FullName
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            if (-not $VisitedLinks.Add($current)) {
                throw "$Purpose traverses a cyclic reparse point: $current"
            }
            try { $target = $item.ResolveLinkTarget($true) }
            catch { throw "$Purpose reparse-point target could not be resolved: $current" }
            if ($null -eq $target -or -not $target.Exists -or $target -isnot [IO.DirectoryInfo]) {
                throw "$Purpose reparse-point target could not be resolved to an existing directory: $current"
            }
            $current = Resolve-PhysicalDirectoryPath -Path $target.FullName -Purpose $Purpose -VisitedLinks $VisitedLinks
        }
    }

    return Get-NormalizedDirectoryPath -Path $current
}

function Assert-OutputDirectoryOutsideRepository {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CandidateOutputDirectory
    )

    $physicalRepository = Resolve-PhysicalDirectoryPath -Path $RepositoryRoot -Purpose 'RepositoryRoot'
    $physicalOutput = Resolve-PhysicalDirectoryPath -Path $CandidateOutputDirectory -Purpose 'OutputDirectory'
    $repositoryPrefix = $physicalRepository + [IO.Path]::DirectorySeparatorChar
    if ($physicalOutput.Equals($physicalRepository, [StringComparison]::OrdinalIgnoreCase) -or
        $physicalOutput.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputDirectory must resolve outside the repository.'
    }
}

$repositoryRoot = Get-NormalizedDirectoryPath -Path (Split-Path -Parent $PSScriptRoot)
$resolvedOutputDirectory = Get-NormalizedDirectoryPath -Path $OutputDirectory
Assert-OutputDirectoryOutsideRepository -RepositoryRoot $repositoryRoot -CandidateOutputDirectory $resolvedOutputDirectory

$privateKeyPath = Join-Path $resolvedOutputDirectory 'codex-usage-monitor-update-ed25519.key'
$publicKeyPath = Join-Path $resolvedOutputDirectory 'codex-usage-monitor-update-ed25519-public.txt'
foreach ($outputPath in @($privateKeyPath, $publicKeyPath)) {
    if (Test-Path -LiteralPath $outputPath) {
        throw "Output already exists: $outputPath"
    }
}

if (-not (Test-Path -LiteralPath $resolvedOutputDirectory -PathType Container)) {
    $null = New-Item -ItemType Directory -Path $resolvedOutputDirectory
}

$releaseTool = Join-Path $repositoryRoot 'tools\CodexUsageMonitor.ReleaseTool\CodexUsageMonitor.ReleaseTool.csproj'
$keypairGenerated = $false
Push-Location $repositoryRoot
try {
    Assert-OutputDirectoryOutsideRepository -RepositoryRoot $repositoryRoot -CandidateOutputDirectory $resolvedOutputDirectory
    foreach ($outputPath in @($privateKeyPath, $publicKeyPath)) {
        if (Test-Path -LiteralPath $outputPath) {
            throw "Output already exists: $outputPath"
        }
    }
    & dotnet run --project $releaseTool --configuration Debug -- `
        generate-keypair --private-key-output $privateKeyPath --public-key-output $publicKeyPath
    if ($LASTEXITCODE -ne 0) {
        throw 'ReleaseTool generate-keypair failed.'
    }
    $keypairGenerated = $true

    $trustAnchor = (Get-Content -Raw -LiteralPath $publicKeyPath).Trim()
    & dotnet run --project $releaseTool --configuration Debug -- `
        validate-keypair --trust-anchor $trustAnchor --private-key $privateKeyPath
    if ($LASTEXITCODE -ne 0) {
        throw 'ReleaseTool validate-keypair failed.'
    }
}
catch {
    $failure = $_
    $cleanupFailed = $false
    if ($keypairGenerated) {
        foreach ($outputPath in @($publicKeyPath, $privateKeyPath)) {
            try { Remove-Item -LiteralPath $outputPath -Force -ErrorAction Stop }
            catch { $cleanupFailed = $true }
        }
    }
    if ($cleanupFailed) {
        throw 'Key generation failed and newly created output cleanup also failed.'
    }
    throw $failure
}
finally {
    Pop-Location
}

Write-Host "UPDATE_TRUST_ANCHOR=$trustAnchor"
Write-Host "Private key path: $privateKeyPath"
Write-Host 'Make encrypted offline backups before using this key.'
Write-Host 'Upload the base64 encoding of the private-key file through the approved secret-management path without displaying it.'
