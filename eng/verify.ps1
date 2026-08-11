[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64','arm64','All')]
    [string]$Architecture = 'All',
    [switch]$SkipUi,
    [switch]$SkipPackaging,
    # NON-PRODUCTION TEST KEY. Production release preflight must reject this value.
    [string]$ValidationUpdatePublicKeyBase64 = '11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=',
    [string]$TestResultsDirectory = 'artifacts/TestResults'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot

& "$PSScriptRoot/bootstrap.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap failed.' }

& python "$PSScriptRoot/verify-static.py"
if ($LASTEXITCODE -ne 0) { throw 'Static repository verification failed.' }

& dotnet format CodexUsageMonitor.slnx --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Formatting or analyzer verification failed.' }

$buildArguments = @(
    'build',
    'CodexUsageMonitor.slnx',
    '--configuration', $Configuration,
    '--no-restore',
    '--no-incremental',
    '--nologo'
)
if ($Configuration -eq 'Release') {
    if ([string]::IsNullOrWhiteSpace($ValidationUpdatePublicKeyBase64)) {
        throw 'Release validation requires an explicit non-production or production update public key.'
    }
    $buildArguments += '-p:UpdatePublicKeyBase64=' + $ValidationUpdatePublicKeyBase64
}
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw "$Configuration solution build failed." }

$architectures = if ($Architecture -eq 'All') { @('x64','arm64') } else { @($Architecture) }
foreach ($arch in $architectures) {
    $runtimeIdentifier = "win-$arch"
    $architectureArguments = @(
        '--configuration', $Configuration,
        '--runtime', $runtimeIdentifier,
        '--no-restore',
        '--no-incremental',
        '--nologo'
    )
    if ($Configuration -eq 'Release') {
        $architectureArguments += '-p:UpdatePublicKeyBase64=' + $ValidationUpdatePublicKeyBase64
    }
    foreach ($project in @(
        'src/CodexUsageMonitor.App/CodexUsageMonitor.App.csproj',
        'src/CodexUsageMonitor.UpdaterHost/CodexUsageMonitor.UpdaterHost.csproj'
    )) {
        & dotnet build $project @architectureArguments
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $project ($runtimeIdentifier)." }
    }
}

foreach ($suite in @('Unit','Contract','Integration','Migration','Performance')) {
    & "$PSScriptRoot/test.ps1" -Suite $suite -Configuration $Configuration -ResultsDirectory $TestResultsDirectory -NoBuild
}
if (-not $SkipPackaging) { & "$PSScriptRoot/test.ps1" -Suite Packaging -Configuration $Configuration -ResultsDirectory $TestResultsDirectory -NoBuild }
if (-not $SkipUi) { & "$PSScriptRoot/test.ps1" -Suite Ui -Configuration $Configuration -ResultsDirectory $TestResultsDirectory -NoBuild }

& dotnet list CodexUsageMonitor.slnx package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) { throw 'Dependency vulnerability scan failed.' }

& git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
Write-Host 'Repository verification completed successfully.' -ForegroundColor Green
