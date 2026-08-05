[CmdletBinding()]
param(
    [string]$OutputRoot = 'artifacts/performance/latest',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateRange(5, 120)]
    [int]$WarmupSeconds = 10,
    [ValidateRange(10, 3600)]
    [int]$SampleSeconds = 30,
    [ValidateRange(0, 1440)]
    [int]$SoakMinutes = 0,
    [ValidateRange(3, 15)]
    [int]$ColdStartIterations = 3,
    [ValidateRange(5, 100)]
    [int]$ActivationIterations = 20,
    [string]$ValidationUpdatePublicKeyBase64 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
    [switch]$Enforce
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedOutput = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $resolvedOutput.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Performance output must stay under the repository artifacts directory.'
}

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$publishRoot = Join-Path $resolvedOutput 'app'
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$existing = Get-Process -Name 'CodexUsageMonitor' -ErrorAction SilentlyContinue
if ($existing) {
    throw 'Close any running Codex Usage Monitor instance before collecting isolated performance evidence.'
}

& dotnet publish (Join-Path $repositoryRoot 'src/CodexUsageMonitor.App/CodexUsageMonitor.App.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    -p:RestoreLockedMode=true `
    -p:UpdatePublicKeyBase64=$ValidationUpdatePublicKeyBase64 `
    --output $publishRoot
if ($LASTEXITCODE -ne 0) { throw "Performance publish failed with exit code $LASTEXITCODE." }

New-Item -ItemType File -Path (Join-Path $publishRoot 'portable.mode') -Force | Out-Null
$dataRoot = Join-Path $publishRoot 'data'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
$settings = @{
    schemaVersion = 2
    general = @{
        closeToTray = $false
        showOnboardingOnNextLaunch = $false
        privacyMode = $true
    }
    notifications = @{ enabled = $false }
    history = @{ enabled = $false }
    updates = @{ automaticChecks = $false; automaticDownload = $false; installOnExit = $false }
    profiles = @(
        @{
            id = '11111111-1111-1111-1111-111111111111'
            name = 'Performance fixture'
            codexHome = $null
            enabled = $false
            monitorInBackground = $false
        }
    )
}
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dataRoot 'settings.json') -Encoding utf8NoBOM

$executable = [IO.Path]::GetFullPath((Join-Path $publishRoot 'CodexUsageMonitor.exe'))
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published executable not found: $executable" }

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) { return 0 }
    $ordered = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Min($ordered.Count - 1, [Math]::Ceiling($Percentile * $ordered.Count) - 1))
    return [Math]::Round($ordered[$index], 3)
}

function Get-DescendantProcessIds([int]$ParentId) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name)
    $pending = [Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($ParentId)
    $result = [Collections.Generic.List[object]]::new()
    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        foreach ($child in $all | Where-Object ParentProcessId -eq $current) {
            $result.Add($child)
            $pending.Enqueue([int]$child.ProcessId)
        }
    }
    return @($result)
}

$process = $null
try {
    $coldStartSamples = [Collections.Generic.List[double]]::new()
    for ($iteration = 1; $iteration -le $ColdStartIterations; $iteration++) {
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $executable -WorkingDirectory $publishRoot -PassThru
        $readyDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 25
            $process.Refresh()
            if ($process.HasExited) { throw "Application exited during startup with code $($process.ExitCode)." }
        } while ($process.MainWindowHandle -eq 0 -and [DateTimeOffset]::UtcNow -lt $readyDeadline)
        $stopwatch.Stop()
        if ($process.MainWindowHandle -eq 0) { throw 'The widget did not expose a useful native window within 10 seconds.' }
        $coldStartSamples.Add($stopwatch.Elapsed.TotalMilliseconds)
        if ($iteration -lt $ColdStartIterations) {
            $process.Kill($true)
            $process.WaitForExit(10000) | Out-Null
            $process.Dispose()
            $process = $null
            Start-Sleep -Milliseconds 500
        }
    }
    $coldStartMilliseconds = Get-Percentile $coldStartSamples.ToArray() 0.5

    Start-Sleep -Milliseconds 250
    $activationSamples = [Collections.Generic.List[double]]::new()
    for ($iteration = 1; $iteration -le $ActivationIterations; $iteration++) {
        $activationWatch = [Diagnostics.Stopwatch]::StartNew()
        $activation = Start-Process -FilePath $executable -WorkingDirectory $publishRoot -ArgumentList '--refresh' -PassThru
        if (-not $activation.WaitForExit(5000)) {
            $activation.Kill($true)
            throw 'A refresh activation did not complete within five seconds.'
        }
        $activationWatch.Stop()
        if ($activation.ExitCode -ne 0) { throw "A refresh activation failed with exit code $($activation.ExitCode)." }
        $activationSamples.Add($activationWatch.Elapsed.TotalMilliseconds)
        $activation.Dispose()
    }
    $activationP95Milliseconds = Get-Percentile $activationSamples.ToArray() 0.95

    Start-Sleep -Seconds $WarmupSeconds
    $cpuSamples = [Collections.Generic.List[double]]::new()
    $memorySamples = [Collections.Generic.List[double]]::new()
    $previousCpu = $process.TotalProcessorTime
    $previousAt = [DateTimeOffset]::UtcNow
    $sampleDeadline = [DateTimeOffset]::UtcNow.AddSeconds($SampleSeconds)
    while ([DateTimeOffset]::UtcNow -lt $sampleDeadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { throw 'Application exited during idle sampling.' }
        $now = [DateTimeOffset]::UtcNow
        $cpu = $process.TotalProcessorTime
        $elapsed = ($now - $previousAt).TotalMilliseconds
        $used = ($cpu - $previousCpu).TotalMilliseconds
        $normalized = if ($elapsed -le 0) { 0 } else { ($used / $elapsed / [Environment]::ProcessorCount) * 100 }
        $cpuSamples.Add([Math]::Max(0, $normalized))
        $memorySamples.Add($process.PrivateMemorySize64 / 1MB)
        $previousCpu = $cpu
        $previousAt = $now
    }

    $warmMemory = $memorySamples[$memorySamples.Count - 1]
    $soakResult = @{ status = 'not-run'; durationMinutes = 0; memoryGrowthPercent = $null }
    if ($SoakMinutes -gt 0) {
        $soakStartMemory = $warmMemory
        $soakDeadline = [DateTimeOffset]::UtcNow.AddMinutes($SoakMinutes)
        while ([DateTimeOffset]::UtcNow -lt $soakDeadline) {
            Start-Sleep -Seconds 60
            $process.Refresh()
            if ($process.HasExited) { throw 'Application exited during soak sampling.' }
        }
        $soakEndMemory = $process.PrivateMemorySize64 / 1MB
        $growth = if ($soakStartMemory -le 0) { 0 } else { (($soakEndMemory - $soakStartMemory) / $soakStartMemory) * 100 }
        $soakResult = @{
            status = 'measured'
            durationMinutes = $SoakMinutes
            startPrivateMiB = [Math]::Round($soakStartMemory, 3)
            endPrivateMiB = [Math]::Round($soakEndMemory, 3)
            memoryGrowthPercent = [Math]::Round($growth, 3)
            passed = $growth -le 10
        }
    }

    $childrenBeforeExit = @(Get-DescendantProcessIds $process.Id)
    $process.Kill($true)
    $process.WaitForExit(10000) | Out-Null
    Start-Sleep -Milliseconds 500
    $leakedChildren = @($childrenBeforeExit | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })

    $processor = Get-CimInstance Win32_Processor | Select-Object -First 1
    $computer = Get-CimInstance Win32_ComputerSystem
    $operatingSystem = Get-CimInstance Win32_OperatingSystem
    $report = [ordered]@{
        schemaVersion = 1
        commit = (git -C $repositoryRoot rev-parse HEAD).Trim()
        executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
        configuration = $Configuration
        architecture = 'x64'
        referenceMachine = [ordered]@{
            processor = $processor.Name.Trim()
            logicalProcessors = [Environment]::ProcessorCount
            memoryGiB = [Math]::Round($computer.TotalPhysicalMemory / 1GB, 1)
            windowsVersion = $operatingSystem.Version
            windowsBuild = $operatingSystem.BuildNumber
        }
        fixture = [ordered]@{
            portable = $true
            profileCount = 1
            enabledProfileCount = 0
            notifications = $false
            history = $false
            syntheticDataOnly = $true
        }
        thresholds = [ordered]@{
            coldStartMilliseconds = 2000
            idleCpuP95Percent = 1
            privateWorkingSetMiB = 150
            soakGrowthPercent = 10
            leakedChildProcesses = 0
        }
        measurements = [ordered]@{
            coldStartMilliseconds = $coldStartMilliseconds
            coldStartSamplesMilliseconds = @($coldStartSamples | ForEach-Object { [Math]::Round($_, 3) })
            externalRefreshActivationP95Milliseconds = $activationP95Milliseconds
            externalRefreshActivationMedianMilliseconds = Get-Percentile $activationSamples.ToArray() 0.5
            externalRefreshActivationSamplesMilliseconds = @($activationSamples | ForEach-Object { [Math]::Round($_, 3) })
            idleCpuP95Percent = Get-Percentile $cpuSamples.ToArray() 0.95
            idleCpuMedianPercent = Get-Percentile $cpuSamples.ToArray() 0.5
            privateWorkingSetMiB = [Math]::Round($warmMemory, 3)
            privateWorkingSetP95MiB = Get-Percentile $memorySamples.ToArray() 0.95
            sampleSeconds = $SampleSeconds
            sampleCount = $cpuSamples.Count
            childProcessesBeforeExit = @($childrenBeforeExit | ForEach-Object Name)
            leakedChildProcesses = @($leakedChildren | ForEach-Object Name)
            soak = $soakResult
        }
    }
    $report.measurements.passed = `
        $coldStartMilliseconds -le 2000 -and `
        $report.measurements.idleCpuP95Percent -le 1 -and `
        $warmMemory -le 150 -and `
        $leakedChildren.Count -eq 0 -and `
        ($SoakMinutes -eq 0 -or $soakResult.passed)

    $reportPath = Join-Path $resolvedOutput 'performance-report.json'
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    Write-Host "Performance report: $reportPath"
    Write-Host ($report.measurements | ConvertTo-Json -Depth 5)
    if ($Enforce -and -not $report.measurements.passed) { throw 'One or more enforced performance thresholds failed.' }
}
finally {
    if ($process -and -not $process.HasExited) {
        $resolvedExecutable = [IO.Path]::GetFullPath($process.MainModule.FileName)
        if (-not $resolvedExecutable.Equals($executable, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to terminate a process outside the isolated performance fixture.'
        }
        $process.Kill($true)
        $process.WaitForExit(10000) | Out-Null
    }
    if ($process) { $process.Dispose() }
}
