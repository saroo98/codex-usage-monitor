[CmdletBinding()]
param(
    [switch]$GenerateLockFiles,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
Import-Module "$PSScriptRoot/ReleaseAuthenticode.psm1" -Force

function Assert-Command {
    param([Parameter(Mandatory)][string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' is not available on PATH."
    }
}

Assert-Command git
Assert-Command dotnet

$dotnetInfo = dotnet --info 2>&1
if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed.`n$dotnetInfo" }
$sdkVersion = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
$selectedSdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $selectedSdkVersion -ne $sdkVersion) {
    throw "Expected repository-pinned .NET SDK $sdkVersion, but dotnet selected $selectedSdkVersion."
}
$installedSdks = @(dotnet --list-sdks)
if (-not ($installedSdks | Where-Object { $_ -match "^$([Regex]::Escape($sdkVersion))\s" })) {
    throw "The repository-pinned .NET SDK $sdkVersion is not installed. Installed SDKs:`n$($installedSdks -join [Environment]::NewLine)"
}

if ($IsWindows) {
    $windowsSdkVersion = Get-ReleaseWindowsSdkVersion
    if ($windowsSdkVersion -cne '10.0.26100.0') {
        throw "Expected reviewed Windows SDK 10.0.26100.0, but the resolver selected $windowsSdkVersion."
    }
    $makeAppx = Find-WindowsSdkTool -Name 'makeappx.exe'
    $makePri = Find-WindowsSdkTool -Name 'makepri.exe'
    $windowsSdkDirectories = @($makeAppx.DirectoryName, $makePri.DirectoryName | Sort-Object -Unique)
    if ($windowsSdkDirectories.Count -ne 1) { throw 'Reviewed Windows SDK tools did not resolve from one x64 directory.' }
    $env:PATH = $windowsSdkDirectories[0] + [IO.Path]::PathSeparator + $env:PATH
} else {
    Write-Warning 'This repository targets Windows. Non-Windows hosts can run portable core tests and static validation only.'
}

$projectPaths = Select-String -Path CodexUsageMonitor.slnx -Pattern 'Project Path="([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value }
$missingProjects = @($projectPaths | Where-Object { -not (Test-Path $_ -PathType Leaf) })
if ($missingProjects.Count -gt 0) {
    throw "Solution references missing projects:`n$($missingProjects -join [Environment]::NewLine)"
}

if (-not $SkipRestore) {
    $restoreArguments = @('restore', 'CodexUsageMonitor.slnx')
    if ($GenerateLockFiles) {
        $restoreArguments += '--force-evaluate'
    } else {
        $lockFiles = Get-ChildItem -Path src,tests -Filter packages.lock.json -Recurse -ErrorAction SilentlyContinue
        if ($lockFiles.Count -eq $projectPaths.Count) {
            $restoreArguments += '--locked-mode'
        } else {
            Write-Warning 'Package lock files are incomplete. Restore will generate or update them; review and commit the resulting lock files before a release build.'
        }
    }

    & dotnet @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
}

Write-Host "Bootstrap complete with .NET SDK $sdkVersion." -ForegroundColor Green
