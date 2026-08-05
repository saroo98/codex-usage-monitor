[CmdletBinding()]
param(
    [switch]$InstallCodexIfMissing,
    [switch]$UpdateCodex,
    [switch]$SkipAlertTest,
    [switch]$DoNotStart,
    [switch]$UseDeviceCode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($env:OS -ne "Windows_NT") {
    throw "This installer supports Windows 10 and Windows 11 only."
}

$SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\CodexUsageNotifier"
$DataDir = Join-Path $env:LOCALAPPDATA "CodexUsageNotifier"
$ConfigPath = Join-Path $DataDir "config.json"
$HeartbeatPath = Join-Path $DataDir "heartbeat.json"
$UiHeartbeatPath = Join-Path $DataDir "ui-heartbeat.json"
$MainTaskName = "Codex Usage Notifier"
$UiTaskName = "Codex Usage Notifier UI"
$WatchdogTaskName = "Codex Usage Notifier Watchdog"
$PriorTaskStates = @{}

function Resolve-BasePython {
    $py = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($py) {
        & $py.Source -3 -c "import sys; raise SystemExit(0 if sys.version_info >= (3,10) else 1)"
        if ($LASTEXITCODE -eq 0) {
            return @{ Executable = $py.Source; Arguments = @("-3") }
        }
    }
    $python = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($python) {
        & $python.Source -c "import sys; raise SystemExit(0 if sys.version_info >= (3,10) else 1)"
        if ($LASTEXITCODE -eq 0) {
            return @{ Executable = $python.Source; Arguments = @() }
        }
    }
    throw "Python 3.10 or newer was not found. Install Python from python.org, then rerun install.ps1."
}

function Resolve-CodexPath {
    foreach ($name in @("codex.exe", "codex.cmd", "codex.ps1", "codex")) {
        $command = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command -and $command.Source) { return $command.Source }
    }
    $candidates = @(
        (Join-Path $HOME ".local\bin\codex.exe"),
        (Join-Path $HOME ".codex\bin\codex.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\OpenAI\Codex\bin\codex.exe"),
        (Join-Path $env:APPDATA "npm\codex.cmd"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\codex.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Install-OfficialCodex {
    Write-Host "Installing the current official Codex CLI in a clean Windows PowerShell process..."
    $cleanPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (-not (Test-Path -LiteralPath $cleanPowerShell -PathType Leaf)) {
        throw "Windows PowerShell was not found at $cleanPowerShell."
    }
    $command = '$env:CODEX_NON_INTERACTIVE=''1''; irm ''https://chatgpt.com/codex/install.ps1'' | iex'
    & $cleanPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $command
    if ($LASTEXITCODE -ne 0) {
        throw "The official Codex installer failed with exit code $LASTEXITCODE."
    }
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $extra = @((Join-Path $HOME ".local\bin"), (Join-Path $HOME ".codex\bin"))
    $env:Path = (@($machinePath, $userPath) + $extra | Where-Object { $_ }) -join ";"
}

function Invoke-CodexLogin {
    param([string]$CodexExecutable)
    if ($UseDeviceCode) {
        Write-Host "Starting ChatGPT device-code login..."
        & $CodexExecutable login --device-auth
    } else {
        Write-Host "Starting ChatGPT browser login..."
        & $CodexExecutable login
    }
    if ($LASTEXITCODE -ne 0) { throw "Codex login did not complete successfully." }
}

function Test-VirtualEnvironment {
    param([string]$PythonPath, [string]$PythonWPath)
    if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) { return $false }
    if (-not (Test-Path -LiteralPath $PythonWPath -PathType Leaf)) { return $false }
    & $PythonPath -c "import sys; raise SystemExit(0 if sys.version_info >= (3,10) else 1)"
    return ($LASTEXITCODE -eq 0)
}

function Wait-ForMonitorHeartbeat {
    param([DateTimeOffset]$After, [int]$TimeoutSeconds = 100)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $task = Get-ScheduledTask -TaskName $MainTaskName -ErrorAction SilentlyContinue
        if ($task -and (Test-Path -LiteralPath $HeartbeatPath -PathType Leaf)) {
            try {
                $heartbeat = Get-Content -LiteralPath $HeartbeatPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $checked = [DateTimeOffset]::Parse([string]$heartbeat.checked_at).ToUniversalTime()
                if ($task.State -eq "Running" -and
                    $checked -gt $After.ToUniversalTime() -and
                    [string]$heartbeat.status -eq "ok") {
                    Write-Host "Background heartbeat verified at $checked; task state $($task.State)."
                    return
                }
            } catch {
                # Retry during concurrent atomic replacement.
            }
        }
        Start-Sleep -Seconds 2
    }
    $task = Get-ScheduledTask -TaskName $MainTaskName -ErrorAction SilentlyContinue
    $state = if ($task) { [string]$task.State } else { "not installed" }
    $logPath = Join-Path $DataDir "monitor.log"
    $tail = ""
    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        $tail = (Get-Content -LiteralPath $logPath -Tail 12 -ErrorAction SilentlyContinue | Out-String).Trim()
    }
    throw "No fresh successful background heartbeat arrived within $TimeoutSeconds seconds. Task state: $state. Recent log: $tail"
}

function Wait-ForUiHeartbeat {
    param(
        [DateTimeOffset]$After,
        [int]$TimeoutSeconds = 45
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $minimumWriteTime = $After.UtcDateTime.AddSeconds(-15)
    $lastReason = "No heartbeat evaluated yet."

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $task = Get-ScheduledTask `
                -TaskName $UiTaskName `
                -ErrorAction SilentlyContinue

            if ($null -eq $task) {
                $lastReason = "UI task is not installed."
            }
            elseif ([string]$task.State -ne "Running") {
                $lastReason = "UI task state is $($task.State)."
            }
            elseif (-not (
                Test-Path `
                    -LiteralPath $UiHeartbeatPath `
                    -PathType Leaf
            )) {
                $lastReason = "Heartbeat file does not exist."
            }
            else {
                $file = Get-Item `
                    -LiteralPath $UiHeartbeatPath `
                    -ErrorAction Stop

                $heartbeat = Get-Content `
                    -LiteralPath $UiHeartbeatPath `
                    -Raw `
                    -Encoding UTF8 `
                    -ErrorAction Stop |
                    ConvertFrom-Json `
                        -ErrorAction Stop

                $status = (
                    [string]$heartbeat.status
                ).Trim().ToLowerInvariant()

                $acceptedStatus = @(
                    "ok",
                    "stale",
                    "waiting"
                ) -contains $status

                $fileAgeSeconds = (
                    [DateTime]::UtcNow -
                    $file.LastWriteTimeUtc
                ).TotalSeconds

                if (
                    $acceptedStatus -and
                    $file.LastWriteTimeUtc -ge $minimumWriteTime -and
                    $fileAgeSeconds -ge -15 -and
                    $fileAgeSeconds -le 30
                ) {
                    $verificationMessage = ("Live widget verified. Status={0}; heartbeat file age={1:N1}s; task state={2}." -f $status, $fileAgeSeconds, $task.State)
                    Write-Host $verificationMessage
                    return
                }

                $lastReason = (
                    "Rejected heartbeat: status={0}; " +
                    "fileAge={1:N1}s; writeTime={2:o}; " +
                    "minimumWriteTime={3:o}."
                ) -f `
                    $status,
                    $fileAgeSeconds,
                    $file.LastWriteTimeUtc,
                    $minimumWriteTime
            }
        }
        catch {
            $lastReason = "Validation error: $($_.Exception.Message)"
        }

        Start-Sleep -Milliseconds 500
    }

    $heartbeatText = ""

    if (
        Test-Path `
            -LiteralPath $UiHeartbeatPath `
            -PathType Leaf
    ) {
        $heartbeatText = Get-Content `
            -LiteralPath $UiHeartbeatPath `
            -Raw `
            -ErrorAction SilentlyContinue
    }

    throw (
        "The live widget did not validate within " +
        "$TimeoutSeconds seconds. Last reason: $lastReason " +
        "Heartbeat: $heartbeatText"
    )
}
$basePython = Resolve-BasePython
$codexPath = Resolve-CodexPath
if (-not $codexPath) {
    if (-not $InstallCodexIfMissing) {
        throw @"
Codex CLI was not found. Install it, then rerun this installer, or run:
  .\install.ps1 -InstallCodexIfMissing
"@
    }
    Install-OfficialCodex
    $codexPath = Resolve-CodexPath
    if (-not $codexPath) {
        throw "Codex was installed but its executable could not be located. Open a new PowerShell window and rerun install.ps1."
    }
} elseif ($UpdateCodex) {
    Install-OfficialCodex
    $codexPath = Resolve-CodexPath
    if (-not $codexPath) { throw "Codex update completed but the executable could not be located." }
}
Write-Host "Using Codex CLI: $codexPath"

foreach ($name in @($WatchdogTaskName, $UiTaskName, $MainTaskName)) {
    $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if ($null -ne $task) {
        $PriorTaskStates[$name] = [pscustomobject]@{
            WasDisabled = ([string]$task.State -eq "Disabled")
            WasRunning = ([string]$task.State -eq "Running")
        }
        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        Disable-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue | Out-Null
    }
}

$backupDir = Join-Path $env:TEMP ("CodexUsageNotifier-backup-{0}" -f [guid]::NewGuid().ToString("N"))
$hadInstall = Test-Path -LiteralPath $InstallDir -PathType Container
$hadConfig = Test-Path -LiteralPath $ConfigPath -PathType Leaf
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
if ($hadInstall) {
    New-Item -ItemType Directory -Path (Join-Path $backupDir "install") -Force | Out-Null
    Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $_.Name -ne ".venv" } |
        Copy-Item -Destination (Join-Path $backupDir "install") -Recurse -Force
}
if ($hadConfig) {
    Copy-Item -LiteralPath $ConfigPath -Destination (Join-Path $backupDir "config.json") -Force
}

try {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

    # Replace the managed payload as a clean set while retaining the isolated
    # Python environment. This prevents files removed in a newer release from
    # lingering after an upgrade.
    Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne ".venv" } |
        Remove-Item -Recurse -Force -ErrorAction Stop

    Get-ChildItem -LiteralPath $SourceDir -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $InstallDir $_.Name) -Force
    }
    $sourceTests = Join-Path $SourceDir "tests"
    if (Test-Path -LiteralPath $sourceTests -PathType Container) {
        $targetTests = Join-Path $InstallDir "tests"
        Remove-Item -LiteralPath $targetTests -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item -LiteralPath $sourceTests -Destination $targetTests -Recurse -Force
    }
    Get-ChildItem -LiteralPath $InstallDir -Recurse -File -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue

    $VenvDir = Join-Path $InstallDir ".venv"
    $VenvPython = Join-Path $VenvDir "Scripts\python.exe"
    $VenvPythonW = Join-Path $VenvDir "Scripts\pythonw.exe"
    if (-not (Test-VirtualEnvironment -PythonPath $VenvPython -PythonWPath $VenvPythonW)) {
        Remove-Item -LiteralPath $VenvDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Creating an isolated Python environment..."
        $pythonExe = [string]$basePython.Executable
        $pythonArgs = [object[]]$basePython.Arguments
        & $pythonExe @pythonArgs -m venv $VenvDir
        if ($LASTEXITCODE -ne 0) { throw "Python could not create the virtual environment." }
    }
    if (-not (Test-VirtualEnvironment -PythonPath $VenvPython -PythonWPath $VenvPythonW)) {
        throw "The isolated Python environment failed its health check."
    }

    $Monitor = Join-Path $InstallDir "codex_usage_notifier.py"
    & $VenvPython $Monitor --init-config --codex-command $codexPath
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize config.json." }

    $LiveWidgetEnabled = $true
    try {
        $installedConfig = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $uiProperty = $installedConfig.PSObject.Properties["ui"]
        if ($null -ne $uiProperty -and $null -ne $uiProperty.Value) {
            $liveProperty = $uiProperty.Value.PSObject.Properties["live_widget"]
            if ($null -ne $liveProperty) {
                $LiveWidgetEnabled = [bool]$liveProperty.Value
            }
        }
    } catch {
        throw "The initialized UI configuration could not be read: $($_.Exception.Message)"
    }

    Write-Host "Verifying ChatGPT-backed authentication through structured Codex App Server data..."
    & $VenvPython $Monitor --verify-account --json
    if ($LASTEXITCODE -ne 0) {
        Invoke-CodexLogin -CodexExecutable $codexPath
        & $VenvPython $Monitor --verify-account --json
        if ($LASTEXITCODE -ne 0) { throw "Codex is not authenticated with a supported ChatGPT account." }
    }

    Write-Host "Testing the live structured rate-limit endpoint..."
    & $VenvPython $Monitor --status
    if ($LASTEXITCODE -ne 0) {
        throw "account/rateLimits/read failed. Update Codex or run repair-login.ps1."
    }

    Write-Host "Saving current values as the initial baseline..."
    & $VenvPython $Monitor --baseline
    if ($LASTEXITCODE -ne 0) { throw "Could not record the initial baseline." }
    $baselineHeartbeat = Get-Content -LiteralPath $HeartbeatPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $baselineChecked = [DateTimeOffset]::Parse([string]$baselineHeartbeat.checked_at)

    if (-not $SkipAlertTest) {
        Write-Host "Displaying the complete desktop alert test..."
        & $VenvPython $Monitor --test-alert
        if ($LASTEXITCODE -ne 0) { throw "The desktop helper failed its acknowledgement test." }
        $seen = Read-Host "Did you see the topmost Codex test popup? Type Y to confirm"
        if ($seen -notmatch "^(y|yes)$") {
            throw "Visual delivery was not confirmed. Check notification.log and rerun install.ps1."
        }
        $heard = Read-Host "Did you hear the repeated Codex test sound? Type Y to confirm"
        if ($heard -notmatch "^(y|yes)$") {
            throw "Sound delivery was not confirmed. Check Windows volume and rerun install.ps1."
        }
    }

    $uiStartAfter = [DateTimeOffset]::UtcNow
    Remove-Item -LiteralPath $UiHeartbeatPath -Force -ErrorAction SilentlyContinue
    # Start the managed components for installation validation even when the
    # requested final state is stopped. This makes -DoNotStart verifiable rather
    # than silently skipping the most important runtime checks.
    & (Join-Path $InstallDir "install-task.ps1") -InstallDir $InstallDir

    Write-Host "Waiting for a real background check from the installed monitor..."
    Wait-ForMonitorHeartbeat -After $baselineChecked -TimeoutSeconds 100
    if ($LiveWidgetEnabled) {
        Write-Host "Waiting for the compact live usage widget..."
        Wait-ForUiHeartbeat -After $uiStartAfter -TimeoutSeconds 45
        if (-not $SkipAlertTest) {
            $widgetSeen = Read-Host "Do you see the small live Codex usage bubble near the desktop edge? Type Y to confirm"
            if ($widgetSeen -notmatch "^(y|yes)$") {
                throw "The live widget was running but was not visually confirmed. Check ui.log and rerun install.ps1."
            }
        }
    } else {
        Write-Host "The live widget is disabled in config.json, so its visual validation was skipped."
    }

    Write-Host "Running final live diagnostics..."
    & $VenvPython $Monitor --diagnose
    if ($LASTEXITCODE -ne 0) { throw "Final diagnostics failed. Run run-diagnostics.ps1 for details." }

    if ($DoNotStart) {
        Write-Host "Stopping and disabling the validated tasks as requested..."
        foreach ($name in @($WatchdogTaskName, $UiTaskName, $MainTaskName)) {
            Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            Disable-ScheduledTask -TaskName $name | Out-Null
        }
    }

    Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "Installation completed and validated."
    Write-Host "Application: $InstallDir"
    Write-Host "State and logs: $DataDir"
    Write-Host "The monitor listens for update events and polls every 60 seconds as a fallback."
    Write-Host "The compact live widget updates from each confirmed usage reading and refreshes its countdown every second."
    if ($DoNotStart) {
        Write-Host "All managed tasks are installed but disabled. Run start-monitor.ps1 when you want to activate them."
    }
} catch {
    $original = $_
    Write-Warning "Installation failed. Restoring the prior managed files where possible."
    try {
        foreach ($name in @($WatchdogTaskName, $UiTaskName, $MainTaskName)) {
            Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        }
        if ($hadInstall) {
            Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -ne ".venv" } |
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            Get-ChildItem -LiteralPath (Join-Path $backupDir "install") -Force -ErrorAction SilentlyContinue |
                Copy-Item -Destination $InstallDir -Recurse -Force
        } else {
            Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($hadConfig) {
            Copy-Item -LiteralPath (Join-Path $backupDir "config.json") -Destination $ConfigPath -Force
        }

        # Remove every task definition created by this installation attempt.
        # Restoring only the files is insufficient because a newly registered UI
        # task could still point at a script that did not exist in the prior release.
        foreach ($name in @($WatchdogTaskName, $UiTaskName, $MainTaskName)) {
            Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
        }

        if ($PriorTaskStates.Count -gt 0) {
            $restoredTaskInstaller = Join-Path $InstallDir "install-task.ps1"
            if (-not (Test-Path -LiteralPath $restoredTaskInstaller -PathType Leaf)) {
                throw "The prior task installer could not be restored from the backup."
            }

            # Recreate definitions from the restored release rather than retaining
            # definitions from the failed upgrade. -DoNotStart gives rollback full
            # control over the final enabled and running states.
            & $restoredTaskInstaller -InstallDir $InstallDir -DoNotStart

            # A restored installer may define more managed tasks than existed before
            # the upgrade. Remove those extras so rollback returns to the exact set.
            foreach ($name in @($WatchdogTaskName, $UiTaskName, $MainTaskName)) {
                if (-not $PriorTaskStates.ContainsKey($name)) {
                    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
                    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
                }
            }

            foreach ($name in $PriorTaskStates.Keys) {
                $prior = $PriorTaskStates[$name]
                $restoredTask = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
                if ($null -eq $restoredTask) {
                    throw "The prior scheduled task '$name' was not recreated."
                }
                if ([bool]$prior.WasDisabled) {
                    Disable-ScheduledTask -TaskName $name -ErrorAction Stop | Out-Null
                } else {
                    Enable-ScheduledTask -TaskName $name -ErrorAction Stop | Out-Null
                }
                if ([bool]$prior.WasRunning) {
                    Start-ScheduledTask -TaskName $name -ErrorAction Stop
                }
            }
        }
    } catch {
        Write-Warning "Rollback also encountered an error: $($_.Exception.Message)"
    }
    Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    throw $original
}

