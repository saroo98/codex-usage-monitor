[CmdletBinding()]
param(
    [ValidateSet('All','Unit','Contract','Integration','Migration','Packaging','Performance','Ui')]
    [string]$Suite = 'All',
    [string]$Filter,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64','arm64','AnyCPU')]
    [string]$Architecture = 'AnyCPU',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot

$projects = [ordered]@{
    Unit = 'tests/CodexUsageMonitor.UnitTests/CodexUsageMonitor.UnitTests.csproj'
    Contract = 'tests/CodexUsageMonitor.ContractTests/CodexUsageMonitor.ContractTests.csproj'
    Integration = 'tests/CodexUsageMonitor.IntegrationTests/CodexUsageMonitor.IntegrationTests.csproj'
    Migration = 'tests/CodexUsageMonitor.MigrationTests/CodexUsageMonitor.MigrationTests.csproj'
    Packaging = 'tests/CodexUsageMonitor.PackagingTests/CodexUsageMonitor.PackagingTests.csproj'
    Performance = 'tests/CodexUsageMonitor.PerformanceTests/CodexUsageMonitor.PerformanceTests.csproj'
    Ui = 'tests/CodexUsageMonitor.UiTests/CodexUsageMonitor.UiTests.csproj'
}

$selected = if ($Suite -eq 'All') { @($projects.GetEnumerator()) } else { @([System.Collections.DictionaryEntry]::new($Suite, $projects[$Suite])) }
foreach ($entry in $selected) {
    if (-not (Test-Path $entry.Value -PathType Leaf)) { throw "Missing test project: $($entry.Value)" }
    if (($entry.Key -in @('Ui','Performance','Packaging','Migration')) -and -not $IsWindows) {
        Write-Warning "Skipping $($entry.Key) tests on a non-Windows host."
        continue
    }

    $arguments = @(
        'test',
        '--project', $entry.Value,
        '--configuration', $Configuration,
        '--report-trx',
        '--report-trx-filename', "$($entry.Key).trx",
        '--results-directory', 'artifacts/TestResults',
        '--no-ansi',
        '--no-progress'
    )
    if ($Architecture -ne 'AnyCPU') { $arguments += @('--arch', $Architecture) }
    if ($NoBuild) { $arguments += '--no-build' }
    if ($Filter) { $arguments += @('--filter', $Filter) }

    Write-Host "Running $($entry.Key) tests..." -ForegroundColor Cyan
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$($entry.Key) tests failed." }
}
