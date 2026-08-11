[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64','arm64','All')]
    [string]$Architecture = 'All',

    [switch]$SkipUi,

    [switch]$SkipPackaging,

    # NON-PRODUCTION TEST KEY. Production release preflight must reject this value.
    [string]$ValidationUpdatePublicKeyBase64 = '11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo='
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repositoryRoot

$testResultsBase = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/TestResults/evidence'))
$testResultsDirectory = [IO.Path]::GetFullPath((Join-Path $testResultsBase ([guid]::NewGuid().ToString('N'))))
$testResultsPrefix = $testResultsBase.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $testResultsDirectory.StartsWith($testResultsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $testResultsDirectory)) {
    throw 'Could not allocate a dedicated verification test-results directory.'
}
New-Item -ItemType Directory -Path $testResultsDirectory | Out-Null

& "$PSScriptRoot/verify.ps1" `
    -Configuration $Configuration `
    -Architecture $Architecture `
    -SkipUi:$SkipUi `
    -SkipPackaging:$SkipPackaging `
    -ValidationUpdatePublicKeyBase64 $ValidationUpdatePublicKeyBase64 `
    -TestResultsDirectory $testResultsDirectory

$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the verified commit.' }
$shortCommit = (git rev-parse --short=12 HEAD).Trim()
$status = @(git status --porcelain=v1 --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the verified working tree.' }

$testTotals = [ordered]@{
    total = 0
    executed = 0
    passed = 0
    failed = 0
    skipped = 0
}
$testReports = @()
$trxFiles = @(Get-ChildItem -LiteralPath $testResultsDirectory -Filter '*.trx' -File -ErrorAction SilentlyContinue)
foreach ($trxFile in $trxFiles) {
    [xml]$document = Get-Content $trxFile.FullName -Raw
    $counters = $document.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -eq $counters) { throw "TRX report has no counters: $($trxFile.FullName)" }
    $report = [ordered]@{
        file = [IO.Path]::GetRelativePath($repositoryRoot, $trxFile.FullName).Replace('\', '/')
        total = [int]$counters.total
        executed = [int]$counters.executed
        passed = [int]$counters.passed
        failed = [int]$counters.failed
        skipped = [int]$counters.notExecuted
    }
    foreach ($name in @('total','executed','passed','failed','skipped')) {
        $testTotals[$name] += $report[$name]
    }
    $testReports += $report
}

$expectedReportCount = 7
if ($SkipUi) { $expectedReportCount-- }
if ($SkipPackaging) { $expectedReportCount-- }
if ($testReports.Count -ne $expectedReportCount) {
    throw "Expected $expectedReportCount test reports but found $($testReports.Count)."
}
if ($testTotals.failed -ne 0 -or $testTotals.executed -ne $testTotals.passed) {
    throw 'Test evidence does not describe a fully passing run.'
}

$runtimeIdentifiers = if ($Architecture -eq 'All') {
    @('win-x64','win-arm64')
} else {
    @("win-$Architecture")
}
$artifactHashes = @()
foreach ($runtimeIdentifier in $runtimeIdentifiers) {
    $runtimeSegment = "$([IO.Path]::DirectorySeparatorChar)$runtimeIdentifier$([IO.Path]::DirectorySeparatorChar)"
    $candidateRoots = @(
        "src/CodexUsageMonitor.App/bin/$Configuration",
        "src/CodexUsageMonitor.UpdaterHost/bin/$Configuration"
    )
    foreach ($candidateRoot in $candidateRoots) {
        $files = @(Get-ChildItem $candidateRoot -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object {
                $_.FullName -match [Regex]::Escape($runtimeSegment) -and
                $_.Name -in @(
                    'CodexUsageMonitor.exe',
                    'CodexUsageMonitor.dll',
                    'CodexUsageMonitor.UpdaterHost.exe',
                    'CodexUsageMonitor.UpdaterHost.dll'
                )
            })
        foreach ($file in $files) {
            $artifactHashes += [ordered]@{
                path = [IO.Path]::GetRelativePath($repositoryRoot, $file.FullName).Replace('\', '/')
                bytes = $file.Length
                sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
}
if ($artifactHashes.Count -eq 0) { throw 'No architecture-specific build artifacts were found to hash.' }

$timestamp = [DateTimeOffset]::UtcNow
$evidenceDirectory = Join-Path $repositoryRoot (
    'artifacts/verification/{0}-{1}' -f $timestamp.ToString('yyyyMMddTHHmmssZ'), $shortCommit)
New-Item $evidenceDirectory -ItemType Directory -Force | Out-Null
$reportPath = Join-Path $evidenceDirectory 'verification.json'
$report = [ordered]@{
    schemaVersion = 1
    recordedAtUtc = $timestamp.ToString('O')
    commit = $commit
    workingTreeClean = $status.Count -eq 0
    workingTreeStatus = $status
    sdkVersion = (dotnet --version).Trim()
    configuration = $Configuration
    runtimeIdentifiers = $runtimeIdentifiers
    checks = @(
        'locked restore',
        'static repository verification',
        'format and analyzer verification',
        'solution build',
        'architecture-specific application and updater builds',
        'test suites',
        'dependency vulnerability scan',
        'git diff check'
    )
    testTotals = $testTotals
    testReports = $testReports
    artifactHashes = $artifactHashes | Sort-Object path
}
$report | ConvertTo-Json -Depth 8 | Set-Content $reportPath -Encoding utf8NoBOM
Write-Host "Verification evidence written to $reportPath" -ForegroundColor Green
Write-Output $reportPath
