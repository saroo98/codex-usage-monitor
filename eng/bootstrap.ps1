[CmdletBinding()]
param(
    [switch]$GenerateLockFiles,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot

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
$requiredSdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
$installedSdks = @(dotnet --list-sdks)
if (-not ($installedSdks | Where-Object { $_ -match "^$([Regex]::Escape($requiredSdk))\s" })) {
    throw "The repository-pinned .NET SDK $requiredSdk is not installed. Installed SDKs:`n$($installedSdks -join [Environment]::NewLine)"
}

if ($IsWindows) {
    $windowsSdkTools = @('makeappx.exe', 'makepri.exe')
    foreach ($tool in $windowsSdkTools) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            $kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
            $resolved = Get-ChildItem $kitsBin -Filter $tool -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -match '[\\/]x64$' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($resolved) {
                $env:PATH = $resolved.DirectoryName + [IO.Path]::PathSeparator + $env:PATH
            } else {
                Write-Warning "Windows SDK tool '$tool' is unavailable. Build and tests may run, but MSIX packaging will not."
            }
        }
    }
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

Write-Host "Bootstrap complete with .NET SDK $requiredSdk." -ForegroundColor Green
